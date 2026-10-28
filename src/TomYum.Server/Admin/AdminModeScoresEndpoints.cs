using Microsoft.EntityFrameworkCore;
using TomYum.Domain.Entities;
using TomYum.Domain.Enums;
using TomYum.Domain.Services;
using TomYum.Persistence;
using TomYum.Server.Beatmaps;

namespace TomYum.Server.Admin;

/// <summary>
/// A Relax/Autopilot score as a profile lists it. <see cref="OsuWebScoreId"/> is the website's score id
/// of a lazer play (its replay carries that id), or null.
/// </summary>
public sealed record ModeScore(
    long Id, int BeatmapId, int Mods, double Pp, double Accuracy, string Grade, int MaxCombo, bool Perfect,
    long Score, bool Passed, DateTimeOffset PlayedAt, int Count300, int Count100, int Count50, int CountMiss, int CountGeki, int CountKatu,
    bool Stable, bool HasReplay, ulong? OsuWebScoreId);

/// <summary>A page of a profile list, and how many there are in all.</summary>
public sealed record ModeScorePage(int Total, IReadOnlyList<ModeScore> Scores);

/// <summary>A place on a map's Relax/Autopilot leaderboard.</summary>
public sealed record ModeLeaderboardEntry(int Position, int UserId, ModeScore Score);

/// <summary>A map's Relax/Autopilot leaderboard, and the viewer's own best on it (wherever it places).</summary>
public sealed record ModeLeaderboard(IReadOnlyList<ModeLeaderboardEntry> Scores, ModeLeaderboardEntry? UserScore);

/// <summary>Only these players' plays (the website's country, friend and team boards), posted since a country's list can be long.</summary>
public sealed record ModeLeaderboardPlayers(IReadOnlyList<int> UserIds);

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
        admin.MapGet("/mode-scores/{scoreId:long}", GetOneAsync);
        admin.MapGet("/mode-scores/top-plays/{mode:int}", TopPlaysAsync);
        admin.MapGet("/beatmaps/{beatmapId:int}/mode-scores/{mode:int}", LeaderboardAsync);
        admin.MapPost("/beatmaps/{beatmapId:int}/mode-scores/{mode:int}", PlayersLeaderboardAsync);
        admin.MapGet("/replays/mode-score/{scoreId:long}", ReplayAsync);
    }

    internal static async Task<IResult> GetAsync(
        int userId, int mode, string? type, int? offset, int? limit, bool? includeFails,
        TomYumDbContext db, RestrictedUsers restricted, TimeProvider time, IConfiguration configuration, CancellationToken cancellationToken)
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
                // Top plays are on maps that give pp (ranked, approved): not Loved, Qualified, graveyard or WIP ones.
                var best = plays.Where(s => s.Status == ScoreStatus.Best && s.Passed
                    && db.Beatmaps.Any(b => b.Md5 == s.BeatmapMd5 && (b.Status == RankedStatus.Ranked || b.Status == RankedStatus.Approved)));
                var total = await best.CountAsync(cancellationToken).ConfigureAwait(false);
                var page = await best.OrderByDescending(s => s.Pp).ThenByDescending(s => s.ScoreValue).Skip(skip).Take(take)
                    .ToListAsync(cancellationToken).ConfigureAwait(false);
                return Results.Ok(new ModeScorePage(total, [.. page.Select(s => Describe(s, mode, configuration))]));
            }
            case "recent":
            {
                var since = time.GetUtcNow().AddDays(-1);
                var recent = plays.Where(s => s.SubmittedAt >= since);
                if (includeFails != true)
                    recent = recent.Where(s => s.Passed);
                var total = await recent.CountAsync(cancellationToken).ConfigureAwait(false);
                var page = await recent.OrderByDescending(s => s.Id).Skip(skip).Take(take).ToListAsync(cancellationToken).ConfigureAwait(false);
                return Results.Ok(new ModeScorePage(total, [.. page.Select(s => Describe(s, mode, configuration))]));
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
                return Results.Ok(new ModeScorePage(firsts.Count, [.. firsts.Skip(skip).Take(take).Select(s => Describe(s, mode, configuration))]));
            }
            default:
                return Results.BadRequest(new { error = "type: best, recent or firsts." });
        }
    }

    /// <summary>
    /// A map's Relax/Autopilot leaderboard for the website's beatmap page: the in-game one (by pp,
    /// restricted players left out), plus the viewer's best when <paramref name="viewerId"/> is given.
    /// <paramref name="mods"/> keeps plays with exactly those mods, like the in-game "Selected Mods"
    /// board; the Relax/Autopilot bit itself is ignored (the mode already says it). <paramref name="sort"/>
    /// "score" orders the same plays (each player's best) by score instead of pp. <paramref name="stableOnly"/>
    /// is the website's stable mode (not lazer's): each player's best stable play, as the in-game board,
    /// with no lazer plays (whose mods, like a custom DT speed, stable doesn't have).
    /// </summary>
    internal static Task<IResult> LeaderboardAsync(
        int beatmapId, int mode, int? limit, int? viewerId, int? mods, string? sort, bool? stableOnly,
        TomYum.Domain.Repositories.IBeatmapRepository beatmaps, TomYumDbContext db, RestrictedUsers restricted, IConfiguration configuration, CancellationToken cancellationToken) =>
        BoardAsync(beatmapId, mode, limit, viewerId, mods, sort == "score", stableOnly == true, null, beatmaps, db, restricted, configuration, cancellationToken);

    /// <summary>The same leaderboard with only <see cref="ModeLeaderboardPlayers.UserIds"/>'s plays (country, friends, team).</summary>
    internal static Task<IResult> PlayersLeaderboardAsync(
        int beatmapId, int mode, int? limit, int? viewerId, int? mods, string? sort, bool? stableOnly, ModeLeaderboardPlayers players,
        TomYum.Domain.Repositories.IBeatmapRepository beatmaps, TomYumDbContext db, RestrictedUsers restricted, IConfiguration configuration, CancellationToken cancellationToken) =>
        BoardAsync(beatmapId, mode, limit, viewerId, mods, sort == "score", stableOnly == true, players.UserIds ?? [], beatmaps, db, restricted, configuration, cancellationToken);

    private static async Task<IResult> BoardAsync(
        int beatmapId, int mode, int? limit, int? viewerId, int? mods, bool byScore, bool stableOnly, IReadOnlyList<int>? userIds,
        TomYum.Domain.Repositories.IBeatmapRepository beatmaps, TomYumDbContext db, RestrictedUsers restricted, IConfiguration configuration, CancellationToken cancellationToken)
    {
        if (mode is < GameMode.RelaxOffset or > GameMode.AutopilotOffset + 3)
            return Results.BadRequest(new { error = "Only Relax (4-6) and Autopilot (8) are kept here." });
        var beatmap = await beatmaps.GetByIdAsync(beatmapId, cancellationToken).ConfigureAwait(false);
        if (beatmap is null)
            return Results.Ok(new ModeLeaderboard([], null));
        if (stableOnly)
            return Results.Ok(await StableBoardAsync(db, beatmap.Md5, mode, limit, viewerId, mods, byScore, userIds, restricted, configuration, cancellationToken).ConfigureAwait(false));

        // As the in-game board (ScoreRepository.GetLeaderboardAsync): by pp (or by score when asked),
        // restricted players left out except to themselves.
        var hiddenFromBoard = restricted.Ids.Where(id => id != viewerId).ToList();
        var visible = Board(db, beatmap.Md5, mode, mods, userIds).Where(s => !hiddenFromBoard.Contains(s.UserId));
        var ordered = byScore
            ? visible.OrderByDescending(s => s.ScoreValue).ThenByDescending(s => s.Pp)
            : visible.OrderByDescending(s => s.Pp).ThenByDescending(s => s.ScoreValue);
        var top = await ordered.Take(Math.Clamp(limit ?? 50, 1, 100)).ToListAsync(cancellationToken).ConfigureAwait(false);
        var entries = top.Select((s, i) => new ModeLeaderboardEntry(i + 1, s.UserId, Describe(s, mode, configuration))).ToList();

        ModeLeaderboardEntry? mine = entries.FirstOrDefault(e => e.UserId == viewerId);
        if (mine is null && viewerId is { } viewer)
        {
            var best = await Board(db, beatmap.Md5, mode, mods, userIds).FirstOrDefaultAsync(s => s.UserId == viewer, cancellationToken).ConfigureAwait(false);
            if (best is not null && best.Passed)
            {
                var hidden = restricted.Ids.Where(id => id != viewer).ToList();
                var ahead = Board(db, beatmap.Md5, mode, mods, userIds).Where(s => !hidden.Contains(s.UserId));
                ahead = byScore ? ahead.Where(s => s.ScoreValue > best.ScoreValue) : ahead.Where(s => s.Pp > best.Pp);
                var position = 1 + await ahead.CountAsync(cancellationToken).ConfigureAwait(false);
                mine = new ModeLeaderboardEntry(position, viewer, Describe(best, mode, configuration));
            }
        }
        return Results.Ok(new ModeLeaderboard(entries, mine));
    }

    /// <summary>
    /// The stable board (the website's stable mode): each player's best stable play, which is their
    /// Best unless that's a lazer play, then their best earlier stable one (ScoreRepository's stable boards).
    /// </summary>
    private static async Task<ModeLeaderboard> StableBoardAsync(
        TomYumDbContext db, string md5, int mode, int? limit, int? viewerId, int? mods, bool byScore, IReadOnlyList<int>? userIds,
        RestrictedUsers restricted, IConfiguration configuration, CancellationToken cancellationToken)
    {
        var plays = db.Scores.AsNoTracking().Where(s => s.BeatmapMd5 == md5 && s.Mode == mode && s.Passed && !s.FromLazer
                                                         && (s.Status == ScoreStatus.Best || s.Status == ScoreStatus.Submitted));
        if (userIds is not null)
        {
            var ids = userIds.ToList();
            plays = plays.Where(s => ids.Contains(s.UserId));
        }
        if (mods is { } wanted)
        {
            var want = wanted & ~ModeMods;
            plays = plays.Where(s => (s.Mods & ~ModeMods) == want);
        }
        var ordered = byScore
            ? plays.OrderByDescending(s => s.ScoreValue).ThenByDescending(s => s.Pp).ThenBy(s => s.Id)
            : plays.OrderByDescending(s => s.Pp).ThenByDescending(s => s.ScoreValue).ThenBy(s => s.Id);
        var bests = (await ordered.ToListAsync(cancellationToken).ConfigureAwait(false)).DistinctBy(s => s.UserId).ToList();

        var hiddenFromBoard = restricted.Ids.Where(id => id != viewerId).ToHashSet();
        var board = bests.Where(s => !hiddenFromBoard.Contains(s.UserId)).ToList();
        var entries = board.Take(Math.Clamp(limit ?? 50, 1, 100))
            .Select((s, i) => new ModeLeaderboardEntry(i + 1, s.UserId, Describe(s, mode, configuration))).ToList();
        ModeLeaderboardEntry? mine = entries.FirstOrDefault(e => e.UserId == viewerId);
        if (mine is null && viewerId is { } viewer && board.FindIndex(s => s.UserId == viewer) is var at and >= 0)
            mine = new ModeLeaderboardEntry(at + 1, viewer, Describe(board[at], mode, configuration));
        return new ModeLeaderboard(entries, mine);
    }

    /// <summary>Relax (128) and Autopilot (8192): implied by the mode, so a mods filter leaves them out.</summary>
    private const int ModeMods = 128 | 8192;

    /// <summary>
    /// A map's bests in a mode, optionally only those with exactly <paramref name="mods"/> (besides RX/AP)
    /// and only <paramref name="userIds"/>'s.
    /// </summary>
    private static IQueryable<Score> Board(TomYumDbContext db, string md5, int mode, int? mods, IReadOnlyList<int>? userIds)
    {
        var board = db.Scores.AsNoTracking().Where(s => s.BeatmapMd5 == md5 && s.Mode == mode && s.Status == ScoreStatus.Best);
        if (userIds is not null)
        {
            var ids = userIds.ToList();
            board = board.Where(s => ids.Contains(s.UserId));
        }
        if (mods is { } wanted)
        {
            var want = wanted & ~ModeMods;
            board = board.Where(s => (s.Mods & ~ModeMods) == want);
        }
        return board;
    }

    /// <summary>
    /// A mode's top plays for the website's rankings (its "Top Plays" with the Relax/Autopilot variant):
    /// every player's best on each map, stable or lazer, by pp; restricted players left out.
    /// </summary>
    internal static async Task<IResult> TopPlaysAsync(int mode, int? limit, TomYumDbContext db, RestrictedUsers restricted, IConfiguration configuration, CancellationToken cancellationToken)
    {
        if (mode is < GameMode.RelaxOffset or > GameMode.AutopilotOffset + 3)
            return Results.BadRequest(new { error = "Only Relax (4-6) and Autopilot (8) are kept here." });
        var hidden = restricted.Ids.ToList();
        var top = await db.Scores.AsNoTracking()
            .Where(s => s.Mode == mode && s.Status == ScoreStatus.Best && s.Passed && s.Pp > 0 && !hidden.Contains(s.UserId))
            .OrderByDescending(s => s.Pp).ThenBy(s => s.Id)
            .Take(Math.Clamp(limit ?? 1000, 1, 1000))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return Results.Ok(new ModeLeaderboard([.. top.Select((s, i) => new ModeLeaderboardEntry(i + 1, s.UserId, Describe(s, mode, configuration)))], null));
    }

    /// <summary>One Relax/Autopilot play for its page: who, which mode, its place on the map's leaderboard, whether there's a replay.</summary>
    internal static async Task<IResult> GetOneAsync(long scoreId, TomYumDbContext db, RestrictedUsers restricted, IConfiguration configuration, CancellationToken cancellationToken)
    {
        var score = await db.Scores.AsNoTracking().FirstOrDefaultAsync(s => s.Id == scoreId && s.Mode >= GameMode.RelaxOffset, cancellationToken).ConfigureAwait(false);
        if (score is null)
            return Results.NotFound();

        int? position = null;
        if (score.Status == ScoreStatus.Best && score.Passed)
        {
            var hidden = restricted.Ids.Where(id => id != score.UserId).ToList();
            position = 1 + await db.Scores.AsNoTracking().CountAsync(s => s.BeatmapMd5 == score.BeatmapMd5 && s.Mode == score.Mode
                && s.Status == ScoreStatus.Best && s.Pp > score.Pp && !hidden.Contains(s.UserId), cancellationToken).ConfigureAwait(false);
        }
        return Results.Ok(new
        {
            score = Describe(score, score.Mode, configuration),
            userId = score.UserId,
            mode = score.Mode,
            position,
            hasReplay = ReplayFiles.Find(configuration, score) is not null,
        });
    }

    /// <summary>The play's replay as a complete .osr (a stable play's stored file is the replay data only; the header comes from the score).</summary>
    internal static async Task<IResult> ReplayAsync(long scoreId, TomYumDbContext db, TomYum.Domain.Repositories.IUserRepository users, IConfiguration configuration, CancellationToken cancellationToken)
    {
        var score = await db.Scores.AsNoTracking().FirstOrDefaultAsync(s => s.Id == scoreId && s.Mode >= GameMode.RelaxOffset, cancellationToken).ConfigureAwait(false);
        if (score is null || ReplayFiles.Find(configuration, score) is not { } replay)
            return Results.NotFound();
        var player = (await users.GetByIdAsync(score.UserId, cancellationToken).ConfigureAwait(false))?.Username ?? string.Empty;
        var fileName = $"{player} - {score.BeatmapMd5} ({score.Id}).osr";
        if (replay.Complete)
            return Results.File(replay.Path, "application/octet-stream", fileName);
        var frames = await File.ReadAllBytesAsync(replay.Path, cancellationToken).ConfigureAwait(false);
        return Results.File(OsrFile.Build(score, player, frames), "application/octet-stream", fileName);
    }

    private static ModeScore Describe(Score s, int mode, IConfiguration configuration) => new(
        s.Id, s.BeatmapId, s.Mods, s.Pp,
        AccuracyCalculator.Compute(GameMode.AsVanilla(mode), s.Count300, s.Count100, s.Count50, s.CountGeki, s.CountKatu, s.CountMiss),
        s.Grade, s.MaxCombo, s.Perfect, s.ScoreValue, s.Passed, s.SubmittedAt,
        s.Count300, s.Count100, s.Count50, s.CountMiss, s.CountGeki, s.CountKatu, !s.FromLazer, ReplayFiles.Find(configuration, s) is not null,
        s.FromLazer ? s.OsuWebScoreId : null);
}
