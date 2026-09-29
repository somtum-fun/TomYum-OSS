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

    /// <summary>The website's address, e.g. <c>https://osu.sundei.eu</c>.</summary>
    public string Site => _site;

    /// <summary>The short link, e.g. <c>https://osu.sundei.eu/b/4622078</c>.</summary>
    public string Beatmap(int beatmapId) => $"{_site}/b/{beatmapId.ToString(CultureInfo.InvariantCulture)}";

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

    private static Dictionary<string, string> _gradeEmojis = new(StringComparer.Ordinal);

    /// <summary>Emojis to show for grades (<see cref="DiscordOptions.GradeEmojis"/>); set once at startup.</summary>
    public static void UseGradeEmojis(IReadOnlyDictionary<string, string> emojis) =>
        _gradeEmojis = new Dictionary<string, string>(emojis ?? throw new ArgumentNullException(nameof(emojis)), StringComparer.Ordinal);

    /// <summary>A grade (<c>XH</c>, <c>X</c>, <c>SH</c>, <c>S</c> … <c>D</c>, <c>F</c>): its emoji when one is set, else in words.</summary>
    public static string Grade(string grade) => Grade(grade, _gradeEmojis);

    public static string Grade(string grade, IReadOnlyDictionary<string, string> emojis)
    {
        ArgumentNullException.ThrowIfNull(emojis);
        var key = string.IsNullOrEmpty(grade) ? "F" : grade;
        if (emojis.TryGetValue(key, out var emoji))
            return emoji;
        return key switch
        {
            "XH" => "SS (silver)",
            "X" => "SS",
            "SH" => "S (silver)",
            _ => key,
        };
    }

    /// <summary>The grade's emoji followed by a space, or nothing when there's no emoji for it.</summary>
    public static string GradeMark(string grade) =>
        _gradeEmojis.TryGetValue(string.IsNullOrEmpty(grade) ? "F" : grade, out var emoji) ? emoji + " " : string.Empty;

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

    /// <summary>
    /// Text for inside a link's [label]. Discord shows backslash escapes there literally, so nothing
    /// is escaped: balanced brackets (a difficulty name) work as they are, and only brackets that
    /// would end the label early become parentheses.
    /// </summary>
    public static string LinkLabel(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var depth = 0;
        foreach (var c in text)
        {
            depth += c == '[' ? 1 : c == ']' ? -1 : 0;
            if (depth < 0)
                break;
        }
        return depth == 0 ? text : text.Replace('[', '(').Replace(']', ')');
    }

    /// <summary>A linked map title.</summary>
    public string MapLink(ScoreCard score)
    {
        ArgumentNullException.ThrowIfNull(score);
        return $"[{LinkLabel(score.MapName)}]({Beatmap(score.BeatmapId)})";
    }

    /// <summary>
    /// Two lines about a score, Bathbot-style: grade, score and accuracy, then pp, combo and misses
    /// ("FC" for a full combo). The title carries the mods.
    /// </summary>
    public static string ScoreDetails(ScoreCard score)
    {
        ArgumentNullException.ThrowIfNull(score);
        var total = score.IsStable && score.LegacyTotalScore > 0 ? score.LegacyTotalScore : score.TotalScore;
        var pp = score.Pp is { } value ? $"**{Pp(value)}**" : score.Passed ? "**no pp**" : "**failed**";
        var combo = score.BeatmapMaxCombo > 0
            ? string.Create(CultureInfo.InvariantCulture, $"**{score.MaxCombo}x**/{score.BeatmapMaxCombo}x")
            : string.Create(CultureInfo.InvariantCulture, $"**{score.MaxCombo}x**");
        var fullCombo = score.Passed && score.Miss == 0 && (score.BeatmapMaxCombo == 0 || score.MaxCombo >= score.BeatmapMaxCombo - 1);
        var misses = fullCombo ? "FC" : string.Create(CultureInfo.InvariantCulture, $"{score.Miss} ❌");
        return $"{Grade(score.Grade)}  {Number(total)}  {Percent(score.Accuracy)}\n{pp} • {combo} • {misses}";
    }

    /// <summary>A score as one block in a list.</summary>
    public string ScoreBlock(ScoreCard score, string? prefix = null)
    {
        ArgumentNullException.ThrowIfNull(score);
        var client = score.IsStable ? "stable" : "lazer";
        var mods = score.Mods.Count > 0 ? $" {Mods(score.Mods)}" : string.Empty;
        return $"{prefix}{MapLink(score)}{mods} {Stars(score.StarRating)}\n{ScoreDetails(score)}\n▸ {client} ▸ {Relative(score.EndedAt)}";
    }

    /// <summary>Restrict / Silence 1 day / Dismiss / Profile, for staff posts about a player.</summary>
    public IReadOnlyList<DiscordButton> ModerationButtons(int userId, string reason, int profileId)
    {
        ArgumentNullException.ThrowIfNull(reason);
        // Button ids are split on ':' and capped at 100 characters.
        var safe = reason.Replace(':', ' ');
        if (safe.Length > 60)
            safe = safe[..59] + "…";
        var user = userId.ToString(CultureInfo.InvariantCulture);
        return
        [
            new DiscordButton("Restrict", DiscordButtonStyle.Danger, $"mod:restrict:{user}:{safe}"),
            new DiscordButton("Silence 1 day", DiscordButtonStyle.Primary, $"mod:silence:{user}:{safe}"),
            new DiscordButton("Dismiss", DiscordButtonStyle.Secondary, $"mod:dismiss:{user}:-"),
            new DiscordButton("Profile", DiscordButtonStyle.Link, Url: Profile(profileId)),
        ];
    }
}
