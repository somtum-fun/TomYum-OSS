using Microsoft.EntityFrameworkCore;
using TomYum.Domain.Entities;
using TomYum.Domain.Services;
using TomYum.Persistence;

namespace TomYum.Server.Admin;

/// <summary>
/// Once a day, everyone's Relax/Autopilot rank (by pp, restricted players left out), for the rank
/// graph on their profile in those modes; 90 days are kept, like the website's.
/// </summary>
public sealed class ModeRankHistoryJob : BackgroundService
{
    public static readonly int[] Modes = [4, 5, 6, 8];
    public const int Days = 90;

    private readonly IServiceScopeFactory _scopes;
    private readonly RestrictedUsers _restricted;
    private readonly TimeProvider _time;
    private readonly ILogger<ModeRankHistoryJob> _logger;

    public ModeRankHistoryJob(IServiceScopeFactory scopes, RestrictedUsers restricted, TimeProvider time, ILogger<ModeRankHistoryJob> logger)
    {
        _scopes = scopes;
        _restricted = restricted;
        _time = time;
        _logger = logger;
    }

    /// <summary>Records today's ranks (again, if already done: the latest wins); returns how many.</summary>
    public async Task<int> SnapshotAsync(CancellationToken cancellationToken = default)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TomYumDbContext>();
        var today = DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime);
        var hidden = _restricted.Ids.ToHashSet();
        var written = 0;

        foreach (var mode in Modes)
        {
            var ranked = (await db.UserStats.AsNoTracking().Where(s => s.Mode == mode && s.Pp > 0)
                    .OrderByDescending(s => s.Pp).Select(s => s.UserId).ToListAsync(cancellationToken).ConfigureAwait(false))
                .Where(id => !hidden.Contains(id)).ToList();
            await db.ModeRankHistory.Where(s => s.Mode == mode && s.Day == today).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            db.ModeRankHistory.AddRange(ranked.Select((userId, index) => new ModeRankSnapshot { UserId = userId, Mode = mode, Day = today, Rank = index + 1 }));
            written += ranked.Count;
        }
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        db.ChangeTracker.Clear();

        var oldest = today.AddDays(-Days);
        await db.ModeRankHistory.Where(s => s.Day < oldest).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        return written;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SnapshotAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Relax/Autopilot rank history: the snapshot failed.");
            }

            // Next at 00:10 UTC (again on a restart, which just refreshes today's).
            var now = _time.GetUtcNow();
            var next = new DateTimeOffset(now.UtcDateTime.Date.AddDays(1).AddMinutes(10), TimeSpan.Zero);
            await Task.Delay(next - now, _time, stoppingToken).ConfigureAwait(false);
        }
    }
}
