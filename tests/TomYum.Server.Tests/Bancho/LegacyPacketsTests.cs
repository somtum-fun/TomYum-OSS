using TomYum.Bancho;
using TomYum.Server.Bancho;
using Xunit;

namespace TomYum.Server.Tests.Bancho;

/// <summary>Packets for old builds, after Lekuruu's chio (docs/old-clients.md).</summary>
public class LegacyPacketsTests
{
    private static readonly ClientBuild EightSlots = ClientBuild.Parse("b20140410.1");
    private static readonly ClientBuild SixteenSlots = ClientBuild.Parse("b20140624");
    private static readonly ClientBuild Today = ClientBuild.Parse("b20240123");

    private static Match Room()
    {
        var match = new Match(7, 2, "old vs new", "secret", "Artist - Title [Hard]", 123, "abcdef",
            0, MatchWinCondition.Score, MatchTeamType.HeadToHead, freeMods: true, mods: 0, seed: 42);
        match.Slots[0].UserId = 2;
        match.Slots[0].Status = SlotStatus.Ready;
        match.Slots[0].Mods = 8;
        match.Slots[3].UserId = 5;
        match.Slots[3].Status = SlotStatus.NotReady;
        match.Slots[9].UserId = 9; // only today's clients have slot 10
        match.Slots[9].Status = SlotStatus.NotReady;
        return match;
    }

    private static byte[] Packet(ServerPacketId id, Match match)
    {
        var w = new BanchoPacketWriter(256);
        MatchEncoder.WriteMatchPacket(w, id, match, sendPassword: true);
        return w.ToArray();
    }

    private static byte[] Payload(byte[] framed) => framed[7..];

    [Theory]
    [InlineData("b20140410.1", 8)]
    [InlineData("b20140528", 16)]
    [InlineData("b20140624", 16)]
    [InlineData("b20240123", 16)]
    [InlineData(null, 16)]
    public void SlotCount_FollowsTheBuild(string? version, int slots) =>
        Assert.Equal(slots, ClientBuild.Parse(version).MatchSlots);

    [Fact]
    public void An8SlotBuild_GetsRoomsWith8Slots_AndSends8SlotRoomsBack()
    {
        var sixteen = Packet(ServerPacketId.UpdateMatch, Room());

        var sent = LegacyPackets.Outgoing(sixteen, EightSlots);
        // 8 fewer statuses, 8 fewer teams, one player fewer, 8 fewer free-mod mods.
        Assert.Equal(sixteen.Length - 8 - 8 - 4 - 32, sent.Length);

        // As the old client would send it back (a settings change): 16 slots for the handlers again.
        var back = LegacyPackets.Room(Payload(sent), 8, 16);
        Assert.NotNull(back);
        var copy = new Match(7, 2, string.Empty, string.Empty, string.Empty, 0, string.Empty, 0, MatchWinCondition.Score, MatchTeamType.HeadToHead, false, 0, 0);
        var reader = new BanchoPacketReader(back);
        MatchEncoder.ReadMatchComposite(reader, copy);

        Assert.Equal("old vs new", copy.Name);
        Assert.Equal(SlotStatus.Ready, copy.Slots[0].Status);
        Assert.Equal(8u, copy.Slots[0].Mods);
        Assert.Equal(SlotStatus.NotReady, copy.Slots[3].Status);
        Assert.All(copy.Slots[8..], s => Assert.Equal(SlotStatus.Locked, s.Status));
        Assert.Equal(42, copy.Seed);
    }

    [Fact]
    public void Builds_With16Slots_GetRoomsAsTheyAre()
    {
        var sixteen = Packet(ServerPacketId.NewMatch, Room());

        Assert.Equal(sixteen, LegacyPackets.Outgoing(sixteen, SixteenSlots));
        Assert.Same(sixteen, LegacyPackets.Outgoing(sixteen, Today));
        Assert.Same(sixteen, LegacyPackets.Incoming(sixteen, SixteenSlots));
    }

    [Theory]
    [InlineData(ServerPacketId.AccountRestricted, "b20140715", false)]
    [InlineData(ServerPacketId.AccountRestricted, "b20140716", true)]
    [InlineData(ServerPacketId.MatchAbort, "b20141103", false)]
    [InlineData(ServerPacketId.MatchAbort, "b20141104", true)]
    [InlineData(ServerPacketId.Notification, "b20131020", true)]
    public void PacketsABuildDoesntKnow_AreLeftOut(ServerPacketId id, string version, bool sent)
    {
        var w = new BanchoPacketWriter(64);
        w.WriteStringPacket(ServerPacketId.Notification, "before");
        w.WritePacket(id, null);
        w.WriteStringPacket(ServerPacketId.Notification, "after");
        var stream = w.ToArray();

        var result = LegacyPackets.Outgoing(stream, ClientBuild.Parse(version));

        Assert.Equal(sent, result.Length == stream.Length);
    }

    [Fact]
    public void ARoomThatDoesntParse_IsPassedOn()
    {
        Assert.Null(LegacyPackets.Room([1, 2, 3], 16, 8));
        var w = new BanchoPacketWriter(32);
        w.WritePacket(ServerPacketId.UpdateMatch, p => p.WriteRaw([1, 2, 3]));
        var stream = w.ToArray();
        Assert.Equal(stream, LegacyPackets.Outgoing(stream, EightSlots));
    }
}
