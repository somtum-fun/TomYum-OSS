using System.Globalization;
using System.Text.RegularExpressions;
using TomYum.Bancho;

namespace TomYum.Server.Bancho;

/// <summary>
/// The map a player last shared with <c>/np</c> (osu!stable's "is playing/listening to/watching/
/// editing [link name]" action), kept on their session for BanchoBot (<c>!map</c>, the pp reply).
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
            "Taiko" => 1,
            "CatchTheBeat" => 2,
            "osu!mania" => 3,
            _ => 0,
        };

        var mods = Mods.None;
        foreach (var token in match.Groups["mods"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (Enum.TryParse<Mods>(token.Trim('+', '-', '~', '|'), ignoreCase: false, out var mod) && mod != Mods.None)
                mods |= mod;
        }
        // The client names Nightcore and Perfect alone; their bits include DoubleTime / SuddenDeath.
        if (mods.HasFlag(Mods.Nightcore))
            mods |= Mods.DoubleTime;
        if (mods.HasFlag(Mods.Perfect))
            mods |= Mods.SuddenDeath;

        return new NowPlaying(beatmapId, setId, mode, mods);
    }

    private static int Number(System.Text.RegularExpressions.Match match) =>
        match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : 0;

    [GeneratedRegex(@"^\u0001ACTION is (?:playing|editing|watching|listening to) \[(?<link>https?://\S+) [^\u0001]*?\](?: <(?<mode>Taiko|CatchTheBeat|osu!mania)>)?(?<mods>(?: [-+~|]\w+[~|]?)*)\u0001$")]
    private static partial Regex Pattern();

    [GeneratedRegex(@"(?:/b/|/beatmaps/|#/?(?:osu|taiko|fruits|mania)?/)(\d{1,10})")]
    private static partial Regex BeatmapInLink();

    [GeneratedRegex(@"/(?:beatmapsets|s)/(\d{1,10})")]
    private static partial Regex SetInLink();
}
