using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using TomYum.Persistence;
using TomYum.Persistence.OsuWeb;

namespace TomYum.Server.Beatmaps;

/// <summary>
/// osu! sets' audio previews (<c>b.&lt;domain&gt;/preview/{set}.mp3</c>, the website's play buttons and
/// osu!direct's), kept on disk: fetched once from the mirror (catboy.best, by one of the set's
/// difficulties) or else from osu! (<c>b.ppy.sh</c>), then served from here. The mirror sends its audio
/// without a length, so a browser playing it straight from there can't show how long it is; served
/// from a file it has one, and can be skipped through. Anyone can ask for any id, so sets this server
/// doesn't have are fetched only a few a minute, misses are remembered for a while, and only the
/// newest <see cref="MaxCachedKey"/> previews are kept.
/// </summary>
public sealed class AudioPreviews : IDisposable
{
    /// <summary>Named <see cref="HttpClient"/> for the downloads (the mirror wants a user agent).</summary>
    public const string HttpClientName = "AudioPreviews";

    /// <summary>The mirror's preview, by a difficulty's id.</summary>
    public static string MirrorUrl(int beatmapId) => string.Create(CultureInfo.InvariantCulture, $"https://catboy.best/preview/audio/{beatmapId}");

    /// <summary>osu!'s own preview, by the set's id.</summary>
    public static string OsuUrl(int setId) => string.Create(CultureInfo.InvariantCulture, $"https://b.ppy.sh/preview/{setId}.mp3");

    private readonly string _directory;
    private readonly IHttpClientFactory _http;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<AudioPreviews> _logger;

    /// <summary>One of a set's difficulties' ids, for the mirror (tests give their own).</summary>
    internal Func<int, CancellationToken, Task<int?>> BeatmapIdFor { get; init; }

    /// <summary>Requests to the mirror left in its window below which it's left alone until the window resets.</summary>
    public const int MirrorReserve = 50;

    // One download per set at a time: everyone asking meanwhile waits for the same one.
    private readonly ConcurrentDictionary<int, Lazy<Task<(string Path, string Type)?>>> _fetching = new();

    // A few downloads at once at most (a page of search results asks for many).
    private readonly SemaphoreSlim _downloads = new(4);

    // catboy.best allows 1200 requests a minute (X-Ratelimit-*): near the end of a window, or told
    // to slow down (429), previews come from osu! until it resets.
    private DateTimeOffset _mirrorPausedUntil = DateTimeOffset.MinValue;

    /// <summary>Configuration key: previews kept on disk at most (default <see cref="DefaultMaxCached"/>); the oldest go first.</summary>
    public const string MaxCachedKey = "Previews:MaxCached";

    public const int DefaultMaxCached = 20_000;

    /// <summary>
    /// Sets this server doesn't have fetched per minute at most: anyone can ask for any number, and
    /// osu!direct's results (from the mirror) aren't all here, so they're allowed, but slowly.
    /// </summary>
    public const int UnknownSetsPerMinute = 30;

    // Bigger than any preview (osu!'s are 10 seconds or so).
    private const long MaxPreviewBytes = 5 * 1024 * 1024;

    // Sets nowhere had a preview for, until when they aren't asked for again (a bounded list).
    private const int MaxMisses = 10_000;
    private static readonly TimeSpan MissRemembered = TimeSpan.FromMinutes(30);
    private readonly ConcurrentDictionary<int, DateTimeOffset> _misses = new();

    private readonly int _maxCached;
    private int _savedSinceTrim;

    private readonly Lock _unknownLock = new();
    private DateTimeOffset _unknownWindow = DateTimeOffset.MinValue;
    private int _unknownInWindow;

    public AudioPreviews(IConfiguration configuration, IHttpClientFactory http, IServiceScopeFactory scopes, ILogger<AudioPreviews> logger)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _directory = AssetsEndpoints.CacheDirectory(configuration, "previews");
        _maxCached = int.TryParse(configuration[MaxCachedKey], CultureInfo.InvariantCulture, out var max) && max > 0 ? max : DefaultMaxCached;
        _http = http;
        _scopes = scopes;
        _logger = logger;
        BeatmapIdFor = BeatmapIdAsync;
    }

    public void Dispose() => _downloads.Dispose();

    /// <summary>The set's preview file and its type (osu!'s are sometimes Ogg); null when nowhere has it.</summary>
    public async Task<(string Path, string Type)?> GetAsync(int setId, CancellationToken cancellationToken = default)
    {
        if (setId <= 0)
            return null;
        if (Cached(setId) is { } cached)
            return cached;
        if (_misses.TryGetValue(setId, out var missUntil) && missUntil > DateTimeOffset.UtcNow)
            return null;

        var fetch = _fetching.GetOrAdd(setId, id => new Lazy<Task<(string, string)?>>(() => FetchAsync(id)));
        try
        {
            return await fetch.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (fetch.IsValueCreated && fetch.Value.IsCompleted)
                _fetching.TryRemove(new KeyValuePair<int, Lazy<Task<(string Path, string Type)?>>>(setId, fetch));
        }
    }

    /// <summary>What the bytes are: Ogg or MP3 (with or without an ID3 tag); null for anything else (an error page).</summary>
    public static string? AudioType(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 1024)
            return null;
        if (bytes.StartsWith("OggS"u8))
            return "audio/ogg";
        if (bytes.StartsWith("ID3"u8) || (bytes[0] == 0xFF && (bytes[1] & 0xE0) == 0xE0))
            return "audio/mpeg";
        return null;
    }

    private (string Path, string Type)? Cached(int setId)
    {
        var mp3 = FilePath(setId, "mp3");
        if (File.Exists(mp3))
            return (mp3, "audio/mpeg");
        var ogg = FilePath(setId, "ogg");
        return File.Exists(ogg) ? (ogg, "audio/ogg") : null;
    }

    private string FilePath(int setId, string extension) =>
        Path.Combine(_directory, setId.ToString(CultureInfo.InvariantCulture) + "." + extension);

    private async Task<(string Path, string Type)?> FetchAsync(int setId)
    {
        // Not the asker's token: others may be waiting for this download too.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var urls = new List<(string Url, bool Mirror)>();
        var beatmapId = await BeatmapIdFor(setId, timeout.Token).ConfigureAwait(false);
        if (beatmapId is null && !TakeUnknownSet())
            return null;
        if (DateTimeOffset.UtcNow >= _mirrorPausedUntil && beatmapId is { } id)
            urls.Add((MirrorUrl(id), true));
        urls.Add((OsuUrl(setId), false));

        try
        {
            await _downloads.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        try
        {
            var preview = await DownloadAsync(setId, urls, timeout.Token).ConfigureAwait(false);
            if (preview is null)
                RememberMiss(setId);
            else if (Interlocked.Increment(ref _savedSinceTrim) % 100 == 0)
                Trim();
            return preview;
        }
        finally
        {
            _downloads.Release();
        }
    }

    /// <summary>Whether a set this server doesn't have may be fetched now (<see cref="UnknownSetsPerMinute"/>).</summary>
    private bool TakeUnknownSet()
    {
        lock (_unknownLock)
        {
            var now = DateTimeOffset.UtcNow;
            if (now - _unknownWindow >= TimeSpan.FromMinutes(1))
            {
                _unknownWindow = now;
                _unknownInWindow = 0;
            }
            return ++_unknownInWindow <= UnknownSetsPerMinute;
        }
    }

    private void RememberMiss(int setId)
    {
        // Bounded: a flood of made-up ids only starts the list over.
        if (_misses.Count >= MaxMisses)
            _misses.Clear();
        _misses[setId] = DateTimeOffset.UtcNow + MissRemembered;
    }

    /// <summary>Deletes the oldest previews while there are more than <see cref="MaxCachedKey"/>.</summary>
    internal void Trim()
    {
        try
        {
            var files = new DirectoryInfo(_directory).EnumerateFiles()
                .Where(f => f.Extension is ".mp3" or ".ogg")
                .ToList();
            if (files.Count <= _maxCached)
                return;
            // Down to 90%, so this doesn't run again on the next few downloads.
            foreach (var file in files.OrderBy(f => f.LastWriteTimeUtc).Take(files.Count - _maxCached * 9 / 10))
                file.Delete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogInformation(ex, "Audio previews: couldn't trim the cache.");
        }
    }

    private async Task<(string Path, string Type)?> DownloadAsync(int setId, List<(string Url, bool Mirror)> urls, CancellationToken cancellationToken)
    {
        using var http = _http.CreateClient(HttpClientName);
        foreach (var (url, mirror) in urls)
        {
            if (mirror && DateTimeOffset.UtcNow < _mirrorPausedUntil)
                continue;
            try
            {
                using var response = await http.GetAsync(url, cancellationToken).ConfigureAwait(false);
                if (mirror)
                    NoteMirrorLimit(response);
                if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxPreviewBytes)
                    continue;
                var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                if (AudioType(bytes) is not { } type)
                    continue;

                // Written whole, then moved into place: a half-written file is never served.
                Directory.CreateDirectory(_directory);
                var path = FilePath(setId, type == "audio/ogg" ? "ogg" : "mp3");
                var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                await File.WriteAllBytesAsync(temp, bytes, cancellationToken).ConfigureAwait(false);
                File.Move(temp, path, overwrite: true);
                return (path, type);
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
            {
                _logger.LogInformation(ex, "Audio preview for set {SetId}: {Url} failed.", setId, url);
            }
        }
        return null;
    }

    /// <summary>
    /// Reads the mirror's rate limit off a response: told to slow down (429, Retry-After) or near the
    /// end of its window (X-Ratelimit-Remaining under <see cref="MirrorReserve"/>), it's left alone
    /// until X-Ratelimit-Reset (a minute when that's missing).
    /// </summary>
    internal void NoteMirrorLimit(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);
        var limited = response.StatusCode == System.Net.HttpStatusCode.TooManyRequests;
        if (!limited && response.Headers.TryGetValues("X-Ratelimit-Remaining", out var remaining)
            && int.TryParse(remaining.FirstOrDefault(), CultureInfo.InvariantCulture, out var left))
            limited = left < MirrorReserve;
        if (!limited)
            return;

        var until = DateTimeOffset.UtcNow.AddMinutes(1);
        if (response.Headers.RetryAfter?.Delta is { } delta)
            until = DateTimeOffset.UtcNow + delta;
        else if (response.Headers.TryGetValues("X-Ratelimit-Reset", out var reset)
            && DateTimeOffset.TryParse(reset.FirstOrDefault(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var resetAt)
            && resetAt > DateTimeOffset.UtcNow)
            until = resetAt;
        _mirrorPausedUntil = until;
        _logger.LogInformation("Audio previews: catboy.best's rate limit is near; osu!'s previews until {Until}.", until);
    }

    /// <summary>Whether the mirror is being left alone for now (its rate limit).</summary>
    internal bool MirrorPaused => DateTimeOffset.UtcNow < _mirrorPausedUntil;

    /// <summary>One of the set's difficulties (the mirror goes by those): the website's list, else TomYum's own.</summary>
    private async Task<int?> BeatmapIdAsync(int setId, CancellationToken cancellationToken)
    {
        using var scope = _scopes.CreateScope();
        try
        {
            if (scope.ServiceProvider.GetService<OsuWebDbContext>() is { } web)
            {
                var ids = await web.Database
                    .SqlQuery<int>($"SELECT CAST(MIN(beatmap_id) AS SIGNED) AS Value FROM osu_beatmaps WHERE beatmapset_id = {setId} HAVING MIN(beatmap_id) IS NOT NULL")
                    .ToListAsync(cancellationToken).ConfigureAwait(false);
                if (ids.Count > 0)
                    return ids[0];
            }
            if (scope.ServiceProvider.GetService<TomYumDbContext>() is { } db)
                return await db.Beatmaps.Where(b => b.SetId == setId).MinAsync(b => (int?)b.Id, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or DbUpdateException or System.Data.Common.DbException)
        {
            _logger.LogInformation(ex, "Audio preview for set {SetId}: couldn't find one of its difficulties.", setId);
        }
        return null;
    }
}
