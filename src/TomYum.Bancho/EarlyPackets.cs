using System.Buffers.Binary;

namespace TomYum.Bancho;

/// <summary>
/// Bancho for numbered builds (<see cref="ClientBuild.HasEarlyPackets"/>; read from the decompiled
/// b1122, example-org/private-repo). That protocol still had a packet at 11 that was later removed, so
/// every id from 11 up is one higher than today's, in both directions. A user's status, stats and
/// presence are one packet (12) instead of today's stats (11) and presence (83). Everything else
/// these builds can read is laid out as today. What they can't (multiplayer, spectator frames,
/// packets they don't know) is left out, since a packet they misread ends the connection.
/// </summary>
public static class EarlyPackets
{
    /// <summary>Server packets sent with their id one higher and the payload as it is.</summary>
    private static readonly HashSet<ServerPacketId> Shifted =
    [
        ServerPacketId.SpectatorJoined, ServerPacketId.SpectatorLeft, ServerPacketId.SpectatorCantSpectate,
        ServerPacketId.GetAttention, ServerPacketId.Notification,
        ServerPacketId.FellowSpectatorJoined, ServerPacketId.FellowSpectatorLeft,
        ServerPacketId.ChannelJoinSuccess, ServerPacketId.ChannelInfo, ServerPacketId.ChannelKick, ServerPacketId.ChannelAutoJoin,
    ];

    /// <summary>Client packets (by the build's id) passed on with their id one lower and the payload as it is.</summary>
    private static readonly HashSet<ushort> ShiftedIncoming =
    [
        (ushort)ClientPacketId.StartSpectating + 1, (ushort)ClientPacketId.StopSpectating + 1,
        (ushort)ClientPacketId.ErrorReport + 1, (ushort)ClientPacketId.CantSpectate + 1,
        (ushort)ClientPacketId.ChannelJoin + 1, (ushort)ClientPacketId.ChannelPart + 1,
        (ushort)ClientPacketId.FriendAdd + 1, (ushort)ClientPacketId.FriendRemove + 1,
    ];

    /// <summary>The build's user update (12): how much of it there is.</summary>
    private enum Completeness : byte
    {
        Status = 0,
        Statistics = 1,
        Full = 2,
    }

    /// <summary>What a numbered build is sent of <paramref name="stream"/> (today's framed server packets).</summary>
    public static byte[] Outgoing(byte[] stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (stream.Length == 0)
            return stream;

        var writer = new BanchoPacketWriter(stream.Length + 64);
        // Presence waits for the same user's stats in this batch, to go out as one full update.
        var presences = new Dictionary<int, Presence>();
        if (!Each(stream, (id, payload) =>
        {
            switch ((ServerPacketId)id)
            {
                case ServerPacketId.UserId or ServerPacketId.SendMessage or ServerPacketId.Pong
                    or ServerPacketId.HandleIrcChangeUsername or ServerPacketId.HandleIrcQuit:
                    writer.WritePacket((ServerPacketId)id, w => w.WriteRaw(payload));
                    break;
                case ServerPacketId.UserPresence:
                    if (Presence.Read(payload) is { } presence)
                        presences[presence.UserId] = presence;
                    break;
                case ServerPacketId.UserStats:
                    if (Stats.Read(payload) is { } stats)
                    {
                        presences.Remove(stats.UserId, out var known);
                        WriteUpdate(writer, stats, known);
                    }
                    break;
                case ServerPacketId.UserLogout:
                    writer.WritePacket(Early(ServerPacketId.UserLogout), w => w.WriteRaw(payload));
                    break;
                case ServerPacketId.Privileges when payload.Length >= 4:
                    // The build knows normal, BAT and supporter (1, 2, 4).
                    writer.WritePacket(Early(ServerPacketId.Privileges), w => w.WriteInt32(BinaryPrimitives.ReadInt32LittleEndian(payload) & 0x7));
                    break;
                case ServerPacketId.FriendsList when FriendIds(payload) is { } friends:
                    // A 32-bit count here; today's is 16-bit.
                    writer.WritePacket(Early(ServerPacketId.FriendsList), w =>
                    {
                        w.WriteInt32(friends.Length);
                        foreach (var friend in friends)
                            w.WriteInt32(friend);
                    });
                    break;
                case var known when Shifted.Contains(known):
                    writer.WritePacket(Early(known), w => w.WriteRaw(payload));
                    break;
                    // Anything else is left out.
            }
        }))
        {
            return stream;
        }

        foreach (var presence in presences.Values)
            WriteUpdate(writer, new Stats(presence.UserId, 0, string.Empty, string.Empty, 0, presence.Mode, 0, 0, 0, 0, 0, presence.Rank), presence);
        return writer.ToArray();
    }

    /// <summary>What a numbered build sent (framed client packets), as today's client would send it.</summary>
    public static byte[] Incoming(byte[] stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (stream.Length == 0)
            return stream;

        var writer = new BanchoPacketWriter(stream.Length + 64);
        if (!Each(stream, (id, payload) =>
        {
            switch (id)
            {
                case (ushort)ClientPacketId.ChangeAction when Status(payload) is { } status:
                    writer.WritePacket((ServerPacketId)ClientPacketId.ChangeAction, w => w.WriteRaw(status));
                    break;
                case (ushort)ClientPacketId.SendPublicMessage:
                case (ushort)ClientPacketId.SendPrivateMessage + 1:
                    // Sender, text and target; today's add the sender's id.
                    writer.WritePacket((ServerPacketId)(id == (ushort)ClientPacketId.SendPublicMessage ? ClientPacketId.SendPublicMessage : ClientPacketId.SendPrivateMessage),
                        w => w.WriteRaw(payload).WriteInt32(0));
                    break;
                case (ushort)ClientPacketId.Logout or (ushort)ClientPacketId.RequestStatusUpdate or (ushort)ClientPacketId.Ping:
                    writer.WritePacket((ServerPacketId)id, w => w.WriteRaw(payload));
                    break;
                case var shifted when ShiftedIncoming.Contains(shifted):
                    writer.WritePacket((ServerPacketId)(shifted - 1), w => w.WriteRaw(payload));
                    break;
                    // Multiplayer, spectator frames and beatmap info requests are left out.
            }
        }))
        {
            return stream;
        }
        return writer.ToArray();
    }

    private static ServerPacketId Early(ServerPacketId id) => (ServerPacketId)((int)id + 1);

    /// <summary>The build's user update (12): status and stats, and presence when known.</summary>
    private static void WriteUpdate(BanchoPacketWriter writer, Stats stats, Presence? presence)
    {
        writer.WritePacket(Early(ServerPacketId.UserStats), w =>
        {
            w.WriteInt32(stats.UserId);
            w.WriteByte((byte)(presence is null ? Completeness.Statistics : Completeness.Full));
            w.WriteByte(stats.Action);
            w.WriteByte(1); // the status's beatmap part follows
            WriteText(w, stats.ActionText);
            WriteText(w, stats.BeatmapMd5);
            w.WriteUInt16((ushort)stats.Mods);
            w.WriteByte(stats.Mode);
            w.WriteInt32(stats.BeatmapId);
            w.WriteInt64(stats.RankedScore);
            w.WriteSingle(stats.Accuracy);
            w.WriteInt32(stats.PlayCount);
            w.WriteInt64(stats.TotalScore);
            w.WriteInt32(stats.Rank);
            if (presence is null)
                return;
            WriteText(w, presence.Username);
            // The avatar's file name: the build fetches osu.<domain>/forum/download.php?avatar=<it>
            // (the stack's Caddy serves the website's avatar there).
            WriteText(w, string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{presence.UserId}.png"));
            w.WriteByte(presence.Timezone);
            WriteText(w, CountryCodes.ToIso(presence.Country) is { Length: > 0 } location ? location : "XX");
            w.WriteByte((byte)(presence.Privileges & 0x7));
        });
    }

    /// <summary>
    /// A string as these builds write one: an empty string is <c>0x0b 0x00</c>. A bare <c>0x00</c>
    /// (how an empty string is written today) reads as null there, which they don't expect.
    /// </summary>
    private static void WriteText(BanchoPacketWriter w, string text)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        w.WriteByte(0x0b).WriteUleb128((uint)bytes.Length).WriteRaw(bytes);
    }

    /// <summary>The build's status (byte, has-beatmap flag, then text, md5, 16-bit mods, mode, beatmap id) as today's.</summary>
    private static byte[]? Status(byte[] payload)
    {
        try
        {
            var r = new BanchoPacketReader(payload);
            var action = r.ReadByte();
            var hasBeatmap = r.ReadByte() != 0;
            var w = new BanchoPacketWriter(payload.Length + 8);
            w.WriteByte(action);
            if (hasBeatmap)
            {
                w.WriteString(r.ReadString());
                w.WriteString(r.ReadString());
                w.WriteUInt32(r.ReadUInt16());
                w.WriteByte(r.ReadByte());
                w.WriteInt32(r.ReadInt32());
            }
            else
            {
                w.WriteString(string.Empty).WriteString(string.Empty).WriteUInt32(0).WriteByte(0).WriteInt32(0);
            }
            return w.ToArray();
        }
        catch (Exception ex) when (ex is InvalidDataException or OverflowException)
        {
            return null;
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
    private static bool Each(byte[] stream, Action<ushort, byte[]> each)
    {
        var offset = 0;
        while (offset + 7 <= stream.Length)
        {
            var id = BinaryPrimitives.ReadUInt16LittleEndian(stream.AsSpan(offset));
            var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(stream.AsSpan(offset + 3));
            if (length < 0 || offset + 7 + length > stream.Length)
                return false;
            each(id, stream.AsSpan(offset + 7, length).ToArray());
            offset += 7 + length;
        }
        return offset == stream.Length;
    }

    /// <summary>Today's USER_PRESENCE (83).</summary>
    private sealed record Presence(int UserId, string Username, byte Timezone, byte Country, int Privileges, byte Mode, int Rank)
    {
        public static Presence? Read(byte[] payload)
        {
            try
            {
                var r = new BanchoPacketReader(payload);
                var userId = r.ReadInt32();
                var name = r.ReadString();
                var timezone = r.ReadByte();
                var country = r.ReadByte();
                var privilegesAndMode = r.ReadByte();
                _ = r.ReadSingle();
                _ = r.ReadSingle();
                var rank = r.ReadInt32();
                return new Presence(userId, name, timezone, country, privilegesAndMode & 0x1F, (byte)(privilegesAndMode >> 5), rank);
            }
            catch (Exception ex) when (ex is InvalidDataException or OverflowException)
            {
                return null;
            }
        }
    }

    /// <summary>Today's USER_STATS (11).</summary>
    private sealed record Stats(int UserId, byte Action, string ActionText, string BeatmapMd5, uint Mods, byte Mode, int BeatmapId,
        long RankedScore, float Accuracy, int PlayCount, long TotalScore, int Rank)
    {
        public static Stats? Read(byte[] payload)
        {
            try
            {
                var r = new BanchoPacketReader(payload);
                return new Stats(r.ReadInt32(), r.ReadByte(), r.ReadString(), r.ReadString(), r.ReadUInt32(), r.ReadByte(), r.ReadInt32(),
                    r.ReadInt64(), r.ReadSingle(), r.ReadInt32(), r.ReadInt64(), r.ReadInt32());
            }
            catch (Exception ex) when (ex is InvalidDataException or OverflowException)
            {
                return null;
            }
        }
    }
}
