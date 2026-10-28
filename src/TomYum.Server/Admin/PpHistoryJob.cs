using Microsoft.EntityFrameworkCore;
using TomYum.Domain.Entities;
using TomYum.Domain.Services;
using TomYum.Persistence;
using TomYum.Persistence.OsuWeb;

namespace TomYum.Server.Admin;

/// <summary>
/// Once a day, everyone's total pp in every mode (the rulesets' from the website, Relax/Autopilot's from TomYum;
/// restricted players left out), for the pp-over-time graph on their profile; a year is kept.
/// </summary>
public sealed class PpHistoryJob : BackgroundService
{
    public static readonly int[] Modes = [0, 1, 2, 3, 4, 5, 6, 8];
    public const int Days = 365;

    private readonly IServiceScopeFactory _scopes;
    private readonly RestrictedUsers _restricted;
    private readonly TimeProvider _time;
    private readonly ILogger<PpHistoryJob> _logger;

    public PpHistoryJob(IServiceScopeFactory scopes, RestrictedUsers restricted, TimeProvider time, ILogger<PpHistoryJob> logger)
    {
        _scopes = scopes;
        _restricted = restricted;
        _time = time;
        _logger = logger;
    }

    /// <summary>Records today's pp (again, if already done: the latest wins); returns how many.</summary>
    public async Task<int> SnapshotAsync(CancellationToken cancellationToken = default)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TomYumDbContext>();
        var website = scope.ServiceProvider.GetService<OsuWebCommunity>();
        var today = DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime);
        var hidden = _restricted.Ids.ToHashSet();
        var written = 0;

        foreach (var mode in Modes)
        {
            IReadOnlyDictionary<int, double> pp;
            if (mode < 4)
            {
                if (website is null)
                    continue;
                pp = await website.GetAllPpAsync(mode, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                pp = await db.UserStats.AsNoTracking().Where(s => s.Mode == mode && s.Pp > 0)
                    .ToDictionaryAsync(s => s.UserId, s => s.Pp, cancellationToken).ConfigureAwait(false);
            }
            await db.PpHistory.Where(s => s.Mode == mode && s.Day == today).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            var rows = pp.Where(p => !hidden.Contains(p.Key)).Select(p => new PpSnapshot { UserId = p.Key, Mode = mode, Day = today, Pp = Math.Round(p.Value, 2) }).ToList();
            db.PpHistory.AddRange(rows);
            written += rows.Count;
        }
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        db.ChangeTracker.Clear();

        var oldest = today.AddDays(-Days);
        await db.PpHistory.Where(s => s.Day < oldest).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        return written;
    }

    /// <summary>A player's pp in a mode over the last <paramref name="days"/> days, oldest first (days without one left out).</summary>
    internal static async Task<IResult> GetAsync(int userId, int mode, int? days, TomYumDbContext db, TimeProvider time, CancellationToken cancellationToken)
    {
        var span = Math.Clamp(days ?? 90, 7, Days);
        var from = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime).AddDays(-span + 1);
        var points = await db.PpHistory.AsNoTracking()
            .Where(s => s.UserId == userId && s.Mode == mode && s.Day >= from)
            .OrderBy(s => s.Day)
            .Select(s => new { day = s.Day, pp = s.Pp })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return Results.Ok(points);
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
                _logger.LogWarning(ex, "pp history: the snapshot failed.");
            }

            // Next at 00:15 UTC (again on a restart, which just refreshes today's).
            var now = _time.GetUtcNow();
            var next = new DateTimeOffset(now.UtcDateTime.Date.AddDays(1).AddMinutes(15), TimeSpan.Zero);
            await Task.Delay(next - now, _time, stoppingToken).ConfigureAwait(false);
        }
    }
}
