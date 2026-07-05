using System.Globalization;
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

        var board = await scores.GetLeaderboardAsync(md5, mode, 50, cancellationToken).ConfigureAwait(false);

        // Look up requesting player's personal best via token
        Score? personalBest = null;
        var token = context.Request.Query["us"].ToString();
        var requestingPlayer = string.IsNullOrEmpty(token) ? null : registry.GetByToken(token);
        if (requestingPlayer is not null)
        {
            personalBest = await scores.GetPersonalBestAsync(requestingPlayer.UserId, md5, mode, cancellationToken).ConfigureAwait(false);
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"{(int)beatmap.Status}|false|{beatmap.Id}|{beatmap.SetId}|{board.Count}|0|"));
        sb.AppendLine("0");
        sb.AppendLine(beatmap.FullName);
        sb.AppendLine("10.0");

        var replayDir = configuration["Replays:Path"] ?? "data/replays";
        var replayDirFull = Path.GetFullPath(replayDir);

        if (personalBest is not null)
        {
            var pbUsername = await GetUsernameAsync(personalBest.UserId, users, registry, cancellationToken).ConfigureAwait(false);
            var pbRank = board.TakeWhile(s => s.Id != personalBest.Id).Count() + 1;
            var pbHasReplay = File.Exists(Path.Combine(replayDirFull, $"{personalBest.Id}.osr"));
            sb.AppendLine(FormatScoreLine(personalBest, pbUsername, pbRank, pbHasReplay));
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
            sb.AppendLine(FormatScoreLine(s, username, i + 1, hasReplay));
        }

        return Results.Text(sb.ToString(), "text/plain");
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

    private static string FormatScoreLine(Score s, string username, int rank, bool hasReplay) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{s.Id}|{username}|{s.ScoreValue}|{s.MaxCombo}|{s.Count50}|{s.Count100}|{s.Count300}|{s.CountMiss}|{s.CountKatu}|{s.CountGeki}|{(s.Perfect ? 1 : 0)}|{s.Mods}|{s.UserId}|{rank}|{s.SubmittedAt.ToUnixTimeSeconds()}|{(hasReplay ? 1 : 0)}");

    private static readonly string NotSubmitted =
        string.Create(CultureInfo.InvariantCulture, $"{(int)RankedStatus.NotSubmitted}|false");
}
