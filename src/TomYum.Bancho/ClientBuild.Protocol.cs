namespace TomYum.Bancho;

/// <summary>
/// Something in bancho's protocol that old builds (b337 from 2008 to b20130915.4) got at some point:
/// <see cref="ClientBuild.Has"/> says whether a build has it. Each one's first build is in
/// <see cref="ClientBuild"/>'s table; docs/old-clients.md has the eras they make up.
/// </summary>
public enum BanchoFeature
{
    /// <summary>b370: the user update with a completeness byte (b337's has a "new stats" flag), and today's ids + 1 from 11 up (b337's own table differs from 51).</summary>
    CompletenessUserUpdate,

    /// <summary>b370: chat channels (b337 has one chat pane, for #osu and private messages).</summary>
    Channels,

    /// <summary>b370: MATCH_SKIP (61) and the client's skip request.</summary>
    MatchSkip,

    /// <summary>b370: beatmap info requests and replies (song select's statuses and grades).</summary>
    BeatmapInfo,

    /// <summary>b420: rooms end with the host's id; a beatmap's status is a byte (a bool before).</summary>
    RoomHost,

    /// <summary>b452: the login's privileges packet.</summary>
    Privileges,

    /// <summary>b476: friends, the osu!direct status (13), the permissions byte in user updates and the room's play mode.</summary>
    Friends,

    /// <summary>b497: PROTOCOL_VERSION; with it sent, statuses carry the play mode and beatmap id.</summary>
    ProtocolVersion,

    /// <summary>b504: beatmap info replies carry three grades (osu!, catch, taiko); one before.</summary>
    ThreeGrades,

    /// <summary>b558: score frames end with a tag byte (27 bytes before); rooms get slot teams, scoring and team type.</summary>
    TaggedScoreFrames,

    /// <summary>b595b: rooms have a password, and joining one sends it.</summary>
    RoomPassword,

    /// <summary>b639: the client says when it leaves a channel.</summary>
    ChannelPart,

    /// <summary>b699: the rank in user updates is 32-bit (16-bit before).</summary>
    WideRank,

    /// <summary>b1122: team vs and tag team vs, and the Quit slot status.</summary>
    TeamVs,

    /// <summary>b1218: full user updates end with the player's longitude and latitude.</summary>
    UserUpdateLocation,

    /// <summary>b1553: MATCH_PLAYER_SKIPPED.</summary>
    PlayerSkipped,

    /// <summary>b1807: today's packet ids, stats and presence as two packets, 16-bit room ids and friend counts.</summary>
    TodaysIds,

    /// <summary>b20120812: stats carry pp; presence ends with the play mode.</summary>
    Performance,

    /// <summary>b20120916: 32-bit mods in statuses, stats and rooms (16-bit before); the menu icon carries an image URL.</summary>
    WideMods,

    /// <summary>b20121003: osu!mania (play mode 3).</summary>
    Mania,

    /// <summary>b20121115: beatmap info replies carry a fourth grade (mania).</summary>
    FourGrades,

    /// <summary>b20121203: today's presence layout (with protocol version 13 or later, which TomYum sends).</summary>
    TodaysPresence,

    /// <summary>b20121223: the menu icon is "image|url".</summary>
    MenuIconLink,

    /// <summary>b20130303: messages from the client carry the sender's id; rooms have free mods.</summary>
    MessageSenderId,

    /// <summary>b20130513: spectator frame bundles start with an extra int; rooms end with a seed.</summary>
    FrameBundleExtra,

    /// <summary>After b20130915.4 (the last build read): login refusals below -6 (-7 and -8) are left as they are.</summary>
    TodaysLoginCodes,
}

// The protocol eras of old builds, read from their decompiled clients (b337 to b20130915.4).
public sealed partial record ClientBuild
{
    /// <summary>The first build with each <see cref="BanchoFeature"/>.</summary>
    private static readonly Dictionary<BanchoFeature, ClientBuild> FirstBuilds = new()
    {
        [BanchoFeature.CompletenessUserUpdate] = Parse("b370"),
        [BanchoFeature.Channels] = Parse("b370"),
        [BanchoFeature.MatchSkip] = Parse("b370"),
        [BanchoFeature.BeatmapInfo] = Parse("b370"),
        [BanchoFeature.RoomHost] = Parse("b420"),
        [BanchoFeature.Privileges] = Parse("b452"),
        [BanchoFeature.Friends] = Parse("b476"),
        [BanchoFeature.ProtocolVersion] = Parse("b497"),
        [BanchoFeature.ThreeGrades] = Parse("b504"),
        [BanchoFeature.TaggedScoreFrames] = Parse("b558"),
        [BanchoFeature.RoomPassword] = Parse("b595"),
        [BanchoFeature.ChannelPart] = Parse("b639"),
        [BanchoFeature.WideRank] = Parse("b699"),
        [BanchoFeature.TeamVs] = Parse("b1122"),
        [BanchoFeature.UserUpdateLocation] = Parse("b1218"),
        [BanchoFeature.PlayerSkipped] = Parse("b1553"),
        [BanchoFeature.TodaysIds] = Parse("b1807"),
        [BanchoFeature.Performance] = Parse("b20120812"),
        [BanchoFeature.WideMods] = Parse("b20120916"),
        [BanchoFeature.Mania] = Parse("b20121003"),
        [BanchoFeature.FourGrades] = Parse("b20121115"),
        [BanchoFeature.TodaysPresence] = Parse("b20121203"),
        [BanchoFeature.MenuIconLink] = Parse("b20121223"),
        [BanchoFeature.MessageSenderId] = Parse("b20130303"),
        [BanchoFeature.FrameBundleExtra] = Parse("b20130513"),
        [BanchoFeature.TodaysLoginCodes] = Parse("b20131001"),
    };

    /// <summary>
    /// The build that sent a login (<c>name\npassword\nversion|utc|...</c>; the version's first field),
    /// for replying to it before there's a session. Today's client when there's none.
    /// </summary>
    public static ClientBuild FromLogin(string? body)
    {
        var lines = (body ?? string.Empty).Split('\n');
        return Parse(lines.Length >= 3 ? lines[2].Split('|')[0].Trim() : null);
    }

    /// <summary>Whether this build has <paramref name="feature"/> (today's clients have them all).</summary>
    public bool Has(BanchoFeature feature) => IsAtLeast(FirstBuilds[feature]);

    /// <summary>Whether this build is <paramref name="first"/> or newer: numbered builds come before dated ones.</summary>
    private bool IsAtLeast(ClientBuild first)
    {
        if (Era is null)
            return true; // today's client
        if (first.Number is { } number)
            return Number is not { } n || n >= number;
        return Date is { } date && first.Date is { } since && date >= since;
    }

    /// <summary>b337 (2008): its own packet ids from 51 up, the "new stats" user update, no channels.</summary>
    public bool IsFirstEra => Number is not null && !Has(BanchoFeature.CompletenessUserUpdate);

    /// <summary>The lowest login refusal this build understands: lower ones read as a successful login there.</summary>
    public int LowestLoginCode =>
        !Has(BanchoFeature.ChannelPart) ? -2           // b337-b595b: failed, too old
        : Number is < 1700 ? -3                        // b639-b1652: + banned
        : !Has(BanchoFeature.TodaysIds) ? -4          // b1700, b1704: + not activated
        : !Has(BanchoFeature.TodaysLoginCodes) ? -6   // b1807-b20130915: + server error, supporter only
        : int.MinValue;

    /// <summary>A login reply's user id (or refusal) as this build reads it: refusals it doesn't know become "failed" (-1).</summary>
    public int LoginCode(int code) => code < LowestLoginCode ? -1 : code;

    /// <summary>The client privileges this build knows (normal, BAT, supporter; then more bits).</summary>
    public int PrivilegeMask => !Has(BanchoFeature.TodaysIds) ? 0x7 : !Has(BanchoFeature.WideMods) ? 0xF : ~0;
}
