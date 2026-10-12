using System.Buffers.Binary;
using System.IO.Compression;

namespace TomYum.Bancho;

/// <summary>
/// Bancho packets for old builds (docs/old-clients.md), following Lekuruu's per-build protocol notes
/// (chio): packets a build doesn't know yet are left out of what it's sent, and builds before
/// b20140528 get multiplayer rooms with 8 slots (and send them), where today's have 16. Builds from
/// b1807 to b20130513 (read from their decompiled clients) also have older layouts in the middle of
/// some packets (<see cref="BanchoFeature"/>), and numbered builds before b1807 speak an older
/// protocol still: <see cref="EarlyPackets"/>. Today's clients' traffic is passed through as it is.
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
        if (build.HasEarlyPackets)
            return EarlyPackets.Outgoing(stream, build);
        return Rewrite(stream, (id, payload, writer) =>
        {
            if (!build.Knows((ServerPacketId)id))
                return; // left out
            if (build.MatchSlots != 16 && SlotOf(id, payload) is { } slot && slot >= build.MatchSlots)
                return; // a slot this build doesn't have
            if (Older((ServerPacketId)id, payload, build) is { } older)
            {
                // A layout that changed before b20130513 (docs/old-clients.md), or left out (empty).
                if (older.Length != 0)
                    writer.WritePacket((ServerPacketId)id, w => w.WriteRaw(older));
                return;
            }
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

    /// <summary>
    /// A server packet in the layout of a build with today's ids from before b20130513 (b1807 on),
    /// where it differs: null when it's today's, empty when the build is better off without it.
    /// (The login reply's refusals are the ones the build knows, up to b20151026.3.)
    /// </summary>
    private static byte[]? Older(ServerPacketId id, byte[] payload, ClientBuild build)
    {
        if (id is ServerPacketId.UserId && payload.Length >= 4)
            return new BanchoPacketWriter(4).WriteInt32(build.LoginCode(BinaryPrimitives.ReadInt32LittleEndian(payload))).ToArray();
        if (build.Has(BanchoFeature.FrameBundleExtra))
            return null; // today's layouts from b20130513, apart from what Outgoing does for every old build
        switch (id)
        {
            case ServerPacketId.UserStats when !build.Has(BanchoFeature.Mania):
                // 16-bit mods before b20120916, no pp before b20120812, no osu!mania before b20121003.
                return LegacyLayouts.Stats.Read(payload) is { } stats ? LegacyLayouts.StatsFor(stats, build) : [];
            case ServerPacketId.UserPresence when !build.Has(BanchoFeature.TodaysPresence):
                return LegacyLayouts.Presence.Read(payload) is { } presence ? LegacyLayouts.PresenceFor(presence, build) : [];
            case ServerPacketId.SpectateFrames:
                return LegacyLayouts.FramesOut(payload, build);
            case ServerPacketId.UpdateMatch or ServerPacketId.NewMatch or ServerPacketId.MatchJoinSuccess or ServerPacketId.MatchStart:
                return LegacyRoom.ReadToday(payload, 16)?.WriteFor(build) ?? [];
            case ServerPacketId.BeatmapInfoReply when !build.Has(BanchoFeature.FourGrades):
                return LegacyLayouts.BeatmapInfo(payload, build) ?? [];
            case ServerPacketId.Privileges when payload.Length >= 4 && build.PrivilegeMask != ~0:
                return new BanchoPacketWriter(4).WriteInt32(BinaryPrimitives.ReadInt32LittleEndian(payload) & build.PrivilegeMask).ToArray();
            case ServerPacketId.MainMenuIcon when !build.Has(BanchoFeature.MenuIconLink):
                // No payload before b20120916 (it re-downloads the title image: left out); then an image URL only.
                return !build.Has(BanchoFeature.WideMods) ? [] : MenuImage(payload);
            default:
                return null;
        }
    }

    /// <summary>The image part of today's "image|url" menu icon, as b20120916-b20121203 read it (none: 0x00).</summary>
    private static byte[] MenuImage(byte[] payload)
    {
        try
        {
            var icon = new BanchoPacketReader(payload).ReadString();
            var image = icon.Split('|')[0];
            return new BanchoPacketWriter(image.Length + 8).WriteString(image).ToArray();
        }
        catch (InvalidDataException)
        {
            return [0];
        }
    }

    /// <summary>What <paramref name="build"/> sent (framed client packets), in today's layout for the handlers.</summary>
    public static byte[] Incoming(byte[] stream, ClientBuild build)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(build);
        if (build.HasEarlyPackets)
            return EarlyPackets.Incoming(stream, build);
        if (build.MatchSlots == 16 || stream.Length == 0)
            return stream;
        return Rewrite(stream, (id, payload, writer) =>
        {
            byte[]? converted;
            if (!build.Has(BanchoFeature.FrameBundleExtra))
            {
                // Builds with today's ids from before b20130513 (b1807 on): older layouts.
                converted = (ClientPacketId)id switch
                {
                    ClientPacketId.ChangeAction when !build.Has(BanchoFeature.WideMods) => LegacyLayouts.Status(payload, build, out _) ?? [],
                    ClientPacketId.SendPublicMessage or ClientPacketId.SendPrivateMessage or ClientPacketId.SetAwayMessage
                        => LegacyLayouts.MessageIn(payload, build),
                    ClientPacketId.SpectateFrames => LegacyLayouts.FramesIn(payload, build),
                    ClientPacketId.CreateMatch or ClientPacketId.MatchChangeSettings or ClientPacketId.MatchChangePassword
                        => LegacyRoom.ReadFrom(payload, build)?.WriteToday(16) ?? [],
                    _ => null,
                };
                if (converted is [])
                    return; // not in the build's layout either: left out
            }
            else
            {
                converted = ClientMatchPackets.Contains(id) ? Room(payload, build.MatchSlots, 16) : null;
            }
            writer.WritePacket((ServerPacketId)id, w => w.WriteRaw(converted ?? payload));
        });
    }

    /// <summary>
    /// The most a request's gzipped payloads may unpack to, all together. A build's own packets are a
    /// few kilobytes at most; more than this is a gzip bomb, and the request is refused.
    /// </summary>
    public const int MaxUnpackedBytes = 2 * 1024 * 1024;

    /// <summary>
    /// <paramref name="stream"/> (framed client packets) with every gzip-compressed payload (header byte 2
    /// set) unpacked: numbered builds before b1807 compress what they send when it's 150 bytes or more
    /// (<see cref="ClientBuild.SendsCompressedPackets"/>); other builds' streams are passed as they are.
    /// The stream itself when nothing is compressed; a payload that doesn't unpack is left out. Null when
    /// the payloads unpack to more than <see cref="MaxUnpackedBytes"/> in all (the request is refused).
    /// </summary>
    public static byte[]? Decompress(byte[] stream, ClientBuild build)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(build);
        if (!build.SendsCompressedPackets)
            return stream;
        long offset = 0;
        var compressed = false;
        while (offset + 7 <= stream.Length)
        {
            compressed |= stream[offset + 2] != 0;
            offset += 7L + BinaryPrimitives.ReadUInt32LittleEndian(stream.AsSpan((int)offset + 3));
        }
        if (!compressed || offset != stream.Length)
            return stream;

        var writer = new BanchoPacketWriter(stream.Length * 2);
        var budget = MaxUnpackedBytes;
        var at = 0;
        while (at + 7 <= stream.Length)
        {
            var id = BinaryPrimitives.ReadUInt16LittleEndian(stream.AsSpan(at));
            var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(stream.AsSpan(at + 3));
            var payload = stream.AsSpan(at + 7, length).ToArray();
            var gzipped = stream[at + 2] != 0;
            at += 7 + length;
            if (!gzipped)
            {
                writer.WritePacket((ServerPacketId)id, w => w.WriteRaw(payload));
                continue;
            }
            switch (Gunzip(payload, budget))
            {
                case { Unpacked: { } unpacked }:
                    budget -= unpacked.Length;
                    writer.WritePacket((ServerPacketId)id, w => w.WriteRaw(unpacked));
                    break;
                case { TooLarge: true }:
                    return null;
            }
        }
        return writer.ToArray();
    }

    /// <summary>
    /// A gzip stream's content; Unpacked is null when it isn't one, or when it unpacks to more than
    /// <paramref name="budget"/> bytes (then TooLarge is set).
    /// </summary>
    private static (byte[]? Unpacked, bool TooLarge) Gunzip(byte[] payload, int budget)
    {
        try
        {
            using var input = new MemoryStream(payload);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            var buffer = new byte[16 * 1024];
            int read;
            while ((read = gzip.Read(buffer, 0, buffer.Length)) > 0)
            {
                output.Write(buffer, 0, read);
                if (output.Length > budget)
                    return (null, true);
            }
            return (output.ToArray(), false);
        }
        catch (InvalidDataException)
        {
            return (null, false);
        }
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
