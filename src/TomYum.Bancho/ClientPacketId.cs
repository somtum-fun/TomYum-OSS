namespace TomYum.Bancho;

/// <summary>
/// Packet identifiers for messages travelling client → server (osu!stable binary protocol).
/// Values are the on-wire <c>uint16</c> ids and are stable across the ecosystem (confirmed
/// identical across the official 2016 source, bancho.py, BanchoNET, and Sunrise).
/// </summary>
/// <remarks>
/// Direction matters: a numeric id means different things to the client and the server, so
/// client and server ids live in separate enums (<see cref="ServerPacketId"/>). Full table:
/// <c>memories/protocols/bancho-stable.md</c> §4.
/// </remarks>
public enum ClientPacketId : ushort
{
    /// <summary>u8 action + string info + string map_md5 + u32 mods + u8 mode + i32 map_id.</summary>
    ChangeAction = 0,

    /// <summary>Send a message to a channel (message composite).</summary>
    SendPublicMessage = 1,

    /// <summary>Client is logging out.</summary>
    Logout = 2,

    /// <summary>Client requests a refresh of its own stats.</summary>
    RequestStatusUpdate = 3,

    /// <summary>Keep-alive ping.</summary>
    Ping = 4,

    /// <summary>i32 target user id to start spectating.</summary>
    StartSpectating = 16,

    /// <summary>Stop spectating the current target.</summary>
    StopSpectating = 17,

    /// <summary>Replay-frame bundle re-broadcast to spectators.</summary>
    SpectateFrames = 18,

    /// <summary>Client-side error report payload.</summary>
    ErrorReport = 20,

    /// <summary>Client cannot spectate the requested target (missing beatmap).</summary>
    CantSpectate = 21,

    /// <summary>Send a private message to another user (message composite).</summary>
    SendPrivateMessage = 25,

    /// <summary>Leave the multiplayer lobby.</summary>
    PartLobby = 29,

    /// <summary>Enter the multiplayer lobby.</summary>
    JoinLobby = 30,

    /// <summary>Create a multiplayer match (match composite).</summary>
    CreateMatch = 31,

    /// <summary>Join an existing match (i32 match id + string password).</summary>
    JoinMatch = 32,

    /// <summary>Leave the current match.</summary>
    PartMatch = 33,

    /// <summary>i32 destination slot for a match slot move.</summary>
    MatchChangeSlot = 38,

    /// <summary>Mark self ready in a match.</summary>
    MatchReady = 39,

    /// <summary>i32 slot index to lock/unlock.</summary>
    MatchLock = 40,

    /// <summary>Host changes match settings (match composite).</summary>
    MatchChangeSettings = 41,

    /// <summary>Host starts the match.</summary>
    MatchStart = 44,

    /// <summary>Per-frame score update during a match (score frame).</summary>
    MatchScoreUpdate = 47,

    /// <summary>Client has finished the map in a match.</summary>
    MatchComplete = 49,

    /// <summary>i32 mods for the match / self (freemods).</summary>
    MatchChangeMods = 51,

    /// <summary>Client has finished loading the map.</summary>
    MatchLoadComplete = 52,

    /// <summary>Client does not have the match's beatmap.</summary>
    MatchNoBeatmap = 54,

    /// <summary>Client toggled itself not-ready.</summary>
    MatchNotReady = 55,

    /// <summary>Client failed the map in a match.</summary>
    MatchFailed = 56,

    /// <summary>Client now has the match's beatmap.</summary>
    MatchHasBeatmap = 59,

    /// <summary>Request to skip the current map intro.</summary>
    MatchSkipRequest = 60,

    /// <summary>string channel name to join.</summary>
    ChannelJoin = 63,

    /// <summary>Batch beatmap-info lookup request.</summary>
    BeatmapInfoRequest = 68,

    /// <summary>i32 target slot to transfer host to.</summary>
    MatchTransferHost = 70,

    /// <summary>i32 user id to add as a friend.</summary>
    FriendAdd = 73,

    /// <summary>i32 user id to remove as a friend.</summary>
    FriendRemove = 74,

    /// <summary>i32 team for the calling player in a match.</summary>
    MatchChangeTeam = 77,

    /// <summary>string channel name to leave.</summary>
    ChannelPart = 78,

    /// <summary>u8 presence filter (none / all / friends).</summary>
    ReceiveUpdates = 79,

    /// <summary>string away message ("" clears it).</summary>
    SetAwayMessage = 82,

    /// <summary>IRC-only marker (legacy, unused by the binary client).</summary>
    IrcOnly = 84,

    /// <summary>i32 list of user ids to fetch stats for.</summary>
    UserStatsRequest = 85,

    /// <summary>i32 target user id to invite to the current match.</summary>
    MatchInvite = 87,

    /// <summary>string new match password.</summary>
    MatchChangePassword = 90,

    /// <summary>Tournament client requests match info.</summary>
    TournamentMatchInfoRequest = 93,

    /// <summary>i32 list of user ids to fetch presence for.</summary>
    UserPresenceRequest = 97,

    /// <summary>Request presence for every online user.</summary>
    UserPresenceRequestAll = 98,

    /// <summary>Toggle blocking DMs from non-friends.</summary>
    ToggleBlockNonFriendDms = 99,

    /// <summary>Tournament client joins a match channel.</summary>
    TournamentJoinMatchChannel = 108,

    /// <summary>Tournament client leaves a match channel.</summary>
    TournamentLeaveMatchChannel = 109,
}
