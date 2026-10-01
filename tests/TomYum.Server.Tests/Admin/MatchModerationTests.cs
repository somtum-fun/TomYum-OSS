using TomYum.Bancho;
using TomYum.Server.Admin;
using TomYum.Server.Bancho;
using Xunit;

namespace TomYum.Server.Tests.Admin;

/// <summary>The website's Tournament Committee panel watching and steering live rooms.</summary>
public sealed class MatchModerationTests
{
    [Fact]
    public void List_ShowsRooms_PlayedOnesFirst_WithTheirPlayers()
    {
        var (players, matches, moderation) = Setup();
        players.Add(new BanchoPresence { UserId = 2, Username = "Host" });
        var idle = matches.Create(2, "casual", "secret", "map a", 1, "md5", 0, default, default, false, 0, 0);
        idle.Slots[0].Join(2);
        var played = matches.Create(2, "OWC: A vs B", string.Empty, "map b", 2, "md5", 1, default, default, false, 0, 0);
        played.Slots[3].Join(2);
        played.Slots[3].Status = SlotStatus.Playing;
        played.InProgress = true;

        var list = moderation.List();

        Assert.Equal([played.Id, idle.Id], list.Select(m => (ushort)m.Id));
        Assert.True(list[1].HasPassword);
        var player = Assert.Single(list[0].Players);
        Assert.Equal((3, 2, "Host", "playing"), (player.Slot, player.UserId, player.Username, player.Status));
    }

    [Fact]
    public void Abort_SendsPlayersBackToTheRoom()
    {
        var (players, matches, moderation) = Setup();
        var host = players.Add(new BanchoPresence { UserId = 2, Username = "Host" });
        var match = matches.Create(2, "room", string.Empty, "map", 1, "md5", 0, default, default, false, 0, 0);
        match.Slots[0].Join(2);
        match.Slots[0].Status = SlotStatus.Playing;
        match.InProgress = true;

        Assert.True(moderation.Abort(match.Id));

        Assert.False(match.InProgress);
        Assert.Equal(SlotStatus.NotReady, match.Slots[0].Status);
        var packets = PacketIds(host.DequeueAll());
        Assert.Contains((ushort)ServerPacketId.MatchAbort, packets);
        Assert.Contains((ushort)ServerPacketId.UpdateMatch, packets);

        Assert.False(moderation.Abort(match.Id));       // nothing left to abort
        Assert.Null(moderation.Abort(9999));            // no such room
    }

    [Fact]
    public void Close_SendsEveryoneToTheLobby_AndDisbandsTheRoom()
    {
        var (players, matches, moderation) = Setup();
        var host = players.Add(new BanchoPresence { UserId = 2, Username = "Host" });
        var guest = players.Add(new BanchoPresence { UserId = 3, Username = "Guest" });
        var watcher = players.Add(new BanchoPresence { UserId = 4, Username = "Lobby" });
        watcher.InLobby = true;
        var match = matches.Create(2, "room", string.Empty, "map", 1, "md5", 0, default, default, false, 0, 0);
        match.Slots[0].Join(2);
        match.Slots[1].Join(3);
        match.Slots[2].Join(77);                        // went offline without leaving

        Assert.True(moderation.Close(match.Id));

        Assert.Null(matches.Get(match.Id));
        Assert.Contains((ushort)ServerPacketId.ChannelKick, PacketIds(host.DequeueAll()));
        Assert.Contains((ushort)ServerPacketId.ChannelKick, PacketIds(guest.DequeueAll()));
        Assert.Contains((ushort)ServerPacketId.DisposeMatch, PacketIds(watcher.DequeueAll()));
        Assert.False(moderation.Close(match.Id));
    }

    [Fact]
    public void Message_PostsInTheRoomsChat()
    {
        var (players, matches, moderation) = Setup();
        var host = players.Add(new BanchoPresence { UserId = 2, Username = "Host" });
        var match = matches.Create(2, "room", string.Empty, "map", 1, "md5", 0, default, default, false, 0, 0);
        match.Slots[0].Join(2);
        match.Channel.Join(host);

        Assert.True(moderation.Message(match.Id, "Referee: 2 minutes to ready up."));
        Assert.Contains((ushort)ServerPacketId.SendMessage, PacketIds(host.DequeueAll()));
        Assert.False(moderation.Message(9999, "hello"));
    }

    private static (PlayerRegistry Players, MatchRegistry Matches, MatchModeration Moderation) Setup()
    {
        var players = new PlayerRegistry();
        var matches = new MatchRegistry();
        return (players, matches, new MatchModeration(matches, players));
    }

    private static List<ushort> PacketIds(byte[] body)
    {
        var ids = new List<ushort>();
        var reader = new BanchoPacketReader(body);
        while (reader.CanReadHeader)
        {
            var header = reader.ReadHeader();
            ids.Add((ushort)header.Id);
            reader.Skip(checked((int)header.Length));
        }
        return ids;
    }
}
