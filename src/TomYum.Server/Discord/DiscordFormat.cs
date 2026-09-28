using System.Globalization;
using System.Text;
using TomYum.Persistence.OsuWeb;

namespace TomYum.Server.Discord;

/// <summary>How the bot writes players, maps and scores (links go to the website).</summary>
public sealed class DiscordFormat
{
    /// <summary>The bot's accent colour (osu! pink).</summary>
    public const uint Accent = 0xFF66AA;

    public const uint Gold = 0xFFCC22;

    public const uint Staff = 0x5865F2;

    public const uint Danger = 0xED4245;

    private static readonly string[] RulesetNames = ["osu!", "osu!taiko", "osu!catch", "osu!mania"];

    /// <summary>The slash-command names of the rulesets, by id.</summary>
    public static readonly IReadOnlyList<string> RulesetKeys = ["osu", "taiko", "catch", "mania"];

    private readonly string _site;
    private readonly string _avatars;

    public DiscordFormat(string siteUrl, string avatarUrl)
    {
        _site = siteUrl.TrimEnd('/');
        _avatars = avatarUrl.TrimEnd('/');
    }

    public static string Ruleset(int ruleset) => ruleset is >= 0 and < 4 ? RulesetNames[ruleset] : "osu!";

    /// <summary>A ruleset id from its command name (or null for unknown).</summary>
    public static int? ParseRuleset(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return null;
        var index = RulesetKeys.ToList().FindIndex(k => string.Equals(k, key.Trim(), StringComparison.OrdinalIgnoreCase));
        return index >= 0 ? index : key.Trim().ToLowerInvariant() switch { "std" or "standard" => 0, "fruits" or "ctb" => 2, _ => null };
    }

    public string Profile(int userId) => $"{_site}/users/{userId.ToString(CultureInfo.InvariantCulture)}";

    public string ProfileIn(int userId, int ruleset) => $"{Profile(userId)}/{(ruleset == 2 ? "fruits" : RulesetKeys[Math.Clamp(ruleset, 0, 3)])}";

    public string Avatar(int userId) => $"{_avatars}/{userId.ToString(CultureInfo.InvariantCulture)}";

    public string Beatmap(int beatmapId) => $"{_site}/beatmaps/{beatmapId.ToString(CultureInfo.InvariantCulture)}";

    public string Beatmapset(int beatmapsetId) => $"{_site}/beatmapsets/{beatmapsetId.ToString(CultureInfo.InvariantCulture)}";

    public string Cover(int beatmapsetId) =>
        $"{_site}/uploads/default/beatmaps/{beatmapsetId.ToString(CultureInfo.InvariantCulture)}/covers/cover.jpg";

    public string ListCover(int beatmapsetId) =>
        $"{_site}/uploads/default/beatmaps/{beatmapsetId.ToString(CultureInfo.InvariantCulture)}/covers/list@2x.jpg";

    public static string Flag(string country) =>
        country.Length == 2 && char.IsAsciiLetter(country[0]) && char.IsAsciiLetter(country[1])
            ? $":flag_{country.ToLowerInvariant()}:"
            : ":pirate_flag:";

    public static string Number(double value) => value.ToString("N0", CultureInfo.InvariantCulture);

    public static string Pp(double pp) => pp.ToString("#,0.##", CultureInfo.InvariantCulture) + "pp";

    public static string Percent(double fraction) => (fraction * 100).ToString("0.00", CultureInfo.InvariantCulture) + "%";

    public static string Stars(double stars) => "★" + stars.ToString("0.00", CultureInfo.InvariantCulture);

    public static string Mods(IReadOnlyList<string> mods) => mods.Count == 0 ? string.Empty : "+" + string.Concat(mods);

    public static string Grade(string grade) => grade switch
    {
        "XH" => "SS (silver)",
        "X" => "SS",
        "SH" => "S (silver)",
        "" => "F",
        _ => grade,
    };

    /// <summary>A Discord timestamp (shown in the reader's time zone), e.g. "3 minutes ago".</summary>
    public static string Relative(DateTime utc) =>
        $"<t:{new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)}:R>";

    public static string Duration(long seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return span.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(long)span.TotalHours}h {span.Minutes}m")
            : string.Create(CultureInfo.InvariantCulture, $"{span.Minutes}m");
    }

    /// <summary>Escapes Discord markdown in names and titles, so they show as written.</summary>
    public static string Escape(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var builder = new StringBuilder(text.Length + 8);
        foreach (var c in text)
        {
            if (c is '\\' or '*' or '_' or '~' or '`' or '|' or '>' or '[' or ']' or '(' or ')')
                builder.Append('\\');
            builder.Append(c);
        }
        return builder.ToString();
    }

    /// <summary>"Artist - Title [Version] +HDDT ★5.43" (without mods, no gap).</summary>
    public static string ScoreTitle(ScoreCard score)
    {
        ArgumentNullException.ThrowIfNull(score);
        return string.Join(' ', new[] { score.MapName, Mods(score.Mods), Stars(score.StarRating) }.Where(part => part.Length > 0));
    }

    /// <summary>A linked map title.</summary>
    public string MapLink(ScoreCard score)
    {
        ArgumentNullException.ThrowIfNull(score);
        return $"[{Escape(score.MapName)}]({Beatmap(score.BeatmapId)})";
    }

    /// <summary>Two lines about a score: pp · accuracy · grade, then score · combo · hits.</summary>
    public static string ScoreDetails(ScoreCard score)
    {
        ArgumentNullException.ThrowIfNull(score);
        var pp = score.Pp is { } value ? Pp(value) : score.Passed ? "no pp" : "failed";
        var combo = score.BeatmapMaxCombo > 0
            ? string.Create(CultureInfo.InvariantCulture, $"x{score.MaxCombo}/{score.BeatmapMaxCombo}")
            : string.Create(CultureInfo.InvariantCulture, $"x{score.MaxCombo}");
        var hits = string.Create(CultureInfo.InvariantCulture, $"[{score.Great}/{score.Ok}/{score.Meh}/{score.Miss}]");
        var total = score.IsStable && score.LegacyTotalScore > 0 ? score.LegacyTotalScore : score.TotalScore;
        return $"▸ **{pp}** ▸ {Percent(score.Accuracy)} ▸ {Grade(score.Grade)}\n▸ {Number(total)} ▸ {combo} ▸ {hits}";
    }

    /// <summary>A score as one block in a list.</summary>
    public string ScoreBlock(ScoreCard score, string? prefix = null)
    {
        ArgumentNullException.ThrowIfNull(score);
        var client = score.IsStable ? "stable" : "lazer";
        var mods = score.Mods.Count > 0 ? $" {Mods(score.Mods)}" : string.Empty;
        return $"{prefix}{MapLink(score)}{mods} {Stars(score.StarRating)}\n{ScoreDetails(score)}\n▸ {client} ▸ {Relative(score.EndedAt)}";
    }
}
