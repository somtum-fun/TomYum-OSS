using System.Globalization;
using TomYum.Bancho;
using TomYum.Domain.Entities;
using TomYum.Domain.Enums;
using TomYum.Domain.Repositories;
using TomYum.Server.Bancho;

namespace TomYum.Server.Beatmaps;

public static class WebEndpoints
{
    public static void MapWeb(this IEndpointRouteBuilder app, params string[] hosts)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/web/osu-osz2-getscores.php", HandleGetScoresAsync)
            .RequireHost(hosts);

        // osu!stable rates beatmaps after score submission; just ack it.
        app.MapGet("/web/osu-rate.php", () => Results.Text("ok", "text/plain"))
            .RequireHost(hosts);

        app.MapGet("/web/osu-getreplay.php", HandleGetReplayAsync)
            .RequireHost(hosts);

        // Last.fm scrobbling and friends list — not implemented, ack to suppress 404s.
        app.MapGet("/web/lastfm.php", () => Results.Text("-3", "text/plain"))
            .RequireHost(hosts);
        app.MapGet("/web/osu-getfriends.php", () => Results.Text(string.Empty, "text/plain"))
            .RequireHost(hosts);

        app.MapScoreSubmission(hosts);

        // Seasonal backgrounds: return an empty array to suppress the feature.
        app.MapGet("/web/osu-getseasonal.php", () => Results.Json(Array.Empty<string>()))
            .RequireHost(hosts);

        // webfm.php: osu!stable hits this from the multiplayer lobby; an empty 200 is sufficient.
        app.MapGet("/web/webfm.php", () => Results.Text(string.Empty, "text/plain"))
            .RequireHost(hosts);
    }

    private static async Task<IResult> HandleGetScoresAsync(
        HttpContext context,
        IConfiguration configuration,
        BeatmapService beatmaps,
        IScoreRepository scores,
        IUserRepository users,
        PlayerRegistry registry,
        CancellationToken cancellationToken)
    {
        var md5 = context.Request.Query["c"].ToString();
        if (string.IsNullOrEmpty(md5))
        {
            return Results.Text(NotSubmitted, "text/plain");
        }

        var beatmap = await beatmaps.GetByMd5Async(md5, cancellationToken).ConfigureAwait(false);
        if (beatmap is null)
        {
            return Results.Text(NotSubmitted, "text/plain");
        }

        if (!beatmap.Status.HasLeaderboard())
        {
            var line = string.Create(CultureInfo.InvariantCulture, $"{(int)beatmap.Status}|false");
            return Results.Text(line, "text/plain");
        }

        if (!int.TryParse(context.Request.Query["m"].ToString(), out var mode))
        {
            mode = 0;
        }

        // The client sends its active mods; Relax/Autopilot select that variant's leaderboard so
        // relax scores never mix with vanilla. The combined mode also picks the sort (pp vs score).
        _ = int.TryParse(context.Request.Query["mods"].ToString(), out var modsArg);
        var gameMode = ((Mods)modsArg).ToGameMode(mode);

        var board = await scores.GetLeaderboardAsync(md5, gameMode, 50, cancellationToken).ConfigureAwait(false);

        // The requesting player's personal best. osu!stable sends its username as `us` (not the
        // session token); personal bests are public (they're on the board too), so the name is enough.
        Score? personalBest = null;
        var requesterId = await ResolveRequesterIdAsync(context.Request.Query["us"].ToString(), registry, users, cancellationToken).ConfigureAwait(false);
        if (requesterId is { } requester)
        {
            personalBest = await scores.GetPersonalBestAsync(requester, md5, gameMode, cancellationToken).ConfigureAwait(false);
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"{(int)beatmap.Status}|false|{beatmap.Id}|{beatmap.SetId}|{board.Count}|0|"));
        sb.AppendLine("0");
        sb.AppendLine(beatmap.FullName);
        sb.AppendLine("10.0");

        var replayDir = configuration["Replays:Path"] ?? "data/replays";
        var replayDirFull = Path.GetFullPath(replayDir);

        // On relax/autopilot boards the client should display pp as the "score" column
        // (bancho.py's leaderboard_value), matching the pp-based ranking.
        var showPp = GameMode.UsesPpLeaderboard(gameMode);

        if (personalBest is not null)
        {
            var pbUsername = await GetUsernameAsync(personalBest.UserId, users, registry, cancellationToken).ConfigureAwait(false);
            var pbRank = board.TakeWhile(s => s.Id != personalBest.Id).Count() + 1;
            var pbHasReplay = File.Exists(Path.Combine(replayDirFull, $"{personalBest.Id}.osr"));
            sb.AppendLine(FormatScoreLine(personalBest, pbUsername, pbRank, pbHasReplay, showPp));
        }
        else
        {
            sb.AppendLine(string.Empty);
        }

        for (var i = 0; i < board.Count; i++)
        {
            var s = board[i];
            var username = await GetUsernameAsync(s.UserId, users, registry, cancellationToken).ConfigureAwait(false);
            var hasReplay = File.Exists(Path.Combine(replayDirFull, $"{s.Id}.osr"));
            sb.AppendLine(FormatScoreLine(s, username, i + 1, hasReplay, showPp));
        }

        return Results.Text(sb.ToString(), "text/plain");
    }

    /// <summary>
    /// The user id behind the <c>us</c> (username) parameter of a stable <c>/web/</c> request: an
    /// online player of that name, else the account. Null when unknown or empty.
    /// </summary>
    internal static async Task<int?> ResolveRequesterIdAsync(string username, PlayerRegistry registry, IUserRepository users, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(username))
            return null;

        if (registry.GetByUsername(username) is { } online)
            return online.UserId;

        var user = await users.GetBySafeNameAsync(User.MakeSafeName(username), cancellationToken).ConfigureAwait(false);
        return user?.Id;
    }

    private static IResult HandleGetReplayAsync(HttpContext context, IConfiguration configuration)
    {
        if (!long.TryParse(context.Request.Query["c"].ToString(), out var scoreId))
            return Results.NotFound();

        var replayDir = configuration["Replays:Path"] ?? "data/replays";
        // Path.GetFullPath resolves relative paths against the process working directory.
        // Results.PhysicalFile requires an absolute path; Results.File with a relative path
        // resolves against the web root (wwwroot), which is wrong for data files on disk.
        var path = Path.GetFullPath(Path.Combine(replayDir, $"{scoreId}.osr"));

        if (!File.Exists(path))
            return Results.NotFound();

        // Results.File(string) resolves against the web root (wwwroot), not the filesystem.
        // Open a FileStream so we can use the stream overload, which serves any path.
        return Results.File(File.OpenRead(path), "application/octet-stream", $"{scoreId}.osr");
    }

    private static async Task<string> GetUsernameAsync(int userId, IUserRepository users, PlayerRegistry registry, CancellationToken ct)
    {
        var online = registry.GetByUserId(userId);
        if (online is not null)
        {
            return online.Presence.Username;
        }

        var user = await users.GetByIdAsync(userId, ct).ConfigureAwait(false);
        return user?.Username ?? userId.ToString(CultureInfo.InvariantCulture);
    }

    private static string FormatScoreLine(Score s, string username, int rank, bool hasReplay, bool showPp)
    {
        // The 3rd field is the value the client shows and ranks by: pp (rounded) on relax/autopilot
        // boards, total score on vanilla — matching the leaderboard's sort metric.
        var leaderboardValue = showPp ? (long)Math.Round(s.Pp, MidpointRounding.AwayFromZero) : s.ScoreValue;
        return string.Create(CultureInfo.InvariantCulture,
            $"{s.Id}|{username}|{leaderboardValue}|{s.MaxCombo}|{s.Count50}|{s.Count100}|{s.Count300}|{s.CountMiss}|{s.CountKatu}|{s.CountGeki}|{(s.Perfect ? 1 : 0)}|{s.Mods}|{s.UserId}|{rank}|{s.SubmittedAt.ToUnixTimeSeconds()}|{(hasReplay ? 1 : 0)}");
    }

    private static readonly string NotSubmitted =
        string.Create(CultureInfo.InvariantCulture, $"{(int)RankedStatus.NotSubmitted}|false");
}
