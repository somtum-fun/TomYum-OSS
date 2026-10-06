using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using TomYum.Domain.Entities;

namespace TomYum.Server.Beatmaps;

/// <summary>Where pack downloads are kept and how big one may get, bound from the <c>Packs</c> section.</summary>
public sealed class PackArchiveOptions
{
    public const string Section = "Packs";

    /// <summary>Folder of the built zips (default <c>data/packs</c>, inside the server's data volume).</summary>
    public string Path { get; set; } = "data/packs";

    /// <summary>The most one pack's zip may weigh; a bigger pack fails to build (default 4 GiB).</summary>
    public long MaxBytes { get; set; } = 4L * 1024 * 1024 * 1024;

    /// <summary>Pause between two sets' downloads, to go easy on the mirror.</summary>
    public TimeSpan DelayBetweenSets { get; set; } = TimeSpan.FromSeconds(1);
}

/// <summary>Where a pack's zip is: <c>none</c> (never built), <c>building</c>, <c>ready</c> or <c>failed</c>.</summary>
public sealed record PackArchiveStatus(string State, int Done, int Total, long Bytes, string? Error, DateTime? FinishedAt);

/// <summary>
/// Beatmap pack downloads (<c>/d/packs/{tag}</c>): one zip of the pack's <c>.osz</c> files, built on
/// demand from the same mirror osu!direct downloads from (custom sets from <see cref="CustomBeatmapStore"/>)
/// and kept on disk. A zip is named after the pack's sets, so changing the sets builds a new one (the
/// old one is removed then). One pack builds at a time.
/// </summary>
public sealed partial class PackArchives : IDisposable
{
    private readonly PackArchiveOptions _options;
    private readonly IOptions<DirectOptions> _direct;
    private readonly CustomBeatmapStore _custom;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<PackArchives> _logger;
    private readonly string _root;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, Job> _jobs = new(StringComparer.Ordinal);

    /// <summary>Downloads one set's <c>.osz</c>: its stream and file name (tests give their own).</summary>
    internal Func<int, CancellationToken, Task<(Stream Body, string? Name)>> OpenSet { get; init; }

    public PackArchives(
        IOptions<PackArchiveOptions> options,
        IOptions<DirectOptions> direct,
        CustomBeatmapStore custom,
        IHttpClientFactory http,
        ILogger<PackArchives> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _direct = direct;
        _custom = custom;
        _http = http;
        _logger = logger;
        _root = System.IO.Path.GetFullPath(_options.Path);
        OpenSet = OpenSetAsync;
    }

    /// <summary>Whether <paramref name="tag"/> can name a file (pack tags are short letters and digits).</summary>
    public static bool IsValidTag(string? tag) => tag is not null && ValidTag().IsMatch(tag);

    /// <summary>The zip's file name for these sets: the tag and a hash of the (sorted) set ids.</summary>
    public static string FileName(string tag, IEnumerable<int> setIds)
    {
        var ids = string.Join(',', setIds.Distinct().Order().Select(id => id.ToString(CultureInfo.InvariantCulture)));
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(ids)))[..12];
        return $"{tag}-{hash}.zip";
    }

    /// <summary>The finished zip for these sets, or null while there's none.</summary>
    public string? ReadyPath(string tag, IReadOnlyCollection<int> setIds)
    {
        var path = System.IO.Path.Combine(_root, FileName(tag, setIds));
        return File.Exists(path) ? path : null;
    }

    /// <summary>Where the zip of these sets is.</summary>
    public PackArchiveStatus Status(string tag, IReadOnlyCollection<int> setIds)
    {
        if (ReadyPath(tag, setIds) is { } path)
            return new PackArchiveStatus("ready", setIds.Count, setIds.Count, new FileInfo(path).Length, null, File.GetLastWriteTimeUtc(path));
        return _jobs.TryGetValue(FileName(tag, setIds), out var job)
            ? job.Snapshot()
            : new PackArchiveStatus("none", 0, setIds.Count, 0, null, null);
    }

    /// <summary>How long a failed build waits before a player's download tries it again (staff can retry at once).</summary>
    public static readonly TimeSpan FailedRetryDelay = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Starts building the zip of these sets unless it's there or on its way. A failed build is tried
    /// again, once it failed at least <paramref name="retryFailedAfter"/> ago (null: right away). Returns where it is.
    /// </summary>
    /// <param name="tag">The pack's tag.</param>
    /// <param name="setIds">The pack's sets.</param>
    /// <param name="retryFailedAfter">How long after a failed build a new one may start (null: right away).</param>
    public PackArchiveStatus Start(string tag, IReadOnlyCollection<int> setIds, TimeSpan? retryFailedAfter = null)
    {
        ArgumentNullException.ThrowIfNull(setIds);
        if (!IsValidTag(tag))
            throw new ArgumentException("A pack tag is letters and digits only.", nameof(tag));
        if (setIds.Count == 0 || ReadyPath(tag, setIds) is not null)
            return Status(tag, setIds);

        var key = FileName(tag, setIds);
        var job = _jobs.AddOrUpdate(
            key,
            _ => new Job(setIds.Count),
            (_, existing) => existing.CanRetry(retryFailedAfter) ? new Job(setIds.Count) : existing);
        if (job.TryClaim())
            _ = Task.Run(() => BuildAsync(tag, key, setIds.Distinct().ToList(), job));
        return job.Snapshot();
    }

    private async Task BuildAsync(string tag, string key, List<int> setIds, Job job)
    {
        var final = System.IO.Path.Combine(_root, key);
        var part = final + ".part";
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_root);
            await using (var file = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
            {
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var setId in setIds)
                {
                    var (body, name) = await OpenWithRetriesAsync(setId).ConfigureAwait(false);
                    await using (body.ConfigureAwait(false))
                    {
                        var entry = zip.CreateEntry(UniqueName(EntryName(setId, name), names), CompressionLevel.NoCompression);
                        await using var target = await entry.OpenAsync().ConfigureAwait(false);
                        await body.CopyToAsync(target).ConfigureAwait(false);
                    }
                    await file.FlushAsync().ConfigureAwait(false);
                    if (file.Length > _options.MaxBytes)
                        throw new InvalidOperationException($"The pack is bigger than the {_options.MaxBytes / (1024 * 1024)} MB a download may be.");
                    job.Progress(file.Length);
                    if (_options.DelayBetweenSets > TimeSpan.Zero)
                        await Task.Delay(_options.DelayBetweenSets).ConfigureAwait(false);
                }
            }

            File.Move(part, final, overwrite: true);
            foreach (var old in Directory.EnumerateFiles(_root, tag + "-*.zip"))
            {
                if (!string.Equals(old, final, StringComparison.Ordinal))
                    File.Delete(old);
            }
            job.Finish(new FileInfo(final).Length, null);
            _jobs.TryRemove(key, out _);
            _logger.LogInformation("Built pack {Tag}: {Count} sets, {Bytes} bytes", tag, setIds.Count, new FileInfo(final).Length);
        }
#pragma warning disable CA1031 // a mirror or disk problem fails this pack's build, never the server
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogWarning(ex, "Building pack {Tag} failed", tag);
            job.Finish(0, ex.Message);
            TryDelete(part);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<(Stream Body, string? Name)> OpenWithRetriesAsync(int setId)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await OpenSet(setId, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < 3 && ex is HttpRequestException or TaskCanceledException)
            {
                await Task.Delay(TimeSpan.FromSeconds(5 * attempt)).ConfigureAwait(false);
            }
        }
    }

    private async Task<(Stream Body, string? Name)> OpenSetAsync(int setId, CancellationToken cancellationToken)
    {
        if (setId >= Beatmap.CustomIdFloor)
        {
            return _custom.HasSet(setId)
                ? (File.OpenRead(_custom.OszPath(setId)), null)
                : throw new InvalidOperationException($"Set {setId} isn't on this server.");
        }

        var cfg = _direct.Value;
        var url = cfg.MirrorBaseUrl + string.Format(CultureInfo.InvariantCulture, cfg.DownloadPath, setId) + "?novideo";
        var response = await _http.CreateClient(DirectEndpoints.DownloadHttpClientName)
            .GetAsync(new Uri(url), HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            response.Dispose();
            if (status is 404)
                throw new InvalidOperationException($"The mirror doesn't have set {setId}.");
            throw new HttpRequestException($"The mirror answered {status} for set {setId}.");
        }

        var name = response.Content.Headers.ContentDisposition?.FileNameStar?.Trim('"')
            ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"');
        return (new OwningStream(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), response), name);
    }

    /// <summary>A set's file name in the zip: the mirror's ("123 Artist - Title.osz") made safe, else "123.osz".</summary>
    internal static string EntryName(int setId, string? name)
    {
        var safe = name is null ? string.Empty : UnsafeName().Replace(System.IO.Path.GetFileName(name), string.Empty).Trim().TrimStart('.');
        if (safe.Length == 0)
            return string.Create(CultureInfo.InvariantCulture, $"{setId}.osz");
        if (!safe.EndsWith(".osz", StringComparison.OrdinalIgnoreCase))
            safe += ".osz";
        return safe.Length > 200 ? string.Create(CultureInfo.InvariantCulture, $"{setId}.osz") : safe;
    }

    private static string UniqueName(string name, HashSet<string> taken)
    {
        var candidate = name;
        for (var i = 2; !taken.Add(candidate); i++)
            candidate = string.Create(CultureInfo.InvariantCulture, $"{System.IO.Path.GetFileNameWithoutExtension(name)} ({i}).osz");
        return candidate;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Overwritten by the next build.
        }
    }

    public void Dispose() => _gate.Dispose();

    [GeneratedRegex("^[A-Za-z0-9]{1,5}$")]
    private static partial Regex ValidTag();

    [GeneratedRegex(@"[\x00-\x1f""*:<>?/\\|]")]
    private static partial Regex UnsafeName();

    /// <summary>One build in progress (or failed).</summary>
    private sealed class Job(int total)
    {
        private readonly Lock _lock = new();
        private int _claimed;
        private int _done;
        private long _bytes;
        private string? _error;
        private DateTime? _finishedAt;
        private bool _finished;

        public string State
        {
            get
            {
                lock (_lock)
                    return !_finished ? "building" : _error is null ? "ready" : "failed";
            }
        }

        /// <summary>Whether this build failed, at least <paramref name="after"/> ago (null: any time).</summary>
        public bool CanRetry(TimeSpan? after)
        {
            lock (_lock)
                return _finished && _error is not null && (after is null || DateTime.UtcNow - _finishedAt >= after);
        }

        public bool TryClaim() => Interlocked.Exchange(ref _claimed, 1) == 0;

        public void Progress(long bytes)
        {
            lock (_lock)
            {
                _done++;
                _bytes = bytes;
            }
        }

        public void Finish(long bytes, string? error)
        {
            lock (_lock)
            {
                _finished = true;
                _error = error;
                if (bytes > 0)
                    _bytes = bytes;
                _finishedAt = DateTime.UtcNow;
            }
        }

        public PackArchiveStatus Snapshot()
        {
            lock (_lock)
                return new PackArchiveStatus(!_finished ? "building" : _error is null ? "ready" : "failed", _done, total, _bytes, _error, _finishedAt);
        }
    }

    /// <summary>A response's body that disposes the response with it.</summary>
    private sealed class OwningStream(Stream inner, IDisposable owner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => inner.ReadAsync(buffer, offset, count, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                owner.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
