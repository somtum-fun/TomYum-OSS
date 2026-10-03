using System.Buffers.Binary;

namespace TomYum.Bancho;

/// <summary>
/// Bancho for numbered builds before b1807 (<see cref="ClientBuild.HasEarlyPackets"/>: b337 to b1704,
/// 2008 to 2011; read from their decompiled clients). That protocol still had a packet at 11 that
/// was later removed, so every id from 11 up is one higher than today's, in both directions (b337
/// differs again from 51: <see cref="FirstEraIds"/>). A user's status, stats and presence are one
/// packet (12) instead of today's stats (11) and presence (83). The layouts changed build by build
/// (<see cref="BanchoFeature"/>); what a build can't read is left out, since a packet it misreads
/// ends the connection, and an id it doesn't know is the only safe kind of leftover.
/// </summary>
public static class EarlyPackets
{
    /// <summary>b1122, the build these were first written for (and the default when no build is given).</summary>
    private static readonly ClientBuild B1122 = ClientBuild.Parse("b1122");

    /// <summary>Server packets sent with their id one higher and the payload as it is, by the feature that brought them.</summary>
    private static readonly Dictionary<ServerPacketId, BanchoFeature?> Shifted = new()
    {
        [ServerPacketId.SpectatorJoined] = null,
        [ServerPacketId.SpectatorLeft] = null,
        [ServerPacketId.SpectatorCantSpectate] = null,
        [ServerPacketId.GetAttention] = null,
        [ServerPacketId.Notification] = null,
        [ServerPacketId.DisposeMatch] = null,
        [ServerPacketId.MatchJoinFail] = null,
        [ServerPacketId.FellowSpectatorJoined] = null,
        [ServerPacketId.FellowSpectatorLeft] = null,
        [ServerPacketId.MatchScoreUpdate] = null,
        [ServerPacketId.MatchTransferHost] = null,
        [ServerPacketId.MatchAllPlayersLoaded] = null,
        [ServerPacketId.MatchPlayerFailed] = null,
        [ServerPacketId.MatchComplete] = null,
        [ServerPacketId.MatchSkip] = BanchoFeature.MatchSkip,
        [ServerPacketId.ChannelJoinSuccess] = BanchoFeature.Channels,
        [ServerPacketId.ChannelInfo] = BanchoFeature.Channels,
        [ServerPacketId.ChannelKick] = BanchoFeature.Channels,
        [ServerPacketId.ChannelAutoJoin] = BanchoFeature.Channels,
        [ServerPacketId.ProtocolVersion] = BanchoFeature.ProtocolVersion,
        [ServerPacketId.MatchPlayerSkipped] = BanchoFeature.PlayerSkipped,
    };

    /// <summary>The server packets that carry a room.</summary>
    private static readonly HashSet<ServerPacketId> Rooms =
    [
        ServerPacketId.UpdateMatch, ServerPacketId.NewMatch, ServerPacketId.MatchJoinSuccess, ServerPacketId.MatchStart,
    ];

    /// <summary>
    /// b337's ids from 51 up: its client had its own "change settings" packet at 51, so its later
    /// server ids are today's + 2 and its client ids map one by one (the rest it never sends).
    /// </summary>
    private static class FirstEraIds
    {
        public static readonly Dictionary<ServerPacketId, ushort> Server = new()
        {
            [ServerPacketId.MatchTransferHost] = 52,
            [ServerPacketId.MatchAllPlayersLoaded] = 55,
            [ServerPacketId.MatchPlayerFailed] = 59,
            [ServerPacketId.MatchComplete] = 60,
        };

        public static readonly Dictionary<ushort, ClientPacketId> Client = new()
        {
            [51] = ClientPacketId.MatchChangeSettings,
            [53] = ClientPacketId.MatchChangeMods,
            [54] = ClientPacketId.MatchLoadComplete,
            [56] = ClientPacketId.MatchNoBeatmap,
            [57] = ClientPacketId.MatchNotReady,
            [58] = ClientPacketId.MatchFailed,
        };
    }

    /// <summary>The build's user update (12): how much of it there is.</summary>
    private enum Completeness : byte
    {
        Status = 0,
        Statistics = 1,
        Full = 2,
    }

    /// <summary>What b1122 is sent of <paramref name="stream"/> (today's framed server packets).</summary>
    public static byte[] Outgoing(byte[] stream) => Outgoing(stream, B1122);

    /// <summary>What <paramref name="build"/> (a numbered build before b1807) is sent of <paramref name="stream"/>.</summary>
    public static byte[] Outgoing(byte[] stream, ClientBuild build)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(build);
        if (stream.Length == 0)
            return stream;

        var writer = new BanchoPacketWriter(stream.Length + 64);
        // Presence waits for the same user's stats in this batch, to go out as one full update.
        var presences = new Dictionary<int, LegacyLayouts.Presence>();
        // The protocol version goes after the login reply, as bancho sent it to these builds.
        byte[]? protocolVersion = null;
        if (!Each(stream, (id, payload) =>
        {
            var today = (ServerPacketId)id;
            switch (today)
            {
                case ServerPacketId.UserId when payload.Length >= 4:
                    writer.WriteInt32Packet(ServerPacketId.UserId, build.LoginCode(BinaryPrimitives.ReadInt32LittleEndian(payload)));
                    if (protocolVersion is not null)
                        writer.WritePacket(Early(ServerPacketId.ProtocolVersion), w => w.WriteRaw(protocolVersion));
                    protocolVersion = [];
                    break;
                case ServerPacketId.ProtocolVersion when build.Has(BanchoFeature.ProtocolVersion) && protocolVersion is null:
                    protocolVersion = payload;
                    break;
                case ServerPacketId.SendMessage when build.Has(BanchoFeature.Channels) || IsForTheOnePane(payload):
                case ServerPacketId.Pong or ServerPacketId.HandleIrcChangeUsername or ServerPacketId.HandleIrcQuit:
                    writer.WritePacket(today, w => w.WriteRaw(payload));
                    break;
                case ServerPacketId.UserPresence:
                    if (LegacyLayouts.Presence.Read(payload) is { } presence)
                        presences[presence.UserId] = presence;
                    break;
                case ServerPacketId.UserStats:
                    if (LegacyLayouts.Stats.Read(payload) is { } stats)
                    {
                        presences.Remove(stats.UserId, out var known);
                        WriteUpdate(writer, stats, known, build);
                    }
                    break;
                case ServerPacketId.UserLogout when payload.Length >= 4:
                    writer.WritePacket(Early(today), w =>
                    {
                        // b337 reads a whole user update: the id, no new stats, status Unknown (7).
                        if (build.IsFirstEra)
                            w.WriteRaw(payload.AsSpan(0, 4)).WriteByte(0).WriteByte(7);
                        else
                            w.WriteRaw(payload);
                    });
                    break;
                case ServerPacketId.SpectateFrames:
                    writer.WritePacket(Early(today), w => w.WriteRaw(LegacyLayouts.FramesOut(payload, build)));
                    break;
                case var room when Rooms.Contains(room):
                    if (LegacyRoom.ReadToday(payload, 16)?.WriteFor(build) is { } converted)
                        writer.WritePacket(Early(room), w => w.WriteRaw(converted));
                    break;
                case ServerPacketId.BeatmapInfoReply when build.Has(BanchoFeature.BeatmapInfo):
                    if (LegacyLayouts.BeatmapInfo(payload, build) is { } reply)
                        writer.WritePacket(Early(today), w => w.WriteRaw(reply));
                    break;
                case ServerPacketId.Privileges when build.Has(BanchoFeature.Privileges) && payload.Length >= 4:
                    // The build knows normal, BAT and supporter (1, 2, 4).
                    writer.WritePacket(Early(today), w => w.WriteInt32(BinaryPrimitives.ReadInt32LittleEndian(payload) & build.PrivilegeMask));
                    break;
                case ServerPacketId.FriendsList when build.Has(BanchoFeature.Friends) && FriendIds(payload) is { } friends:
                    // A 32-bit count here; today's is 16-bit.
                    writer.WritePacket(Early(today), w =>
                    {
                        w.WriteInt32(friends.Length);
                        foreach (var friend in friends)
                            w.WriteInt32(friend);
                    });
                    break;
                case ServerPacketId.ChannelJoinSuccess when build.Has(BanchoFeature.Channels) && IsRoomChat(payload):
                    // Room and spectator chat: the tab opens from "auto-join" (as for later old builds), then "joined".
                    writer.WritePacket(Early(ServerPacketId.ChannelAutoJoin), w => w.WriteRaw(payload));
                    writer.WritePacket(Early(today), w => w.WriteRaw(payload));
                    break;
                case var other when build.IsFirstEra && FirstEraIds.Server.TryGetValue(other, out var firstId):
                    writer.WritePacket((ServerPacketId)firstId, w => w.WriteRaw(payload));
                    break;
                case var other when Shifted.TryGetValue(other, out var since) && (since is not { } feature || build.Has(feature)):
                    writer.WritePacket(Early(other), w => w.WriteRaw(payload));
                    break;
                    // Anything else is left out: the shifted id could mean something else there.
            }
        }))
        {
            return stream;
        }

        if (protocolVersion is { Length: > 0 })
            writer.WritePacket(Early(ServerPacketId.ProtocolVersion), w => w.WriteRaw(protocolVersion));
        foreach (var presence in presences.Values)
            WriteUpdate(writer, new LegacyLayouts.Stats(presence.UserId, 0, string.Empty, string.Empty, 0, presence.Mode, 0, 0, 0, 0, 0, presence.Rank, 0), presence, build);
        return writer.ToArray();
    }

    /// <summary>What b1122 sent (framed client packets), as today's client would send it.</summary>
    public static byte[] Incoming(byte[] stream) => Incoming(stream, B1122);

    /// <summary>What <paramref name="build"/> (a numbered build before b1807) sent, as today's client would send it.</summary>
    public static byte[] Incoming(byte[] stream, ClientBuild build)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(build);
        if (stream.Length == 0)
            return stream;

        var writer = new BanchoPacketWriter(stream.Length + 64);
        if (!Each(stream, (id, payload) =>
        {
            if (Today(id, build) is not { } today)
                return; // b337's packets it never sends
            switch (today)
            {
                case ClientPacketId.ChangeAction:
                    if (LegacyLayouts.Status(payload, build, out var statsPing) is { } status)
                        writer.WritePacket((ServerPacketId)today, w => w.WriteRaw(status));
                    else if (statsPing)
                        writer.WritePacket((ServerPacketId)ClientPacketId.RequestStatusUpdate, null);
                    break;
                case ClientPacketId.SendPublicMessage or ClientPacketId.SendPrivateMessage or ClientPacketId.SetAwayMessage:
                    writer.WritePacket((ServerPacketId)today, w => w.WriteRaw(LegacyLayouts.MessageIn(payload, build)));
                    break;
                case ClientPacketId.SpectateFrames:
                    writer.WritePacket((ServerPacketId)today, w => w.WriteRaw(LegacyLayouts.FramesIn(payload, build)));
                    break;
                case ClientPacketId.MatchScoreUpdate:
                    writer.WritePacket((ServerPacketId)today, w => w.WriteRaw(LegacyLayouts.ScoreFrameIn(payload, build)));
                    break;
                case ClientPacketId.CreateMatch or ClientPacketId.MatchChangeSettings:
                    // A room it couldn't have sent is left out: the handlers read today's.
                    if (LegacyRoom.ReadFrom(payload, build) is { } room)
                        writer.WritePacket((ServerPacketId)today, w => w.WriteRaw(room.WriteToday(16)));
                    break;
                case ClientPacketId.JoinMatch:
                    // The room's password came with b595b.
                    writer.WritePacket((ServerPacketId)today, w =>
                    {
                        w.WriteRaw(payload);
                        if (!build.Has(BanchoFeature.RoomPassword))
                            w.WriteString(string.Empty);
                    });
                    break;
                default:
                    writer.WritePacket((ServerPacketId)today, w => w.WriteRaw(payload));
                    break;
            }
        }))
        {
            return stream;
        }
        return writer.ToArray();
    }

    /// <summary>Today's id of the build's client packet <paramref name="id"/>; null for one b337 never sends.</summary>
    private static ClientPacketId? Today(ushort id, ClientBuild build)
    {
        if (id < 11)
            return (ClientPacketId)id;
        if (build.IsFirstEra && id >= 42)
        {
            // 42 (change beatmap) and 46 (all loaded) are never sent; from 51 b337 has its own ids.
            if (id is 42 or 46)
                return null;
            if (id >= 51)
                return FirstEraIds.Client.TryGetValue(id, out var first) ? first : null;
        }
        return id == 11 ? null : (ClientPacketId)(id - 1);
    }

    private static ServerPacketId Early(ServerPacketId id) => (ServerPacketId)((int)id + 1);

    /// <summary>
    /// The build's user update (12): status and stats, and presence when known. b337's says whether
    /// stats follow (only with the name); later ones say how much follows (0 status, 1 stats, 2 full).
    /// </summary>
    private static void WriteUpdate(BanchoPacketWriter writer, LegacyLayouts.Stats stats, LegacyLayouts.Presence? presence, ClientBuild build)
    {
        writer.WritePacket(Early(ServerPacketId.UserStats), w =>
        {
            w.WriteInt32(stats.UserId);
            if (build.IsFirstEra)
            {
                // A user b337 doesn't know is added only from one with the name, so stats go only with presence.
                w.WriteByte(presence is null ? (byte)0 : (byte)1);
                if (presence is not null)
                {
                    LegacyLayouts.WriteText(w, presence.Username);
                    WriteStatistics(w, stats, build);
                    LegacyLayouts.WriteText(w, LegacyLayouts.AvatarFile(presence.UserId));
                    w.WriteByte(presence.Timezone);
                    LegacyLayouts.WriteText(w, LegacyLayouts.Location(presence.Country));
                }
                WriteStatus(w, stats, build);
                return;
            }

            w.WriteByte((byte)(presence is null ? Completeness.Statistics : Completeness.Full));
            WriteStatus(w, stats, build);
            WriteStatistics(w, stats, build);
            if (presence is null)
                return;
            LegacyLayouts.WriteText(w, presence.Username);
            // The avatar's file name: the build fetches osu.<domain>/forum/download.php?avatar=<it>
            // (the stack's Caddy serves the website's avatar there).
            LegacyLayouts.WriteText(w, LegacyLayouts.AvatarFile(presence.UserId));
            w.WriteByte(presence.Timezone);
            LegacyLayouts.WriteText(w, LegacyLayouts.Location(presence.Country));
            if (build.Has(BanchoFeature.Friends))
                w.WriteByte((byte)(presence.Privileges & build.PrivilegeMask));
            if (build.Has(BanchoFeature.UserUpdateLocation))
                w.WriteSingle(presence.Longitude).WriteSingle(presence.Latitude);
        });
    }

    /// <summary>The status in a user update: b337's has the beatmap part unless it's Unknown (7), later ones say whether it's there.</summary>
    private static void WriteStatus(BanchoPacketWriter w, LegacyLayouts.Stats stats, ClientBuild build)
    {
        var action = LegacyLayouts.Action(stats.Action, build);
        w.WriteByte(action);
        if (build.IsFirstEra)
        {
            if (action == 7)
                return;
        }
        else
        {
            w.WriteByte(1); // the status's beatmap part follows
        }
        LegacyLayouts.WriteText(w, stats.ActionText);
        LegacyLayouts.WriteText(w, stats.BeatmapMd5);
        w.WriteUInt16((ushort)stats.Mods);
        if (build.Has(BanchoFeature.ProtocolVersion))
            w.WriteByte(LegacyLayouts.Mode(stats.Mode, build)).WriteInt32(stats.BeatmapId);
    }

    /// <summary>Ranked score, accuracy, play count, total score and rank (16-bit for b370-b639; 0 when it doesn't fit).</summary>
    private static void WriteStatistics(BanchoPacketWriter w, LegacyLayouts.Stats stats, ClientBuild build)
    {
        w.WriteInt64(stats.RankedScore);
        w.WriteSingle(stats.Accuracy);
        w.WriteInt32(stats.PlayCount);
        w.WriteInt64(stats.TotalScore);
        if (build.Has(BanchoFeature.WideRank) || build.IsFirstEra)
            w.WriteInt32(stats.Rank);
        else
            w.WriteUInt16(stats.Rank is > 0 and <= ushort.MaxValue ? (ushort)stats.Rank : (ushort)0);
    }

    /// <summary>Whether b337 (one chat pane) should get this message: #osu and private messages only.</summary>
    private static bool IsForTheOnePane(byte[] payload)
    {
        try
        {
            var r = new BanchoPacketReader(payload);
            _ = r.ReadString();
            _ = r.ReadString();
            var target = r.ReadString();
            return target == "#osu" || !target.StartsWith('#');
        }
        catch (Exception ex) when (ex is InvalidDataException or OverflowException)
        {
            return false;
        }
    }

    /// <summary>Whether a JOIN_SUCCESS is for room or spectator chat.</summary>
    private static bool IsRoomChat(byte[] payload)
    {
        try
        {
            var name = new BanchoPacketReader(payload).ReadString();
            return name.StartsWith("#multiplayer", StringComparison.Ordinal) || name.StartsWith("#spectator", StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is InvalidDataException or OverflowException)
        {
            return false;
        }
    }

    private static int[]? FriendIds(byte[] payload)
    {
        try
        {
            return new BanchoPacketReader(payload).ReadInt32List();
        }
        catch (Exception ex) when (ex is InvalidDataException or OverflowException)
        {
            return null;
        }
    }

    /// <summary>Calls <paramref name="each"/> for every packet; false when the stream can't be followed.</summary>
    internal static bool Each(byte[] stream, Action<ushort, byte[]> each)
    {
        var offset = 0;
        while (offset + 7 <= stream.Length)
        {
            var id = BinaryPrimitives.ReadUInt16LittleEndian(stream.AsSpan(offset));
            var length = BinaryPrimitives.ReadUInt32LittleEndian(stream.AsSpan(offset + 3));
            if (length > (uint)(stream.Length - offset - 7))
                return false;
            each(id, stream.AsSpan(offset + 7, (int)length).ToArray());
            offset += 7 + (int)length;
        }
        return offset == stream.Length;
    }
}
