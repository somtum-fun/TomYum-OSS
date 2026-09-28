using System.Globalization;
using System.Text.RegularExpressions;
using TomYum.Bancho;

namespace TomYum.Server.Bancho;

/// <summary>
/// The map a player last shared with <c>/np</c>: the "is playing/listening to/watching/editing
/// [link name]" action message of osu!stable (<c>&lt;Taiko&gt; +Hidden -Easy ~Relax~</c>) or lazer
/// (<c>&lt;osu!taiko&gt; +HD -EZ</c>, sent through website chat), kept for BanchoBot's <c>!map</c>
/// and pp reply (<see cref="NowPlayingStore"/>).
/// </summary>
/// <param name="BeatmapId">The difficulty, or 0 when only a set was shared (listening in the song select of a set).</param>
/// <param name="SetId">The set, or 0 when the link didn't say.</param>
/// <param name="Mode">0 osu!, 1 taiko, 2 catch, 3 mania (from the <c>&lt;Taiko&gt;</c>-style tag).</param>
/// <param name="Mods">Legacy mod bits from the <c>+Hidden -Easy ~Relax~ |Autopilot|</c> tokens.</param>
public sealed partial record NowPlaying(int BeatmapId, int SetId, int Mode, Mods Mods)
{
    /// <summary>Reads a <c>/np</c> message; null for any other message.</summary>
    public static NowPlaying? TryParse(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return null;
        var match = Pattern().Match(text);
        if (!match.Success)
            return null;

        var link = match.Groups["link"].Value;
        var beatmapId = Number(BeatmapInLink().Match(link));
        var setId = Number(SetInLink().Match(link));
        if (beatmapId == 0 && setId == 0)
            return null;

        var mode = match.Groups["mode"].Value switch
        {
            "Taiko" or "osu!taiko" => 1,
            "CatchTheBeat" or "osu!catch" => 2,
            "osu!mania" => 3,
            _ => 0,
        };

        var mods = Mods.None;
        foreach (var token in match.Groups["mods"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var name = token.Trim('+', '-', '~', '|');
            if (Acronyms.TryGetValue(name, out var acronym))
                mods |= acronym;
            else if (Enum.TryParse<Mods>(name, ignoreCase: false, out var mod) && mod != Mods.None)
                mods |= mod;
        }
        // The client names Nightcore and Perfect alone; their bits include DoubleTime / SuddenDeath.
        if (mods.HasFlag(Mods.Nightcore))
            mods |= Mods.DoubleTime;
        if (mods.HasFlag(Mods.Perfect))
            mods |= Mods.SuddenDeath;

        return new NowPlaying(beatmapId, setId, mode, mods);
    }

    /// <summary>
    /// Wraps a website (lazer) action message the way stable sends one, so both parse alike.
    /// </summary>
    public static NowPlaying? TryParseAction(string? text) =>
        string.IsNullOrEmpty(text) ? null : TryParse($"\u0001ACTION {text}\u0001");

    /// <summary>
    /// Mods from acronyms written together or apart (<c>HDDT</c>, <c>+HD DT</c>, <c>hd,hr</c>);
    /// null when something isn't a mod.
    /// </summary>
    public static Mods? ParseAcronyms(string? text)
    {
        var letters = new string((text ?? string.Empty).Where(char.IsAsciiLetter).ToArray()).ToUpperInvariant();
        if (letters.Length % 2 != 0)
            return null;
        var mods = Mods.None;
        for (var i = 0; i < letters.Length; i += 2)
        {
            if (!Acronyms.TryGetValue(letters.Substring(i, 2), out var mod))
                return null;
            mods |= mod;
        }
        // Nightcore and Perfect carry the mods they imply, as the client sends them.
        if (mods.HasFlag(Mods.Nightcore))
            mods |= Mods.DoubleTime;
        if (mods.HasFlag(Mods.Perfect))
            mods |= Mods.SuddenDeath;
        return mods;
    }

    /// <summary>Lazer's mod acronyms (stable writes the full names).</summary>
    private static readonly Dictionary<string, Mods> Acronyms = new(StringComparer.Ordinal)
    {
        ["NF"] = Mods.NoFail, ["EZ"] = Mods.Easy, ["HD"] = Mods.Hidden, ["HR"] = Mods.HardRock,
        ["SD"] = Mods.SuddenDeath, ["PF"] = Mods.Perfect, ["DT"] = Mods.DoubleTime, ["NC"] = Mods.Nightcore,
        ["HT"] = Mods.HalfTime, ["DC"] = Mods.HalfTime, ["FL"] = Mods.Flashlight, ["SO"] = Mods.SpunOut,
        ["RX"] = Mods.Relax, ["AP"] = Mods.Autopilot, ["FI"] = Mods.FadeIn, ["MR"] = Mods.Mirror,
    };

    private static int Number(System.Text.RegularExpressions.Match match) =>
        match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : 0;

    [GeneratedRegex(@"^\u0001ACTION is (?:playing|editing|watching|listening to) \[(?<link>https?://\S+) [^\u0001]*?\](?: <(?<mode>Taiko|CatchTheBeat|osu!mania|osu!taiko|osu!catch|osu!)>)?(?<mods>(?: [-+~|]\w+[~|]?)*) *\u0001$")]
    private static partial Regex Pattern();

    [GeneratedRegex(@"(?:/b/|/beatmaps/|#/?(?:osu|taiko|fruits|mania)?/)(\d{1,10})")]
    private static partial Regex BeatmapInLink();

    [GeneratedRegex(@"/(?:beatmapsets|s)/(\d{1,10})")]
    private static partial Regex SetInLink();
}
