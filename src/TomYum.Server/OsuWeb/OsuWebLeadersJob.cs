using TomYum.Persistence.OsuWeb;

namespace TomYum.Server.OsuWeb;

/// <summary>
/// Keeps the website's first places (<see cref="OsuWebLeaders"/>) up to date: every map with a new
/// score (stable or lazer) is looked at again within seconds, and everything is rebuilt at start and
/// every quarter hour (restrictions, removed scores and status changes move first places too).
/// </summary>
public sealed class OsuWebLeadersJob : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RebuildEvery = TimeSpan.FromMinutes(15);

    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _time;
    private readonly ILogger<OsuWebLeadersJob> _logger;

    public OsuWebLeadersJob(IServiceScopeFactory scopes, TimeProvider time, ILogger<OsuWebLeadersJob> logger)
    {
        _scopes = scopes;
        _time = time;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        ulong mark = 0;
        var nextRebuild = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var leaders = scope.ServiceProvider.GetRequiredService<OsuWebLeaders>();
                if (_time.GetUtcNow() >= nextRebuild)
                {
                    // Read the mark first: scores arriving during the rebuild are looked at next pass.
                    mark = await leaders.NewestScoreIdAsync(stoppingToken).ConfigureAwait(false);
                    await leaders.RebuildAsync(stoppingToken).ConfigureAwait(false);
                    nextRebuild = _time.GetUtcNow() + RebuildEvery;
                }
                else
                {
                    var (maps, newest) = await leaders.ChangedSinceAsync(mark, stoppingToken).ConfigureAwait(false);
                    await leaders.SyncAsync(maps, stoppingToken).ConfigureAwait(false);
                    mark = newest;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // a database hiccup: try again next pass
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _logger.LogWarning(ex, "Updating the website's first places failed; trying again shortly.");
            }
            await Task.Delay(Interval, _time, stoppingToken).ConfigureAwait(false);
        }
    }
}
