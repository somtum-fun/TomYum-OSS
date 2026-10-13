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
        var window = registry.AddTournamentClient(new BanchoPresence { UserId = 5, Username = "Ref" })!;

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
    public void OneAccount_HasAtMost16Windows()
    {
        var registry = new PlayerRegistry();
        for (var i = 0; i < PlayerRegistry.MaxTournamentClients; i++)
            Assert.NotNull(registry.AddTournamentClient(new BanchoPresence { UserId = 5, Username = "Ref" }));

        Assert.Null(registry.AddTournamentClient(new BanchoPresence { UserId = 5, Username = "Ref" }));
        Assert.NotNull(registry.AddTournamentClient(new BanchoPresence { UserId = 6, Username = "Other" }));
    }

    [Fact]
    public async Task ATournamentLogin_StillGoesThroughThePluginsLoginCheck()
    {
        var users = new InMemoryUserRepository();
        var hasher = new BcryptPasswordHasher();
        await users.AddAsync(new User { Username = "Ref", SafeName = "ref", Email = "r@x.dev", PasswordBcrypt = hasher.HashPassword(hasher.ComputeMd5Hex("password")) });
        var events = new TomYum.Server.Plugins.EventBus(NullLogger<TomYum.Server.Plugins.EventBus>.Instance);
        var asked = new List<string>();
        events.Subscribe<TomYum.Plugins.Abstractions.ClientLoginAttemptedEvent>((e, _) =>
        {
            asked.Add(e.OsuVersion);
            e.Veto("Please update your game client.");
            return Task.CompletedTask;
        });
        var registry = new PlayerRegistry();
        var login = new BanchoLoginHandler(users, hasher, new InMemoryUserStatsRepository(), registry, new ChannelRegistry(), NullLogger<BanchoLoginHandler>.Instance, events);

        var outcome = await login.HandleAsync($"Ref\n{hasher.ComputeMd5Hex("password")}\nb20250101.1tourney|0|0|abc:runningunderwine:def:ghi:jkl:|0\n", "TH", CancellationToken.None);

        Assert.Null(outcome.Token);
        Assert.Equal(["b20250101.1tourney"], asked);
        Assert.Empty(registry.AllSessions);
    }

    [Fact]
    public async Task AWindow_FollowsAMatch_ItsStateAndChat()
    {
        var registry = new PlayerRegistry();
        var matches = new MatchRegistry();
        var player = registry.Add(new BanchoPresence { UserId = 2, Username = "Player" });
        var window = registry.AddTournamentClient(Staff(5, "Ref"))!;
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
    public async Task OnlyTournamentWindows_FollowAMatch()
    {
        // From the game, following a match would show its chat and password to anyone.
        var registry = new PlayerRegistry();
        var matches = new MatchRegistry();
        var stranger = registry.Add(new BanchoPresence { UserId = 3, Username = "Stranger" });
        var match = matches.Create(2, "Private room", "secret", "map", 1, "md5", 0, default, default, false, 0, 0);
        match.Slots[0].Join(2);

        var join = new BanchoPacketWriter(64);
        await new TournamentJoinMatchChannelHandler().HandleAsync(Context(stranger, registry, matches, match.Id, join));

        Assert.Empty(join.ToArray());
        Assert.Empty(match.TournamentWatchers);
        Assert.Null(stranger.WatchingMatchId);
        Assert.DoesNotContain(match.Channel.Members, m => m.UserId == 3);
    }

    [Fact]
    public async Task AnyonesTournamentWindow_CantFollowSomeoneElsesMatch()
    {
        // The version string that makes a tournament client is the client's own say: on its own it
        // would let any player read any room's chat.
        var registry = new PlayerRegistry();
        var matches = new MatchRegistry();
        var window = registry.AddTournamentClient(new BanchoPresence { UserId = 3, Username = "Stranger" })!;
        var match = matches.Create(2, "Private room", "secret", "map", 1, "md5", 0, default, default, false, 0, 0);
        match.Slots[0].Join(2);

        var join = new BanchoPacketWriter(64);
        await new TournamentJoinMatchChannelHandler().HandleAsync(Context(window, registry, matches, match.Id, join));

        Assert.Empty(join.ToArray());
        Assert.Empty(match.TournamentWatchers);
        Assert.DoesNotContain(match.Channel.Members, m => m.UserId == 3);
    }

    [Fact]
    public async Task APlayersOwnWindow_FollowsTheirMatch_ButNotWhenRestricted()
    {
        var registry = new PlayerRegistry();
        var matches = new MatchRegistry();
        var match = matches.Create(2, "OWC: A vs B", string.Empty, "map", 1, "md5", 0, default, default, false, 0, 0);
        match.Slots[0].Join(4);
        var own = registry.AddTournamentClient(new BanchoPresence { UserId = 4, Username = "Player" })!;
        var restricted = registry.AddTournamentClient(Staff(6, "Restricted"))!;
        restricted.IsRestricted = true;

        await new TournamentJoinMatchChannelHandler().HandleAsync(Context(own, registry, matches, match.Id, new BanchoPacketWriter(64)));
        await new TournamentJoinMatchChannelHandler().HandleAsync(Context(restricted, registry, matches, match.Id, new BanchoPacketWriter(64)));

        Assert.Equal([own], match.TournamentWatchers);
    }

    [Fact]
    public async Task Watchers_NeverGetTheRoomsPassword()
    {
        var registry = new PlayerRegistry();
        var matches = new MatchRegistry();
        var player = registry.Add(new BanchoPresence { UserId = 2, Username = "Player" });
        var window = registry.AddTournamentClient(Staff(5, "Ref"))!;
        var match = matches.Create(2, "Private room", "hunter2", "map", 1, "md5", 0, default, default, false, 0, 0);
        match.Slots[0].Join(2);
        await new TournamentJoinMatchChannelHandler().HandleAsync(Context(window, registry, matches, match.Id, new BanchoPacketWriter(64)));
        window.DequeueAll();
        player.DequeueAll();

        TomYum.Server.Bancho.Packets.Match.MatchBroadcast.UpdateMatch(match, registry);
        var toWindow = window.DequeueAll();
        Assert.Contains((ushort)ServerPacketId.UpdateMatch, PacketIds(toWindow));
        Assert.False(Contains(toWindow, "hunter2"));
        Assert.True(Contains(player.DequeueAll(), "hunter2")); // the room's own players still get it

        var change = new BanchoPacketWriter(4).WriteString("swordfish").ToArray();
        await new TomYum.Server.Bancho.Packets.Match.MatchChangePasswordHandler().HandleAsync(new ClientPacketContext(player, registry, new ChannelRegistry(), matches,
            new ServiceCollection().BuildServiceProvider(), change, new BanchoPacketWriter(16)));
        Assert.False(Contains(window.DequeueAll(), "swordfish"));
    }

    [Fact]
    public async Task ALoggedOutWindow_NoLongerFollowsTheMatch()
    {
        var registry = new PlayerRegistry();
        var matches = new MatchRegistry();
        var window = registry.AddTournamentClient(Staff(5, "Ref"))!;
        var match = matches.Create(2, "OWC: A vs B", string.Empty, "map", 1, "md5", 0, default, default, false, 0, 0);
        match.Slots[0].Join(2);
        await new TournamentJoinMatchChannelHandler().HandleAsync(Context(window, registry, matches, match.Id, new BanchoPacketWriter(64)));

        window.LoggedInUtc -= LogoutHandler.IgnoreAfterLogin; // a logout right after login is ignored
        await new LogoutHandler().HandleAsync(Context(window, registry, matches, 0, new BanchoPacketWriter(16)));

        Assert.Empty(match.TournamentWatchers);
        Assert.Null(registry.GetByToken(window.Token));
    }

    [Fact]
    public async Task ReplayFrames_GoToTheWindowWatching_NotThePlayersGame()
    {
        var registry = new PlayerRegistry();
        var host = registry.Add(new BanchoPresence { UserId = 2, Username = "Player" });
        var game = registry.Add(new BanchoPresence { UserId = 5, Username = "Ref" });
        var window = registry.AddTournamentClient(new BanchoPresence { UserId = 5, Username = "Ref" })!;
        SpectatorManager.Attach(host, window, registry);
        game.DequeueAll();
        window.DequeueAll();

        await new SpectateFramesHandler().HandleAsync(new ClientPacketContext(host, registry, new ChannelRegistry(), new MatchRegistry(),
            new ServiceCollection().BuildServiceProvider(), new byte[] { 1, 2, 3, 4 }, new BanchoPacketWriter(16)));

        Assert.Contains((ushort)ServerPacketId.SpectateFrames, PacketIds(window.DequeueAll()));
        Assert.Empty(game.DequeueAll());
    }

    private static BanchoPresence Staff(int userId, string username) =>
        new() { UserId = userId, Username = username, Privileges = BanchoPrivileges.Player | BanchoPrivileges.Moderator };

    private static bool Contains(byte[] bytes, string text) =>
        bytes.AsSpan().IndexOf(System.Text.Encoding.UTF8.GetBytes(text)) >= 0;

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
