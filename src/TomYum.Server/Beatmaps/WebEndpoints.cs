using System.Globalization;
using TomYum.Bancho;
using TomYum.Domain.Entities;
using TomYum.Domain.Enums;
using TomYum.Domain.Repositories;
using TomYum.Domain.Services;
using TomYum.Server.Bancho;

namespace TomYum.Server.Beatmaps;

public static class WebEndpoints
{
    public static void MapWeb(this IEndpointRouteBuilder app, params string[] hosts)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/web/osu-osz2-getscores.php", HandleGetScoresAsync)
            .RequireHost(hosts);

        // The 1-10 rating after playing a map (kept with the website's ratings).
        app.MapGet("/web/osu-rate.php", HandleRateAsync)
            .RequireHost(hosts);

        app.MapGet("/web/osu-getreplay.php", HandleGetReplayAsync)
            .RequireHost(hosts);

        // Last.fm scrobbles (not kept) and the client's hidden anti-cheat flags (Anticheat.ClientFlags).
        app.MapGet("/web/lastfm.php", HandleLastFmAsync)
            .RequireHost(hosts);
        app.MapScoreSubmission(hosts);

        // Seasonal backgrounds (the admin panel's; none out of season or without the website).
        app.MapGet("/web/osu-getseasonal.php", HandleSeasonalAsync)
            .RequireHost(hosts);

        // webfm.php: osu!stable hits this from the multiplayer lobby; an empty 200 is sufficient.
        app.MapGet("/web/webfm.php", () => Results.Text(string.Empty, "text/plain"))
            .RequireHost(hosts);
    }

    internal static async Task<IResult> HandleGetScoresAsync(
        HttpContext context,
        IConfiguration configuration,
        BeatmapService beatmaps,
        IScoreRepository scores,
        IUserRepository users,
        PlayerRegistry registry,
        IPasswordHasher passwords,
        RestrictedUsers restricted,
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
            // An unknown hash may be an old version of a known map: then the client offers an update.
            return Results.Text(await IsOutdatedAsync(context, beatmaps, cancellationToken).ConfigureAwait(false) ? UpdateAvailable : NotSubmitted, "text/plain");
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

        // The requesting player. osu!stable sends its username as `us` and its password's md5 as `ha`.
        // Personal bests are public (they're on the board too), so the name is enough, except for a
        // restricted player: their scores are hidden from everyone else, so they see them (in place
        // on the board, bancho.py's shadow leaderboard) only when the password checks out.
        Score? personalBest = null;
        var requesterId = await ResolveRequesterIdAsync(context.Request.Query["us"].ToString(), registry, users, cancellationToken).ConfigureAwait(false);
        if (requesterId is { } requester && restricted.Contains(requester)
            && (await users.GetByIdAsync(requester, cancellationToken).ConfigureAwait(false) is not { } requesterUser
                || context.Request.Query["ha"].ToString() is not { Length: > 0 } passwordMd5
                || !passwords.Verify(passwordMd5, requesterUser.PasswordBcrypt)))
        {
            requesterId = null;
        }

        // The tab in song select (`v`): 1 global, 2 selected mods, 3 friends, 4 country.
        _ = int.TryParse(context.Request.Query["v"].ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var boardType);
        var board = await BoardAsync(context, scores, users, md5, gameMode, boardType, modsArg, requesterId, cancellationToken).ConfigureAwait(false);
        if (requesterId is { } viewer)
        {
            personalBest = await scores.GetStablePersonalBestAsync(viewer, md5, gameMode, cancellationToken).ConfigureAwait(false);
            // On the selected mods tab, a best with other mods isn't this board's.
            if (boardType == SelectedModsBoard && personalBest is not null && personalBest.Mods != modsArg)
                personalBest = null;
        }

        // The reply's format version (`vv`): old clients (docs/old-clients.md) send 1–3, today's 4.
        if (!int.TryParse(context.Request.Query["vv"].ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var formatVersion))
            formatVersion = 1;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"{StatusFor((int)beatmap.Status, formatVersion)}|false|{beatmap.Id}|{beatmap.SetId}|{board.Count}|0|"));
        sb.AppendLine("0");
        sb.AppendLine(beatmap.FullName);
        // The players' average rating (the website's); 10.0 without the website.
        var rating = context.RequestServices?.GetService<TomYum.Persistence.OsuWeb.OsuWebStableClient>() is { } website
            ? await website.AverageRatingAsync(beatmap.SetId, cancellationToken).ConfigureAwait(false)
            : 10.0;
        sb.AppendLine(rating.ToString("0.0#", CultureInfo.InvariantCulture));

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
            sb.AppendLine(FormatScoreLine(personalBest, pbUsername, pbRank, pbHasReplay, showPp, formatVersion));
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
            sb.AppendLine(FormatScoreLine(s, username, i + 1, hasReplay, showPp, formatVersion));
        }

        return Results.Text(sb.ToString(), "text/plain");
    }

    /// <summary>The menu backgrounds for osu!stable: a JSON list of image URLs on the website.</summary>
    internal static async Task<IResult> HandleSeasonalAsync(HttpContext context, IConfiguration configuration, CancellationToken cancellationToken)
    {
        if (context.RequestServices?.GetService<TomYum.Persistence.OsuWeb.OsuWebStableClient>() is not { } website)
            return Results.Json(Array.Empty<string>());
        var uploads = $"https://osu.{BanchoHosts.ResolveDomain(configuration)}/uploads/default/";
        var paths = await website.SeasonalBackgroundPathsAsync(DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
        return Results.Json(paths.Select(path => uploads + path).ToArray());
    }

    /// <summary>
    /// <c>?u=&amp;p=&amp;c={md5}[&amp;v=1..10]</c>: without <c>v</c>, whether the player may still rate the map
    /// (<c>ok</c>); with it, their rating. Either way a player who rated gets <c>alreadyvoted</c> and the average.
    /// </summary>
    internal static async Task<IResult> HandleRateAsync(HttpContext context, BeatmapService beatmaps, IUserRepository users, IPasswordHasher passwords, CancellationToken cancellationToken)
    {
        var query = context.Request.Query;
        var user = await users.GetBySafeNameAsync(User.MakeSafeName(query["u"].ToString()), cancellationToken).ConfigureAwait(false);
        if (user is null || query["p"].ToString() is not { Length: > 0 } passwordMd5 || !passwords.Verify(passwordMd5, user.PasswordBcrypt))
            return Results.Text("auth fail", "text/plain", statusCode: StatusCodes.Status401Unauthorized);
        if (context.RequestServices.GetService<TomYum.Persistence.OsuWeb.OsuWebStableClient>() is not { } website)
            return Results.Text("ok", "text/plain");
        if (await beatmaps.GetByMd5Async(query["c"].ToString(), cancellationToken).ConfigureAwait(false) is not { } beatmap)
            return Results.Text("no exist", "text/plain");
        if (!beatmap.Status.HasLeaderboard())
            return Results.Text("not ranked", "text/plain");

        double average;
        if (int.TryParse(query["v"], NumberStyles.None, CultureInfo.InvariantCulture, out var vote) && vote is >= 1 and <= 10)
        {
            average = await website.RateAsync(user.Id, beatmap.SetId, vote, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var (voted, current) = await website.RatingAsync(user.Id, beatmap.SetId, cancellationToken).ConfigureAwait(false);
            if (!voted)
                return Results.Text("ok", "text/plain");
            average = current;
        }
        return Results.Text("alreadyvoted\n" + average.ToString("0.0#", CultureInfo.InvariantCulture), "text/plain");
    }

    /// <summary>
    /// <c>?action=&amp;b=&amp;us=&amp;ha=</c>: a scrobble (answered <c>-3</c>, "stop sending"), or, when <c>b</c> is
    /// <c>a&lt;flags&gt;</c>, the client's anti-cheat flags (<see cref="Anticheat.ClientFlags"/>).
    /// </summary>
    internal static async Task<IResult> HandleLastFmAsync(HttpContext context, IUserRepository users, IPasswordHasher passwords, CancellationToken cancellationToken)
    {
        var query = context.Request.Query;
        var user = await users.GetBySafeNameAsync(User.MakeSafeName(query["us"].ToString()), cancellationToken).ConfigureAwait(false);
        if (user is null || query["ha"].ToString() is not { Length: > 0 } passwordMd5 || !passwords.Verify(passwordMd5, user.PasswordBcrypt))
            return Results.Unauthorized();
        if (Anticheat.ClientFlags.Parse(query["b"].ToString()) is not { } flags)
            return Results.Text("-3", "text/plain");
        await Anticheat.ClientFlags.HandleAsync(context.RequestServices, user, flags, cancellationToken).ConfigureAwait(false);
        return Results.Text(string.Empty, "text/plain");
    }

    private const int SelectedModsBoard = 2;
    private const int FriendsBoard = 3;
    private const int CountryBoard = 4;

    /// <summary>The top 50 on the tab the player picked; the global board for a tab that needs a player when there's none.</summary>
    internal static async Task<IReadOnlyList<Score>> BoardAsync(
        HttpContext context, IScoreRepository scores, IUserRepository users, string md5, int gameMode, int boardType, int mods, int? viewerId, CancellationToken cancellationToken)
    {
        switch (boardType)
        {
            case SelectedModsBoard:
                return await scores.GetLeaderboardAsync(md5, gameMode, 50, viewerId, new LeaderboardFilter(Mods: mods, StableOnly: true), cancellationToken).ConfigureAwait(false);
            case FriendsBoard when viewerId is { } friendsOf:
                var friends = await context.RequestServices.GetRequiredService<IFriendRepository>().GetFriendIdsAsync(friendsOf, cancellationToken).ConfigureAwait(false);
                return await scores.GetLeaderboardAsync(md5, gameMode, 50, viewerId, new LeaderboardFilter(UserIds: [.. friends, friendsOf], StableOnly: true), cancellationToken).ConfigureAwait(false);
            case CountryBoard when viewerId is { } countryOf
                && await users.GetByIdAsync(countryOf, cancellationToken).ConfigureAwait(false) is { Country: { Length: > 0 } country } && country != "xx":
                // Scores don't carry a country: take a deep board and keep the viewer's countrymen.
                var countrymen = new List<Score>();
                foreach (var score in await scores.GetLeaderboardAsync(md5, gameMode, 1000, viewerId, new LeaderboardFilter(StableOnly: true), cancellationToken).ConfigureAwait(false))
                {
                    if (await users.GetByIdAsync(score.UserId, cancellationToken).ConfigureAwait(false) is { } player
                        && string.Equals(player.Country, country, StringComparison.OrdinalIgnoreCase))
                    {
                        countrymen.Add(score);
                        if (countrymen.Count == 50)
                            break;
                    }
                }
                return countrymen;
            default:
                return await scores.GetLeaderboardAsync(md5, gameMode, 50, viewerId, new LeaderboardFilter(StableOnly: true), cancellationToken).ConfigureAwait(false);
        }
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

    internal static string FormatScoreLine(Score s, string username, int rank, bool hasReplay, bool showPp, int formatVersion = 4)
    {
        // The 3rd field is the value the client shows and ranks by: pp (rounded) on relax/autopilot
        // boards, total score on vanilla — matching the leaderboard's sort metric.
        var leaderboardValue = showPp ? (long)Math.Round(s.Pp, MidpointRounding.AwayFromZero) : s.ScoreValue;
        // Format 1 has the date as text, later ones as unix time; "has a replay" came with format 4.
        var date = formatVersion <= 1
            ? s.SubmittedAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
            : s.SubmittedAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var line = string.Create(CultureInfo.InvariantCulture,
            $"{s.Id}|{username}|{leaderboardValue}|{s.MaxCombo}|{s.Count50}|{s.Count100}|{s.Count300}|{s.CountMiss}|{s.CountKatu}|{s.CountGeki}|{(s.Perfect ? 1 : 0)}|{s.Mods}|{s.UserId}|{rank}|{date}");
        return formatVersion >= 4 ? line + (hasReplay ? "|1" : "|0") : line;
    }

    /// <summary>
    /// A map's status in the leaderboard reply's format version <paramref name="formatVersion"/> (`vv`).
    /// Today's (4): 2 ranked, 3 approved, 4 qualified, 5 loved. Format 3 had no loved (sent as approved);
    /// formats 1–2 numbered ranked 4 and qualified 2 as well. An old client waits forever on a number it
    /// doesn't know.
    /// </summary>
    internal static int StatusFor(int status, int formatVersion) => formatVersion switch
    {
        >= 4 => status,
        3 => status == 5 ? 3 : status,
        _ => status switch { 2 => 4, 4 => 2, 5 => 3, _ => status },
    };

    private static readonly string UpdateAvailable = string.Create(CultureInfo.InvariantCulture, $"{(int)RankedStatus.UpdateAvailable}|false");

    /// <summary>
    /// Whether the client's map (file <c>f</c> of set <c>i</c>) is an outdated copy: a difficulty of that
    /// name is known here, or osu! has one in the set (bancho.py's check).
    /// </summary>
    private static async Task<bool> IsOutdatedAsync(HttpContext context, BeatmapService beatmaps, CancellationToken cancellationToken)
    {
        var filename = context.Request.Query["f"].ToString();
        if (filename.Length == 0)
            return false;
        if (context.RequestServices.GetService<TomYum.Persistence.OsuWeb.OsuWebStableClient>() is { } website
            && (await website.FindByFilenamesAsync([filename], cancellationToken).ConfigureAwait(false)).Count > 0)
        {
            return true;
        }
        if (!int.TryParse(context.Request.Query["i"].ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var setId) || setId <= 0)
            return false;
        IReadOnlyList<TomYum.Domain.Entities.Beatmap> set;
        try
        {
            set = await beatmaps.FetchSetOnceAsync(setId, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return false; // osu! unreachable: say "not submitted", as before
        }
        return set.Any(b => string.Equals(OsuFileName.For(b.Artist, b.Title, b.Creator, b.Version), filename, StringComparison.OrdinalIgnoreCase));
    }

    private static readonly string NotSubmitted =
        string.Create(CultureInfo.InvariantCulture, $"{(int)RankedStatus.NotSubmitted}|false");
}
