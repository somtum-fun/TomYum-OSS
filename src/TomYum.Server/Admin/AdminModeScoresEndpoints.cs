using Microsoft.EntityFrameworkCore;
using TomYum.Domain.Entities;
using TomYum.Domain.Enums;
using TomYum.Domain.Services;
using TomYum.Persistence;

namespace TomYum.Server.Admin;

/// <summary>A Relax/Autopilot score as a profile lists it.</summary>
public sealed record ModeScore(
    long Id, int BeatmapId, int Mods, double Pp, double Accuracy, string Grade, int MaxCombo, bool Perfect,
    long Score, bool Passed, DateTimeOffset PlayedAt, int Count300, int Count100, int Count50, int CountMiss, int CountGeki, int CountKatu);

/// <summary>A page of a profile list, and how many there are in all.</summary>
public sealed record ModeScorePage(int Total, IReadOnlyList<ModeScore> Scores);

/// <summary>
/// A player's Relax/Autopilot plays for their profile's lists in those modes (the website doesn't have
/// them): best by pp, recent (the last day), and first places on the mode's leaderboards.
/// </summary>
public static class AdminModeScoresEndpoints
{
    public static void MapModeScores(this RouteGroupBuilder admin)
    {
        ArgumentNullException.ThrowIfNull(admin);
        admin.MapGet("/users/{userId:int}/mode-scores/{mode:int}", GetAsync);
    }

    internal static async Task<IResult> GetAsync(
        int userId, int mode, string? type, int? offset, int? limit, bool? includeFails,
        TomYumDbContext db, RestrictedUsers restricted, TimeProvider time, CancellationToken cancellationToken)
    {
        if (mode is < GameMode.RelaxOffset or > GameMode.AutopilotOffset + 3)
            return Results.BadRequest(new { error = "Only Relax (4-6) and Autopilot (8) are kept here." });
        var skip = Math.Max(0, offset ?? 0);
        var take = Math.Clamp(limit ?? 5, 1, 100);
        var plays = db.Scores.AsNoTracking().Where(s => s.UserId == userId && s.Mode == mode);

        switch (type)
        {
            case "best":
            {
                var best = plays.Where(s => s.Status == ScoreStatus.Best && s.Passed);
                var total = await best.CountAsync(cancellationToken).ConfigureAwait(false);
                var page = await best.OrderByDescending(s => s.Pp).ThenByDescending(s => s.ScoreValue).Skip(skip).Take(take)
                    .ToListAsync(cancellationToken).ConfigureAwait(false);
                return Results.Ok(new ModeScorePage(total, [.. page.Select(s => Describe(s, mode))]));
            }
            case "recent":
            {
                var since = time.GetUtcNow().AddDays(-1);
                var recent = plays.Where(s => s.SubmittedAt >= since);
                if (includeFails != true)
                    recent = recent.Where(s => s.Passed);
                var total = await recent.CountAsync(cancellationToken).ConfigureAwait(false);
                var page = await recent.OrderByDescending(s => s.Id).Skip(skip).Take(take).ToListAsync(cancellationToken).ConfigureAwait(false);
                return Results.Ok(new ModeScorePage(total, [.. page.Select(s => Describe(s, mode))]));
            }
            case "firsts":
            {
                // The player's bests that nobody (unrestricted) beats on that map's leaderboard for the mode.
                var hidden = restricted.Ids.Where(id => id != userId).ToList();
                var mine = await plays.Where(s => s.Status == ScoreStatus.Best && s.Passed).ToListAsync(cancellationToken).ConfigureAwait(false);
                var md5s = mine.Select(s => s.BeatmapMd5).ToList();
                var rivals = await db.Scores.AsNoTracking()
                    .Where(s => s.Mode == mode && s.Status == ScoreStatus.Best && s.UserId != userId && md5s.Contains(s.BeatmapMd5) && !hidden.Contains(s.UserId))
                    .GroupBy(s => s.BeatmapMd5).Select(g => new { Md5 = g.Key, Pp = g.Max(s => s.Pp) })
                    .ToDictionaryAsync(g => g.Md5, g => g.Pp, cancellationToken).ConfigureAwait(false);
                var firsts = mine.Where(s => !rivals.TryGetValue(s.BeatmapMd5, out var pp) || s.Pp > pp).OrderByDescending(s => s.SubmittedAt).ToList();
                return Results.Ok(new ModeScorePage(firsts.Count, [.. firsts.Skip(skip).Take(take).Select(s => Describe(s, mode))]));
            }
            default:
                return Results.BadRequest(new { error = "type: best, recent or firsts." });
        }
    }

    private static ModeScore Describe(Score s, int mode) => new(
        s.Id, s.BeatmapId, s.Mods, s.Pp,
        AccuracyCalculator.Compute(GameMode.AsVanilla(mode), s.Count300, s.Count100, s.Count50, s.CountGeki, s.CountKatu, s.CountMiss),
        s.Grade, s.MaxCombo, s.Perfect, s.ScoreValue, s.Passed, s.SubmittedAt,
        s.Count300, s.Count100, s.Count50, s.CountMiss, s.CountGeki, s.CountKatu);
}
