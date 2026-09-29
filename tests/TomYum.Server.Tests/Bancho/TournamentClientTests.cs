using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TomYum.Bancho;
using TomYum.Domain.Entities;
using TomYum.Domain.Services;
using TomYum.Server.Bancho;
using TomYum.Server.Bancho.Packets;
using TomYum.Server.Bancho.Packets.Spectate;
using Xunit;

namespace TomYum.Server.Tests.Bancho;

/// <summary>osu!stable's tournament client: extra windows on one account that follow a match.</summary>
public sealed class TournamentClientTests
{
    [Fact]
    public void TournamentWindows_AreExtraSessions_NotThePlayer()
    {
        var registry = new PlayerRegistry();
        var game = registry.Add(new BanchoPresence { UserId = 5, Username = "Ref" });
        var window = registry.AddTournamentClient(new BanchoPresence { UserId = 5, Username = "Ref" });

        Assert.Same(game, registry.GetByUserId(5));
        Assert.Same(window, registry.GetByToken(window.Token));
        Assert.Equal([game], registry.All);
        Assert.Equal(1, registry.Count);

        registry.Remove(window.Token);
        Assert.Same(game, registry.GetByUserId(5));   // a window closing isn't the player leaving
    }

    [Fact]
    public async Task ATournamentLogin_DoesntReplaceThePlayersGame()
    {
        var users = new InMemoryUserRepository();
        var hasher = new BcryptPasswordHasher();
        await users.AddAsync(new User { Username = "Ref", SafeName = "ref", Email = "r@x.dev", PasswordBcrypt = hasher.HashPassword(hasher.ComputeMd5Hex("password")) });
        var registry = new PlayerRegistry();
        var login = new BanchoLoginHandler(users, hasher, new InMemoryUserStatsRepository(), registry, new ChannelRegistry(), NullLogger<BanchoLoginHandler>.Instance);
        string Body(string version) => $"Ref\n{hasher.ComputeMd5Hex("password")}\n{version}|0|0|abc:runningunderwine:def:ghi:jkl:|0\n";

        var game = await login.HandleAsync(Body("b20250101.1"), "TH", CancellationToken.None);
        var first = await login.HandleAsync(Body("b20250101.1tourney"), "TH", CancellationToken.None);
        var second = await login.HandleAsync(Body("b20250101.1tourney"), "TH", CancellationToken.None);

        var id = registry.GetByToken(game.Token!)!.UserId;
        Assert.Equal(game.Token, registry.GetByUserId(id)!.Token);
        Assert.True(registry.GetByToken(first.Token!)!.IsTournamentClient);
        Assert.True(registry.GetByToken(second.Token!)!.IsTournamentClient);
        Assert.Single(registry.All);
    }

    [Fact]
    public async Task AWindow_FollowsAMatch_ItsStateAndChat()
    {
        var registry = new PlayerRegistry();
        var matches = new MatchRegistry();
        var player = registry.Add(new BanchoPresence { UserId = 2, Username = "Player" });
        var window = registry.AddTournamentClient(new BanchoPresence { UserId = 5, Username = "Ref" });
        var match = matches.Create(2, "OWC: A vs B", string.Empty, "map", 1, "md5", 0, default, default, false, 0, 0);
        match.Slots[0].Join(2);

        var info = new BanchoPacketWriter(64);
        await new TournamentMatchInfoHandler().HandleAsync(Context(window, registry, matches, match.Id, info));
        Assert.Equal([(ushort)ServerPacketId.UpdateMatch], PacketIds(info.ToArray()));

        var join = new BanchoPacketWriter(64);
        await new TournamentJoinMatchChannelHandler().HandleAsync(Context(window, registry, matches, match.Id, join));
        Assert.Equal([(ushort)ServerPacketId.ChannelJoinSuccess], PacketIds(join.ToArray()));
        Assert.Contains(window, match.TournamentWatchers);
        Assert.Contains(match.Channel.Members, m => m.Token == window.Token);

        // The match's packets reach the window like its players.
        TomYum.Server.Bancho.Packets.Match.MatchBroadcast.UpdateMatch(match, registry);
        Assert.Contains((ushort)ServerPacketId.UpdateMatch, PacketIds(window.DequeueAll()));
        Assert.Contains((ushort)ServerPacketId.UpdateMatch, PacketIds(player.DequeueAll()));

        var leave = new BanchoPacketWriter(64);
        await new TournamentLeaveMatchChannelHandler().HandleAsync(Context(window, registry, matches, match.Id, leave));
        Assert.Equal([(ushort)ServerPacketId.ChannelKick], PacketIds(leave.ToArray()));
        Assert.Empty(match.TournamentWatchers);
    }

    [Fact]
    public async Task ReplayFrames_GoToTheWindowWatching_NotThePlayersGame()
    {
        var registry = new PlayerRegistry();
        var host = registry.Add(new BanchoPresence { UserId = 2, Username = "Player" });
        var game = registry.Add(new BanchoPresence { UserId = 5, Username = "Ref" });
        var window = registry.AddTournamentClient(new BanchoPresence { UserId = 5, Username = "Ref" });
        SpectatorManager.Attach(host, window, registry);
        game.DequeueAll();
        window.DequeueAll();

        await new SpectateFramesHandler().HandleAsync(new ClientPacketContext(host, registry, new ChannelRegistry(), new MatchRegistry(),
            new ServiceCollection().BuildServiceProvider(), new byte[] { 1, 2, 3, 4 }, new BanchoPacketWriter(16)));

        Assert.Contains((ushort)ServerPacketId.SpectateFrames, PacketIds(window.DequeueAll()));
        Assert.Empty(game.DequeueAll());
    }

    private static ClientPacketContext Context(OnlinePlayer player, PlayerRegistry registry, MatchRegistry matches, int matchId, BanchoPacketWriter response) =>
        new(player, registry, new ChannelRegistry(), matches, new ServiceCollection().BuildServiceProvider(),
            new BanchoPacketWriter(4).WriteInt32(matchId).ToArray(), response);

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
