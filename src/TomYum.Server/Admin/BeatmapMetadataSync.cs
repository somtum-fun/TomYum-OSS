using StackExchange.Redis;

using TomYum.Server.Bancho;

namespace TomYum.Server.Admin;

/// <summary>
/// Keeps TomYum's stored maps current with osu!: every <c>Beatmaps:SyncHours</c> (6 by default, 0 turns it off),
/// the sets ranked or loved since the last sync are imported through <see cref="BeatmapImportRunner"/> (osu!'s API,
/// never the mirror). Each stored map keeps osu!'s status and details, so a score on it is looked up from the table
/// instead of online (<see cref="Beatmaps.BeatmapService"/>), and a mirror or osu! outage doesn't hold submissions up.
/// The first sync looks back two days; older maps come from a bulk import by ranked date in the BN panel (once).
/// Runs on the cluster's leader only; where it got to is kept in Redis (<see cref="CursorKey"/>).
/// </summary>
public sealed class BeatmapMetadataSync : BackgroundService
{
    /// <summary>The ranked date the last finished sync reached (Unix seconds).</summary>
    public const string CursorKey = "tomyum:beatmaps:synced-until";

    private static readonly TimeSpan FirstLookBack = TimeSpan.FromDays(2);
    private static readonly TimeSpan Overlap = TimeSpan.FromHours(1); // a map ranked while the last sync ran

    private readonly BeatmapImportRunner _runner;
    private readonly TimeProvider _time;
    private readonly ILogger<BeatmapMetadataSync> _logger;
    private readonly IDatabase? _redis;
    private readonly TimeSpan _every;

    public BeatmapMetadataSync(BeatmapImportRunner runner, IConfiguration configuration, TimeProvider time,
        ILogger<BeatmapMetadataSync> logger, IConnectionMultiplexer? redis = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _runner = runner;
        _time = time;
        _logger = logger;
        _redis = redis?.GetDatabase();
        _every = TimeSpan.FromHours(Math.Max(0, configuration.GetValue("Beatmaps:SyncHours", 6.0)));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_every <= TimeSpan.Zero)
            return;
        // Not with the server's own start-up work.
        await Task.Delay(TimeSpan.FromMinutes(2), _time, stoppingToken).ConfigureAwait(false);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SyncOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Beatmap sync failed; trying again in {Hours} h.", _every.TotalHours);
            }
            await Task.Delay(_every, _time, stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>One sync: imports the sets ranked since the cursor and moves it on when the import finished.</summary>
    public async Task<BeatmapImportJob> SyncOnceAsync(CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        var from = await CursorAsync().ConfigureAwait(false) is { } cursor ? cursor - Overlap : now - FirstLookBack;
        var job = _runner.Enqueue(new BulkImportRequest(null, from, now), BanchoBot.DefaultUserId);
        while (job.FinishedAt is null)
            await Task.Delay(TimeSpan.FromSeconds(5), _time, cancellationToken).ConfigureAwait(false);

        if (job.Status == "done" && job.Failed == 0)
        {
            await SetCursorAsync(now).ConfigureAwait(false);
            _logger.LogInformation("Beatmap sync: {Imported} set(s) ranked since {From:u} stored.", job.Imported, from);
        }
        else
        {
            _logger.LogWarning("Beatmap sync: {Status}, {Imported} stored, {Failed} failed; the same range is asked again next time.",
                job.Status, job.Imported, job.Failed);
        }
        return job;
    }

    private async Task<DateTimeOffset?> CursorAsync()
    {
        if (_redis is null)
            return null;
        try
        {
            var value = await _redis.StringGetAsync(CursorKey).ConfigureAwait(false);
            return value.TryParse(out long seconds) ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            return null;
        }
    }

    private async Task SetCursorAsync(DateTimeOffset at)
    {
        if (_redis is null)
            return;
        try
        {
            await _redis.StringSetAsync(CursorKey, at.ToUnixTimeSeconds()).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            _logger.LogWarning(ex, "Beatmap sync: couldn't keep where it got to.");
        }
    }
}
