using System.Globalization;
using System.Text;
using TomYum.Persistence.OsuWeb;

namespace TomYum.Server.Discord;

/// <summary>
/// <c>/best</c> (Bathbot's <c>/top</c> layout: five plays a page, buttons through the top 100) and
/// the two-player table of <c>/compare</c>.
/// </summary>
public sealed partial class DiscordCommands
{
    /// <summary>Plays per <c>/best</c> page.</summary>
    public const int BestPerPage = 5;

    /// <summary>How deep <c>/best</c> goes.</summary>
    public const int BestDepth = 100;

    /// <summary>The <c>/best</c> page <paramref name="page"/> (0 = the top five); null when the player has no ranked plays.</summary>
    public async Task<DiscordPost?> BestCardAsync(OsuWebCommunity community, PlayerCard player, int page, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(community);
        ArgumentNullException.ThrowIfNull(player);
        var scores = await community.GetBestAsync(player.UserId, player.Ruleset, BestDepth, cancellationToken).ConfigureAwait(false);
        if (scores.Count == 0)
            return null;
        var pages = (scores.Count + BestPerPage - 1) / BestPerPage;
        page = Math.Clamp(page, 0, pages - 1);

        var blocks = scores.Skip(page * BestPerPage).Take(BestPerPage).Select((score, i) => BestBlock(score, page * BestPerPage + i + 1));
        var embed = new DiscordEmbed
        {
            AuthorName = $"{player.Username}: {Pp2(player.Pp)}pp ({Ranks(player)})",
            AuthorUrl = _format.ProfileIn(player.UserId, player.Ruleset),
            AuthorIconUrl = _format.Avatar(player.UserId),
            ThumbnailUrl = _format.Avatar(player.UserId),
            Description = string.Join("\n", blocks),
            Color = DiscordFormat.Accent,
            Footer = string.Create(CultureInfo.InvariantCulture, $"Page {page + 1}/{pages} • {DiscordFormat.Ruleset(player.Ruleset)} top plays (stable and lazer)"),
        };
        return new DiscordPost(Embed: embed) { Buttons = PageButtons("top", player.UserId, player.Ruleset, page, pages) };
    }

    /// <summary>One play in a list: the map, then grade, pp and accuracy, combo and misses, then mods, score, when and the client.</summary>
    private string BestBlock(ScoreCard score, int position)
    {
        var total = score.IsStable && score.LegacyTotalScore > 0 ? score.LegacyTotalScore : score.TotalScore;
        var fullCombo = score.Miss == 0 && (score.BeatmapMaxCombo == 0 || score.MaxCombo >= score.BeatmapMaxCombo - 1);
        var combo = score.BeatmapMaxCombo > 0
            ? string.Create(CultureInfo.InvariantCulture, $"**{score.MaxCombo}x**/{score.BeatmapMaxCombo}x")
            : string.Create(CultureInfo.InvariantCulture, $"**{score.MaxCombo}x**");
        var misses = fullCombo ? "FC" : string.Create(CultureInfo.InvariantCulture, $"{score.Miss} ❌");
        var mods = score.Mods.Count > 0 ? "+" + string.Concat(score.Mods) : "+NM";
        return new StringBuilder()
            .Append(CultureInfo.InvariantCulture, $"**{position}.** [{DiscordFormat.LinkLabel($"{score.Artist} - {score.Title} [{score.Version}]")}]({_format.Beatmap(score.BeatmapId)}) [{score.StarRating:0.00}★]\n")
            .Append(CultureInfo.InvariantCulture, $"{DiscordFormat.Grade(score.Grade)} **{(score.Pp ?? 0):0.00}pp** ({DiscordFormat.Percent(score.Accuracy)}) • {combo} • {misses}\n")
            .Append(CultureInfo.InvariantCulture, $"{mods} • {DiscordFormat.Number(total)} • {DiscordFormat.Relative(score.EndedAt)} • {(score.IsStable ? "stable" : "lazer")}\n")
            .ToString();
    }

    /// <summary>⏮ ◀ 🔄 ▶ ⏭ for a paged card: <c>{kind}:{user}:{ruleset}:{page}:{slot}</c>.</summary>
    public static IReadOnlyList<DiscordButton> PageButtons(string kind, int userId, int ruleset, int page, int pages)
    {
        string Id(int to, string slot) => string.Create(CultureInfo.InvariantCulture, $"{kind}:{userId}:{ruleset}:{to}:{slot}");
        var last = Math.Max(0, pages - 1);
        return
        [
            new DiscordButton("⏮", DiscordButtonStyle.Secondary, Id(0, "a")) { Disabled = page == 0 },
            new DiscordButton("◀", DiscordButtonStyle.Secondary, Id(Math.Max(0, page - 1), "b")) { Disabled = page == 0 },
            new DiscordButton("🔄", DiscordButtonStyle.Primary, Id(page, "c")),
            new DiscordButton("▶", DiscordButtonStyle.Secondary, Id(Math.Min(last, page + 1), "d")) { Disabled = page >= last },
            new DiscordButton("⏭", DiscordButtonStyle.Secondary, Id(last, "e")) { Disabled = page >= last },
        ];
    }

    /// <summary>
    /// Two players side by side in a table (the leader of each row marked), with the overall pp gap.
    /// </summary>
    private DiscordEmbed CompareTable(PlayerCard a, PlayerCard b, ScoreCard? topA, ScoreCard? topB)
    {
        static string Shorten(string name) => name.Length > 12 ? name[..11] + "…" : name;
        var rows = new List<(string Label, string A, string B, int Leader)>
        {
            ("Rank", a.GlobalRank is { } ra ? "#" + DiscordFormat.Number(ra) : "-", b.GlobalRank is { } rb ? "#" + DiscordFormat.Number(rb) : "-",
                Lead(-(a.GlobalRank ?? int.MaxValue), -(b.GlobalRank ?? int.MaxValue))),
            ("Country", a.Country.ToUpperInvariant() + (a.CountryRank is { } ca ? " #" + DiscordFormat.Number(ca) : string.Empty),
                b.Country.ToUpperInvariant() + (b.CountryRank is { } cb ? " #" + DiscordFormat.Number(cb) : string.Empty), 0),
            ("pp", Pp2(a.Pp), Pp2(b.Pp), Lead(a.Pp, b.Pp)),
            ("Accuracy", a.Accuracy.ToString("0.00", CultureInfo.InvariantCulture) + "%", b.Accuracy.ToString("0.00", CultureInfo.InvariantCulture) + "%", Lead(a.Accuracy, b.Accuracy)),
            ("Level", a.Level.ToString("0.00", CultureInfo.InvariantCulture), b.Level.ToString("0.00", CultureInfo.InvariantCulture), Lead(a.Level, b.Level)),
            ("Plays", DiscordFormat.Number(a.PlayCount), DiscordFormat.Number(b.PlayCount), Lead(a.PlayCount, b.PlayCount)),
            ("Play time", DiscordFormat.Duration(a.PlaySeconds), DiscordFormat.Duration(b.PlaySeconds), Lead(a.PlaySeconds, b.PlaySeconds)),
            ("Ranked score", Compact(a.RankedScore), Compact(b.RankedScore), Lead(a.RankedScore, b.RankedScore)),
            ("Max combo", DiscordFormat.Number(a.MaxCombo) + "x", DiscordFormat.Number(b.MaxCombo) + "x", Lead(a.MaxCombo, b.MaxCombo)),
            ("Top play", topA?.Pp is { } pa ? Pp2(pa) + "pp" : "-", topB?.Pp is { } pb ? Pp2(pb) + "pp" : "-", Lead(topA?.Pp ?? 0, topB?.Pp ?? 0)),
        };

        var nameA = Shorten(a.Username);
        var nameB = Shorten(b.Username);
        var labelWidth = rows.Max(r => r.Label.Length);
        var widthA = Math.Max(nameA.Length, rows.Max(r => r.A.Length)) + 2;
        var widthB = Math.Max(nameB.Length, rows.Max(r => r.B.Length)) + 2;
        var table = new StringBuilder("```\n")
            .Append(new string(' ', labelWidth + 2)).Append(nameA.PadRight(widthA)).Append(nameB).Append('\n');
        foreach (var (label, valueA, valueB, leader) in rows)
        {
            table.Append(label.PadRight(labelWidth + 2))
                .Append((valueA + (leader > 0 ? " ◀" : string.Empty)).PadRight(widthA))
                .Append(valueB + (leader < 0 ? " ◀" : string.Empty)).Append('\n');
        }
        table.Append("```");

        var wins = rows.Count(r => r.Leader > 0);
        var losses = rows.Count(r => r.Leader < 0);
        return new DiscordEmbed
        {
            AuthorName = $"{a.Username} vs {b.Username} ▸ {DiscordFormat.Ruleset(a.Ruleset)}",
            AuthorUrl = _format.ProfileIn(a.UserId, a.Ruleset),
            AuthorIconUrl = _format.Avatar(a.UserId),
            ThumbnailUrl = _format.Avatar(b.UserId),
            Description = table + "\n" + Ahead(a.Username, b.Username, a.Pp, b.Pp, "pp"),
            Color = DiscordFormat.Accent,
            Footer = string.Create(CultureInfo.InvariantCulture, $"{a.Username} leads {wins} of {rows.Count(r => r.Leader != 0)} ▸ {b.Username} leads {losses}"),
        };

        static int Lead(double first, double second) => Math.Abs(first - second) < 1e-9 ? 0 : first > second ? 1 : -1;
    }

    /// <summary>25722552 → "25.7M".</summary>
    private static string Compact(long value) => value switch
    {
        >= 1_000_000_000 => (value / 1e9).ToString("0.##", CultureInfo.InvariantCulture) + "B",
        >= 1_000_000 => (value / 1e6).ToString("0.#", CultureInfo.InvariantCulture) + "M",
        >= 10_000 => (value / 1e3).ToString("0.#", CultureInfo.InvariantCulture) + "K",
        _ => DiscordFormat.Number(value),
    };
}
