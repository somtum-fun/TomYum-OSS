using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using TomYum.Domain.Repositories;
using TomYum.Persistence.OsuWeb;
using TomYum.Server.Bancho;

namespace TomYum.Server.Discord;

/// <summary>An option of a slash command.</summary>
public sealed record DiscordOptionSpec(string Name, string Description, bool Required = false, IReadOnlyList<string>? Choices = null);

/// <summary>A slash command as registered with Discord.</summary>
public sealed record DiscordCommandSpec(string Name, string Description, IReadOnlyList<DiscordOptionSpec> Options);

/// <summary>
/// The bot's slash commands. Player lookups read the website's data (stable and lazer together,
/// as profiles show them). A command without a <c>user</c> is about the caller's linked account.
/// </summary>
public sealed partial class DiscordCommands
{
    private static readonly DiscordOptionSpec UserOption = new("user", "Player name (or @mention of a linked member); you if left out");
    private static readonly DiscordOptionSpec ModeOption = new("mode", "Ruleset; their main one if left out", Choices: DiscordFormat.RulesetKeys);

    /// <summary>Every command, for registration.</summary>
    public static readonly IReadOnlyList<DiscordCommandSpec> Specs =
    [
        new("profile", "A player's profile", [UserOption, ModeOption]),
        new("rank", "A player's rank and pp", [UserOption, ModeOption]),
        new("recent", "A player's latest play", [UserOption, ModeOption]),
        new("best", "A player's top plays", [UserOption, ModeOption]),
        new("top", "The pp ranking", [new("mode", "Ruleset (osu! if left out)", Choices: DiscordFormat.RulesetKeys), new("country", "Two-letter country code, e.g. NL")]),
        new("leaderboard", "A beatmap's leaderboard", [new("beatmap", "Beatmap id or link", Required: true), new("mode", "Ruleset (the map's own if left out)", Choices: DiscordFormat.RulesetKeys)]),
        new("online", "Who's in-game", []),
        new("link", "Link your game account to Discord", []),
        new("unlink", "Unlink your game account from Discord", []),
        new("staff", "Run a staff command as your linked account (e.g. silence Name 1h spam)", [new("command", "restrict, unrestrict, silence, unsilence, alert, alertu, alts or verify, with its arguments", Required: true)]),
    ];

    private readonly IServiceScopeFactory _scopes;
    private readonly PlayerRegistry _players;
    private readonly DiscordLinkCodes _codes;
    private readonly DiscordFormat _format;
    private readonly ModerationCommands? _moderation;

    public DiscordCommands(IServiceScopeFactory scopes, PlayerRegistry players, DiscordLinkCodes codes, DiscordFormat format, ModerationCommands? moderation = null)
    {
        _scopes = scopes;
        _players = players;
        _codes = codes;
        _format = format;
        _moderation = moderation;
    }

    /// <summary>Runs a slash command for the Discord user <paramref name="callerId"/>.</summary>
    public async Task<DiscordReply> RunAsync(string name, IReadOnlyDictionary<string, string> options, ulong callerId, CancellationToken cancellationToken = default)
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
                return await OnlineAsync(scope.ServiceProvider.GetService<OsuWebCommunity>(), cancellationToken).ConfigureAwait(false);

            case "staff":
                return await StaffAsync(links, options, callerId, cancellationToken).ConfigureAwait(false);
        }

        if (scope.ServiceProvider.GetService<OsuWebCommunity>() is not { } community)
            return DiscordReply.Text("Player lookups need the website (osu-web mode).", isPrivate: true);

        switch (name)
        {
            case "top":
                return await TopAsync(community, options, cancellationToken).ConfigureAwait(false);
            case "leaderboard":
                return await LeaderboardAsync(community, options, cancellationToken).ConfigureAwait(false);
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
            "recent" => await RecentAsync(community, player, cancellationToken).ConfigureAwait(false),
            "best" => await BestAsync(community, player, cancellationToken).ConfigureAwait(false),
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

    private async Task<DiscordReply> RecentAsync(OsuWebCommunity community, PlayerCard player, CancellationToken cancellationToken)
    {
        if (await community.GetRecentAsync(player.UserId, player.Ruleset, 1, cancellationToken).ConfigureAwait(false) is not [var score, ..])
            return DiscordReply.Text($"{DiscordFormat.Escape(player.Username)} has no {DiscordFormat.Ruleset(player.Ruleset)} plays yet.", player.IsRestricted);

        return new DiscordReply(new DiscordPost(Embed: new DiscordEmbed
        {
            AuthorName = $"{player.Username}: {DiscordFormat.Pp(player.Pp)}",
            AuthorUrl = _format.Profile(player.UserId),
            AuthorIconUrl = _format.Avatar(player.UserId),
            Title = DiscordFormat.ScoreTitle(score),
            Url = _format.Beatmap(score.BeatmapId),
            Description = DiscordFormat.ScoreDetails(score) + $"\n▸ {(score.IsStable ? "stable" : "lazer")} ▸ {DiscordFormat.Relative(score.EndedAt)}",
            ThumbnailUrl = _format.ListCover(score.BeatmapsetId),
            Color = score.Passed ? DiscordFormat.Accent : DiscordFormat.Danger,
        }), player.IsRestricted);
    }

    private async Task<DiscordReply> BestAsync(OsuWebCommunity community, PlayerCard player, CancellationToken cancellationToken)
    {
        var scores = await community.GetBestAsync(player.UserId, player.Ruleset, 5, cancellationToken).ConfigureAwait(false);
        if (scores.Count == 0)
            return DiscordReply.Text($"{DiscordFormat.Escape(player.Username)} has no ranked {DiscordFormat.Ruleset(player.Ruleset)} plays yet.", player.IsRestricted);

        var blocks = scores.Select((s, i) => _format.ScoreBlock(s, string.Create(CultureInfo.InvariantCulture, $"**{i + 1}.** ")));
        return new DiscordReply(new DiscordPost(Embed: new DiscordEmbed
        {
            AuthorName = $"{player.Username}'s top {DiscordFormat.Ruleset(player.Ruleset)} plays",
            AuthorUrl = _format.ProfileIn(player.UserId, player.Ruleset),
            AuthorIconUrl = _format.Avatar(player.UserId),
            ThumbnailUrl = _format.Avatar(player.UserId),
            Description = string.Join("\n\n", blocks),
            Color = DiscordFormat.Accent,
        }), player.IsRestricted);
    }

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
            $"**{i + 1}.** [{DiscordFormat.LinkLabel(s.Username)}]({_format.Profile(s.UserId)}) {DiscordFormat.Mods(s.Mods)} ▸ {DiscordFormat.Number(s.TotalScore)} ▸ {DiscordFormat.Percent(s.Accuracy)} ▸ {(s.Pp is { } pp ? DiscordFormat.Pp(pp) : "-")}"));
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

    /// <summary>Who's in-game on stable, and who else the website shows online (lazer, or browsing).</summary>
    private async Task<DiscordReply> OnlineAsync(OsuWebCommunity? community, CancellationToken cancellationToken)
    {
        var stable = _players.All
            .Where(p => p.UserId != BanchoBot.UserId && !p.IsRestricted)
            .Select(p => (p.UserId, p.Presence.Username))
            .ToList();
        var inGame = stable.Select(p => p.UserId).ToHashSet();
        var elsewhere = community is null ? [] : (await community.GetOnlineAsync(cancellationToken).ConfigureAwait(false))
            .Where(p => !inGame.Contains(p.UserId))
            .ToList();
        if (stable.Count + elsewhere.Count == 0)
            return DiscordReply.Text("Nobody is online right now.");

        static string Names(IEnumerable<(int UserId, string Username)> players, int max)
        {
            var sorted = players.Select(p => p.Username).Order(StringComparer.OrdinalIgnoreCase).ToList();
            var more = sorted.Count > max ? string.Create(CultureInfo.InvariantCulture, $" and {sorted.Count - max} more") : string.Empty;
            return string.Join(", ", sorted.Take(max).Select(DiscordFormat.Escape)) + more;
        }

        var lines = new List<string>
        {
            string.Create(CultureInfo.InvariantCulture, $"**{stable.Count + elsewhere.Count}** online"),
        };
        if (stable.Count > 0)
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"▸ **stable** ({stable.Count}): {Names(stable, 40)}"));
        if (elsewhere.Count > 0)
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"▸ **lazer / website** ({elsewhere.Count}): {Names(elsewhere, 40)}"));
        return DiscordReply.Text(string.Join('\n', lines));
    }

    private async Task<DiscordReply> StaffAsync(IDiscordLinkRepository links, IReadOnlyDictionary<string, string> options, ulong callerId, CancellationToken cancellationToken)
    {
        if (_moderation is null)
            return DiscordReply.Text("Moderation isn't available on this server.", isPrivate: true);
        if (await links.GetByDiscordIdAsync(callerId, cancellationToken).ConfigureAwait(false) is not { } link)
            return DiscordReply.Text("Link your account first (/link).", isPrivate: true);

        var parts = (options.GetValueOrDefault("command") ?? string.Empty).TrimStart('!').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || !ModerationCommands.Handles(parts[0].ToLowerInvariant()))
            return DiscordReply.Text("Staff commands: restrict, unrestrict, silence, unsilence, alert, alertu, alts, verify.", isPrivate: true);

        var reply = await _moderation.RunAsync(link.UserId, parts[0].ToLowerInvariant(), parts[1..], cancellationToken).ConfigureAwait(false);
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
