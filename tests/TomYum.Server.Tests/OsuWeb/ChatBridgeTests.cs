using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TomYum.Bancho;
using TomYum.Server.Bancho;
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
