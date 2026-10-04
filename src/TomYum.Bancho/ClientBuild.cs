using System.Globalization;
using System.Text.RegularExpressions;

namespace TomYum.Bancho;

/// <summary>
/// An osu!stable build, from the version a client sends at login (<c>b20140624</c>,
/// <c>b20150101cuttingedge</c>, <c>b20240123.2</c>, <c>b20240123tourney</c>). Old builds (docs/old-clients.md)
/// speak older versions of the protocol and of the web endpoints; their build date says which.
/// Builds from before dated versions are numbered (<c>b1122</c>): <see cref="Number"/>, and no date.
/// </summary>
public sealed partial record ClientBuild(string Version, DateOnly? Date)
{
    /// <summary>The build number of a numbered build (<c>b1122</c> → 1122), else null.</summary>
    public int? Number { get; init; }

    /// <summary>
    /// Whether this is a numbered build before b1807 (b337 to b1704, 2008 to 2011): the packet ids from 11 up
    /// are one higher than today's, and a user's stats and presence are one packet (<see cref="EarlyPackets"/>).
    /// b1807 to b1844 already use today's ids, with older layouts (<see cref="LegacyPackets"/>).
    /// </summary>
    public bool HasEarlyPackets => Number is not null && !Has(BanchoFeature.TodaysIds);

    /// <summary>The build's date for the checks below: numbered builds are older than any dated one.</summary>
    private DateOnly? Era => Date ?? (Number is null ? null : DateOnly.MinValue);

    /// <summary>
    /// Builds before this run on .NET Framework 2.0: no TLS 1.2, so they can't reach today's HTTPS
    /// sites (beatmap mirrors) and are served over plain HTTP. osu! moved stable to .NET 4 in November 2015.
    /// </summary>
    public static readonly DateOnly ModernFrom = new(2016, 1, 1);

    /// <summary>The build of <paramref name="version"/>; <see cref="Date"/> is null when there's no date in it.</summary>
    public static ClientBuild Parse(string? version)
    {
        version ??= string.Empty;
        var match = DatePattern().Match(version);
        DateOnly? date = match.Success
            && DateOnly.TryParseExact(match.Groups["date"].Value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : null;
        // Numbered builds stay below 2000; "b2014" is a cut-off date, not a build.
        var numbered = date is null ? NumberPattern().Match(version) : Match.Empty;
        int? number = numbered.Success && int.Parse(numbered.Groups["number"].Value, CultureInfo.InvariantCulture) is var n && n < 2000 ? n : null;
        return new ClientBuild(version, date) { Number = number };
    }

    /// <summary>A build from before <see cref="ModernFrom"/> (an unknown date counts as today's client).</summary>
    public bool IsLegacy => IsBefore(ModernFrom);

    /// <summary>Whether this build is older than <paramref name="date"/>: numbered builds always are, an unknown one never.</summary>
    public bool IsBefore(DateOnly date) => Era is { } era && era < date;

    /// <summary>osu!mania came with this build (b20121003shine.test); older builds can't play mania maps.</summary>
    public static readonly DateOnly ManiaFrom = new(2012, 10, 3);

    /// <summary>Whether this build has osu!mania (today's does).</summary>
    public bool HasMania => !IsBefore(ManiaFrom);

    /// <summary>Score frames end with a ScoreV2 flag (and its extra numbers when set) from this build on.</summary>
    public static readonly DateOnly ScoreV2FramesFrom = new(2015, 11, 6);

    /// <summary>Spectator frame bundles end with a sequence number from this build on.</summary>
    public static readonly DateOnly FrameSequenceFrom = new(2016, 4, 4);

    /// <summary>Whether this build's score frames have the ScoreV2 flag (today's do).</summary>
    public bool HasScoreV2Frames => Era is not { } date || date >= ScoreV2FramesFrom;

    /// <summary>Whether this build's spectator frame bundles end with a sequence number (today's do).</summary>
    public bool HasFrameSequence => Era is not { } date || date >= FrameSequenceFrom;

    /// <summary>Multiplayer rooms have 16 slots from this build on (with protocol 19, which TomYum sends); 8 before.</summary>
    public static readonly DateOnly SixteenSlotsFrom = new(2014, 5, 28);

    /// <summary>How many slots this build's multiplayer rooms have (Lekuruu's chio, b20140528).</summary>
    public int MatchSlots => Era is { } date && date < SixteenSlotsFrom ? 8 : 16;

    /// <summary>
    /// Whether this build knows server packet <paramref name="id"/>: ones added after 2013 (Lekuruu's
    /// chio: b20140716, b20140731, b20141104) would be misread by older builds, so they aren't sent.
    /// </summary>
    public bool Knows(ServerPacketId id)
    {
        if (Era is not { } date)
            return true;
        return id switch
        {
            ServerPacketId.AccountRestricted => date >= new DateOnly(2014, 7, 16),
            (ServerPacketId)105 => date >= new DateOnly(2014, 7, 31), // RTX
            ServerPacketId.MatchAbort or ServerPacketId.SwitchTournamentServer => date >= new DateOnly(2014, 11, 4),
            _ => true,
        };
    }

    [GeneratedRegex(@"^b(?<date>\d{8})", RegexOptions.CultureInvariant)]
    private static partial Regex DatePattern();

    [GeneratedRegex(@"^b(?<number>\d{1,4})(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex NumberPattern();
}
