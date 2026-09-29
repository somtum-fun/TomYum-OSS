using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using TomYum.Domain.Entities;
using TomYum.Domain.Enums;
using TomYum.Domain.Repositories;
using TomYum.Performance;
using TomYum.Persistence.OsuWeb;
using TomYum.Server.Admin;
using TomYum.Server.Bancho;

namespace TomYum.Server.Discord;

/// <summary>
/// The newer commands: map cards (<c>/map</c>, and cards for links posted in the server), rank
/// graphs, medals, comparisons, the staff account card and score tools, and ranking maps with a
/// confirm button.
/// </summary>
public sealed partial class DiscordCommands
{
    private static readonly double[] Accuracies = [0.95, 0.98, 0.99, 1.0];

    /// <summary>The map status commands and the status each sets.</summary>
    public static readonly IReadOnlyDictionary<string, (string Status, string Verb)> StatusCommands =
        new Dictionary<string, (string, string)>(StringComparer.Ordinal)
        {
            ["rankmap"] = ("ranked", "Rank"),
            ["lovemap"] = ("loved", "Love"),
            ["qualify"] = ("qualified", "Qualify"),
        };

    // ── /map and link cards ───────────────────────────────────────────────────────────────

    private async Task<DiscordReply> MapAsync(IServiceProvider services, OsuWebCommunity community, IReadOnlyDictionary<string, string> options, CancellationToken cancellationToken)
    {
        if (!options.TryGetValue("beatmap", out var text) || await ResolveMapAsync(community, text, cancellationToken).ConfigureAwait(false) is not { } beatmapId)
            return DiscordReply.Text("Give a beatmap link or id (e.g. https://osu.sundei.eu/b/129891).", isPrivate: true);
        var mods = TomYum.Bancho.Mods.None;
        if (options.TryGetValue("mods", out var modText) && !string.IsNullOrWhiteSpace(modText))
        {
            if (NowPlaying.ParseAcronyms(modText) is not { } parsed)
                return DiscordReply.Text("Mods like HD, HDDT or HR,FL.", isPrivate: true);
            mods = parsed;
        }
        int? ruleset = options.TryGetValue("mode", out var modeText) ? DiscordFormat.ParseRuleset(modeText) : null;

        var card = await MapCardAsync(services, community, beatmapId, mods, ruleset, cancellationToken).ConfigureAwait(false);
        return card is null
            ? DiscordReply.Text("That beatmap isn't on the server.", isPrivate: true)
            : new DiscordReply(new DiscordPost(Embed: card));
    }

    /// <summary>A map's card: difficulty, length, BPM, settings and pp at 95–100% (with <paramref name="mods"/>).</summary>
    public async Task<DiscordEmbed?> MapCardAsync(IServiceProvider services, OsuWebCommunity community, int beatmapId, TomYum.Bancho.Mods mods, int? ruleset, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(community);
        if (await community.GetMapAsync(beatmapId, cancellationToken).ConfigureAwait(false) is not { } map)
            return null;

        // Only osu! maps convert to the other rulesets.
        var mode = ruleset is { } chosen && (map.Ruleset == 0 || chosen == map.Ruleset) ? chosen : map.Ruleset;
        var rate = mods.HasFlag(TomYum.Bancho.Mods.DoubleTime) ? 1.5 : mods.HasFlag(TomYum.Bancho.Mods.HalfTime) ? 0.75 : 1.0;
        var stars = map.StarRating;
        IReadOnlyList<double>? pp = null;
        if (services.GetService<IPerformanceCalculator>() is IAccuracyPerformance calculator)
        {
            try
            {
                if (mods != TomYum.Bancho.Mods.None || mode != map.Ruleset)
                    stars = await calculator.StarRatingAsync(beatmapId, mode, (int)mods, cancellationToken).ConfigureAwait(false) ?? stars;
                pp = await calculator.ForAccuraciesAsync(beatmapId, mode, (int)mods, Accuracies, cancellationToken).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // pp is a bonus: the .osu may be unreachable, the card still helps
            catch (Exception)
#pragma warning restore CA1031
            {
            }
        }

        var modText = mods == TomYum.Bancho.Mods.None ? string.Empty : " +" + MapCommands.ModNames(mods);
        var length = (long)Math.Round(map.TotalLength / rate);
        var drain = (long)Math.Round(map.HitLength / rate);
        var description = new StringBuilder()
            .AppendLine(CultureInfo.InvariantCulture, $"▸ **{DiscordFormat.Stars(stars)}**{modText} ▸ {StatusName(map.Status)} ▸ mapped by **{DiscordFormat.Escape(map.Creator)}**")
            .AppendLine(CultureInfo.InvariantCulture, $"▸ ⏱ {Clock(length)} (drain {Clock(drain)}) ▸ ♪ {(map.Bpm * rate).ToString("0.#", CultureInfo.InvariantCulture)} BPM ▸ x{DiscordFormat.Number(map.MaxCombo)}")
            .Append(CultureInfo.InvariantCulture, $"▸ CS {Setting(map.CircleSize)} ▸ AR {Setting(map.ApproachRate)} ▸ OD {Setting(map.OverallDifficulty)} ▸ HP {Setting(map.DrainRate)}");
        if (pp is { Count: 4 })
        {
            description.Append("\n▸ ").Append(string.Join(" ▸ ", Accuracies.Select((acc, i) =>
                string.Create(CultureInfo.InvariantCulture, $"{acc * 100:0}%: **{pp[i]:0.##}pp**"))));
        }

        return new DiscordEmbed
        {
            Title = map.Name,
            Url = _format.Beatmap(map.BeatmapId),
            Description = description.ToString(),
            ThumbnailUrl = _format.ListCover(map.BeatmapsetId),
            Color = DiscordFormat.Accent,
            Footer = string.Create(CultureInfo.InvariantCulture, $"{DiscordFormat.Ruleset(mode)} ▸ {DiscordFormat.Number(map.PlayCount)} plays ▸ {DiscordFormat.Number(map.PassCount)} passes"),
        };
    }

    /// <summary>Cards for the website's map and player links in a message (at most three; &lt;link&gt;s are left alone).</summary>
    public async Task<IReadOnlyList<DiscordEmbed>> PreviewAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(text) || !Uri.TryCreate(_format.Site, UriKind.Absolute, out var site))
            return [];
        var cards = new List<DiscordEmbed>();
        using var scope = _scopes.CreateScope();
        if (scope.ServiceProvider.GetService<OsuWebCommunity>() is not { } community)
            return [];

        foreach (System.Text.RegularExpressions.Match link in SiteLink().Matches(text))
        {
            if (cards.Count >= 3)
                break;
            // Links in <…> were posted without a preview on purpose.
            if (link.Index > 0 && text[link.Index - 1] == '<')
                continue;
            if (!Uri.TryCreate(link.Value, UriKind.Absolute, out var url) || !url.Host.Equals(site.Host, StringComparison.OrdinalIgnoreCase))
                continue;

            var path = url.AbsolutePath + url.Fragment;
            DiscordEmbed? card = null;
            if (ParseBeatmapId(path) is { } beatmapId)
            {
                card = await MapCardAsync(scope.ServiceProvider, community, beatmapId, TomYum.Bancho.Mods.None, null, cancellationToken).ConfigureAwait(false);
            }
            else if (SetPath().Match(path) is { Success: true } set
                     && await community.GetHardestMapAsync(int.Parse(set.Groups[1].Value, CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false) is { } hardest)
            {
                card = await MapCardAsync(scope.ServiceProvider, community, hardest, TomYum.Bancho.Mods.None, null, cancellationToken).ConfigureAwait(false);
            }
            else if (UserPath().Match(path) is { Success: true } user)
            {
                var who = Uri.UnescapeDataString(user.Groups[1].Value);
                var found = int.TryParse(who, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
                    ? await community.GetVisibleNamesAsync([id], cancellationToken).ConfigureAwait(false) is { Count: 1 } names ? (id, names[id]) : ((int, string)?)null
                    : await community.FindUserAsync(who, cancellationToken).ConfigureAwait(false);
                if (found is { } player)
                {
                    var ruleset = user.Groups[2].Success ? DiscordFormat.ParseRuleset(user.Groups[2].Value == "fruits" ? "catch" : user.Groups[2].Value) ?? 0
                        : await community.MainRulesetAsync(player.Item1, cancellationToken).ConfigureAwait(false);
                    if (await community.GetPlayerAsync(player.Item1, ruleset, cancellationToken).ConfigureAwait(false) is { IsRestricted: false } card2)
                        card = Profile(card2).Post.Embed;
                }
            }
            if (card is not null)
                cards.Add(card);
        }
        return cards;
    }

    /// <summary>A beatmap from a map or set link or id (a set gives its hardest difficulty).</summary>
    private static async Task<int?> ResolveMapAsync(OsuWebCommunity community, string text, CancellationToken cancellationToken)
    {
        if (ParseBeatmapId(text) is { } beatmapId)
            return beatmapId;
        return SetPath().Match(text ?? string.Empty) is { Success: true } set
            ? await community.GetHardestMapAsync(int.Parse(set.Groups[1].Value, CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false)
            : null;
    }

    private static string Clock(long seconds) =>
        string.Create(CultureInfo.InvariantCulture, $"{seconds / 60}:{seconds % 60:00}");

    private static string Setting(double value) => value.ToString("0.#", CultureInfo.InvariantCulture);

    private static string StatusName(int approved) => approved switch
    {
        1 => "Ranked",
        2 => "Approved",
        3 => "Qualified",
        4 => "Loved",
        -1 => "WIP",
        -2 => "Graveyard",
        _ => "Pending",
    };

    // ── /graph, /medals, /compare ─────────────────────────────────────────────────────────

    private async Task<DiscordReply> GraphAsync(OsuWebCommunity community, PlayerCard player, CancellationToken cancellationToken)
    {
        var days = await community.GetRankHistoryAsync(player.UserId, player.Ruleset, cancellationToken).ConfigureAwait(false);
        if (RankGraph.Render(days) is not { } png)
            return DiscordReply.Text($"{DiscordFormat.Escape(player.Username)} has no {DiscordFormat.Ruleset(player.Ruleset)} rank history yet (it's recorded once a day).", player.IsRestricted);

        var ranked = days.Where(d => d > 0).ToList();
        var now = player.GlobalRank ?? ranked[^1];
        var change = ranked[0] - now;
        var trend = change > 0 ? $"▲ {DiscordFormat.Number(change)}" : change < 0 ? $"▼ {DiscordFormat.Number(-change)}" : "no change";
        return new DiscordReply(new DiscordPost(Embed: new DiscordEmbed
        {
            AuthorName = $"{player.Username}: {DiscordFormat.Ruleset(player.Ruleset)} rank history",
            AuthorUrl = _format.ProfileIn(player.UserId, player.Ruleset),
            AuthorIconUrl = _format.Avatar(player.UserId),
            Description = string.Create(CultureInfo.InvariantCulture,
                $"▸ Now **#{DiscordFormat.Number(now)}** ▸ best **#{DiscordFormat.Number(ranked.Min())}** ▸ {trend} over {ranked.Count} days"),
            ImageUrl = "attachment://rank.png",
            Color = DiscordFormat.Accent,
            Footer = "Pink: best day ▸ white: today",
        })
        { File = new DiscordFile("rank.png", png) }, player.IsRestricted);
    }

    private async Task<DiscordReply> MedalsAsync(OsuWebCommunity community, PlayerCard player, CancellationToken cancellationToken)
    {
        var medals = await community.GetMedalProgressAsync(player.UserId, cancellationToken).ConfigureAwait(false);
        if (medals.Count == 0)
            return DiscordReply.Text("The server has no medals yet.", isPrivate: true);

        var unlocked = medals.Where(m => m.UnlockedAt is not null).ToList();
        var groups = medals.GroupBy(m => m.Grouping)
            .Select(g => new DiscordField(g.Key, string.Create(CultureInfo.InvariantCulture, $"{g.Count(m => m.UnlockedAt is not null)}/{g.Count()}")))
            .ToList();
        var latest = unlocked.OrderByDescending(m => m.UnlockedAt).Take(5)
            .Select(m => $"🏅 {DiscordFormat.Escape(m.Name)} ▸ {DiscordFormat.Relative(m.UnlockedAt!.Value)}");
        // What's left in the groups they're furthest along in: the likeliest next ones.
        var missing = medals.GroupBy(m => m.Grouping)
            .OrderByDescending(g => (double)g.Count(m => m.UnlockedAt is not null) / g.Count())
            .SelectMany(g => g.Where(m => m.UnlockedAt is null).OrderBy(m => m.Ordering))
            .Take(12)
            .Select(m => DiscordFormat.Escape(m.Name))
            .ToList();

        var fields = groups.Take(20).ToList();
        if (unlocked.Count > 0)
            fields.Add(new DiscordField("Latest", string.Join('\n', latest), Inline: false));
        if (missing.Count > 0)
            fields.Add(new DiscordField("Still to get (some)", string.Join(" ▸ ", missing), Inline: false));

        return new DiscordReply(new DiscordPost(Embed: new DiscordEmbed
        {
            AuthorName = $"{player.Username}: medals",
            AuthorUrl = _format.Profile(player.UserId),
            AuthorIconUrl = _format.Avatar(player.UserId),
            Description = string.Create(CultureInfo.InvariantCulture,
                $"**{unlocked.Count}** of {medals.Count} ({(double)unlocked.Count / medals.Count * 100:0.#}%)"),
            Fields = fields,
            Color = DiscordFormat.Gold,
        }), player.IsRestricted);
    }

    private async Task<DiscordReply> CompareAsync(
        IServiceProvider services, OsuWebCommunity community, IDiscordLinkRepository links, IReadOnlyDictionary<string, string> options, ulong callerId, CancellationToken cancellationToken)
    {
        var first = await ResolvePlayerAsync(community, links, options, callerId, cancellationToken).ConfigureAwait(false);
        if (first.Error is { } error)
            return DiscordReply.Text(error, isPrivate: true);
        var secondOptions = new Dictionary<string, string>(StringComparer.Ordinal) { ["user"] = options.GetValueOrDefault("player") ?? string.Empty };
        var second = await ResolvePlayerAsync(community, links, secondOptions, callerId, cancellationToken).ConfigureAwait(false);
        if (second.Error is { } error2)
            return DiscordReply.Text(error2, isPrivate: true);
        if (first.UserId == second.UserId)
            return DiscordReply.Text("Pick two different players.", isPrivate: true);

        var ruleset = options.TryGetValue("mode", out var modeText) && DiscordFormat.ParseRuleset(modeText) is { } chosen
            ? chosen
            : await community.MainRulesetAsync(first.UserId, cancellationToken).ConfigureAwait(false);
        var a = await community.GetPlayerAsync(first.UserId, ruleset, cancellationToken).ConfigureAwait(false);
        var b = await community.GetPlayerAsync(second.UserId, ruleset, cancellationToken).ConfigureAwait(false);
        if (a is null || b is null || (a.IsRestricted && !first.IsCaller) || (b.IsRestricted && !second.IsCaller))
            return DiscordReply.Text("No such player.", isPrivate: true);

        // Both on one map.
        if (options.TryGetValue("beatmap", out var beatmapText) && !string.IsNullOrWhiteSpace(beatmapText))
        {
            if (await ResolveMapAsync(community, beatmapText, cancellationToken).ConfigureAwait(false) is not { } beatmapId
                || await community.GetMapAsync(beatmapId, cancellationToken).ConfigureAwait(false) is not { } map)
                return DiscordReply.Text("That beatmap isn't on the server.", isPrivate: true);
            var mapRuleset = options.ContainsKey("mode") ? ruleset : map.Ruleset;
            var scoreA = await community.GetBestOnMapAsync(a.UserId, beatmapId, mapRuleset, cancellationToken).ConfigureAwait(false);
            var scoreB = await community.GetBestOnMapAsync(b.UserId, beatmapId, mapRuleset, cancellationToken).ConfigureAwait(false);
            string Line(PlayerCard p, ScoreCard? s) => s is null
                ? $"**{DiscordFormat.Escape(p.Username)}** hasn't passed it."
                : $"**{DiscordFormat.Escape(p.Username)}** {(s.Mods.Count > 0 ? "+" + string.Concat(s.Mods) : "+NM")} • {DiscordFormat.Relative(s.EndedAt)} • {(s.IsStable ? "stable" : "lazer")}" + "\n" + DiscordFormat.ScoreDetails(s);
            var ahead = (scoreA, scoreB) switch
            {
                (null, null) => "Neither has passed it yet.",
                (not null, null) => $"**{DiscordFormat.Escape(a.Username)}** has it, **{DiscordFormat.Escape(b.Username)}** doesn't.",
                (null, not null) => $"**{DiscordFormat.Escape(b.Username)}** has it, **{DiscordFormat.Escape(a.Username)}** doesn't.",
                _ => Ahead(a.Username, b.Username, Total(scoreA!), Total(scoreB!), "score"),
            };
            return new DiscordReply(new DiscordPost(Embed: new DiscordEmbed
            {
                Title = $"{map.Artist} - {map.Title} [{map.Version}] [{map.StarRating.ToString("0.00", CultureInfo.InvariantCulture)}★]",
                Url = _format.Beatmap(beatmapId),
                Description = $"{Line(a, scoreA)}\n\n{Line(b, scoreB)}\n\n{ahead}",
                ThumbnailUrl = _format.ListCover(map.BeatmapsetId),
                Color = DiscordFormat.Accent,
                Footer = $"{DiscordFormat.Ruleset(mapRuleset)} • Mapset by {map.Creator}",
            }), a.IsRestricted || b.IsRestricted);
        }

        var topA = await community.GetBestAsync(a.UserId, ruleset, 1, cancellationToken).ConfigureAwait(false);
        var topB = await community.GetBestAsync(b.UserId, ruleset, 1, cancellationToken).ConfigureAwait(false);
        return new DiscordReply(new DiscordPost(Embed: CompareTable(a, b, topA.Count > 0 ? topA[0] : null, topB.Count > 0 ? topB[0] : null)), a.IsRestricted || b.IsRestricted);

        static long Total(ScoreCard s) => s.IsStable && s.LegacyTotalScore > 0 ? s.LegacyTotalScore : s.TotalScore;
    }

    private static string Ahead(string a, string b, double first, double second, string unit)
    {
        if (Math.Abs(first - second) < 0.005)
            return "Dead even.";
        var (leader, gap) = first > second ? (a, first - second) : (b, second - first);
        var amount = unit == "pp" ? DiscordFormat.Pp(gap) : $"{DiscordFormat.Number(gap)} {unit}";
        return $"**{DiscordFormat.Escape(leader)}** is ahead by {amount}.";
    }

    // ── staff: /user, /wipe, /removescore ─────────────────────────────────────────────────

    /// <summary>The caller's game account when it's staff (moderators, administrators, developers).</summary>
    private static async Task<(User? Staff, string? Error)> StaffCallerAsync(IServiceProvider services, IDiscordLinkRepository links, ulong callerId, CancellationToken cancellationToken)
    {
        if (await links.GetByDiscordIdAsync(callerId, cancellationToken).ConfigureAwait(false) is not { } link)
            return (null, "Link your account first with /link: staff commands run as your game account.");
        var user = await services.GetRequiredService<IUserRepository>().GetByIdAsync(link.UserId, cancellationToken).ConfigureAwait(false);
        return user is not null && ModerationCommands.IsStaff(user.Privileges) && !user.IsRestricted
            ? (user, null)
            : (null, "That command is staff-only.");
    }

    /// <summary>A player by name or linked member mention, for staff (restricted accounts included).</summary>
    private static async Task<User?> StaffTargetAsync(IServiceProvider services, IDiscordLinkRepository links, string? text, CancellationToken cancellationToken)
    {
        var users = services.GetRequiredService<IUserRepository>();
        text = text?.Trim() ?? string.Empty;
        if (MentionPattern().Match(text) is { Success: true } mention)
        {
            var link = await links.GetByDiscordIdAsync(ulong.Parse(mention.Groups[1].Value, CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
            return link is null ? null : await users.GetByIdAsync(link.UserId, cancellationToken).ConfigureAwait(false);
        }
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            return await users.GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
        return await users.GetBySafeNameAsync(User.MakeSafeName(text), cancellationToken).ConfigureAwait(false);
    }

    private async Task<DiscordReply> UserCardAsync(IServiceProvider services, IDiscordLinkRepository links, IReadOnlyDictionary<string, string> options, ulong callerId, CancellationToken cancellationToken)
    {
        var (staff, error) = await StaffCallerAsync(services, links, callerId, cancellationToken).ConfigureAwait(false);
        if (staff is null)
            return DiscordReply.Text(error!, isPrivate: true);
        if (services.GetService<OsuWebCommunity>() is not { } community)
            return DiscordReply.Text("Account cards need the website (osu-web mode).", isPrivate: true);
        if (await StaffTargetAsync(services, links, options.GetValueOrDefault("player"), cancellationToken).ConfigureAwait(false) is not { } target
            || await community.GetAccountAsync(target.Id, cancellationToken).ConfigureAwait(false) is not { } account)
            return DiscordReply.Text("No such player.", isPrivate: true);

        var groups = await community.GetGroupsAsync(account.UserId, cancellationToken).ConfigureAwait(false);
        var silence = services.GetService<OsuWebModeration>() is { } moderation
            ? await moderation.SilenceEndAsync(account.UserId, cancellationToken).ConfigureAwait(false)
            : null;
        var status = account.Banned ? "🔨 **Banned**"
            : account.Restricted ? "⛔ **Restricted**"
            : silence is { } until && until > DateTimeOffset.UtcNow ? $"🔇 Silenced until {DiscordFormat.Relative(until.UtcDateTime)}"
            : "✅ In good standing";

        var ruleset = await community.MainRulesetAsync(account.UserId, cancellationToken).ConfigureAwait(false);
        var recent = await community.GetRecentAsync(account.UserId, ruleset, 3, cancellationToken).ConfigureAwait(false);
        var history = account.History.Count == 0 ? "Nothing."
            : string.Join('\n', account.History.Select(h =>
                $"▸ {h.Kind}{(h.Length is { } length ? $" ({ModerationLength(length)})" : string.Empty)} {DiscordFormat.Relative(h.At)}{(h.By is { } by ? $" by {DiscordFormat.Escape(by)}" : string.Empty)}: {DiscordFormat.Escape(Shorten(h.Reason, 120))}"));
        var alts = _moderation is null ? "-" : await _moderation.RunAsync(staff.Id, "alts", [account.Username], cancellationToken).ConfigureAwait(false);

        var addresses = new List<string>();
        if (_loginIps is not null)
        {
            addresses.AddRange((await _loginIps.ListAsync(account.UserId).ConfigureAwait(false))
                .Select(ip => $"`{ip.Address}` {DiscordFormat.Relative(ip.LastSeen.UtcDateTime)}"));
        }
        if (!string.IsNullOrWhiteSpace(account.RegistrationIp))
            addresses.Add($"`{account.RegistrationIp}` (registration)");

        var linked = await links.GetByUserIdAsync(account.UserId, cancellationToken).ConfigureAwait(false);
        var fields = new List<DiscordField>
        {
            new("Status", status),
            new("Groups", groups.Count == 0 ? "-" : string.Join(", ", groups.Order(StringComparer.Ordinal))),
            new("Discord", linked is null ? "not linked" : $"<@{linked.DiscordId.ToString(CultureInfo.InvariantCulture)}>"),
            new("Joined", DiscordFormat.Relative(account.JoinedAt)),
            new("Last seen", account.LastVisit is { } seen ? DiscordFormat.Relative(seen) : "never"),
            new("Online", _players.All.Any(p => p.UserId == account.UserId) ? "in stable now" : "-"),
            new("Account history", Shorten(history, 1000), Inline: false),
            new("Same machine", Shorten(alts, 1000), Inline: false),
            new("IP addresses", addresses.Count == 0 ? "none recorded" : Shorten(string.Join('\n', addresses), 1000), Inline: false),
        };
        if (recent.Count > 0)
            fields.Add(new DiscordField("Recent plays", Shorten(string.Join('\n', recent.Select(s => $"▸ {_format.MapLink(s)} {DiscordFormat.Mods(s.Mods)} ▸ {(s.Pp is { } pp ? DiscordFormat.Pp(pp) : s.Passed ? "no pp" : "failed")} ▸ {DiscordFormat.Relative(s.EndedAt)}")), 1000), Inline: false));

        return new DiscordReply(new DiscordPost(Embed: new DiscordEmbed
        {
            AuthorName = $"{account.Username} (#{account.UserId.ToString(CultureInfo.InvariantCulture)}) {DiscordFormat.Flag(account.Country)}",
            AuthorUrl = _format.Profile(account.UserId),
            AuthorIconUrl = _format.Avatar(account.UserId),
            ThumbnailUrl = _format.Avatar(account.UserId),
            Fields = fields,
            Color = account.Restricted || account.Banned ? DiscordFormat.Danger : DiscordFormat.Staff,
        }), Private: true);
    }

    private static async Task<DiscordReply> WipeAsync(IServiceProvider services, IDiscordLinkRepository links, IReadOnlyDictionary<string, string> options, ulong callerId, CancellationToken cancellationToken)
    {
        var (staff, error) = await StaffCallerAsync(services, links, callerId, cancellationToken).ConfigureAwait(false);
        if (staff is null)
            return DiscordReply.Text(error!, isPrivate: true);
        if (services.GetService<ScoreModeration>() is not { } scores)
            return DiscordReply.Text("Score tools need the website (osu-web mode).", isPrivate: true);
        if (await StaffTargetAsync(services, links, options.GetValueOrDefault("player"), cancellationToken).ConfigureAwait(false) is not { } target)
            return DiscordReply.Text("No such player.", isPrivate: true);
        if (MayNotModerate(staff, target) is { } refusal)
            return DiscordReply.Text(refusal, isPrivate: true);
        var reason = options.GetValueOrDefault("reason")?.Trim();
        if (string.IsNullOrEmpty(reason))
            return DiscordReply.Text("Give a reason.", isPrivate: true);

        int? mode = options.GetValueOrDefault("mode") is { } modeText && modeText != "all" ? DiscordFormat.ParseRuleset(modeText) : null;
        var result = await scores.WipeAsync(target.Id, mode, staff.Id, reason, cancellationToken).ConfigureAwait(false);
        var which = mode is { } m ? DiscordFormat.Ruleset(m) : "every mode";
        return DiscordReply.Text(string.Create(CultureInfo.InvariantCulture,
            $"Wiped {result.Removed} score(s) of **{DiscordFormat.Escape(target.Username)}** in {which}; their stats were recalculated."), isPrivate: true);
    }

    private static async Task<DiscordReply> RemoveScoreAsync(IServiceProvider services, IDiscordLinkRepository links, IReadOnlyDictionary<string, string> options, ulong callerId, CancellationToken cancellationToken)
    {
        var (staff, error) = await StaffCallerAsync(services, links, callerId, cancellationToken).ConfigureAwait(false);
        if (staff is null)
            return DiscordReply.Text(error!, isPrivate: true);
        if (services.GetService<ScoreModeration>() is not { } scores || services.GetService<OsuWebCommunity>() is not { } community)
            return DiscordReply.Text("Score tools need the website (osu-web mode).", isPrivate: true);

        var text = options.GetValueOrDefault("score")?.Trim() ?? string.Empty;
        long scoreId;
        if (ScoreLink().Match(text) is { Success: true } link)
        {
            // A website score link: its stable score (lazer scores aren't removed from here).
            if (await community.GetStableScoreAsync(ulong.Parse(link.Groups[1].Value, CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false) is not { } stable)
                return DiscordReply.Text("That's a lazer score or doesn't exist; only stable scores can be removed here.", isPrivate: true);
            scoreId = stable.StableScoreId;
        }
        else if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out scoreId))
        {
            return DiscordReply.Text("Give a score id (from the GMT panel) or a score link from the website.", isPrivate: true);
        }

        var owner = await ScoreOwnerAsync(services, scoreId, cancellationToken).ConfigureAwait(false);
        if (owner is not null && MayNotModerate(staff, owner) is { } refusal)
            return DiscordReply.Text(refusal, isPrivate: true);

        var result = await scores.RemoveAsync(scoreId, staff.Id, options.GetValueOrDefault("reason"), cancellationToken).ConfigureAwait(false);
        return result is null
            ? DiscordReply.Text("That score is already gone.", isPrivate: true)
            : DiscordReply.Text(string.Create(CultureInfo.InvariantCulture,
                $"Removed score {scoreId}{(owner is null ? string.Empty : $" of **{DiscordFormat.Escape(owner.Username)}**")}; their best on that map and their stats were recalculated."), isPrivate: true);
    }

    private static async Task<User?> ScoreOwnerAsync(IServiceProvider services, long scoreId, CancellationToken cancellationToken)
    {
        if (services.GetService<TomYum.Persistence.TomYumDbContext>() is not { } db)
            return null;
        var userId = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.FirstOrDefaultAsync(
            db.Scores.Where(s => s.Id == scoreId).Select(s => (int?)s.UserId), cancellationToken).ConfigureAwait(false);
        return userId is { } id ? await services.GetRequiredService<IUserRepository>().GetByIdAsync(id, cancellationToken).ConfigureAwait(false) : null;
    }

    /// <summary>Like the panel: only administrators act on other staff.</summary>
    private static string? MayNotModerate(User staff, User target) =>
        target.Id != staff.Id && ModerationCommands.IsStaff(target.Privileges) && !staff.Privileges.HasFlag(UserPrivileges.Administrator)
            ? "Only administrators can moderate other staff."
            : target.Id == BanchoBot.UserId ? "BanchoBot can't be moderated." : null;

    private static string ModerationLength(TimeSpan length) =>
        length.TotalDays >= 1 ? string.Create(CultureInfo.InvariantCulture, $"{length.TotalDays:0.#}d")
        : length.TotalHours >= 1 ? string.Create(CultureInfo.InvariantCulture, $"{length.TotalHours:0.#}h")
        : string.Create(CultureInfo.InvariantCulture, $"{length.TotalMinutes:0}m");

    private static string Shorten(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    // ── /rankmap, /lovemap, /qualify ──────────────────────────────────────────────────────

    private async Task<DiscordReply> StatusRequestAsync(IServiceProvider services, IDiscordLinkRepository links, string command, IReadOnlyDictionary<string, string> options, ulong callerId, CancellationToken cancellationToken)
    {
        if (await links.GetByDiscordIdAsync(callerId, cancellationToken).ConfigureAwait(false) is not { } link)
            return DiscordReply.Text("Link your account first with /link: map status changes are made as your game account.", isPrivate: true);
        if (!await MapCommands.MayChangeStatusAsync(services, link.UserId, cancellationToken).ConfigureAwait(false))
            return DiscordReply.Text("That command is for the Beatmap Nominators, NAT and admins.", isPrivate: true);
        if (services.GetService<OsuWebCommunity>() is not { } community)
            return DiscordReply.Text("Status changes need the website (osu-web mode).", isPrivate: true);

        var text = options.GetValueOrDefault("beatmap")?.Trim() ?? string.Empty;
        var wholeSet = options.GetValueOrDefault("scope") != "map";
        int setId;
        var beatmapId = 0;
        if (ParseBeatmapId(text) is { } mapId && !int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out _))
        {
            // A map link: that difficulty's set (or only it).
            if (await community.GetMapAsync(mapId, cancellationToken).ConfigureAwait(false) is not { } map)
                return DiscordReply.Text("That beatmap isn't on the server. Import it from the BN panel first.", isPrivate: true);
            setId = map.BeatmapsetId;
            beatmapId = map.BeatmapId;
        }
        else if (SetPath().Match(text) is { Success: true } set)
        {
            setId = int.Parse(set.Groups[1].Value, CultureInfo.InvariantCulture);
        }
        else if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0)
        {
            // A bare number: a set id, or a difficulty id when only one difficulty is changed.
            if (!wholeSet)
            {
                if (await community.GetMapAsync(number, cancellationToken).ConfigureAwait(false) is not { } map)
                    return DiscordReply.Text("That beatmap isn't on the server.", isPrivate: true);
                setId = map.BeatmapsetId;
                beatmapId = number;
            }
            else
            {
                setId = number;
            }
        }
        else
        {
            return DiscordReply.Text("Give a set or beatmap link (or a set id).", isPrivate: true);
        }
        if (!wholeSet && beatmapId == 0)
            return DiscordReply.Text("To change one difficulty, give that difficulty's link or id.", isPrivate: true);
        if (await community.GetBeatmapsetAsync(setId, cancellationToken).ConfigureAwait(false) is not { } card)
            return DiscordReply.Text("That set isn't on the server. Import it from the BN panel first.", isPrivate: true);

        var (status, verb) = StatusCommands[command];
        var target = wholeSet ? $"all {card.Versions.Count} difficulties" : $"[{DiscordFormat.Escape((await community.GetMapAsync(beatmapId, cancellationToken).ConfigureAwait(false))!.Version)}] only";
        var id = string.Create(CultureInfo.InvariantCulture, $"status:{status}:{setId}:{beatmapId}");
        return new DiscordReply(new DiscordPost(Embed: new DiscordEmbed
        {
            Title = $"{card.Artist} - {card.Title}",
            Url = _format.Beatmapset(setId),
            Description = $"{verb} **{target}**? (now {StatusName(card.Status).ToLowerInvariant()})\nmapped by **{DiscordFormat.Escape(card.Creator)}**",
            ThumbnailUrl = _format.ListCover(setId),
            Color = DiscordFormat.Accent,
        })
        {
            Buttons =
            [
                new DiscordButton($"{verb} it", DiscordButtonStyle.Success, id),
                new DiscordButton("Cancel", DiscordButtonStyle.Secondary, "cancel"),
            ],
        }, Private: true);
    }

    [GeneratedRegex(@"https?://[^\s<>|]+")]
    private static partial Regex SiteLink();

    [GeneratedRegex(@"/(?:beatmapsets|s)/(\d{1,10})")]
    private static partial Regex SetPath();

    [GeneratedRegex(@"^/(?:u|users)/([^/?#\s]+)(?:/(osu|taiko|fruits|mania))?")]
    private static partial Regex UserPath();

    [GeneratedRegex(@"/scores/(?:osu/|taiko/|fruits/|mania/)?(\d{1,20})")]
    private static partial Regex ScoreLink();
}
