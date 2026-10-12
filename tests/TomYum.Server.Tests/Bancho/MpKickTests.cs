using TomYum.Bancho;
using TomYum.Domain.Entities;
using TomYum.Server.Bancho;
using TomYum.Server.Beatmaps;
using Xunit;

namespace TomYum.Server.Tests.Bancho;

/// <summary>BanchoBot's <c>!mp kick</c>: the host's (or staff's) to use, and a kicked host hands the room on.</summary>
public class MpKickTests
{
    private sealed class NoMaps : IOsuApiClient
    {
        public Task<Beatmap?> GetByMd5Async(string md5, CancellationToken cancellationToken = default) => Task.FromResult<Beatmap?>(null);

        public Task<IReadOnlyList<Beatmap>> GetSetAsync(int setId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Beatmap>>([]);

        public Task<IReadOnlyList<Beatmap>> GetSinceAsync(DateTimeOffset since, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Beatmap>>([]);

        public bool SupportsSince => false;
    }

    private static (BanchoBotCommands Commands, PlayerRegistry Players, TomYum.Server.Bancho.Match Room) Room(params string[] names)
    {
        var players = new PlayerRegistry();
        var matches = new MatchRegistry();
        var room = matches.Create(2, "room", string.Empty, "map", 1, "md5", 0, default, default, false, 0, 0);
        for (var i = 0; i < names.Length; i++)
        {
            var player = players.Add(new BanchoPresence { UserId = i + 2, Username = names[i] });
            room.Slots[i].Join(player.UserId);
            room.Channel.Join(player);
        }
        var commands = new BanchoBotCommands(new TomYumVersion(), players, new ChannelRegistry(), matches, new BeatmapService(new NoMaps()),
            TestScopeFactory.Create(new InMemoryUserRepository()), new Plugins.PluginCommandRegistry(), new Plugins.LoadedPluginRegistry());
        return (commands, players, room);
    }

    [Fact]
    public async Task OnlyTheHost_MayKick()
    {
        var (commands, players, room) = Room("Host", "Guest", "Other");

        var reply = await commands.TryHandleAsync(players.GetByUsername("Guest")!, "!mp kick Other", BanchoPrivileges.Player);

        Assert.Equal("Only the host can kick players.", reply);
        Assert.Equal(3, room.PlayerIds().Count);
    }

    [Fact]
    public async Task TheHost_Kicks()
    {
        var (commands, players, room) = Room("Host", "Guest");

        var reply = await commands.TryHandleAsync(players.GetByUsername("Host")!, "!mp kick Guest", BanchoPrivileges.Player);

        Assert.Equal("Guest was kicked from the room.", reply);
        Assert.Equal([2], room.PlayerIds());
        Assert.Equal("You can't kick yourself; leave the room instead.",
            await commands.TryHandleAsync(players.GetByUsername("Host")!, "!mp kick Host", BanchoPrivileges.Player));
    }

    [Fact]
    public async Task AHostKickedByStaff_HandsTheRoomOn()
    {
        var (commands, players, room) = Room("Host", "Staff");

        await commands.TryHandleAsync(players.GetByUsername("Staff")!, "!mp kick Host", BanchoPrivileges.Player | BanchoPrivileges.Developer);

        Assert.Equal([3], room.PlayerIds());
        Assert.Equal(3, room.HostUserId);
    }
}
