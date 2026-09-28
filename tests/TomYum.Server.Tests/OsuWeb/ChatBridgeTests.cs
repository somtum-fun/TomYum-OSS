using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TomYum.Bancho;
using TomYum.Domain.Entities;
using TomYum.Domain.Enums;
using TomYum.Domain.Repositories;
using TomYum.Server.Bancho;
using TomYum.Server.Beatmaps;
using TomYum.Server.OsuWeb;
using Xunit;

namespace TomYum.Server.Tests.OsuWeb;

/// <summary>Website chat messages delivered in-game (<see cref="OsuWebChatBridge.Deliver"/>).</summary>
public sealed class ChatBridgeTests
{
    [Fact]
    public void ChannelMessage_ReachesMembers_ButNotTheSendersOwnSession()
    {
        var (bridge, players, channels) = NewBridge();
        var sender = players.Add(new BanchoPresence { UserId = 2, Username = "Lilian" });
        var member = players.Add(new BanchoPresence { UserId = 3, Username = "Friend" });
        var outsider = players.Add(new BanchoPresence { UserId = 4, Username = "Elsewhere" });
        channels.Get("#osu")!.Join(sender);
        channels.Get("#osu")!.Join(member);

        var delivered = bridge.Deliver(new WebChatMessage(1, 2, "Lilian", "#osu", null, "hi from the site", false));

        Assert.Equal(1, delivered);
        Assert.Equal(("Lilian", "hi from the site", "#osu"), ReadMessage(member.DequeueAll()));
        Assert.Empty(sender.DequeueAll());
        Assert.Empty(outsider.DequeueAll());
    }

    [Fact]
    public void PrivateMessage_ReachesTheRecipient_AsAnActionWhenItIsOne()
    {
        var (bridge, players, _) = NewBridge();
        var recipient = players.Add(new BanchoPresence { UserId = 3, Username = "Friend" });

        bridge.Deliver(new WebChatMessage(1, 2, "Lilian", null, 3, "waves", true));

        Assert.Equal(("Lilian", "\u0001ACTION waves\u0001", "Friend"), ReadMessage(recipient.DequeueAll()));
    }

    [Fact]
    public void UnknownChannelsAndOfflineRecipients_DeliverNothing()
    {
        var (bridge, _, _) = NewBridge();

        Assert.Equal(0, bridge.Deliver(new WebChatMessage(1, 2, "Lilian", "#nope", null, "x", false)));
        Assert.Equal(0, bridge.Deliver(new WebChatMessage(1, 2, "Lilian", null, 99, "x", false)));
    }

    [Theory]
    [InlineData("hello", "hello", false)]
    [InlineData("\u0001ACTION dances\u0001", "dances", true)]
    public void ActionFraming_IsSplitForTheWebsite(string game, string text, bool isAction)
    {
        Assert.Equal((text, isAction), OsuWebChatBridge.SplitAction(game));
    }

    [Fact]
    public async Task WebsiteCommandInAGameChannel_IsAnsweredForEveryoneThere()
    {
        var (bridge, players, channels, lilian) = await NewBridgeWithBotAsync();
        var inGame = players.Add(new BanchoPresence { UserId = 3, Username = "Friend" });
        channels.Get("#osu")!.Join(inGame);
        var message = new WebChatMessage(1, lilian, "Lilian", "#osu", null, "!ping", false);

        Assert.True(bridge.IsForBanchoBot(message));
        Assert.Equal("pong!", await bridge.AnswerWebsiteCommandAsync(message, CancellationToken.None));
        Assert.Equal(("BanchoBot", "pong!", "#osu"), ReadMessage(inGame.DequeueAll()));
    }

    [Fact]
    public async Task WebsitePmToBanchoBot_GetsAnAnswer_EvenWithoutACommand()
    {
        var (bridge, _, _, lilian) = await NewBridgeWithBotAsync();

        Assert.Equal("pong!", await bridge.AnswerWebsiteCommandAsync(new WebChatMessage(1, lilian, "Lilian", null, BanchoBot.UserId, "!ping", false), CancellationToken.None));
        Assert.StartsWith("Hi!", await bridge.AnswerWebsiteCommandAsync(new WebChatMessage(2, lilian, "Lilian", null, BanchoBot.UserId, "hello", false), CancellationToken.None));
    }

    [Fact]
    public async Task OrdinaryChat_AndBanchoBotItself_AreLeftAlone()
    {
        var (bridge, _, _, lilian) = await NewBridgeWithBotAsync();

        Assert.False(bridge.IsForBanchoBot(new WebChatMessage(1, lilian, "Lilian", "#osu", null, "hi all", false)));
        Assert.False(bridge.IsForBanchoBot(new WebChatMessage(1, 2, "Lilian", "#nope", null, "!ping", false)));
        Assert.False(bridge.IsForBanchoBot(new WebChatMessage(1, BanchoBot.UserId, "BanchoBot", "#osu", null, "!ping", false)));
        Assert.False(bridge.IsForBanchoBot(new WebChatMessage(1, 2, "Lilian", null, 3, "!ping", false)));
    }

    [Fact]
    public async Task LazerNp_SetsTheMap_AndBanchoBotDescribesIt()
    {
        var store = new NowPlayingStore();
        var (bridge, _, _, lilian) = await NewBridgeWithBotAsync(store);
        const string np = "is listening to [https://osu.sundei.eu/b/4622078 Camellia - Parallel Universe Shifter (iLyne) [To Infinity and Beyond]]";

        // In a channel: remembered for !map.
        bridge.Deliver(new WebChatMessage(1, lilian, "Lilian", "#osu", null, np, IsAction: true));
        Assert.Equal(new NowPlaying(4622078, 0, 0, Mods.None), store.Get(lilian));

        // To BanchoBot: answered with the map.
        var reply = await bridge.AnswerWebsiteCommandAsync(new WebChatMessage(2, lilian, "Lilian", null, BanchoBot.UserId, np, IsAction: true), CancellationToken.None);
        // In-game, maps are named as stable names them (the mapper is only added on Discord).
        Assert.StartsWith($"[https://osu.{BanchoHosts.DefaultDomain}/b/4622078 Camellia - Parallel Universe Shifter [To Infinity and Beyond]] | ★6.66 | Loved", reply);

        // The same words typed without /np (not an action) are just chat.
        Assert.StartsWith("Hi!", await bridge.AnswerWebsiteCommandAsync(new WebChatMessage(3, lilian, "Lilian", null, BanchoBot.UserId, np, IsAction: false), CancellationToken.None));
    }

    [Theory]
    [InlineData(10, "short", new[] { "short" })]
    [InlineData(10, "one two three four", new[] { "one two", "three four" })]
    [InlineData(10, "abcdefghijklmnop", new[] { "abcdefghij", "klmnop" })]
    [InlineData(10, "  ", new string[0])]
    public void LongMessages_AreSplitForTheWebsite(int limit, string text, string[] expected) =>
        Assert.Equal(expected, OsuWebChatBridge.SplitForWebsite(text, limit));

    [Fact]
    public void TheStaffHelp_FitsTheWebsiteInPieces()
    {
        var help = new string('x', 300) + " " + new string('y', 300) + " " + new string('z', 100);
        var parts = OsuWebChatBridge.SplitForWebsite(help);
        Assert.All(parts, p => Assert.InRange(p.Length, 1, OsuWebChatBridge.WebsiteMessageLimit));
        Assert.Equal(help, string.Join(' ', parts));
    }

    /// <summary>A bridge whose services have BanchoBot and a user repository holding Lilian (whose id is returned).</summary>
    private static async Task<(OsuWebChatBridge, PlayerRegistry, ChannelRegistry, int)> NewBridgeWithBotAsync(NowPlayingStore? store = null)
    {
        store ??= new NowPlayingStore();
        var players = new PlayerRegistry();
        var channels = new ChannelRegistry();
        var users = new InMemoryUserRepository();
        var lilian = await users.AddAsync(new User { Username = "Lilian", SafeName = "lilian", Email = "l@example.com", PasswordBcrypt = "x", Country = "nl" });
        var services = new ServiceCollection()
            .AddSingleton<IUserRepository>(users)
            .AddSingleton(new BanchoBot(new BanchoBotCommands(
                new TomYumVersion(), players, channels, new MatchRegistry(), new BeatmapService(new NoMapsApi()),
                TestScopeFactory.Create(users), new Plugins.PluginCommandRegistry(), new Plugins.LoadedPluginRegistry())))
            .AddSingleton<IBeatmapRepository>(new OneMapRepository())
            .AddSingleton(sp => new MapCommands(
                sp.GetRequiredService<IServiceScopeFactory>(), new BeatmapService(new NoMapsApi()), store,
                new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()))
            .BuildServiceProvider();
        var scopes = services.GetRequiredService<IServiceScopeFactory>();
        var chatLog = new ChatLog(scopes, TimeProvider.System, NullLogger<ChatLog>.Instance);
        return (new OsuWebChatBridge(scopes, players, channels, chatLog, NullLogger<OsuWebChatBridge>.Instance, nowPlaying: store), players, channels, lilian.Id);
    }

    /// <summary>Knows one (loved) map.</summary>
    private sealed class OneMapRepository : IBeatmapRepository
    {
        private static readonly Beatmap Map = new()
        {
            Id = 4622078, SetId = 2203483, Md5 = "md5", Status = RankedStatus.Loved, StarRating = 6.66,
            Artist = "Camellia", Title = "Parallel Universe Shifter", Creator = "iLyne", Version = "To Infinity and Beyond",
        };

        public Task<Beatmap?> GetByMd5Async(string md5, CancellationToken cancellationToken = default) => Task.FromResult<Beatmap?>(null);

        public Task<Beatmap?> GetByIdAsync(int id, CancellationToken cancellationToken = default) => Task.FromResult(id == Map.Id ? Map : null);

        public Task<IReadOnlyList<Beatmap>> GetBySetAsync(int setId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Beatmap>>(setId == Map.SetId ? [Map] : []);

        public Task<bool> SetStatusAsync(int beatmapId, RankedStatus status, CancellationToken cancellationToken = default) => Task.FromResult(false);

        public Task<bool> UnfreezeStatusAsync(int beatmapId, CancellationToken cancellationToken = default) => Task.FromResult(false);

        public Task UpsertAsync(Beatmap beatmap, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<int> ReserveCustomSetIdAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);

        public Task<int> ReserveCustomBeatmapIdAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
    }

    private sealed class NoMapsApi : IOsuApiClient
    {
        public bool SupportsSince => false;

        public Task<Beatmap?> GetByMd5Async(string md5, CancellationToken cancellationToken = default) => Task.FromResult<Beatmap?>(null);

        public Task<IReadOnlyList<Beatmap>> GetSetAsync(int setId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Beatmap>>([]);

        public Task<IReadOnlyList<Beatmap>> GetSinceAsync(DateTimeOffset since, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Beatmap>>([]);
    }

    private static (OsuWebChatBridge, PlayerRegistry, ChannelRegistry) NewBridge()
    {
        var players = new PlayerRegistry();
        var channels = new ChannelRegistry();
        var scopes = new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var chatLog = new ChatLog(scopes, TimeProvider.System, NullLogger<ChatLog>.Instance);
        return (new OsuWebChatBridge(scopes, players, channels, chatLog, NullLogger<OsuWebChatBridge>.Instance), players, channels);
    }

    /// <summary>The (sender, text, target) of the single SEND_MESSAGE in <paramref name="body"/>.</summary>
    private static (string, string, string) ReadMessage(byte[] body)
    {
        var reader = new BanchoPacketReader(body);
        var header = reader.ReadHeader();
        Assert.Equal((ushort)ServerPacketId.SendMessage, (ushort)header.Id);
        var sender = reader.ReadString();
        var text = reader.ReadString();
        var target = reader.ReadString();
        return (sender, text, target);
    }
}
