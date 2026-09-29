using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TomYum.Bancho;
using TomYum.Domain.Entities;
using TomYum.Domain.Enums;
using TomYum.Domain.Repositories;
using TomYum.Domain.Services;
using TomYum.Server.Bancho;
using TomYum.Server.Bancho.Packets;
using TomYum.Server.Beatmaps;
using Xunit;

namespace TomYum.Server.Tests.Bancho;

/// <summary>osu!stable friends, the leaderboard tabs, away messages and friends-only PMs.</summary>
public sealed class FriendsAndDmTests
{
    [Fact]
    public async Task Login_SendsTheFriendsList()
    {
        var users = new InMemoryUserRepository();
        var me = await AddUserAsync(users, "Me");
        var friends = new InMemoryFriendRepository();
        await friends.AddAsync(me.Id, 42);
        await friends.AddAsync(me.Id, 43);
        var registry = new PlayerRegistry();
        var handler = new BanchoLoginHandler(users, new BcryptPasswordHasher(), new InMemoryUserStatsRepository(), registry, new ChannelRegistry(),
            NullLogger<BanchoLoginHandler>.Instance, friends: friends);

        var outcome = await handler.HandleAsync(LoginBody("Me"), "TH", CancellationToken.None);

        Assert.Equal([42, 43], FriendsList(outcome.Body).Order());
        Assert.Equal([42, 43], registry.GetByUserId(me.Id)!.Friends.Order());
    }

    [Fact]
    public async Task FriendAddAndRemove_AreKept()
    {
        var users = new InMemoryUserRepository();
        var me = await AddUserAsync(users, "Me");
        var them = await AddUserAsync(users, "Them");
        var friends = new InMemoryFriendRepository();
        var services = new ServiceCollection().AddSingleton<IUserRepository>(users).AddSingleton<IFriendRepository>(friends).BuildServiceProvider();
        var registry = new PlayerRegistry();
        var player = registry.Add(new BanchoPresence { UserId = me.Id, Username = "Me" });

        await new FriendAddHandler().HandleAsync(Context(player, registry, Id(them.Id), services));
        await new FriendAddHandler().HandleAsync(Context(player, registry, Id(9999), services));      // nobody
        await new FriendAddHandler().HandleAsync(Context(player, registry, Id(me.Id), services));     // themselves
        Assert.Equal([them.Id], await friends.GetFriendIdsAsync(me.Id));
        Assert.Contains(them.Id, player.Friends);

        await new FriendRemoveHandler().HandleAsync(Context(player, registry, Id(them.Id), services));
        Assert.Empty(await friends.GetFriendIdsAsync(me.Id));
        Assert.Empty(player.Friends);
    }

    [Fact]
    public async Task FriendsOnlyPms_RefuseOthers_AndLetFriendsThrough()
    {
        var (registry, sender, recipient) = Two();
        await new ToggleBlockNonFriendDmsHandler().HandleAsync(Context(recipient, registry, Id(1)));
        Assert.True(recipient.BlocksNonFriendDms);

        var response = new BanchoPacketWriter(64);
        await Pm().HandleAsync(Context(sender, registry, Message("hi", "Recipient"), response: response));
        Assert.Empty(recipient.DequeueAll());
        Assert.Equal([(ushort)ServerPacketId.UserDmBlocked], PacketIds(response.ToArray()));

        recipient.Friends = [sender.UserId];
        await Pm().HandleAsync(Context(sender, registry, Message("hi", "Recipient")));
        Assert.Equal([(ushort)ServerPacketId.SendMessage], PacketIds(recipient.DequeueAll()));
    }

    [Fact]
    public async Task ASilencedRecipient_IsReportedToTheSender()
    {
        var (registry, sender, recipient) = Two();
        recipient.SilenceEndUtc = DateTimeOffset.UtcNow.AddMinutes(5);
        var response = new BanchoPacketWriter(64);

        await Pm().HandleAsync(Context(sender, registry, Message("hi", "Recipient"), response: response));

        Assert.Empty(recipient.DequeueAll());
        Assert.Equal([(ushort)ServerPacketId.TargetIsSilenced], PacketIds(response.ToArray()));
    }

    [Fact]
    public async Task AnAwayRecipient_AnswersWithTheirAwayMessage()
    {
        var (registry, sender, recipient) = Two();
        await new SetAwayMessageHandler().HandleAsync(Context(recipient, registry, Message("brb, dinner", string.Empty)));
        Assert.Equal("brb, dinner", recipient.AwayMessage);

        var response = new BanchoPacketWriter(64);
        await Pm().HandleAsync(Context(sender, registry, Message("hi", "Recipient"), response: response));

        Assert.Equal([(ushort)ServerPacketId.SendMessage], PacketIds(recipient.DequeueAll()));
        Assert.Equal(("Recipient", "\u0001ACTION is away: brb, dinner\u0001"), FirstMessage(response.ToArray()));

        await new SetAwayMessageHandler().HandleAsync(Context(recipient, registry, Message(string.Empty, string.Empty)));
        Assert.Null(recipient.AwayMessage);
    }

    [Fact]
    public async Task Leaderboards_ByMods_Friends_AndCountry()
    {
        var users = new InMemoryUserRepository();
        var me = await AddUserAsync(users, "Me", "nl");
        var friend = await AddUserAsync(users, "Friend", "de");
        var countryman = await AddUserAsync(users, "Countryman", "nl");
        var stranger = await AddUserAsync(users, "Stranger", "us");
        var scores = new InMemoryScoreRepository();
        foreach (var (user, value, mods) in new[] { (me, 100, 0), (friend, 200, 8), (countryman, 300, 8), (stranger, 400, 0) })
            await scores.AddAsync(new Score { UserId = user.Id, BeatmapMd5 = "md5", Mode = 0, ScoreValue = value, Mods = mods, Status = ScoreStatus.Best, Passed = true });
        var friends = new InMemoryFriendRepository();
        await friends.AddAsync(me.Id, friend.Id);
        var context = new DefaultHttpContext { RequestServices = new ServiceCollection().AddSingleton<IFriendRepository>(friends).BuildServiceProvider() };

        async Task<int[]> Board(int type, int mods = 0) =>
            [.. (await WebEndpoints.BoardAsync(context, scores, users, "md5", 0, type, mods, me.Id, CancellationToken.None)).Select(s => s.UserId)];

        Assert.Equal([stranger.Id, countryman.Id, friend.Id, me.Id], await Board(1));
        Assert.Equal([countryman.Id, friend.Id], await Board(2, mods: 8));
        Assert.Equal([friend.Id, me.Id], await Board(3));
        Assert.Equal([countryman.Id, me.Id], await Board(4));
    }

    private static SendPrivateMessageHandler Pm() => new(new BanchoBot(new BanchoBotCommands(
        new TomYumVersion(), new PlayerRegistry(), new ChannelRegistry(), new MatchRegistry(),
        new BeatmapService(new NoMapsApi()), TestScopeFactory.Create(new InMemoryUserRepository()),
        new Plugins.PluginCommandRegistry(), new Plugins.LoadedPluginRegistry())));

    private static (PlayerRegistry, OnlinePlayer Sender, OnlinePlayer Recipient) Two()
    {
        var registry = new PlayerRegistry();
        return (registry, registry.Add(new BanchoPresence { UserId = 2, Username = "Sender" }), registry.Add(new BanchoPresence { UserId = 3, Username = "Recipient" }));
    }

    private static async Task<User> AddUserAsync(InMemoryUserRepository users, string name, string country = "nl")
    {
        var hasher = new BcryptPasswordHasher();
        return await users.AddAsync(new User
        {
            Username = name,
            SafeName = User.MakeSafeName(name),
            Email = $"{name.ToLowerInvariant()}@tomyum.dev",
            Country = country,
            PasswordBcrypt = hasher.HashPassword(hasher.ComputeMd5Hex("password")),
        });
    }

    private static string LoginBody(string username) =>
        $"{username}\n{new BcryptPasswordHasher().ComputeMd5Hex("password")}\nb20231228.1|0|0|abc:runningunderwine:def:ghi:jkl:|0\n";

    private static ClientPacketContext Context(OnlinePlayer player, PlayerRegistry registry, byte[] payload, IServiceProvider? services = null, BanchoPacketWriter? response = null) =>
        new(player, registry, new ChannelRegistry(), new MatchRegistry(), services ?? new ServiceCollection().BuildServiceProvider(), payload, response ?? new BanchoPacketWriter(64));

    private static byte[] Id(int id) => new BanchoPacketWriter(4).WriteInt32(id).ToArray();

    private static byte[] Message(string text, string target) =>
        new BanchoPacketWriter(64).WriteString(string.Empty).WriteString(text).WriteString(target).WriteInt32(0).ToArray();

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

    private static (string Sender, string Text) FirstMessage(byte[] body)
    {
        var reader = new BanchoPacketReader(body);
        _ = reader.ReadHeader();
        return (reader.ReadString(), reader.ReadString());
    }

    private static List<int> FriendsList(byte[] body)
    {
        var reader = new BanchoPacketReader(body);
        while (reader.CanReadHeader)
        {
            var header = reader.ReadHeader();
            if ((ushort)header.Id != (ushort)ServerPacketId.FriendsList)
            {
                reader.Skip(checked((int)header.Length));
                continue;
            }
            var ids = new List<int>();
            for (var count = reader.ReadUInt16(); count > 0; count--)
                ids.Add(reader.ReadInt32());
            return ids;
        }
        throw new InvalidOperationException("No FRIENDS_LIST packet.");
    }

    private sealed class NoMapsApi : IOsuApiClient
    {
        public Task<Beatmap?> GetByMd5Async(string md5, CancellationToken cancellationToken = default) => Task.FromResult<Beatmap?>(null);

        public Task<IReadOnlyList<Beatmap>> GetSetAsync(int setId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Beatmap>>([]);

        public Task<IReadOnlyList<Beatmap>> GetSinceAsync(DateTimeOffset since, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Beatmap>>([]);

        public bool SupportsSince => false;
    }
}
