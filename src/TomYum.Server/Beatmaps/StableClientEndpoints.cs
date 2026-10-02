using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TomYum.Domain.Entities;
using TomYum.Domain.Enums;
using TomYum.Domain.Repositories;
using TomYum.Domain.Services;
using TomYum.Performance;
using TomYum.Persistence.OsuWeb;
using TomYum.Server.Bancho;

namespace TomYum.Server.Beatmaps;

/// <summary>
/// The rest of what osu!stable asks the server (bancho.py's set): the pre-login check, song
/// select's map statuses and grades (<c>osu-getbeatmapinfo.php</c>), outdated maps' new files
/// (<c>/web/maps/{file}</c>), favourites, friends, in-game comments (<c>osu-comment.php</c>), screenshots (<c>osu-screenshot.php</c>, shown at
/// <c>/ss/{file}</c>), star ratings with mods (<c>/difficulty-rating</c>), and the calls that only
/// need an answer.
/// </summary>
public static partial class StableClientEndpoints
{
    private const int MaxScreenshotBytes = 8 * 1024 * 1024;

    public static void MapStableClient(this IEndpointRouteBuilder app, params string[] hosts)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/web/bancho_connect.php", HandleBanchoConnectAsync).RequireHost(hosts);

        // Answers the client only needs to get (bancho.py answers these the same way).
        app.MapGet("/web/osu-markasread.php", HandleMarkAsReadAsync).RequireHost(hosts);
        app.MapPost("/web/osu-session.php", () => Results.Text(string.Empty, "text/plain")).RequireHost(hosts);
        app.MapPost("/web/osu-error.php", HandleClientErrorAsync).RequireHost(hosts).DisableAntiforgery();
        app.MapGet("/web/check-updates.php", (HttpContext context) =>
            Results.Redirect("https://osu.ppy.sh/web/check-updates.php" + context.Request.QueryString.Value)).RequireHost(hosts);
        // Old clients' update checks (docs/old-clients.md): never an update, the build stays as it is.
        // Without an answer a 2014 build waits on "Receiving data..." for good.
        app.MapGet("/release/update.php", () => Results.Text("0", "text/plain")).RequireHost(hosts);
        app.MapGet("/release/update2.php", () => Results.Text(string.Empty, "text/plain")).RequireHost(hosts);
        app.MapGet("/release/patches.php", () => Results.Text(string.Empty, "text/plain")).RequireHost(hosts);
        app.MapGet("/release/update", () => Results.Text(string.Empty, "text/plain")).RequireHost(hosts);

        app.MapPost("/web/osu-getbeatmapinfo.php", HandleBeatmapInfoAsync).RequireHost(hosts);
        app.MapGet("/web/maps/{filename}", HandleMapFileAsync).RequireHost(hosts);
        app.MapGet("/web/osu-addfavourite.php", HandleAddFavouriteAsync).RequireHost(hosts);
        app.MapGet("/web/osu-getfavourites.php", HandleFavouritesAsync).RequireHost(hosts);
        app.MapGet("/web/osu-getfriends.php", HandleFriendsAsync).RequireHost(hosts);
        app.MapPost("/web/osu-comment.php", HandleCommentAsync).RequireHost(hosts).DisableAntiforgery();
        app.MapPost("/web/osu-screenshot.php", HandleScreenshotAsync).RequireHost(hosts).DisableAntiforgery();
        app.MapGet("/ss/{file}", HandleScreenshotFile).RequireHost(hosts);
        app.MapPost("/difficulty-rating", HandleDifficultyRatingAsync).RequireHost(hosts);
    }

    /// <summary>
    /// The client's check before logging in, which it also polls while a login is held for device
    /// verification (<c>retry=1</c>): the player's country when they may log in, <c>error: verify</c>
    /// while the device (<c>ch</c>: osu md5 : adapters : adapters md5 : uninstall md5 : disk md5) still
    /// waits for them on the website, and <c>error: pass</c> for a wrong password (as Ripple's lets
    /// answers). The country is what makes a waiting client log in again, on its own.
    /// </summary>
    internal static async Task<IResult> HandleBanchoConnectAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var query = context.Request.Query;
        var user = await UserAsync(context, query["u"], query["h"], cancellationToken).ConfigureAwait(false);
        if (user is null)
            return Results.Text("error: pass\n", "text/plain");

        // Only while the client is waiting: a first try goes on to log in, which holds it and
        // opens the verify page.
        if (query["retry"] != "0"
            && context.RequestServices.GetService<Microsoft.Extensions.Options.IOptions<ClientVerificationOptions>>()?.Value.Enabled == true
            && context.RequestServices.GetService<OsuWebClientVerification>() is { } verification
            && ((string?)query["ch"])?.Split(':') is { Length: >= 5 } hashes
            && await verification.IsPendingAsync(user.Id, hashes[3], cancellationToken).ConfigureAwait(false))
        {
            return Results.Text("error: verify\n", "text/plain");
        }

        return Results.Text(user.Country.ToLowerInvariant(), "text/plain");
    }

    /// <summary>
    /// Song select's statuses for the player's local maps, by file name:
    /// <c>{index}|{beatmap}|{set}|{md5}|{status}|{grade osu!}|{taiko}|{catch}|{mania}</c> (N: no grade).
    /// </summary>
    internal static async Task<IResult> HandleBeatmapInfoAsync(HttpContext context, CancellationToken cancellationToken)
    {
        if (context.RequestServices.GetService<OsuWebStableClient>() is not { } website)
            return Results.Text(string.Empty, "text/plain");
        var user = await UserAsync(context, context.Request.Query["u"], context.Request.Query["h"], cancellationToken).ConfigureAwait(false);
        if (user is null)
            return Results.Unauthorized();

        List<string> filenames;
        try
        {
            using var document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: cancellationToken).ConfigureAwait(false);
            filenames = document.RootElement.TryGetProperty("Filenames", out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Select(f => f.GetString() ?? string.Empty).ToList()
                : [];
        }
        catch (JsonException)
        {
            return Results.BadRequest();
        }

        var found = (await website.FindByFilenamesAsync(filenames.Take(2000).ToList(), cancellationToken).ConfigureAwait(false))
            .GroupBy(m => m.Filename, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var grades = await website.BestGradesAsync(user.Id, found.Values.Select(m => m.BeatmapId).ToList(), cancellationToken).ConfigureAwait(false);

        var text = new StringBuilder();
        for (var index = 0; index < filenames.Count; index++)
        {
            if (!found.TryGetValue(filenames[index], out var map))
                continue;
            var perMode = Enumerable.Range(0, 4).Select(ruleset => grades.TryGetValue((map.BeatmapId, ruleset), out var grade) ? grade : "N");
            text.Append(CultureInfo.InvariantCulture, $"{index}|{map.BeatmapId}|{map.BeatmapsetId}|{map.Checksum}|{map.Approved}|{string.Join('|', perMode)}\n");
        }
        return Results.Text(text.ToString(), "text/plain");
    }

    /// <summary>
    /// An outdated map's new <c>.osu</c> (stable's "update" button): custom maps from this server's
    /// copy, osu!'s from osu!.
    /// </summary>
    internal static async Task<IResult> HandleMapFileAsync(string filename, HttpContext context, OsuFileCache files, CancellationToken cancellationToken)
    {
        if (context.RequestServices.GetService<OsuWebStableClient>() is { } website
            && await website.FindByFilenamesAsync([filename], cancellationToken).ConfigureAwait(false) is [var map, ..]
            && map.BeatmapId >= Beatmap.CustomIdFloor)
        {
            try
            {
                var bytes = await files.GetAsync((int)map.BeatmapId, cancellationToken).ConfigureAwait(false);
                return Results.File(bytes, "application/octet-stream", map.Filename);
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
            {
                return Results.NotFound();
            }
        }
        return Results.Redirect("https://osu.ppy.sh/web/maps/" + Uri.EscapeDataString(filename));
    }

    /// <summary>The ♥ in song select: <c>?u=&amp;h=&amp;a={set}</c>.</summary>
    internal static async Task<IResult> HandleAddFavouriteAsync(HttpContext context, CancellationToken cancellationToken)
    {
        if (context.RequestServices.GetService<OsuWebStableClient>() is not { } website)
            return Results.Text(string.Empty, "text/plain");
        var user = await UserAsync(context, context.Request.Query["u"], context.Request.Query["h"], cancellationToken).ConfigureAwait(false);
        if (user is null)
            return Results.Unauthorized();
        if (!int.TryParse(context.Request.Query["a"], NumberStyles.None, CultureInfo.InvariantCulture, out var setId))
            return Results.BadRequest();
        return await website.AddFavouriteAsync(user.Id, setId, cancellationToken).ConfigureAwait(false)
            ? Results.Text(string.Empty, "text/plain")
            : Results.Text("You've favourited this beatmap already!", "text/plain");
    }

    /// <summary>
    /// <c>?u=&amp;h=&amp;channel=</c>: the player opened a chat tab; a conversation (a player's name) or a
    /// channel (<c>#osu</c>) is marked read on the website, so its messages aren't shown again at login.
    /// </summary>
    internal static async Task<IResult> HandleMarkAsReadAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var user = await UserAsync(context, context.Request.Query["u"], context.Request.Query["h"], cancellationToken).ConfigureAwait(false);
        if (user is null)
            return Results.Unauthorized();
        var channel = context.Request.Query["channel"].ToString().Trim();
        if (channel.Length == 0 || context.RequestServices.GetService<OsuWebPrivateMessages>() is not { } messages)
            return Results.Text(string.Empty, "text/plain");

        if (channel.StartsWith('#'))
        {
            await messages.MarkChannelReadAsync(user.Id, channel, cancellationToken).ConfigureAwait(false);
        }
        else if (await context.RequestServices.GetRequiredService<IUserRepository>()
                     .GetBySafeNameAsync(User.MakeSafeName(channel), cancellationToken).ConfigureAwait(false) is { } other)
        {
            await messages.MarkConversationReadAsync(user.Id, other.Id, cancellationToken).ConfigureAwait(false);
        }
        return Results.Text(string.Empty, "text/plain");
    }

    /// <summary>The player's favourite sets, one id per line (<c>?u=&amp;h=</c>).</summary>
    internal static async Task<IResult> HandleFavouritesAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var user = await UserAsync(context, context.Request.Query["u"], context.Request.Query["h"], cancellationToken).ConfigureAwait(false);
        if (user is null)
            return Results.Unauthorized();
        var sets = context.RequestServices.GetService<OsuWebStableClient>() is { } website
            ? await website.FavouriteSetIdsAsync(user.Id, cancellationToken).ConfigureAwait(false)
            : [];
        return Results.Text(string.Join('\n', sets), "text/plain");
    }

    /// <summary>The player's friends, one user id per line (<c>?u=&amp;h=</c>).</summary>
    internal static async Task<IResult> HandleFriendsAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var user = await UserAsync(context, context.Request.Query["u"], context.Request.Query["h"], cancellationToken).ConfigureAwait(false);
        if (user is null)
            return Results.Unauthorized();
        var friends = await context.RequestServices.GetRequiredService<IFriendRepository>().GetFriendIdsAsync(user.Id, cancellationToken).ConfigureAwait(false);
        return Results.Text(string.Join('\n', friends), "text/plain");
    }

    /// <summary>
    /// The client's own error report (<c>osu-error.php</c>): osu! posts it when it catches an
    /// exception it can survive, or just before it gives up. Logged (without the client's config
    /// and hashes) so a stuck or crashing client, old builds especially, can be diagnosed from the
    /// server log. The client only needs an empty answer.
    /// </summary>
    internal static async Task<IResult> HandleClientErrorAsync(HttpContext context, ILoggerFactory loggers, CancellationToken cancellationToken)
    {
        if (context.Request.HasFormContentType)
        {
            var form = await context.Request.ReadFormAsync(cancellationToken).ConfigureAwait(false);
            static string Field(IFormCollection f, string key, int max) =>
                f[key].ToString() is { Length: > 0 } v ? (v.Length > max ? v[..max] + "…" : v) : "-";
            loggers.CreateLogger("TomYum.Server.ClientErrors").LogWarning(
                "Client error from {User} on {Version} (mode {OsuMode}, gamemode {GameMode}): {Exception}\n{StackTrace}\nFeedback: {Feedback}",
                Field(form, "u", 64), Field(form, "version", 64), Field(form, "osumode", 32), Field(form, "gamemode", 8),
                Field(form, "exception", 1000), Field(form, "stacktrace", 6000), Field(form, "feedback", 500));
        }
        return Results.Text(string.Empty, "text/plain");
    }

    /// <summary>
    /// In-game comments (form: <c>u</c>, <c>p</c>, <c>b</c> map, <c>s</c> set, <c>r</c> score, <c>a</c> get/post;
    /// posting adds <c>target</c> song/map/replay, <c>starttime</c>, <c>comment</c> and a supporter's colour <c>f</c>).
    /// Getting answers one <c>time	target	format	text</c> line per comment.
    /// </summary>
    internal static async Task<IResult> HandleCommentAsync(HttpContext context, CancellationToken cancellationToken)
    {
        if (!context.Request.HasFormContentType)
            return Results.BadRequest();
        var form = await context.Request.ReadFormAsync(cancellationToken).ConfigureAwait(false);
        if (await UserAsync(context, form["u"], form["p"], cancellationToken).ConfigureAwait(false) is not { } user)
            return Results.Unauthorized();
        if (context.RequestServices.GetService<IBeatmapCommentRepository>() is not { } comments)
            return Results.Text(string.Empty, "text/plain");
        _ = long.TryParse(form["b"], NumberStyles.None, CultureInfo.InvariantCulture, out var beatmapId);
        _ = long.TryParse(form["s"], NumberStyles.None, CultureInfo.InvariantCulture, out var setId);
        _ = long.TryParse(form["r"], NumberStyles.None, CultureInfo.InvariantCulture, out var scoreId);

        if (form["a"] == "get")
        {
            var users = context.RequestServices.GetRequiredService<IUserRepository>();
            var lines = new List<string>();
            foreach (var comment in await comments.ForPlayAsync(setId, beatmapId, scoreId, cancellationToken).ConfigureAwait(false))
            {
                var author = await users.GetByIdAsync(comment.UserId, cancellationToken).ConfigureAwait(false);
                if (author is null || author.IsRestricted)
                    continue;
                var format = CommentFormat(author.Privileges) + (comment.Colour is { } shade ? "|" + shade : string.Empty);
                lines.Add(string.Create(CultureInfo.InvariantCulture, $"{comment.Time}\t{comment.TargetType}\t{format}\t{comment.Text}"));
            }
            return Results.Text(string.Join('\n', lines), "text/plain");
        }

        if (form["a"] != "post")
            return Results.BadRequest();
        // Restricted and silenced players can't comment, like they can't chat.
        if (user.IsRestricted || user.SilenceEndUtc > DateTimeOffset.UtcNow)
            return Results.Text(string.Empty, "text/plain");
        var text = form["comment"].ToString().Trim();
        if (text.Length is 0 or > 80 || !int.TryParse(form["starttime"], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var time))
            return Results.BadRequest();
        var (targetType, targetId) = form["target"].ToString() switch
        {
            "song" => ("song", setId),
            "map" => ("map", beatmapId),
            "replay" when scoreId > 0 => ("replay", scoreId),
            _ => (string.Empty, 0L),
        };
        if (targetId <= 0)
            return Results.BadRequest();
        // Colours are a supporter perk; osu! sends them as hex without the #.
        var colour = form["f"].ToString().TrimStart('#');
        await comments.AddAsync(new BeatmapComment
        {
            TargetType = targetType,
            TargetId = targetId,
            UserId = user.Id,
            Time = Math.Max(0, time),
            Text = text,
            Colour = user.Privileges.HasFlag(UserPrivileges.Supporter) && HexColour().IsMatch(colour) ? colour.ToLowerInvariant() : null,
            CreatedAt = DateTime.UtcNow,
        }, cancellationToken).ConfigureAwait(false);
        return Results.Text(string.Empty, "text/plain");
    }

    /// <summary>How osu! styles a commenter: staff as "bat" (osu!'s nominator colour), supporters in their colour.</summary>
    private static string CommentFormat(UserPrivileges privileges) =>
        (privileges & (UserPrivileges.Moderator | UserPrivileges.Administrator | UserPrivileges.Developer)) != 0 ? "bat"
        : privileges.HasFlag(UserPrivileges.Supporter) ? "supporter"
        : string.Empty;

    /// <summary>An uploaded screenshot (form: <c>u</c>, <c>p</c>, file <c>ss</c>); answers its file name.</summary>
    internal static async Task<IResult> HandleScreenshotAsync(HttpContext context, IConfiguration configuration, CancellationToken cancellationToken)
    {
        if (!context.Request.HasFormContentType)
            return Results.BadRequest();
        var form = await context.Request.ReadFormAsync(cancellationToken).ConfigureAwait(false);
        if (await UserAsync(context, form["u"], form["p"], cancellationToken).ConfigureAwait(false) is null)
            return Results.Unauthorized();
        if (form.Files.GetFile("ss") is not { Length: > 0 and <= MaxScreenshotBytes } upload)
            return Results.BadRequest();

        using var buffer = new MemoryStream();
        await upload.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        var bytes = buffer.ToArray();
        var extension = bytes is [0x89, 0x50, 0x4E, 0x47, ..] ? "png" : bytes is [0xFF, 0xD8, ..] ? "jpg" : null;
        if (extension is null)
            return Results.BadRequest();

        var name = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6)) + "." + extension;
        var directory = ScreenshotDirectory(configuration);
        Directory.CreateDirectory(directory);
        await File.WriteAllBytesAsync(Path.Combine(directory, name), bytes, cancellationToken).ConfigureAwait(false);
        return Results.Text(name, "text/plain");
    }

    internal static IResult HandleScreenshotFile(string file, IConfiguration configuration)
    {
        if (!ScreenshotName().IsMatch(file))
            return Results.NotFound();
        var path = Path.Combine(ScreenshotDirectory(configuration), file);
        return File.Exists(path) ? Results.File(path, file.EndsWith(".png", StringComparison.Ordinal) ? "image/png" : "image/jpeg") : Results.NotFound();
    }

    /// <summary>
    /// A star rating with mods (osu.ppy.sh's <c>/difficulty-rating</c>: <c>{beatmap_id, ruleset_id,
    /// mods: [{acronym}]}</c>, answered with the number), from this server's calculator; osu!'s when
    /// that can't tell.
    /// </summary>
    internal static async Task<IResult> HandleDifficultyRatingAsync(HttpContext context, CancellationToken cancellationToken)
    {
        int beatmapId, ruleset;
        var mods = TomYum.Bancho.Mods.None;
        try
        {
            using var document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            beatmapId = root.GetProperty("beatmap_id").GetInt32();
            ruleset = root.TryGetProperty("ruleset_id", out var r) && r.ValueKind == JsonValueKind.Number ? r.GetInt32() : 0;
            if (root.TryGetProperty("mods", out var list))
            {
                if (list.ValueKind == JsonValueKind.Number)
                {
                    mods = (TomYum.Bancho.Mods)list.GetInt32();
                }
                else if (list.ValueKind == JsonValueKind.Array)
                {
                    foreach (var mod in list.EnumerateArray())
                    {
                        var acronym = mod.ValueKind == JsonValueKind.String ? mod.GetString() : mod.TryGetProperty("acronym", out var a) ? a.GetString() : null;
                        if (NowPlaying.ParseAcronyms(acronym) is { } known)
                            mods |= known;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return Results.BadRequest();
        }

        if (context.RequestServices.GetService<IPerformanceCalculator>() is IAccuracyPerformance calculator)
        {
            try
            {
                if (await calculator.StarRatingAsync(beatmapId, ruleset, (int)mods, cancellationToken).ConfigureAwait(false) is { } stars)
                    return Results.Content(stars.ToString("0.0###", CultureInfo.InvariantCulture), "application/json");
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or IOException)
            {
                // Fall through to osu!.
            }
        }
        return beatmapId >= Beatmap.CustomIdFloor ? Results.Content("0.0", "application/json") : Results.Redirect("https://osu.ppy.sh/difficulty-rating", preserveMethod: true);
    }

    private static string ScreenshotDirectory(IConfiguration configuration) =>
        Path.GetFullPath(configuration["Screenshots:Path"] is { Length: > 0 } path ? path : "data/screenshots");

    /// <summary>The player, when the name and password md5 check out.</summary>
    private static async Task<User?> UserAsync(HttpContext context, string? username, string? passwordMd5, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(passwordMd5))
            return null;
        var users = context.RequestServices.GetRequiredService<IUserRepository>();
        var passwords = context.RequestServices.GetRequiredService<IPasswordHasher>();
        return await users.GetBySafeNameAsync(User.MakeSafeName(username), cancellationToken).ConfigureAwait(false) is { } user
               && passwords.Verify(passwordMd5, user.PasswordBcrypt)
            ? user
            : null;
    }

    [GeneratedRegex("^[0-9a-fA-F]{6}$")]
    private static partial Regex HexColour();

    [GeneratedRegex(@"^[0-9a-f]{12}\.(png|jpg)$")]
    private static partial Regex ScreenshotName();
}
