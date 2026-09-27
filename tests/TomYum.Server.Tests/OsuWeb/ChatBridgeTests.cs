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

    /// <summary>A bridge whose services have BanchoBot and a user repository holding Lilian (whose id is returned).</summary>
    private static async Task<(OsuWebChatBridge, PlayerRegistry, ChannelRegistry, int)> NewBridgeWithBotAsync()
    {
        var players = new PlayerRegistry();
        var channels = new ChannelRegistry();
        var users = new InMemoryUserRepository();
        var lilian = await users.AddAsync(new User { Username = "Lilian", SafeName = "lilian", Email = "l@example.com", PasswordBcrypt = "x", Country = "nl" });
        var services = new ServiceCollection()
            .AddSingleton<IUserRepository>(users)
            .AddSingleton(new BanchoBot(new BanchoBotCommands(
                new TomYumVersion(), players, channels, new MatchRegistry(), new BeatmapService(new NoMapsApi()),
                TestScopeFactory.Create(users), new Plugins.PluginCommandRegistry(), new Plugins.LoadedPluginRegistry())))
            .BuildServiceProvider();
        var scopes = services.GetRequiredService<IServiceScopeFactory>();
        var chatLog = new ChatLog(scopes, TimeProvider.System, NullLogger<ChatLog>.Instance);
        return (new OsuWebChatBridge(scopes, players, channels, chatLog, NullLogger<OsuWebChatBridge>.Instance), players, channels, lilian.Id);
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
