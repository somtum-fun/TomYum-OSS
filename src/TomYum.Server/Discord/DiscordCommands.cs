using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using TomYum.Domain.Repositories;
using TomYum.Persistence.OsuWeb;
using TomYum.Server.Bancho;

namespace TomYum.Server.Discord;

/// <summary>A fixed choice of an option: what Discord shows, and the value the command gets.</summary>
public sealed record DiscordChoice(string Name, string Value);

/// <summary>An option of a slash command.</summary>
public sealed record DiscordOptionSpec(string Name, string Description, bool Required = false, IReadOnlyList<DiscordChoice>? Choices = null);

/// <summary>
/// A slash command as registered with Discord; <see cref="Staff"/> ones are hidden from members who
/// can't moderate, and <see cref="Private"/> ones (and staff ones) answer only the caller.
/// <see cref="Live"/> ones answer in the channel at once and update that message as they work
/// (their refusals still go only to the caller).
/// </summary>
public sealed record DiscordCommandSpec(string Name, string Description, IReadOnlyList<DiscordOptionSpec> Options, bool Staff = false, bool Private = false, bool Live = false);

/// <summary>
/// The bot's slash commands. Player lookups read the website's data (stable and lazer together,
/// as profiles show them). A command without a <c>user</c> is about the caller's linked account.
/// </summary>
public sealed partial class DiscordCommands
{
    private static readonly DiscordOptionSpec UserOption = new("user", "Player name (or @mention of a linked member); you if left out");
    private static readonly IReadOnlyList<DiscordChoice> Rulesets =
        [new("osu!", "osu"), new("osu!taiko", "taiko"), new("osu!catch", "catch"), new("osu!mania", "mania")];

    private static readonly DiscordOptionSpec ModeOption = new("mode", "Ruleset; their main one if left out", Choices: Rulesets);

    private static readonly DiscordOptionSpec PlayerOption = new("player", "The player's name (or @mention a linked member)", Required: true);

    private static readonly IReadOnlyList<DiscordChoice> SilenceLengths =
    [
        new("10 minutes", "10m"), new("1 hour", "1h"), new("6 hours", "6h"), new("1 day", "1d"),
        new("3 days", "3d"), new("1 week", "1w"), new("2 weeks", "2w"), new("1 month", "30d"),
    ];

    /// <summary>The staff commands, and how their options become the moderation command's arguments.</summary>
    private static readonly Dictionary<string, Func<IReadOnlyDictionary<string, string>, string, string[]>> StaffArguments =
        new Dictionary<string, Func<IReadOnlyDictionary<string, string>, string, string[]>>(StringComparer.Ordinal)
        {
            ["restrict"] = (o, player) => [player, o.GetValueOrDefault("reason") ?? string.Empty],
            ["unrestrict"] = (o, player) => [player, o.GetValueOrDefault("reason") ?? string.Empty],
            ["silence"] = (o, player) => [player, o.GetValueOrDefault("length") ?? string.Empty, o.GetValueOrDefault("reason") ?? string.Empty],
            ["unsilence"] = (o, player) => o.TryGetValue("reason", out var reason) ? [player, reason] : [player],
            ["alts"] = (_, player) => [player],
            ["verify"] = (_, player) => [player],
            ["alert"] = (o, _) => [o.GetValueOrDefault("message") ?? string.Empty],
        };

    private static readonly IReadOnlyList<DiscordChoice> ScanOfficial =
        [new("osu!'s top 30 (can take a few minutes the first time)", "30"), new("osu!'s top 5", "5"), new("none (only this server's)", "0")];

    private static readonly IReadOnlyList<DiscordChoice> StandingKinds =
        [new("everything", "all"), new("silences", "silences"), new("restrictions", "restrictions"), new("notes", "notes")];

    private static readonly IReadOnlyList<DiscordChoice> WipeModes =
        [new("every mode", "all"), new("osu!", "osu"), new("osu!taiko", "taiko"), new("osu!catch", "catch"), new("osu!mania", "mania")];

    private static readonly IReadOnlyList<DiscordOptionSpec> StatusOptions =
    [
        new("beatmap", "Set or beatmap link (or a set id)", Required: true),
        new("scope", "The whole set, or only that difficulty", Choices: [new("whole set", "set"), new("only this difficulty", "map")]),
    ];

    // Declared above: static fields initialise in order, and Specs uses these.
    /// <summary>Every command, for registration.</summary>
    public static readonly IReadOnlyList<DiscordCommandSpec> Specs =
    [
        new("profile", "A player's profile", [UserOption, ModeOption]),
        new("rank", "A player's rank and pp", [UserOption, ModeOption]),
        new("recent", "A player's latest play (buttons go back through the last 50)", [UserOption, ModeOption]),
        new("rs", "Same as /recent", [UserOption, ModeOption]),
        new("best", "A player's top plays (buttons go through the top 100)", [UserOption, ModeOption]),
        new("top", "The pp ranking", [new("mode", "Ruleset (osu! if left out)", Choices: Rulesets), new("country", "Two-letter country code, e.g. NL")]),
        new("leaderboard", "A beatmap's leaderboard", [new("beatmap", "Beatmap id or link", Required: true), new("mode", "Ruleset (the map's own if left out)", Choices: Rulesets)]),
        new("map", "A beatmap: difficulty, length, BPM and pp", [new("beatmap", "Beatmap or set link, or a beatmap id", Required: true), new("mods", "Mods, e.g. HDDT"), new("mode", "Ruleset (converts from osu!)", Choices: Rulesets)]),
        new("graph", "A player's rank over the last 90 days", [UserOption, ModeOption]),
        new("medals", "A player's medals: how many, the latest, what's left", [UserOption]),
        new("compare", "Two players side by side (or both on one map)", [new("player", "Who to compare with", Required: true), new("user", "The first player; you if left out"), ModeOption, new("beatmap", "Compare both players' best on this map instead")]),
        new("online", "Who's in-game", []),
        new("link", "Link your game account to Discord", []),
        new("unlink", "Unlink your game account from Discord", []),
        new("restrict", "Staff: restrict a player (hidden, off rankings)", [PlayerOption, new("reason", "Why (shown on their account history)", Required: true)], Staff: true),
        new("unrestrict", "Staff: lift a restriction", [PlayerOption, new("reason", "Why", Required: true)], Staff: true),
        new("silence", "Staff: stop a player from chatting for a while", [PlayerOption, new("length", "For how long", Required: true, Choices: SilenceLengths), new("reason", "Why", Required: true)], Staff: true),
        new("unsilence", "Staff: lift a silence early", [PlayerOption, new("reason", "Why (optional)")], Staff: true),
        new("alts", "Staff: other accounts seen on the same computer", [PlayerOption], Staff: true),
        new("verify", "Staff: let in a new account that shares a computer with a restricted one", [PlayerOption], Staff: true),
        new("alert", "Staff: a pop-up notice to everyone in-game", [new("message", "What to say", Required: true)], Staff: true),
        new("user", "Staff: a player's account: status, history, alts, IPs, recent plays", [PlayerOption], Staff: true),
        new("wipe", "Staff: remove all of a player's scores (stable)", [PlayerOption, new("mode", "Which mode", Required: true, Choices: WipeModes), new("reason", "Why", Required: true)], Staff: true),
        new("removescore", "Staff: remove one stable score", [new("score", "Score id (GMT panel) or the score's link", Required: true), new("reason", "Why")], Staff: true),
        new("clearstanding", "Staff: clear a player's account standing (silences, restrictions, notes)", [PlayerOption, new("reason", "Why", Required: true), new("what", "Which records (everything if left out)", Choices: StandingKinds)], Staff: true),
        new("addbadge", "Admin: give a player a profile badge", [PlayerOption, new("image", "An osu! badge's file name (e.g. contributor.png) or a link to an image (86x40 fits best)", Required: true), new("description", "The badge's tooltip", Required: true), new("url", "Where clicking the badge goes (optional)"), new("name", "What a linked image is saved as, like ndc-2023 (optional: else the link's file name)")], Staff: true),
        new("scan", "Staff: check a score with circleguard, against the map's top 30 here and on osu!", [new("score", "The score's link (or its id on the website)", Required: true), new("official", "osu!'s top plays to compare with (top 30 if left out)", Choices: ScanOfficial)], Staff: true, Live: true),
        new("removebadge", "Admin: take a profile badge away", [PlayerOption, new("badge", "Its number, description or image; left out: list their badges")], Staff: true),
        new("rankmap", "NAT: rank a set (or one difficulty) without the qualified period", StatusOptions, Private: true),
        new("lovemap", "BN: love a set (or one difficulty)", StatusOptions, Private: true),
        new("qualify", "BN: qualify a set (or one difficulty)", StatusOptions, Private: true),
    ];


    private readonly IServiceScopeFactory _scopes;
    private readonly PlayerRegistry _players;
    private readonly DiscordLinkCodes _codes;
    private readonly DiscordFormat _format;
    private readonly ModerationCommands? _moderation;
    private readonly OsuWeb.OnlinePlayers? _online;
    private readonly LoginIps? _loginIps;

    public DiscordCommands(
        IServiceScopeFactory scopes, PlayerRegistry players, DiscordLinkCodes codes, DiscordFormat format,
        ModerationCommands? moderation = null, OsuWeb.OnlinePlayers? online = null, LoginIps? loginIps = null)
    {
        _online = online;
        _loginIps = loginIps;
        _scopes = scopes;
        _players = players;
        _codes = codes;
        _format = format;
        _moderation = moderation;
    }

    /// <summary>Runs a slash command for the Discord user <paramref name="callerId"/>.</summary>
    public Task<DiscordReply> RunAsync(string name, IReadOnlyDictionary<string, string> options, ulong callerId, CancellationToken cancellationToken = default) =>
        RunAsync(name, options, callerId, progress: null, cancellationToken);

    /// <summary>
    /// Runs a slash command; a <see cref="DiscordCommandSpec.Live"/> one shows where it is through
    /// <paramref name="progress"/> (the message so far) before its reply.
    /// </summary>
    public async Task<DiscordReply> RunAsync(
        string name, IReadOnlyDictionary<string, string> options, ulong callerId, Action<DiscordPost>? progress, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(options);

        using var scope = _scopes.CreateScope();
        var links = scope.ServiceProvider.GetRequiredService<IDiscordLinkRepository>();

        switch (name)
        {
            case "link":
                var code = _codes.Create(callerId);
                var already = await links.GetByDiscordIdAsync(callerId, cancellationToken).ConfigureAwait(false) is not null
                    ? " (this replaces your current link)"
                    : string.Empty;
                return DiscordReply.Text(
                    $"Send **`!link {code}`** to BanchoBot (a PM in-game or on the website) within {DiscordLinkCodes.Lifetime.TotalMinutes:0} minutes to link your account{already}.",
                    isPrivate: true);

            case "unlink":
                if (!await links.UnlinkAsync(callerId, cancellationToken).ConfigureAwait(false))
                    return DiscordReply.Text("Your Discord account isn't linked.", isPrivate: true);
                await _codes.OnUnlinkedAsync(callerId).ConfigureAwait(false);
                return DiscordReply.Text("Unlinked.", isPrivate: true);

            case "online":
                return await OnlineAsync(cancellationToken).ConfigureAwait(false);

            case var staff when StaffArguments.ContainsKey(staff):
                return await StaffAsync(scope.ServiceProvider, links, staff, options, callerId, cancellationToken).ConfigureAwait(false);
            case "user":
                return await UserCardAsync(scope.ServiceProvider, links, options, callerId, cancellationToken).ConfigureAwait(false);
            case "wipe":
                return await WipeAsync(scope.ServiceProvider, links, options, callerId, cancellationToken).ConfigureAwait(false);
            case "removescore":
                return await RemoveScoreAsync(scope.ServiceProvider, links, options, callerId, cancellationToken).ConfigureAwait(false);
            case "scan":
                return await ScanAsync(scope.ServiceProvider, links, options, callerId, progress, cancellationToken).ConfigureAwait(false);
            case "clearstanding":
                return await ClearStandingAsync(scope.ServiceProvider, links, options, callerId, cancellationToken).ConfigureAwait(false);
            case "addbadge":
                return await AddBadgeAsync(scope.ServiceProvider, links, options, callerId, cancellationToken).ConfigureAwait(false);
            case "removebadge":
                return await RemoveBadgeAsync(scope.ServiceProvider, links, options, callerId, cancellationToken).ConfigureAwait(false);
            case var status when StatusCommands.ContainsKey(status):
                return await StatusRequestAsync(scope.ServiceProvider, links, status, options, callerId, cancellationToken).ConfigureAwait(false);
        }

        if (scope.ServiceProvider.GetService<OsuWebCommunity>() is not { } community)
            return DiscordReply.Text("Player lookups need the website (osu-web mode).", isPrivate: true);

        switch (name)
        {
            case "top":
                return await TopAsync(community, options, cancellationToken).ConfigureAwait(false);
            case "leaderboard":
                return await LeaderboardAsync(community, options, cancellationToken).ConfigureAwait(false);
            case "map":
                return await MapAsync(scope.ServiceProvider, community, options, cancellationToken).ConfigureAwait(false);
            case "compare":
                return await CompareAsync(scope.ServiceProvider, community, links, options, callerId, cancellationToken).ConfigureAwait(false);
        }

        var target = await ResolvePlayerAsync(community, links, options, callerId, cancellationToken).ConfigureAwait(false);
        if (target.Error is { } error)
            return DiscordReply.Text(error, isPrivate: true);
        var userId = target.UserId;

        var ruleset = options.TryGetValue("mode", out var modeText) && DiscordFormat.ParseRuleset(modeText) is { } chosen
            ? chosen
            : await community.MainRulesetAsync(userId, cancellationToken).ConfigureAwait(false);
        var player = await community.GetPlayerAsync(userId, ruleset, cancellationToken).ConfigureAwait(false);
        if (player is null || (player.IsRestricted && !target.IsCaller))
            return DiscordReply.Text("No such player.", isPrivate: true);

        return name switch
        {
            "profile" => Profile(player),
            "rank" => Rank(player),
            "recent" or "rs" => await RecentAsync(scope.ServiceProvider, community, player, cancellationToken).ConfigureAwait(false),
            "best" => await BestAsync(community, player, cancellationToken).ConfigureAwait(false),
            "graph" => await GraphAsync(community, player, cancellationToken).ConfigureAwait(false),
            "medals" => await MedalsAsync(community, player, cancellationToken).ConfigureAwait(false),
            _ => DiscordReply.Text("Unknown command.", isPrivate: true),
        };
    }

    private DiscordReply Profile(PlayerCard player)
    {
        var rank = player.GlobalRank is { } global
            ? $"#{DiscordFormat.Number(global)} ({DiscordFormat.Flag(player.Country)} #{DiscordFormat.Number(player.CountryRank ?? 0)})"
            : "unranked";
        var description = new StringBuilder()
            .AppendLine(CultureInfo.InvariantCulture, $"▸ **Rank:** {rank}")
            .AppendLine(CultureInfo.InvariantCulture, $"▸ **Performance:** {DiscordFormat.Pp(player.Pp)} ▸ **Accuracy:** {player.Accuracy.ToString("0.00", CultureInfo.InvariantCulture)}%")
            .AppendLine(CultureInfo.InvariantCulture, $"▸ **Level:** {player.Level.ToString("0.00", CultureInfo.InvariantCulture)} ▸ **Max combo:** {DiscordFormat.Number(player.MaxCombo)}")
            .AppendLine(CultureInfo.InvariantCulture, $"▸ **Play count:** {DiscordFormat.Number(player.PlayCount)} ({DiscordFormat.Duration(player.PlaySeconds)})")
            .AppendLine(CultureInfo.InvariantCulture, $"▸ **Ranked score:** {DiscordFormat.Number(player.RankedScore)} ▸ **Total:** {DiscordFormat.Number(player.TotalScore)}")
            .Append(CultureInfo.InvariantCulture, $"▸ **Grades:** SS {player.CountSsh + player.CountSs} ▸ S {player.CountSh + player.CountS} ▸ A {player.CountA}");
        if (player.IsRestricted)
            description.Append("\n▸ **This account is restricted** (only you can see this).");

        return new DiscordReply(new DiscordPost(Embed: new DiscordEmbed
        {
            AuthorName = $"{player.Username}: {DiscordFormat.Ruleset(player.Ruleset)} profile",
            AuthorUrl = _format.ProfileIn(player.UserId, player.Ruleset),
            AuthorIconUrl = _format.Avatar(player.UserId),
            ThumbnailUrl = _format.Avatar(player.UserId),
            Description = description.ToString(),
            Color = DiscordFormat.Accent,
            Footer = $"Joined {player.JoinedAt.ToString("d MMMM yyyy", CultureInfo.InvariantCulture)}",
        }), player.IsRestricted);
    }

    private static DiscordReply Rank(PlayerCard player)
    {
        var text = player.GlobalRank is { } global
            ? $"**{DiscordFormat.Escape(player.Username)}** is #{DiscordFormat.Number(global)} in {DiscordFormat.Ruleset(player.Ruleset)} ({DiscordFormat.Flag(player.Country)} #{DiscordFormat.Number(player.CountryRank ?? 0)}) with {DiscordFormat.Pp(player.Pp)}."
            : $"**{DiscordFormat.Escape(player.Username)}** isn't ranked in {DiscordFormat.Ruleset(player.Ruleset)} yet.";
        return DiscordReply.Text(text, player.IsRestricted);
    }

    private async Task<DiscordReply> RecentAsync(IServiceProvider services, OsuWebCommunity community, PlayerCard player, CancellationToken cancellationToken) =>
        await RecentCardAsync(services, community, player, 0, cancellationToken).ConfigureAwait(false) is { } card
            ? new DiscordReply(card, player.IsRestricted)
            : DiscordReply.Text($"{DiscordFormat.Escape(player.Username)} has no {DiscordFormat.Ruleset(player.Ruleset)} plays yet.", player.IsRestricted);

    private async Task<DiscordReply> BestAsync(OsuWebCommunity community, PlayerCard player, CancellationToken cancellationToken) =>
        await BestCardAsync(community, player, 0, cancellationToken).ConfigureAwait(false) is { } card
            ? new DiscordReply(card, player.IsRestricted)
            : DiscordReply.Text($"{DiscordFormat.Escape(player.Username)} has no ranked {DiscordFormat.Ruleset(player.Ruleset)} plays yet.", player.IsRestricted);

    private async Task<DiscordReply> TopAsync(OsuWebCommunity community, IReadOnlyDictionary<string, string> options, CancellationToken cancellationToken)
    {
        var ruleset = options.TryGetValue("mode", out var modeText) ? DiscordFormat.ParseRuleset(modeText) ?? 0 : 0;
        var country = options.TryGetValue("country", out var countryText) ? countryText.Trim().ToUpperInvariant() : null;
        if (country is not null && (country.Length != 2 || !country.All(char.IsAsciiLetter)))
            return DiscordReply.Text("Give a two-letter country code, e.g. NL.", isPrivate: true);

        var ranking = await community.GetRankingAsync(ruleset, 10, country, cancellationToken).ConfigureAwait(false);
        if (ranking.Count == 0)
            return DiscordReply.Text("Nobody is ranked there yet.", isPrivate: true);

        var lines = ranking.Select(r => string.Create(CultureInfo.InvariantCulture,
            $"**{r.Position}.** {DiscordFormat.Flag(r.Country)} [{DiscordFormat.LinkLabel(r.Username)}]({_format.ProfileIn(r.UserId, ruleset)}) ▸ **{DiscordFormat.Pp(r.Pp)}** ▸ {r.Accuracy.ToString("0.00", CultureInfo.InvariantCulture)}%"));
        return new DiscordReply(new DiscordPost(Embed: new DiscordEmbed
        {
            Title = $"{DiscordFormat.Ruleset(ruleset)} ranking{(country is null ? string.Empty : $" in {country}")}",
            Description = string.Join('\n', lines),
            Color = DiscordFormat.Accent,
        }));
    }

    private async Task<DiscordReply> LeaderboardAsync(OsuWebCommunity community, IReadOnlyDictionary<string, string> options, CancellationToken cancellationToken)
    {
        if (!options.TryGetValue("beatmap", out var beatmapText) || ParseBeatmapId(beatmapText) is not { } beatmapId)
            return DiscordReply.Text("Give a beatmap id or link (e.g. https://osu.ppy.sh/beatmaps/129891).", isPrivate: true);
        if (await community.GetBeatmapRulesetAsync(beatmapId, cancellationToken).ConfigureAwait(false) is not { } mapRuleset)
            return DiscordReply.Text("That beatmap isn't on the server.", isPrivate: true);

        var ruleset = options.TryGetValue("mode", out var modeText) ? DiscordFormat.ParseRuleset(modeText) ?? mapRuleset : mapRuleset;
        var scores = await community.GetBeatmapLeaderboardAsync(beatmapId, ruleset, 10, cancellationToken).ConfigureAwait(false);
        if (scores.Count == 0)
            return DiscordReply.Text("Nobody has passed that map yet.", isPrivate: true);

        var first = scores[0];
        var lines = scores.Select((s, i) => string.Create(CultureInfo.InvariantCulture,
            $"**{i + 1}.** {DiscordFormat.GradeMark(s.Grade)}[{DiscordFormat.LinkLabel(s.Username)}]({_format.Profile(s.UserId)}) {DiscordFormat.Mods(s.Mods)} ▸ {DiscordFormat.Number(s.TotalScore)} ▸ {DiscordFormat.Percent(s.Accuracy)} ▸ {(s.Pp is { } pp ? DiscordFormat.Pp(pp) : "-")}"));
        return new DiscordReply(new DiscordPost(Embed: new DiscordEmbed
        {
            Title = $"{first.MapName} {DiscordFormat.Stars(first.StarRating)}",
            Url = _format.Beatmap(beatmapId),
            Description = string.Join('\n', lines),
            ThumbnailUrl = _format.ListCover(first.BeatmapsetId),
            Color = DiscordFormat.Accent,
            Footer = $"{DiscordFormat.Ruleset(ruleset)} ▸ mapped by {first.Creator}",
        }));
    }

    /// <summary>Who's online: in stable, on lazer (with what they're doing), and on the website.</summary>
    private async Task<DiscordReply> OnlineAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<OsuWeb.OnlineEntry> online = _online is not null
            ? await _online.GetAsync(cancellationToken).ConfigureAwait(false)
            : _players.All.Where(p => p.UserId != BanchoBot.UserId && !p.IsRestricted)
                .Select(p => new OsuWeb.OnlineEntry(p.UserId, p.Presence.Username, OsuWeb.OnlineClient.Stable, null, null, null, DateTime.UtcNow, false))
                .ToList();
        if (online.Count == 0)
            return DiscordReply.Text("Nobody is online right now.");

        static string Names(IEnumerable<OsuWeb.OnlineEntry> players, int max)
        {
            var sorted = players.OrderBy(p => p.Username, StringComparer.OrdinalIgnoreCase).ToList();
            var more = sorted.Count > max ? string.Create(CultureInfo.InvariantCulture, $" and {sorted.Count - max} more") : string.Empty;
            return string.Join(", ", sorted.Take(max).Select(p => p.Playing ? $"{DiscordFormat.Escape(p.Username)} (playing)" : DiscordFormat.Escape(p.Username))) + more;
        }

        var lines = new List<string> { string.Create(CultureInfo.InvariantCulture, $"**{online.Count}** online") };
        foreach (var (client, label) in new[] { (OsuWeb.OnlineClient.Stable, "stable"), (OsuWeb.OnlineClient.Lazer, "lazer"), (OsuWeb.OnlineClient.Website, "website") })
        {
            var group = online.Where(p => p.Client == client).ToList();
            if (group.Count > 0)
                lines.Add(string.Create(CultureInfo.InvariantCulture, $"▸ **{label}** ({group.Count}): {Names(group, 40)}"));
        }
        return DiscordReply.Text(string.Join('\n', lines));
    }

    /// <summary>
    /// A staff command, run as the caller's linked account through BanchoBot's moderation commands
    /// (same permission checks, logs and in-game effect). The player is a name or a linked member's mention.
    /// </summary>
    private async Task<DiscordReply> StaffAsync(
        IServiceProvider services, IDiscordLinkRepository links, string command, IReadOnlyDictionary<string, string> options, ulong callerId, CancellationToken cancellationToken)
    {
        if (_moderation is null)
            return DiscordReply.Text("Moderation isn't available on this server.", isPrivate: true);
        if (await links.GetByDiscordIdAsync(callerId, cancellationToken).ConfigureAwait(false) is not { } link)
            return DiscordReply.Text("Link your account first with /link: staff commands run as your game account.", isPrivate: true);

        var player = options.GetValueOrDefault("player")?.Trim() ?? string.Empty;
        if (MentionPattern().Match(player) is { Success: true } mention)
        {
            var mentioned = await links.GetByDiscordIdAsync(ulong.Parse(mention.Groups[1].Value, CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
            var user = mentioned is null ? null : await services.GetRequiredService<IUserRepository>().GetByIdAsync(mentioned.UserId, cancellationToken).ConfigureAwait(false);
            if (user is null)
                return DiscordReply.Text("That member hasn't linked their account; give the player's name instead.", isPrivate: true);
            player = user.Username;
        }

        var reply = await _moderation.RunAsync(link.UserId, command, StaffArguments[command](options, player), cancellationToken).ConfigureAwait(false);
        return DiscordReply.Text(reply, isPrivate: true);
    }

    private sealed record Target(int UserId, bool IsCaller, string? Error);

    private static async Task<Target> ResolvePlayerAsync(
        OsuWebCommunity community, IDiscordLinkRepository links, IReadOnlyDictionary<string, string> options, ulong callerId, CancellationToken cancellationToken)
    {
        var text = options.GetValueOrDefault("user")?.Trim();
        ulong? discordId = string.IsNullOrEmpty(text) ? callerId : MentionPattern().Match(text) is { Success: true } mention
            ? ulong.Parse(mention.Groups[1].Value, CultureInfo.InvariantCulture)
            : null;

        if (discordId is { } id)
        {
            if (await links.GetByDiscordIdAsync(id, cancellationToken).ConfigureAwait(false) is { } link)
                return new Target(link.UserId, id == callerId, null);
            return new Target(0, false, id == callerId
                ? "Your Discord account isn't linked: use /link, or give a player name."
                : "That member hasn't linked their account.");
        }

        if (await community.FindUserAsync(text!, cancellationToken).ConfigureAwait(false) is not { } found)
            return new Target(0, false, "No such player.");
        var own = await links.GetByDiscordIdAsync(callerId, cancellationToken).ConfigureAwait(false);
        return new Target(found.UserId, own?.UserId == found.UserId, null);
    }

    /// <summary>A beatmap id from an id or a beatmap link (website, osu.ppy.sh or /b/ style).</summary>
    public static int? ParseBeatmapId(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        text = text.Trim();
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0)
            return id;
        var match = BeatmapLinkPattern().Match(text);
        return match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out id) && id > 0 ? id : null;
    }

    [GeneratedRegex(@"^<@!?(\d{1,20})>$")]
    private static partial Regex MentionPattern();

    [GeneratedRegex(@"(?:/beatmaps/|/b/|#(?:osu|taiko|fruits|mania)/)(\d+)")]
    private static partial Regex BeatmapLinkPattern();
}
