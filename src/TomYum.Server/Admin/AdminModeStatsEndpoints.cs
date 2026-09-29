using Microsoft.EntityFrameworkCore;
using TomYum.Domain.Entities;
using TomYum.Domain.Enums;
using TomYum.Domain.Repositories;
using TomYum.Domain.Services;
using TomYum.Persistence;
using TomYum.Persistence.OsuWeb;

namespace TomYum.Server.Admin;

/// <summary>
/// A player's Relax/Autopilot statistics in the website's format (osu-web's UserStatisticsTransformer),
/// for lazer's profile in those modes: the website has no such modes, TomYum keeps their stats and
/// scores. Ranks leave restricted players out, like the website's rankings.
/// </summary>
public static class AdminModeStatsEndpoints
{
    public static void MapModeStats(this RouteGroupBuilder admin)
    {
        ArgumentNullException.ThrowIfNull(admin);
        admin.MapGet("/users/{userId:int}/stats/{mode:int}", GetAsync);
    }

    internal static async Task<IResult> GetAsync(
        int userId, int mode, IUserRepository users, TomYumDbContext db, RestrictedUsers restricted, CancellationToken cancellationToken)
    {
        if (mode is < GameMode.RelaxOffset or > GameMode.AutopilotOffset + 3)
            return Results.BadRequest(new { error = "Only Relax (4-6) and Autopilot (8) are kept here." });
        if (await users.GetByIdAsync(userId, cancellationToken).ConfigureAwait(false) is not { } user)
            return Results.NotFound();

        var stats = await db.UserStats.AsNoTracking().FirstOrDefaultAsync(s => s.UserId == userId && s.Mode == mode, cancellationToken).ConfigureAwait(false)
            ?? new UserStats { UserId = userId, Mode = mode };

        // Ranks by pp among players who have any, restricted ones left out.
        var hidden = restricted.Ids.ToHashSet();
        var ranked = (await db.UserStats.AsNoTracking().Where(s => s.Mode == mode && s.Pp > 0)
                .OrderByDescending(s => s.Pp).Select(s => s.UserId).ToListAsync(cancellationToken).ConfigureAwait(false))
            .Where(id => !hidden.Contains(id) || id == userId).ToList();
        int? globalRank = stats.Pp > 0 && ranked.IndexOf(userId) is >= 0 and var at ? at + 1 : null;
        int? countryRank = null;
        if (globalRank is { } place)
        {
            var countrymen = 0;
            foreach (var id in ranked.Take(place))
            {
                if ((await users.GetByIdAsync(id, cancellationToken).ConfigureAwait(false))?.Country == user.Country)
                    countrymen++;
            }
            countryRank = countrymen;
        }

        var plays = db.Scores.AsNoTracking().Where(s => s.UserId == userId && s.Mode == mode);
        var hits = await plays.GroupBy(_ => 1).Select(g => new
        {
            Count300 = g.Sum(s => (long)s.Count300),
            Count100 = g.Sum(s => (long)s.Count100),
            Count50 = g.Sum(s => (long)s.Count50),
            CountMiss = g.Sum(s => (long)s.CountMiss),
            MaxCombo = g.Max(s => s.MaxCombo),
        }).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var grades = await plays.Where(s => s.Status == ScoreStatus.Best).GroupBy(s => s.Grade)
            .Select(g => new { Grade = g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Grade, g => g.Count, cancellationToken).ConfigureAwait(false);
        var playTime = await plays.Join(db.Beatmaps, s => s.BeatmapMd5, b => b.Md5, (s, b) => (long)b.TotalLength)
            .SumAsync(cancellationToken).ConfigureAwait(false);

        var level = OsuWebScores.Level(stats.TotalScore);
        return Results.Ok(new
        {
            count_100 = hits?.Count100 ?? 0,
            count_300 = hits?.Count300 ?? 0,
            count_50 = hits?.Count50 ?? 0,
            count_miss = hits?.CountMiss ?? 0,
            level = new { current = (int)Math.Floor(level), progress = (int)Math.Floor((level - Math.Floor(level)) * 100) },
            global_rank = globalRank,
            global_rank_percent = globalRank is { } g && ranked.Count > 0 ? (double)g / ranked.Count : (double?)null,
            global_rank_exp = (int?)null,
            pp = stats.Pp,
            pp_exp = 0,
            ranked_score = stats.RankedScore,
            hit_accuracy = stats.Accuracy * 100,
            accuracy = stats.Accuracy,
            play_count = stats.PlayCount,
            play_time = playTime,
            total_score = stats.TotalScore,
            total_hits = (hits?.Count300 ?? 0) + (hits?.Count100 ?? 0) + (hits?.Count50 ?? 0),
            maximum_combo = hits?.MaxCombo ?? 0,
            replays_watched_by_others = 0,
            is_ranked = globalRank is not null,
            grade_counts = new
            {
                ss = grades.GetValueOrDefault("X"),
                ssh = grades.GetValueOrDefault("XH"),
                s = grades.GetValueOrDefault("S"),
                sh = grades.GetValueOrDefault("SH"),
                a = grades.GetValueOrDefault("A"),
            },
            country_rank = countryRank,
            rank = new { country = countryRank },
        });
    }
}
