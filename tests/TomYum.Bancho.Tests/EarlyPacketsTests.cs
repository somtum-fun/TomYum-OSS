using TomYum.Bancho;
using Xunit;

namespace TomYum.Bancho.Tests;

/// <summary>Bancho for numbered builds (b1122): ids from 11 up one higher, one user update packet.</summary>
public class EarlyPacketsTests
{
    private static List<(ushort Id, byte[] Payload)> Packets(byte[] stream)
    {
        var list = new List<(ushort, byte[])>();
        var r = new BanchoPacketReader(stream);
        while (r.CanReadHeader)
        {
            var id = r.ReadUInt16();
            _ = r.ReadByte();
            var length = (int)r.ReadUInt32();
            var payload = new byte[length];
            for (var i = 0; i < length; i++)
                payload[i] = r.ReadByte();
            list.Add((id, payload));
        }
        return list;
    }

    private static BanchoPresence Peppy() => new()
    {
        UserId = 2,
        Username = "peppy",
        UtcOffset = 7,
        CountryCode = CountryCodes.ToNumeric("th"),
        Privileges = (BanchoPrivileges)5,
        Action = 2,
        ActionInfo = "Artist - Title [Hard]",
        BeatmapMd5 = "abcdef",
        Mods = (Mods)72,
        BeatmapId = 123,
        RankedScore = 1_000_000,
        Accuracy = 0.98f,
        PlayCount = 42,
        TotalScore = 5_000_000,
        GlobalRank = 3,
    };

    [Fact]
    public void PresenceAndStats_BecomeOneFullUserUpdate()
    {
        var w = new BanchoPacketWriter();
        LoginResponseBuilder.WritePresence(w, Peppy());
        LoginResponseBuilder.WriteStats(w, Peppy());

        var packets = Packets(EarlyPackets.Outgoing(w.ToArray()));

        var (id, payload) = Assert.Single(packets);
        Assert.Equal(12, id);
        var r = new BanchoPacketReader(payload);
        Assert.Equal(2, r.ReadInt32());
        Assert.Equal(2, r.ReadByte());           // full
        Assert.Equal(2, r.ReadByte());           // playing
        Assert.Equal(1, r.ReadByte());           // with beatmap
        Assert.Equal("Artist - Title [Hard]", r.ReadString());
        Assert.Equal("abcdef", r.ReadString());
        Assert.Equal(72, r.ReadUInt16());
        Assert.Equal(0, r.ReadByte());
        Assert.Equal(123, r.ReadInt32());
        Assert.Equal(1_000_000, r.ReadInt64());
        Assert.Equal(0.98f, r.ReadSingle());
        Assert.Equal(42, r.ReadInt32());
        Assert.Equal(5_000_000, r.ReadInt64());
        Assert.Equal(3, r.ReadInt32());
        Assert.Equal("peppy", r.ReadString());
        Assert.Equal("2.png", r.ReadString()); // the avatar, served at /forum/download.php
        Assert.Equal(7 + 24, r.ReadByte());
        Assert.Equal("TH", r.ReadString());
        Assert.Equal(5, r.ReadByte());
        Assert.Equal(0, r.Remaining);
    }

    [Fact]
    public void StatsAlone_AreAStatisticsUpdate_AndPresenceAlone_AFullOne()
    {
        var stats = new BanchoPacketWriter();
        LoginResponseBuilder.WriteStats(stats, Peppy());
        var presence = new BanchoPacketWriter();
        LoginResponseBuilder.WritePresence(presence, Peppy());

        var (statsId, statsPayload) = Assert.Single(Packets(EarlyPackets.Outgoing(stats.ToArray())));
        var (presenceId, presencePayload) = Assert.Single(Packets(EarlyPackets.Outgoing(presence.ToArray())));

        Assert.Equal(12, statsId);
        Assert.Equal(1, statsPayload[4]);
        Assert.Equal(12, presenceId);
        Assert.Equal(2, presencePayload[4]);
    }

    [Fact]
    public void IdsFrom11Up_AreOneHigher_AndPacketsItCantRead_AreLeftOut()
    {
        var w = new BanchoPacketWriter()
            .WriteInt32Packet(ServerPacketId.UserId, 2)
            .WriteInt32Packet(ServerPacketId.ProtocolVersion, 19)
            .WritePacket(ServerPacketId.MainMenuIcon, x => x.WriteString(string.Empty))
            .WriteStringPacket(ServerPacketId.ChannelJoinSuccess, "#osu")
            .WriteStringPacket(ServerPacketId.Notification, "hi")
            .WritePacket(ServerPacketId.UserLogout, x => x.WriteInt32(5).WriteByte(0))
            .WritePacket(ServerPacketId.ChannelInfoEnd, null)
            .WriteInt32Packet(ServerPacketId.Privileges, 5)
            .WritePacket(ServerPacketId.FriendsList, x => x.WriteInt32List([4, 9]));

        var packets = Packets(EarlyPackets.Outgoing(w.ToArray()));

        Assert.Equal([5, 65, 25, 13, 72, 73], packets.Select(p => (int)p.Id));
        var friends = new BanchoPacketReader(packets[5].Payload);
        Assert.Equal(2, friends.ReadInt32()); // a 32-bit count
        Assert.Equal(4, friends.ReadInt32());
        Assert.Equal(9, friends.ReadInt32());
    }

    [Fact]
    public void WhatTheBuildSends_ComesInAsTodaysPackets()
    {
        var w = new BanchoPacketWriter()
            .WritePacket((ServerPacketId)ClientPacketId.ChangeAction, x => x
                .WriteByte(2).WriteByte(1).WriteString("Artist - Title").WriteString("abcdef").WriteUInt16(8).WriteByte(1).WriteInt32(123))
            .WritePacket((ServerPacketId)ClientPacketId.SendPublicMessage, x => x.WriteString(null).WriteString("hello").WriteString("#osu"))
            .WritePacket((ServerPacketId)26, x => x.WriteString(null).WriteString("hey").WriteString("peppy"))
            .WriteStringPacket((ServerPacketId)64, "#english")
            .WriteStringPacket((ServerPacketId)21, "an error")
            .WritePacket((ServerPacketId)32, x => x.WriteInt32(1)); // multiplayer: left out

        var packets = Packets(EarlyPackets.Incoming(w.ToArray()));

        Assert.Equal([(int)ClientPacketId.ChangeAction, (int)ClientPacketId.SendPublicMessage, (int)ClientPacketId.SendPrivateMessage,
            (int)ClientPacketId.ChannelJoin, (int)ClientPacketId.ErrorReport], packets.Select(p => (int)p.Id));
        var status = new BanchoPacketReader(packets[0].Payload);
        Assert.Equal(2, status.ReadByte());
        Assert.Equal("Artist - Title", status.ReadString());
        Assert.Equal("abcdef", status.ReadString());
        Assert.Equal(8u, status.ReadUInt32());
        Assert.Equal(1, status.ReadByte());
        Assert.Equal(123, status.ReadInt32());
        var chat = new BanchoPacketReader(packets[1].Payload);
        Assert.Equal(string.Empty, chat.ReadString());
        Assert.Equal("hello", chat.ReadString());
        Assert.Equal("#osu", chat.ReadString());
        Assert.Equal(0, chat.ReadInt32()); // the sender id today's clients add
    }

    [Fact]
    public void AStatusWithoutABeatmap_StillHasTodaysFields()
    {
        var w = new BanchoPacketWriter().WritePacket((ServerPacketId)ClientPacketId.ChangeAction, x => x.WriteByte(1).WriteByte(0));

        var (_, payload) = Assert.Single(Packets(EarlyPackets.Incoming(w.ToArray())));

        var r = new BanchoPacketReader(payload);
        Assert.Equal(1, r.ReadByte());
        Assert.Equal(string.Empty, r.ReadString());
        Assert.Equal(string.Empty, r.ReadString());
        Assert.Equal(0u, r.ReadUInt32());
        Assert.Equal(0, r.ReadByte());
        Assert.Equal(0, r.ReadInt32());
        Assert.Equal(0, r.Remaining);
    }

    [Fact]
    public void EmptyStringsInAUserUpdate_AreWrittenTheBuildsWay()
    {
        var idle = Peppy();
        idle.Action = 0;
        idle.ActionInfo = string.Empty;
        idle.BeatmapMd5 = string.Empty;
        var w = new BanchoPacketWriter();
        LoginResponseBuilder.WriteStats(w, idle);

        var (_, payload) = Assert.Single(Packets(EarlyPackets.Outgoing(w.ToArray())));

        // id, completeness, status, has-beatmap, then the text and md5: 0x0b 0x00 each, never a bare 0x00 (null there).
        Assert.Equal(new byte[] { 0x0b, 0x00, 0x0b, 0x00 }, payload[7..11]);
    }
}
