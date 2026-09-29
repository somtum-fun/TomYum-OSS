using Microsoft.EntityFrameworkCore;
using TomYum.Domain.Entities;
using TomYum.Domain.Enums;
using TomYum.Domain.Repositories;
using TomYum.Domain.Services;
using TomYum.Persistence;

namespace TomYum.Server.Admin;

/// <summary>A row of a Relax/Autopilot ranking.</summary>
public sealed record ModeRankingRow(int UserId, double Pp, double Accuracy, int PlayCount, long RankedScore, int Ss, int S, int A);

/// <summary>
/// The website's rankings in Relax/Autopilot (it has none: TomYum keeps those stats): by pp or ranked
/// score, optionally one country's players or a player's friends; restricted players left out.
/// </summary>
public static class AdminModeRankingsEndpoints
{
    public static void MapModeRankings(this RouteGroupBuilder admin)
    {
        ArgumentNullException.ThrowIfNull(admin);
        admin.MapGet("/rankings/{mode:int}", GetAsync);
    }

    internal static async Task<IResult> GetAsync(
        int mode, string? sort, string? country, int? friendsOf, int? offset, int? limit,
        TomYumDbContext db, IUserRepository users, IFriendRepository friends, RestrictedUsers restricted, CancellationToken cancellationToken)
    {
        if (mode is < GameMode.RelaxOffset or > GameMode.AutopilotOffset + 3)
            return Results.BadRequest(new { error = "Only Relax (4-6) and Autopilot (8) are kept here." });

        var byScore = sort == "score";
        var hidden = restricted.Ids.ToHashSet();
        var rows = await db.UserStats.AsNoTracking()
            .Where(s => s.Mode == mode && (byScore ? s.RankedScore > 0 : s.Pp > 0))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var ranked = rows.Where(s => !hidden.Contains(s.UserId));

        if (friendsOf is { } viewer)
        {
            var circle = (await friends.GetFriendIdsAsync(viewer, cancellationToken).ConfigureAwait(false)).Append(viewer).ToHashSet();
            ranked = ranked.Where(s => circle.Contains(s.UserId));
        }
        if (!string.IsNullOrEmpty(country))
        {
            var inCountry = new List<Domain.Entities.UserStats>();
            foreach (var stats in ranked)
            {
                if (string.Equals((await users.GetByIdAsync(stats.UserId, cancellationToken).ConfigureAwait(false))?.Country, country, StringComparison.OrdinalIgnoreCase))
                    inCountry.Add(stats);
            }
            ranked = inCountry;
        }

        var ordered = (byScore ? ranked.OrderByDescending(s => s.RankedScore) : ranked.OrderByDescending(s => s.Pp)).ToList();
        var page = ordered.Skip(Math.Max(0, offset ?? 0)).Take(Math.Clamp(limit ?? 50, 1, 100)).ToList();
        var ids = page.Select(s => s.UserId).ToList();
        var grades = await db.Scores.AsNoTracking()
            .Where(s => s.Mode == mode && s.Status == ScoreStatus.Best && ids.Contains(s.UserId))
            .GroupBy(s => new { s.UserId, s.Grade }).Select(g => new { g.Key.UserId, g.Key.Grade, Count = g.Count() })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        int Grades(int userId, params string[] names) => grades.Where(g => g.UserId == userId && names.Contains(g.Grade)).Sum(g => g.Count);

        return Results.Ok(new
        {
            total = ordered.Count,
            rows = page.Select(s => new ModeRankingRow(s.UserId, s.Pp, s.Accuracy, s.PlayCount, s.RankedScore,
                Grades(s.UserId, "X", "XH"), Grades(s.UserId, "S", "SH"), Grades(s.UserId, "A"))),
        });
    }
}
