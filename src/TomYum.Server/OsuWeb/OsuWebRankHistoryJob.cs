using TomYum.Persistence;
using TomYum.Persistence.OsuWeb;

namespace TomYum.Server.OsuWeb;

/// <summary>
/// Records the day's ranks for the website's profile rank graph once per UTC day
/// (<see cref="OsuWebRankHistory"/>): checked at startup and every <see cref="Interval"/>, so a
/// restart or a busy database never skips a day for long.
/// </summary>
public sealed class OsuWebRankHistoryJob : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);

    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _time;
    private readonly ILogger<OsuWebRankHistoryJob> _logger;

    public OsuWebRankHistoryJob(IServiceScopeFactory scopes, TimeProvider time, ILogger<OsuWebRankHistoryJob> logger)
    {
        _scopes = scopes;
        _time = time;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var history = scope.ServiceProvider.GetRequiredService<OsuWebRankHistory>();
                var now = _time.GetUtcNow();
                if (RankingInactivity.Enabled && await history.IsDueAsync(now, stoppingToken).ConfigureAwait(false))
                {
                    // The inactivity rule: players who haven't played lose their rank once a day, before the
                    // day's ranks are recorded (otherwise only a new score would recompute the ranks).
                    var mirror = scope.ServiceProvider.GetRequiredService<OsuWebScoreMirror>();
                    for (var mode = 0; mode < 4; mode++)
                        await mirror.RecomputeGlobalRanksAsync(mode, stoppingToken).ConfigureAwait(false);
                }
                if (await history.RecordIfDueAsync(now, stoppingToken).ConfigureAwait(false))
                    _logger.LogInformation("Recorded today's ranks for the website's rank graphs.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Recording the day's ranks failed; trying again in {Minutes} minutes.", Interval.TotalMinutes);
            }
            await Task.Delay(Interval, _time, stoppingToken).ConfigureAwait(false);
        }
    }
}
