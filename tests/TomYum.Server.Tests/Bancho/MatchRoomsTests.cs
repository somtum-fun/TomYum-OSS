using Microsoft.Extensions.DependencyInjection;
using TomYum.Bancho;
using TomYum.Server.Bancho;
using TomYum.Server.Bancho.Packets;
using TomYum.Server.Bancho.Packets.Match;
using Xunit;

namespace TomYum.Server.Tests.Bancho;

/// <summary>Making and joining rooms: one room per player, ids that never land on an open room, and no room floods.</summary>
public class MatchRoomsTests
{
    private static ClientPacketContext Context(OnlinePlayer player, PlayerRegistry players, MatchRegistry matches, byte[] payload, BanchoPacketWriter response) =>
        new(player, players, new ChannelRegistry(), matches, new ServiceCollection().BuildServiceProvider(), payload, response);

    /// <summary>A CREATE_MATCH payload: a room as the client sends it.</summary>
    private static byte[] NewRoom(string name)
    {
        var template = new MatchRegistry().Create(2, name, string.Empty, "map", 1, "md5", 0, default, default, false, 0, 0);
        return MatchBroadcast.EncodeMatch(ServerPacketId.UpdateMatch, template, sendPassword: true)[PacketHeader.Size..];
    }

    private static byte[] Join(int id) => new BanchoPacketWriter(16).WriteInt32(id).WriteString(string.Empty).ToArray();

    [Fact]
    public void Ids_SkipZeroAndOpenRooms_WhenTheyWrapAround()
    {
        var matches = new MatchRegistry();
        var open = matches.Create(2, "stays open", string.Empty, "map", 1, "md5", 0, default, default, false, 0, 0);
        for (var id = open.Id + 1; id <= ushort.MaxValue; id++)
            matches.Remove(matches.Create(3, "gone", string.Empty, "map", 1, "md5", 0, default, default, false, 0, 0).Id);

        var first = matches.Create(4, "after the wrap", string.Empty, "map", 1, "md5", 0, default, default, false, 0, 0);
        var second = matches.Create(5, "and the next", string.Empty, "map", 1, "md5", 0, default, default, false, 0, 0);

        Assert.Equal(1, first.Id);
        Assert.Equal(open.Id + 1, second.Id);
        Assert.Same(open, matches.Get(open.Id));
    }

    [Fact]
    public async Task MakingARoom_LeavesTheOneThePlayerIsIn()
    {
        var players = new PlayerRegistry();
        var matches = new MatchRegistry();
        var player = players.Add(new BanchoPresence { UserId = 2, Username = "Host" });
        var guest = players.Add(new BanchoPresence { UserId = 3, Username = "Guest" });
        await new CreateMatchHandler().HandleAsync(Context(player, players, matches, NewRoom("first"), new BanchoPacketWriter()));
        var first = matches.GetByUserId(2)!;
        await new JoinMatchHandler().HandleAsync(Context(guest, players, matches, Join(first.Id), new BanchoPacketWriter()));

        await new CreateMatchHandler().HandleAsync(Context(player, players, matches, NewRoom("second"), new BanchoPacketWriter()));

        Assert.Equal("second", matches.GetByUserId(2)!.Name);
        Assert.Equal([3], first.PlayerIds());
        Assert.Equal(3, first.HostUserId);
    }

    [Fact]
    public async Task JoiningARoom_LeavesTheOneThePlayerIsIn()
    {
        var players = new PlayerRegistry();
        var matches = new MatchRegistry();
        var a = players.Add(new BanchoPresence { UserId = 2, Username = "A" });
        var b = players.Add(new BanchoPresence { UserId = 3, Username = "B" });
        await new CreateMatchHandler().HandleAsync(Context(a, players, matches, NewRoom("A's"), new BanchoPacketWriter()));
        await new CreateMatchHandler().HandleAsync(Context(b, players, matches, NewRoom("B's"), new BanchoPacketWriter()));
        var aRoom = matches.GetByUserId(2)!;
        var bRoom = matches.GetByUserId(3)!;

        await new JoinMatchHandler().HandleAsync(Context(a, players, matches, Join(bRoom.Id), new BanchoPacketWriter()));
        await new JoinMatchHandler().HandleAsync(Context(a, players, matches, Join(bRoom.Id), new BanchoPacketWriter()));

        Assert.Null(matches.Get(aRoom.Id)); // left empty: closed
        Assert.Equal([3, 2], bRoom.PlayerIds());
    }

    [Theory]
    [InlineData(65536)] // was room 0 (ushort), and 65538 room 2
    [InlineData(65538)]
    [InlineData(-65534)]
    public async Task AnIdPastTheRange_JoinsNothing(int id)
    {
        var players = new PlayerRegistry();
        var matches = new MatchRegistry();
        var host = players.Add(new BanchoPresence { UserId = 2, Username = "Host" });
        var other = players.Add(new BanchoPresence { UserId = 3, Username = "Other" });
        await new CreateMatchHandler().HandleAsync(Context(host, players, matches, NewRoom("room"), new BanchoPacketWriter()));

        var response = new BanchoPacketWriter();
        await new JoinMatchHandler().HandleAsync(Context(other, players, matches, Join(id), response));

        Assert.Null(matches.GetByUserId(3));
    }

    [Fact]
    public async Task OnlyAFewRooms_AMinute()
    {
        var players = new PlayerRegistry();
        var matches = new MatchRegistry();
        var player = players.Add(new BanchoPresence { UserId = 2, Username = "Busy" });
        for (var i = 0; i < 5; i++)
            await new CreateMatchHandler().HandleAsync(Context(player, players, matches, NewRoom("room " + i), new BanchoPacketWriter()));

        await new CreateMatchHandler().HandleAsync(Context(player, players, matches, NewRoom("one too many"), new BanchoPacketWriter()));

        Assert.Equal("room 4", matches.GetByUserId(2)!.Name);
        Assert.Equal(1, matches.Count); // each new room left the last, which closed
    }
}
