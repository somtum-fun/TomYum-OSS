using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TomYum.Bancho;
using TomYum.Domain.Entities;
using TomYum.Server.Bancho;
using TomYum.Server.Bancho.Packets;
using TomYum.Server.Beatmaps;
using Xunit;

namespace TomYum.Server.Tests;

/// <summary>
/// Tests the chat slice: public messages fan out to channel members, private messages relay to
/// the target, BanchoBot answers commands, and channel join/part update membership. Channels and
/// the bot are wired the same way the server wires them at startup.
/// </summary>
public class ChatHandlerTests
{
    private static BanchoBot NewBot() => new(
        new BanchoBotCommands(
            new TomYumVersion(),
            new PlayerRegistry(),
            new ChannelRegistry(),
            new MatchRegistry(),
            new BeatmapService(new StubApiClient()),
            TestScopeFactory.Create(new InMemoryUserRepository()),
            new Plugins.PluginCommandRegistry(),
            new Plugins.LoadedPluginRegistry()));

    /// <summary>An <see cref="IOsuApiClient"/> that knows no maps — enough to construct BeatmapService.</summary>
    private sealed class StubApiClient : IOsuApiClient
    {
        public Task<Beatmap?> GetByMd5Async(string md5, CancellationToken cancellationToken = default) =>
            Task.FromResult<Beatmap?>(null);
    }

    private static OnlinePlayer AddPlayer(PlayerRegistry registry, int userId, string name) =>
        registry.Add(new BanchoPresence { UserId = userId, Username = name });

    /// <summary>Frames a single client packet (u16 id + pad + u32 length + payload).</summary>
    private static byte[] Frame(ClientPacketId id, ReadOnlySpan<byte> payload)
    {
        var buffer = new byte[PacketHeader.Size + payload.Length];
        buffer[0] = (byte)((ushort)id & 0xFF);
        buffer[1] = (byte)((ushort)id >> 8);
        var len = (uint)payload.Length;
        buffer[3] = (byte)(len & 0xFF);
        buffer[4] = (byte)((len >> 8) & 0xFF);
        buffer[5] = (byte)((len >> 16) & 0xFF);
        buffer[6] = (byte)((len >> 24) & 0xFF);
        payload.CopyTo(buffer.AsSpan(PacketHeader.Size));
        return buffer;
    }

    private static byte[] MessagePayload(string text, string target)
    {
        var w = new BanchoPacketWriter(64);
        w.WriteString(string.Empty); // sender (ignored)
        w.WriteString(text);
        w.WriteString(target);
        w.WriteInt32(0);             // sender_id (ignored)
        return w.ToArray();
    }

    /// <summary>Collects the ids of every framed packet in a server→client body.</summary>
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

    private static ClientPacketContext Context(
        OnlinePlayer player, PlayerRegistry registry, ChannelRegistry channels, byte[] payload, BanchoPacketWriter response) =>
        new(player, registry, channels, new MatchRegistry(), new ServiceCollection().BuildServiceProvider(), payload, response);

    [Fact]
    public async Task PublicMessage_FansOutToOtherChannelMembers()
    {
        var registry = new PlayerRegistry();
        var channels = new ChannelRegistry();
        var sender = AddPlayer(registry, 2, "Sender");
        var listener = AddPlayer(registry, 3, "Listener");

        var osu = channels.Get("#osu")!;
        osu.Join(sender);
        osu.Join(listener);

        var response = new BanchoPacketWriter(64);
        await new SendPublicMessageHandler(NewBot()).HandleAsync(
            Context(sender, registry, channels, MessagePayload("hello world", "#osu"), response));

        // The listener received a SEND_MESSAGE; the sender did not get it echoed back.
        Assert.Contains((ushort)ServerPacketId.SendMessage, PacketIds(listener.DequeueAll()));
        Assert.DoesNotContain((ushort)ServerPacketId.SendMessage, PacketIds(sender.DequeueAll()));
    }

    [Fact]
    public async Task PublicMessage_NonMember_IsDropped()
    {
        var registry = new PlayerRegistry();
        var channels = new ChannelRegistry();
        var sender = AddPlayer(registry, 2, "Sender");
        var listener = AddPlayer(registry, 3, "Listener");
        channels.Get("#osu")!.Join(listener); // sender is NOT a member

        var response = new BanchoPacketWriter(64);
        await new SendPublicMessageHandler(NewBot()).HandleAsync(
            Context(sender, registry, channels, MessagePayload("hi", "#osu"), response));

        Assert.Empty(listener.DequeueAll());
    }

    [Fact]
    public async Task PublicMessage_BotCommand_PostsReplyToChannel()
    {
        var registry = new PlayerRegistry();
        var channels = new ChannelRegistry();
        var sender = AddPlayer(registry, 2, "Sender");
        channels.Get("#osu")!.Join(sender);

        var response = new BanchoPacketWriter(64);
        await new SendPublicMessageHandler(NewBot()).HandleAsync(
            Context(sender, registry, channels, MessagePayload("!ping", "#osu"), response));

        // The bot's reply is queued back to the sender (a channel member).
        Assert.Contains((ushort)ServerPacketId.SendMessage, PacketIds(sender.DequeueAll()));
    }

    [Fact]
    public async Task PrivateMessage_ToBanchoBot_RepliesToSender()
    {
        var registry = new PlayerRegistry();
        var channels = new ChannelRegistry();
        var sender = AddPlayer(registry, 2, "Sender");

        var response = new BanchoPacketWriter(64);
        await new SendPrivateMessageHandler(NewBot()).HandleAsync(
            Context(sender, registry, channels, MessagePayload("!help", "BanchoBot"), response));

        Assert.Contains((ushort)ServerPacketId.SendMessage, PacketIds(response.ToArray()));
    }

    [Fact]
    public async Task PrivateMessage_ToOnlineUser_IsRelayed()
    {
        var registry = new PlayerRegistry();
        var channels = new ChannelRegistry();
        var sender = AddPlayer(registry, 2, "Sender");
        var target = AddPlayer(registry, 3, "Target");

        var response = new BanchoPacketWriter(64);
        await new SendPrivateMessageHandler(NewBot()).HandleAsync(
            Context(sender, registry, channels, MessagePayload("hey", "Target"), response));

        Assert.Contains((ushort)ServerPacketId.SendMessage, PacketIds(target.DequeueAll()));
    }

    [Fact]
    public async Task ChannelJoin_Known_AddsMember_AndConfirms()
    {
        var registry = new PlayerRegistry();
        var channels = new ChannelRegistry();
        var player = AddPlayer(registry, 2, "Player");

        var nameW = new BanchoPacketWriter(16);
        nameW.WriteString("#osu");

        var response = new BanchoPacketWriter(32);
        await new ChannelJoinHandler().HandleAsync(Context(player, registry, channels, nameW.ToArray(), response));

        Assert.True(channels.Get("#osu")!.Contains(2));
        Assert.True(player.IsInChannel("#osu"));
        Assert.Contains((ushort)ServerPacketId.ChannelJoinSuccess, PacketIds(response.ToArray()));
    }

    [Fact]
    public async Task ChannelJoin_Unknown_IsKicked()
    {
        var registry = new PlayerRegistry();
        var channels = new ChannelRegistry();
        var player = AddPlayer(registry, 2, "Player");

        var nameW = new BanchoPacketWriter(16);
        nameW.WriteString("#nope");

        var response = new BanchoPacketWriter(32);
        await new ChannelJoinHandler().HandleAsync(Context(player, registry, channels, nameW.ToArray(), response));

        Assert.Contains((ushort)ServerPacketId.ChannelKick, PacketIds(response.ToArray()));
    }

    [Fact]
    public async Task ChannelPart_RemovesMember()
    {
        var registry = new PlayerRegistry();
        var channels = new ChannelRegistry();
        var player = AddPlayer(registry, 2, "Player");
        channels.Get("#osu")!.Join(player);
        player.JoinChannel("#osu");

        var nameW = new BanchoPacketWriter(16);
        nameW.WriteString("#osu");

        var response = new BanchoPacketWriter(16);
        await new ChannelPartHandler().HandleAsync(Context(player, registry, channels, nameW.ToArray(), response));

        Assert.False(channels.Get("#osu")!.Contains(2));
        Assert.False(player.IsInChannel("#osu"));
    }

    /// <summary>Builds an offline <see cref="OnlinePlayer"/> with the given client privileges (for bot-command tests).</summary>
    private static OnlinePlayer Caller(string name, BanchoPrivileges privileges = BanchoPrivileges.Player) =>
        new("token-" + name, new BanchoPresence { UserId = 50, Username = name, Privileges = privileges });

    [Fact]
    public async Task Bot_UnknownCommand_StillReplies()
    {
        var reply = await NewBot().TryHandleCommandAsync(Caller("Someone"), "!frobnicate", BanchoPrivileges.Player);
        Assert.NotNull(reply);
    }

    [Fact]
    public async Task Bot_NonCommand_ReturnsNull()
    {
        Assert.Null(await NewBot().TryHandleCommandAsync(Caller("Someone"), "just chatting", BanchoPrivileges.Player));
    }

    [Fact]
    public async Task Bot_Stats_IsStaffOnly_RefusedForPlayers()
    {
        var reply = await NewBot().TryHandleCommandAsync(Caller("Player"), "!stats", BanchoPrivileges.Player);
        Assert.Equal("That command is staff-only.", reply);
    }

    [Fact]
    public async Task Bot_Stats_AllowedForAdministrators()
    {
        var reply = await NewBot().TryHandleCommandAsync(Caller("Admin"), "!stats", BanchoPrivileges.Owner);
        Assert.NotNull(reply);
        Assert.NotEqual("That command is staff-only.", reply);
        // Build identity, runtime/host details, and the live counters all appear.
        Assert.Contains("TomYum", reply);
        Assert.Contains("@", reply);                        // version @commit
        Assert.Contains(".NET", reply);                     // RuntimeInformation.FrameworkDescription
        Assert.Contains(Environment.MachineName, reply);    // running host
        Assert.Contains(" up ", reply);                     // process uptime
        Assert.Contains("Players:", reply);
        Assert.Contains("Heap:", reply);
    }

    [Fact]
    public async Task Bot_Version_ReportsVersionAndCommit()
    {
        var reply = await NewBot().TryHandleCommandAsync(Caller("Player"), "!version", BanchoPrivileges.Player);
        Assert.NotNull(reply);
        Assert.StartsWith("TomYum ", reply);
        Assert.Contains("@", reply); // includes the build commit (a sha or "unknown")
    }

    [Fact]
    public async Task Bot_Help_HidesStatsFromNonStaff()
    {
        var bot = NewBot();
        Assert.DoesNotContain("!stats", await bot.TryHandleCommandAsync(Caller("Player"), "!help", BanchoPrivileges.Player));
        Assert.Contains("!stats", await bot.TryHandleCommandAsync(Caller("Admin"), "!help", BanchoPrivileges.Owner));
    }
}
