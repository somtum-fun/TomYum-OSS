using System.Globalization;
using System.Text;
using TomYum.Persistence.OsuWeb;
using TomYum.Server.Anticheat;

namespace TomYum.Server.Discord;

/// <summary>
/// How a circleguard result looks on Discord (the automatic flags and <c>/scan</c>): a verdict line
/// with a risk bar, the reasons, the play, the numbers side by side, and the closest replay (a
/// match to a top play is spelled out, with a link to it).
/// </summary>
public static class CircleguardEmbed
{
    /// <summary>A verdict's label, emoji and colour by risk.</summary>
    public static (string Label, uint Color) Level(int risk, bool restricted) => restricted
        ? ("⛔ Restricted automatically", DiscordFormat.Danger)
        : risk switch
        {
            >= 70 => ("🔴 Likely cheating", DiscordFormat.Danger),
            >= 40 => ("🟠 Suspicious", 0xE67E22u),
            >= 15 => ("🟡 Worth a look", 0xF1C40Fu),
            _ => ("🟢 Clean", 0x57F287u),
        };

    /// <summary>A 20-cell bar for a 0–100 risk.</summary>
    public static string Bar(int risk)
    {
        var filled = (int)Math.Round(Math.Clamp(risk, 0, 100) / 5.0);
        return new string('█', filled) + new string('░', 20 - filled);
    }

    public static DiscordEmbed Build(DiscordFormat format, ScoreCard score, CircleguardScan scan, bool restricted, string? requestedBy = null)
    {
        ArgumentNullException.ThrowIfNull(format);
        ArgumentNullException.ThrowIfNull(score);
        ArgumentNullException.ThrowIfNull(scan);
        var verdict = scan.Verdict;
        var (label, color) = Level(verdict.RiskScore, restricted);

        var description = new StringBuilder()
            .Append(CultureInfo.InvariantCulture, $"**{label}** ▸ risk **{verdict.RiskScore}**/100\n`{Bar(verdict.RiskScore)}`\n");
        description.Append(verdict.Reasons.Count == 0
            ? "\nNothing stood out."
            : "\n" + string.Join('\n', verdict.Reasons.Select(r => "• " + DiscordFormat.Escape(r))));

        var fields = new List<DiscordField>
        {
            new("Play", DiscordFormat.ScoreDetails(score), Inline: false),
            new("Unstable rate", Ur(verdict)),
            new("Frametime", verdict.Frametime is { } frametime ? string.Create(CultureInfo.InvariantCulture, $"{frametime:0.0} ms") : "-"),
            new("Snaps", verdict.SnapCount.ToString(CultureInfo.InvariantCulture)),
            new("Closest replay", Closest(format, verdict.Similarity), Inline: false),
            new("Compared with", string.Create(CultureInfo.InvariantCulture, $"{scan.LocalReferences} here ▸ {scan.OfficialReferences} on osu!")),
            new("Client", $"{(score.IsStable ? "stable" : "lazer")} ▸ {DiscordFormat.Ruleset(score.Ruleset)}"),
        };

        var footer = "circleguard ▸ " + (verdict.Mods.Length > 0 ? verdict.Mods : "NM") + (verdict.Note is { Length: > 0 } note ? " ▸ " + note : string.Empty);
        if (requestedBy is not null)
            footer += " ▸ scanned by " + requestedBy;
        return new DiscordEmbed
        {
            AuthorName = $"circleguard ▸ {score.Username}",
            AuthorUrl = format.Profile(score.UserId),
            AuthorIconUrl = format.Avatar(score.UserId),
            Title = DiscordFormat.ScoreTitle(score),
            Url = $"{format.Site}/scores/{score.ScoreId.ToString(CultureInfo.InvariantCulture)}",
            ThumbnailUrl = format.ListCover(score.BeatmapsetId),
            Description = description.ToString(),
            Fields = fields,
            Color = color,
            Footer = footer,
            Timestamp = new DateTimeOffset(score.EndedAt, TimeSpan.Zero),
        };
    }

    /// <summary>Unrestrict (false alarm) after an automatic restriction; otherwise Restrict / Silence / Dismiss / Profile.</summary>
    public static IReadOnlyList<DiscordButton> Buttons(DiscordFormat format, ScoreCard score, int risk, bool restricted)
    {
        ArgumentNullException.ThrowIfNull(format);
        ArgumentNullException.ThrowIfNull(score);
        var user = score.UserId.ToString(CultureInfo.InvariantCulture);
        return restricted
            ?
            [
                new DiscordButton("Unrestrict (false alarm)", DiscordButtonStyle.Success, $"mod:unrestrict:{user}:circleguard false alarm"),
                new DiscordButton("Dismiss", DiscordButtonStyle.Secondary, $"mod:dismiss:{user}:-"),
                new DiscordButton("Profile", DiscordButtonStyle.Link, Url: format.Profile(score.UserId)),
            ]
            : format.ModerationButtons(score.UserId, string.Create(CultureInfo.InvariantCulture, $"circleguard risk {risk} (score {score.ScoreId})"), score.UserId);
    }

    private static string Ur(CircleguardVerdict verdict) => (verdict.UnstableRate, verdict.ConvertedUnstableRate) switch
    {
        ({ } ur, { } converted) when Math.Abs(ur - converted) > 0.05 => string.Create(CultureInfo.InvariantCulture, $"{ur:0.00} (cv {converted:0.00})"),
        ({ } ur, _) => ur.ToString("0.00", CultureInfo.InvariantCulture),
        (null, { } converted) => string.Create(CultureInfo.InvariantCulture, $"cv {converted:0.00}"),
        _ => "-",
    };

    /// <summary>The nearest top play: a match is spelled out; osu!'s plays link to osu!, ours to the site.</summary>
    private static string Closest(DiscordFormat format, CircleguardSimilarity? similarity)
    {
        if (similarity is null || similarity.Comparisons.Count == 0)
            return "No top plays to compare with.";
        var nearest = similarity.Matched ?? similarity.Comparisons.MinBy(c => c.Distance)!;
        var official = nearest.Username.EndsWith("(osu!)", StringComparison.Ordinal);
        var url = official
            ? string.Create(CultureInfo.InvariantCulture, $"https://osu.ppy.sh/scores/osu/{nearest.ScoreId}")
            : string.Create(CultureInfo.InvariantCulture, $"{format.Site}/scores/{nearest.ScoreId}");
        var who = $"[{DiscordFormat.LinkLabel(nearest.Username)}]({url})";
        return similarity.Matched is not null
            ? string.Create(CultureInfo.InvariantCulture, $"🚨 **Matches {who}'s replay**: {nearest.Distance:0.0} px apart (a copy is under {similarity.Threshold:0} px)")
            : string.Create(CultureInfo.InvariantCulture, $"{who}: {nearest.Distance:0.0} px apart (a copy is under {similarity.Threshold:0} px)");
    }
}
