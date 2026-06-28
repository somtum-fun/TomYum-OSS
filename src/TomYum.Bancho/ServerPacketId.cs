namespace TomYum.Bancho;

/// <summary>
/// Packet identifiers for messages travelling server → client (osu!stable binary protocol).
/// Values are the on-wire <c>uint16</c> ids and are stable across the ecosystem (confirmed
/// identical across the official 2016 source, bancho.py, BanchoNET, and Sunrise).
/// </summary>
/// <remarks>
/// Direction matters: a numeric id means different things to the client and the server, so
/// server and client ids live in separate enums (<see cref="ClientPacketId"/>). Full table:
/// <c>memories/protocols/bancho-stable.md</c> §4.
/// </remarks>
public enum ServerPacketId : ushort
{
    /// <summary>Login reply: i32 user id (>= 0) or a negative failure sentinel. See login §3.</summary>
    UserId = 5,

    /// <summary>A chat message (message composite) delivered to the client.</summary>
    SendMessage = 7,

    /// <summary>Keep-alive pong.</summary>
    Pong = 8,

    /// <summary>Deprecated IRC username-change relay.</summary>
    HandleIrcChangeUsername = 9,

    /// <summary>IRC quit relay.</summary>
    HandleIrcQuit = 10,

    /// <summary>Dynamic per-user stats block (action, beatmap, score, acc, pp, rank).</summary>
    UserStats = 11,

    /// <summary>i32 user id that just logged out.</summary>
    UserLogout = 12,

    /// <summary>i32 user id that started spectating this client.</summary>
    SpectatorJoined = 13,

    /// <summary>i32 user id that stopped spectating this client.</summary>
    SpectatorLeft = 14,

    /// <summary>Replay-frame bundle relayed to a spectator.</summary>
    SpectateFrames = 15,

    /// <summary>A client version-update notice.</summary>
    VersionUpdate = 19,

    /// <summary>A fellow spectator cannot spectate the shared target.</summary>
    SpectatorCantSpectate = 22,

    /// <summary>Request the client window's attention.</summary>
    GetAttention = 23,

    /// <summary>string notification shown to the user.</summary>
    Notification = 24,

    /// <summary>Updated match state (match composite).</summary>
    UpdateMatch = 26,

    /// <summary>A newly created match (match composite).</summary>
    NewMatch = 27,

    /// <summary>i32 match id that was disposed.</summary>
    DisposeMatch = 28,

    /// <summary>Confirms the client's block-non-friend-DMs toggle.</summary>
    ToggleBlockNonFriendDms = 34,

    /// <summary>Successful match join (match composite).</summary>
    MatchJoinSuccess = 36,

    /// <summary>Match join failed.</summary>
    MatchJoinFail = 37,

    /// <summary>i32 user id of a fellow spectator that joined.</summary>
    FellowSpectatorJoined = 42,

    /// <summary>i32 user id of a fellow spectator that left.</summary>
    FellowSpectatorLeft = 43,

    /// <summary>Match has started (match composite).</summary>
    MatchStart = 46,

    /// <summary>Per-slot score update during a match (score frame).</summary>
    MatchScoreUpdate = 48,

    /// <summary>i32 new host slot for the match.</summary>
    MatchTransferHost = 50,

    /// <summary>All players in the match finished loading.</summary>
    MatchAllPlayersLoaded = 53,

    /// <summary>i32 slot index of a player that failed.</summary>
    MatchPlayerFailed = 57,

    /// <summary>The match completed.</summary>
    MatchComplete = 58,

    /// <summary>The match skipped the intro.</summary>
    MatchSkip = 61,

    /// <summary>string channel name successfully joined.</summary>
    ChannelJoinSuccess = 64,

    /// <summary>Channel metadata (channel composite).</summary>
    ChannelInfo = 65,

    /// <summary>string channel name the client was kicked/removed from.</summary>
    ChannelKick = 66,

    /// <summary>Channel the client should auto-join (channel composite).</summary>
    ChannelAutoJoin = 67,

    /// <summary>Batch beatmap-info reply.</summary>
    BeatmapInfoReply = 69,

    /// <summary>i32 bancho privileges bitmask for the client.</summary>
    Privileges = 71,

    /// <summary>i32 list of the client's friend ids.</summary>
    FriendsList = 72,

    /// <summary>i32 supported protocol version (19).</summary>
    ProtocolVersion = 75,

    /// <summary>string <c>image_url|click_url</c> for the main-menu icon.</summary>
    MainMenuIcon = 76,

    /// <summary>i32 slot index of a player that skipped.</summary>
    MatchPlayerSkipped = 81,

    /// <summary>Static per-user presence block (name, country, rank, location).</summary>
    UserPresence = 83,

    /// <summary>i32 milliseconds until the client should reconnect.</summary>
    Restart = 86,

    /// <summary>Invite to a match (message composite).</summary>
    MatchInvite = 88,

    /// <summary>Marks the end of the initial CHANNEL_INFO burst.</summary>
    ChannelInfoEnd = 89,

    /// <summary>string new match password.</summary>
    MatchChangePassword = 91,

    /// <summary>u32 seconds remaining on the client's silence.</summary>
    SilenceEnd = 92,

    /// <summary>i32 user id that was silenced.</summary>
    UserSilenced = 94,

    /// <summary>i32 single user id whose presence follows.</summary>
    UserPresenceSingle = 95,

    /// <summary>i32 list of user ids whose presence is bundled.</summary>
    UserPresenceBundle = 96,

    /// <summary>string user whose DMs were blocked.</summary>
    UserDmBlocked = 100,

    /// <summary>string target user that is silenced.</summary>
    TargetIsSilenced = 101,

    /// <summary>The client must update before continuing.</summary>
    VersionUpdateForced = 102,

    /// <summary>string host to switch the client to.</summary>
    SwitchServer = 103,

    /// <summary>The account is restricted.</summary>
    AccountRestricted = 104,

    /// <summary>string tournament server to switch to.</summary>
    SwitchTournamentServer = 107,
}
