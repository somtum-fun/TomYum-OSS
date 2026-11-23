using System.Collections.Concurrent;
using System.Text.Json;

using StackExchange.Redis;

using TomYum.Persistence.OsuWeb;

namespace TomYum.Server.Beatmaps;

/// <summary>
/// The beatmap mirrors this server uses, and how each one is doing. Staff set them in the website's admin panel
/// (<see cref="OsuWebBeatmapMirrors"/>, re-read every minute); until they do, the server's own settings are the one
/// mirror for lookups (<c>Mirror:*</c>, with its own pace as before) and for downloads (<c>Direct:*</c>).
/// <list type="bullet">
/// <item>Lookups ask the enabled lookup mirrors by priority, each at its own pace; one that refused (403), said
/// "too many" (429) or failed is passed over for the next (<see cref="BeatmapMirror"/>).</item>
/// <item>Players' downloads go to the first enabled download mirror.</item>
/// <item>How each one is doing (paused until, the last problem) goes to Redis (<see cref="HealthKey"/>) for the panel.</item>
/// </list>
/// </summary>
public sealed class BeatmapMirrorPool : IDisposable
{
    /// <summary>Redis hash: mirror address → its health as JSON (the website's admin panel shows it).</summary>
    public const string HealthKey = "tomyum:beatmap-mirrors:health";

    private static readonly TimeSpan ReadEvery = TimeSpan.FromMinutes(1);

    /// <summary>One mirror as this server uses it.</summary>
    public sealed record Mirror(string Name, Uri BaseUri, int RequestsPerMinute, bool Lookups, bool Downloads, string DownloadPath);

    private readonly MirrorOptions _options;
    private readonly OsuApiBudget _configuredPace;
    private readonly IServiceScopeFactory? _scopes;
    private readonly IDatabase? _redis;
    private readonly TimeProvider _time;
    private readonly ILogger<BeatmapMirrorPool> _logger;
    private readonly ConcurrentDictionary<string, OsuApiBudget> _paces = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _reading = new(1, 1);
    private IReadOnlyList<Mirror>? _fromPanel;
    private DateTimeOffset _readAt = DateTimeOffset.MinValue;
    private bool _noScopesSaid;

    public BeatmapMirrorPool(MirrorOptions options, [FromKeyedServices(BeatmapMirror.PaceKey)] OsuApiBudget configuredPace,
        TimeProvider time, ILogger<BeatmapMirrorPool> logger, IServiceScopeFactory? scopes = null, IConnectionMultiplexer? redis = null)
    {
        _options = options;
        _configuredPace = configuredPace;
        _time = time;
        _logger = logger;
        _scopes = scopes;
        _redis = redis?.GetDatabase();
    }

    public void Dispose()
    {
        _reading.Dispose();
        foreach (var pace in _paces.Values)
            pace.Dispose();
    }

    /// <summary>The configured mirror (<c>Mirror:*</c>), used for lookups while the panel sets none.</summary>
    private Mirror? Configured => _options.Enabled && Uri.TryCreate(_options.Url, UriKind.Absolute, out var uri)
        ? new Mirror("configured", new Uri(uri.AbsoluteUri.TrimEnd('/') + "/"), _options.RequestsPerMinute, true, false, "")
        : null;

    /// <summary>The mirrors lookups ask, in order.</summary>
    public async Task<IReadOnlyList<Mirror>> LookupMirrorsAsync(CancellationToken cancellationToken = default)
    {
        var fromPanel = await FromPanelAsync(cancellationToken).ConfigureAwait(false);
        if (fromPanel is not null)
            return [.. fromPanel.Where(m => m.Lookups)];
        return Configured is { } configured ? [configured] : [];
    }

    /// <summary>Where players' downloads go: the first enabled download mirror, or null for the server's <c>Direct:*</c>.</summary>
    public Mirror? DownloadMirror
    {
        get
        {
            // Read in the background when it's time; a download never waits for the database.
            if (_time.GetUtcNow() - _readAt >= ReadEvery)
                _ = Task.Run(() => FromPanelAsync(CancellationToken.None));
            return _fromPanel?.FirstOrDefault(m => m.Downloads);
        }
    }

    /// <summary>The pace (and pause) of <paramref name="mirror"/>.</summary>
    public OsuApiBudget PaceFor(Mirror mirror)
    {
        ArgumentNullException.ThrowIfNull(mirror);
        if (mirror.Name == "configured" && _fromPanel is null)
            return _configuredPace;
        var key = mirror.BaseUri.AbsoluteUri;
        var pace = _paces.GetOrAdd(key, _ => new OsuApiBudget(_time, Math.Max(1, mirror.RequestsPerMinute)));
        if (pace.PerMinute != Math.Max(1, mirror.RequestsPerMinute))
        {
            // Staff changed its rate: a new pace, keeping a pause it was under.
            var changed = new OsuApiBudget(_time, Math.Max(1, mirror.RequestsPerMinute));
            if (pace.IsPaused)
                changed.Pause(pace.PausedUntil);
            _paces[key] = changed;
            pace = changed;
        }
        return pace;
    }

    /// <summary>Notes a problem with <paramref name="mirror"/> (for the panel); <paramref name="pausedUntil"/> when it's paused.</summary>
    public void Report(Mirror mirror, string problem, DateTimeOffset? pausedUntil = null)
    {
        ArgumentNullException.ThrowIfNull(mirror);
        if (_redis is null)
            return;
        try
        {
            var health = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["name"] = mirror.Name,
                ["last_problem"] = problem,
                ["last_problem_at"] = _time.GetUtcNow().ToUnixTimeSeconds(),
                ["paused_until"] = pausedUntil?.ToUnixTimeSeconds(),
                ["used_last_minute"] = PaceFor(mirror).UsedLastMinute,
            });
            _redis.HashSet(HealthKey, mirror.BaseUri.AbsoluteUri, health, flags: CommandFlags.FireAndForget);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
        }
    }

    private static bool SameList(IReadOnlyList<Mirror>? a, IReadOnlyList<Mirror>? b) =>
        a is null ? b is null : b is not null && a.SequenceEqual(b);

    /// <summary>Takes <paramref name="entries"/> as the panel's list (tests; null: none set).</summary>
    internal void UsePanelList(IReadOnlyList<BeatmapMirrorEntry>? entries)
    {
        _fromPanel = entries is null ? null : Usable(entries);
        _readAt = _time.GetUtcNow();
    }

    // The enabled ones this server may ask, by priority.
    private static IReadOnlyList<Mirror> Usable(IReadOnlyList<BeatmapMirrorEntry> entries) =>
    [
        .. entries
            .Where(e => e.Enabled && e.BaseUri is not null)
            .OrderBy(e => e.Priority)
            .Select(e => new Mirror(e.Name, e.BaseUri!, e.RequestsPerMinute, e.Lookups, e.Downloads,
                string.IsNullOrWhiteSpace(e.DownloadPath) ? "/d/{id}" : e.DownloadPath.Trim())),
    ];

    // The panel's list (enabled ones, by priority), read at most once a minute; null when the panel set none.
    private async Task<IReadOnlyList<Mirror>?> FromPanelAsync(CancellationToken cancellationToken)
    {
        if (_scopes is null)
        {
            if (!_noScopesSaid)
            {
                _noScopesSaid = true;
                _logger.LogWarning("Beatmap mirrors: no way to read the admin panel's list; using the server's own settings.");
            }
            return _fromPanel;
        }
        if (_time.GetUtcNow() - _readAt < ReadEvery)
            return _fromPanel;
        if (!await _reading.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return _fromPanel; // another request is reading it
        try
        {
            await using var scope = _scopes.CreateAsyncScope();
            var before = _fromPanel;
            if (scope.ServiceProvider.GetService<OsuWebBeatmapMirrors>() is { } settings
                && await settings.GetAsync(cancellationToken).ConfigureAwait(false) is { } entries)
            {
                _fromPanel = Usable(entries);
            }
            else
            {
                _fromPanel = null;
            }
            if (!SameList(before, _fromPanel))
            {
                _logger.LogInformation("Beatmap mirrors: {List}.", _fromPanel is null
                    ? "the server's own settings (the admin panel sets none)"
                    : _fromPanel.Count == 0 ? "none (the admin panel's list is empty)" : string.Join(", ", _fromPanel.Select(m =>
                        $"{m.Name} ({(m.Lookups ? "lookups" : "")}{(m.Lookups && m.Downloads ? ", " : "")}{(m.Downloads ? "downloads" : "")})")));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The database down: keep what we had.
            _logger.LogWarning(ex, "Beatmap mirrors: couldn't read the admin panel's list; keeping the one before.");
        }
        finally
        {
            _readAt = _time.GetUtcNow();
            _reading.Release();
        }
        return _fromPanel;
    }
}
