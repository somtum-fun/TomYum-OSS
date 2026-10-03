using System.Buffers.Binary;

namespace TomYum.Bancho;

/// <summary>
/// Bancho packets for old builds (docs/old-clients.md), following Lekuruu's per-build protocol notes
/// (chio): packets a build doesn't know yet are left out of what it's sent, and builds before
/// b20140528 get multiplayer rooms with 8 slots (and send them), where today's have 16. Today's
/// clients' traffic is passed through as it is.
/// </summary>
public static class LegacyPackets
{
    /// <summary>The room is the whole payload of these server packets.</summary>
    private static readonly HashSet<ushort> ServerMatchPackets =
    [
        (ushort)ServerPacketId.UpdateMatch, (ushort)ServerPacketId.NewMatch,
        (ushort)ServerPacketId.MatchJoinSuccess, (ushort)ServerPacketId.MatchStart,
    ];

    /// <summary>...and of these client packets.</summary>
    private static readonly HashSet<ushort> ClientMatchPackets =
    [
        (ushort)ClientPacketId.CreateMatch, (ushort)ClientPacketId.MatchChangeSettings,
        (ushort)ClientPacketId.MatchChangePassword,
    ];

    /// <summary>What <paramref name="build"/> is sent of <paramref name="stream"/> (framed server packets).</summary>
    public static byte[] Outgoing(byte[] stream, ClientBuild build)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(build);
        if (!build.IsLegacy || stream.Length == 0)
            return stream;
        return Rewrite(stream, (id, payload, writer) =>
        {
            if (!build.Knows((ServerPacketId)id))
                return; // left out
            if (build.MatchSlots != 16 && SlotOf(id, payload) is { } slot && slot >= build.MatchSlots)
                return; // a slot this build doesn't have
            switch ((ServerPacketId)id)
            {
                case ServerPacketId.MainMenuIcon when payload is [0]:
                    // "image|url" with neither: old builds split it on the pipe (chio sends "|").
                    writer.WritePacket(ServerPacketId.MainMenuIcon, w => w.WriteString("|"));
                    return;
                case ServerPacketId.ChannelJoinSuccess when ChannelName(payload) is { } name
                    && (name.StartsWith("#multiplayer", StringComparison.Ordinal) || name.StartsWith("#spectator", StringComparison.Ordinal)):
                    // Room and spectator chat: old builds open the tab from "auto-join", then "joined" (as osuTitanic sends them).
                    ChatEncoder.WriteChannelAutoJoin(writer, name, string.Empty, 1);
                    break;
            }
            var converted = build.MatchSlots != 16 && ServerMatchPackets.Contains(id)
                ? Room(payload, 16, build.MatchSlots)
                : null;
            writer.WritePacket((ServerPacketId)id, w => w.WriteRaw(converted ?? payload));
        });
    }

    /// <summary>What <paramref name="build"/> sent (framed client packets), in today's layout for the handlers.</summary>
    public static byte[] Incoming(byte[] stream, ClientBuild build)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(build);
        if (build.MatchSlots == 16 || stream.Length == 0)
            return stream;
        return Rewrite(stream, (id, payload, writer) =>
        {
            var converted = ClientMatchPackets.Contains(id) ? Room(payload, build.MatchSlots, 16) : null;
            writer.WritePacket((ServerPacketId)id, w => w.WriteRaw(converted ?? payload));
        });
    }

    /// <summary>
    /// A room (the match composite) with <paramref name="from"/> slots rewritten to <paramref name="to"/>:
    /// slots past the end are dropped, or added locked. Null when the payload isn't a room in that layout
    /// (then it's passed on as it is).
    /// </summary>
    public static byte[]? Room(ReadOnlySpan<byte> payload, int from, int to)
    {
        try
        {
            var r = new BanchoPacketReader(payload);
            var w = new BanchoPacketWriter(payload.Length + 64);
            w.WriteUInt16(r.ReadUInt16());   // match id
            w.WriteByte(r.ReadByte());       // in progress
            w.WriteByte(r.ReadByte());       // match type
            w.WriteUInt32(r.ReadUInt32());   // mods
            w.WriteString(r.ReadString());   // name
            w.WriteString(r.ReadString());   // password
            w.WriteString(r.ReadString());   // beatmap name
            w.WriteInt32(r.ReadInt32());     // beatmap id
            w.WriteString(r.ReadString());   // beatmap checksum

            var statuses = new byte[from];
            var teams = new byte[from];
            var players = new int[from];
            for (var i = 0; i < from; i++) statuses[i] = r.ReadByte();
            for (var i = 0; i < from; i++) teams[i] = r.ReadByte();
            for (var i = 0; i < from; i++)
                if (((SlotStatus)statuses[i] & SlotStatus.HasPlayer) != 0)
                    players[i] = r.ReadInt32();
            var host = r.ReadInt32();
            var mode = r.ReadByte();
            var scoring = r.ReadByte();
            var teamType = r.ReadByte();
            var freeMods = r.ReadByte();
            var slotMods = new uint[from];
            if (freeMods != 0)
                for (var i = 0; i < from; i++) slotMods[i] = r.ReadUInt32();
            var seed = r.ReadInt32();
            if (r.Remaining != 0)
                return null;

            for (var i = 0; i < to; i++) w.WriteByte(i < from ? statuses[i] : (byte)SlotStatus.Locked);
            for (var i = 0; i < to; i++) w.WriteByte(i < from ? teams[i] : (byte)0);
            for (var i = 0; i < Math.Min(from, to); i++)
                if (((SlotStatus)statuses[i] & SlotStatus.HasPlayer) != 0)
                    w.WriteInt32(players[i]);
            w.WriteInt32(host);
            w.WriteByte(mode);
            w.WriteByte(scoring);
            w.WriteByte(teamType);
            w.WriteByte(freeMods);
            if (freeMods != 0)
                for (var i = 0; i < to; i++) w.WriteUInt32(i < from ? slotMods[i] : 0);
            w.WriteInt32(seed);
            return w.ToArray();
        }
        catch (InvalidDataException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    /// <summary>The match slot a server packet is about, for the ones that name one (score frames, fails, skips).</summary>
    private static int? SlotOf(ushort id, byte[] payload) => (ServerPacketId)id switch
    {
        ServerPacketId.MatchScoreUpdate when payload.Length > 4 => payload[4], // the score frame's id byte
        ServerPacketId.MatchPlayerFailed or ServerPacketId.MatchPlayerSkipped when payload.Length >= 4
            => BinaryPrimitives.ReadInt32LittleEndian(payload),
        _ => null,
    };

    /// <summary>The channel name in a JOIN_SUCCESS payload, or null.</summary>
    private static string? ChannelName(byte[] payload)
    {
        try
        {
            return new BanchoPacketReader(payload).ReadString();
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private delegate void PacketRewriter(ushort id, byte[] payload, BanchoPacketWriter writer);

    private static byte[] Rewrite(byte[] stream, PacketRewriter each)
    {
        var writer = new BanchoPacketWriter(stream.Length + 64);
        var offset = 0;
        while (offset + 7 <= stream.Length)
        {
            var id = BinaryPrimitives.ReadUInt16LittleEndian(stream.AsSpan(offset));
            var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(stream.AsSpan(offset + 3));
            if (length < 0 || offset + 7 + length > stream.Length)
                return stream; // not a packet stream we can follow: unchanged
            each(id, stream.AsSpan(offset + 7, length).ToArray(), writer);
            offset += 7 + length;
        }
        return offset == stream.Length ? writer.ToArray() : stream;
    }
}
