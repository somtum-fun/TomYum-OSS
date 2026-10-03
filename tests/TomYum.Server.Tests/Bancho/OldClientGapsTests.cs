using TomYum.Bancho;
using TomYum.Server.Bancho;
using TomYum.Server.Bancho.Packets;
using Xunit;

namespace TomYum.Server.Tests.Bancho;

/// <summary>What old builds ask for and get beyond today's protocol (docs/old-clients.md; Lekuruu's chio, osuTitanic).</summary>
public class OldClientGapsTests
{
    private static readonly ClientBuild EightSlots = ClientBuild.Parse("b20140410.1");
    private static readonly ClientBuild Old = ClientBuild.Parse("b20140624");

    [Fact]
    public void BeatmapInfoRequests_AreRead_ByNameAndById()
    {
        var w = new BanchoPacketWriter(128);
        w.WriteInt32(2);
        w.WriteString("Artist - Title (Mapper) [Hard].osu");
        w.WriteString("Other.osu");
        w.WriteInt32(1);
        w.WriteInt32(123);

        var (filenames, ids) = BeatmapInfoRequestHandler.Read(w.ToArray())!.Value;

        Assert.Equal(["Artist - Title (Mapper) [Hard].osu", "Other.osu"], filenames);
        Assert.Equal([123], ids);
        // A request with names only (as b20140624 sends it) is fine; garbage isn't a request.
        Assert.NotNull(BeatmapInfoRequestHandler.Read([0, 0, 0, 0]));
        Assert.Null(BeatmapInfoRequestHandler.Read([5, 0, 0, 0, 0x0b]));
    }

    [Theory]
    // graveyard, WIP, pending: pending
    [InlineData(-2, "b20140624", 0)]
    [InlineData(-1, "b20140624", 0)]
    [InlineData(0, "b20140624", 0)]
    [InlineData(1, "b20140624", 1)]
    [InlineData(2, "b20140624", 2)]
    // qualified from b20150826, loved from b20161101; approved before
    [InlineData(3, "b20140624", 2)]
    [InlineData(3, "b20150826", 3)]
    [InlineData(4, "b20150826", 2)]
    [InlineData(4, "b20161101", 4)]
    public void TheMapsStatus_IsTheBuilds(int approved, string version, int expected) =>
        Assert.Equal(expected, BeatmapInfoRequestHandler.Status(approved, ClientBuild.Parse(version)));

    [Theory]
    [InlineData("XH", 0)]
    [InlineData("SH", 1)]
    [InlineData("X", 2)]
    [InlineData("S", 3)]
    [InlineData("A", 4)]
    [InlineData("D", 7)]
    [InlineData("F", 8)]
    [InlineData(null, 9)]
    public void Grades_AreNumberedAsTheClientReadsThem(string? grade, int expected) =>
        Assert.Equal(expected, BeatmapInfoRequestHandler.Grade(grade));

    [Fact]
    public void An8SlotBuild_GetsNothingAboutSlotsItDoesntHave()
    {
        var w = new BanchoPacketWriter(128);
        var frame = new byte[29];
        frame[4] = 2; // slot 3
        w.WritePacket(ServerPacketId.MatchScoreUpdate, p => p.WriteRaw(frame));
        frame[4] = 11; // slot 12
        w.WritePacket(ServerPacketId.MatchScoreUpdate, p => p.WriteRaw(frame));
        w.WriteInt32Packet(ServerPacketId.MatchPlayerFailed, 9);
        w.WriteInt32Packet(ServerPacketId.MatchPlayerSkipped, 1);
        var stream = w.ToArray();

        var sent = LegacyPackets.Outgoing(stream, EightSlots);
        // The slot-3 score and the slot-2 skip remain; slot 12's score and slot 10's fail don't.
        Assert.Equal((7 + 29) + (7 + 4), sent.Length);
        // A 16-slot build gets everything.
        Assert.Equal(stream, LegacyPackets.Outgoing(stream, Old));
    }

    [Fact]
    public void OldBuilds_GetAMenuIconTheyCanSplit_AndRoomChatTabs()
    {
        var w = new BanchoPacketWriter(64);
        w.WriteStringPacket(ServerPacketId.MainMenuIcon, string.Empty);
        ChatEncoder.WriteChannelJoinSuccess(w, "#multiplayer");
        ChatEncoder.WriteChannelJoinSuccess(w, "#osu");

        var sent = LegacyPackets.Outgoing(w.ToArray(), Old);

        var ids = new List<ushort>();
        for (var offset = 0; offset + 7 <= sent.Length; offset += 7 + BitConverter.ToInt32(sent, offset + 3))
            ids.Add(BitConverter.ToUInt16(sent, offset));
        Assert.Equal([(ushort)ServerPacketId.MainMenuIcon, (ushort)ServerPacketId.ChannelAutoJoin, (ushort)ServerPacketId.ChannelJoinSuccess, (ushort)ServerPacketId.ChannelJoinSuccess], ids);
        Assert.Equal("|", new BanchoPacketReader(sent.AsSpan(7, 3)).ReadString());
    }

    [Fact]
    public void OldBuilds_JoinRooms_InSlotsTheyHave()
    {
        var match = new Match(1, 2, "room", string.Empty, "map", 1, "md5", 0, MatchWinCondition.Score, MatchTeamType.HeadToHead, false, 0, 0);
        for (var i = 0; i < 8; i++)
            match.Slots[i].Join(100 + i);

        Assert.Equal(-1, match.FirstOpenSlot(EightSlots.MatchSlots));
        Assert.Equal(8, match.FirstOpenSlot(Old.MatchSlots));
    }
}
