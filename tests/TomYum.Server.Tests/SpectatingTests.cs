using Microsoft.Extensions.DependencyInjection;

using TomYum.Bancho;
using TomYum.Domain.Entities;
using TomYum.Server.Bancho;
using TomYum.Server.Bancho.Packets;
using TomYum.Server.Bancho.Packets.Spectate;
using TomYum.Server.Beatmaps;

using Xunit;

namespace TomYum.Server.Tests;

/// <summary>
/// Tests the stable spectating slice: START/STOP/FRAMES/CANT_SPECTATE fan out the right server
/// packets to the host, the joining/leaving spectator, and the fellow spectators, and logout tears
/// the links down. Packets destined for a player land on that player's outbound queue.
/// </summary>
public class SpectatingTests
{
    private static OnlinePlayer AddPlayer(PlayerRegistry registry, int userId, string name) =>
        registry.Add(new BanchoPresence { UserId = userId, Username = name });

    private static ClientPacketContext Context(OnlinePlayer player, PlayerRegistry registry, byte[] payload) =>
        new(player, registry, new ChannelRegistry(), new MatchRegistry(),
            new ServiceCollection().BuildServiceProvider(), payload, new BanchoPacketWriter(16));

    private static byte[] TargetId(int userId) => new BanchoPacketWriter(8).WriteInt32(userId).ToArray();

    /// <summary>A resident BanchoBot wired with empty registries — enough to route a non-command message.</summary>
    private static BanchoBot Bot() => new(
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

    /// <summary>Builds a SEND_PUBLIC_MESSAGE payload (sender + text + target + sender_id; sender fields ignored).</summary>
    private static byte[] PublicMessage(string text, string target)
    {
        var w = new BanchoPacketWriter(64);
        w.WriteString(string.Empty); // sender (ignored)
        w.WriteString(text);
        w.WriteString(target);
        w.WriteInt32(0);             // sender_id (ignored)
        return w.ToArray();
    }

    /// <summary>Collects the ids of every framed packet in an outbound body.</summary>
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

    private static bool Has(OnlinePlayer p, ServerPacketId id) => PacketIds(p.DequeueAll()).Contains((ushort)id);

    [Fact]
    public async Task Start_attaches_spectator_and_notifies_host()
    {
        var registry = new PlayerRegistry();
        var host = AddPlayer(registry, 10, "Host");
        var spectator = AddPlayer(registry, 20, "Spectator");

        await new StartSpectatingHandler().HandleAsync(Context(spectator, registry, TargetId(host.UserId)));

        Assert.Equal(host.UserId, spectator.SpectatingUserId);
        Assert.Contains(spectator.UserId, host.Spectators);
        Assert.True(Has(host, ServerPacketId.SpectatorJoined));
    }

    [Fact]
    public async Task Second_spectator_exchanges_fellow_notifications()
    {
        var registry = new PlayerRegistry();
        var host = AddPlayer(registry, 10, "Host");
        var first = AddPlayer(registry, 20, "First");
        var second = AddPlayer(registry, 30, "Second");

        await new StartSpectatingHandler().HandleAsync(Context(first, registry, TargetId(host.UserId)));
        first.DequeueAll(); // clear the first spectator's queue
        host.DequeueAll();

        await new StartSpectatingHandler().HandleAsync(Context(second, registry, TargetId(host.UserId)));

        Assert.Equal(2, host.SpectatorCount);
        Assert.True(Has(host, ServerPacketId.SpectatorJoined));           // host learns Second joined
        Assert.True(Has(first, ServerPacketId.FellowSpectatorJoined));    // existing spectator learns of Second
        Assert.True(Has(second, ServerPacketId.FellowSpectatorJoined));   // Second learns of First
    }

    [Fact]
    public async Task Frames_relay_verbatim_to_all_spectators_only()
    {
        var registry = new PlayerRegistry();
        var host = AddPlayer(registry, 10, "Host");
        var spectator = AddPlayer(registry, 20, "Spectator");
        var bystander = AddPlayer(registry, 30, "Bystander");

        await new StartSpectatingHandler().HandleAsync(Context(spectator, registry, TargetId(host.UserId)));
        spectator.DequeueAll();

        var frame = new byte[] { 1, 2, 3, 4, 5 };
        await new SpectateFramesHandler().HandleAsync(Context(host, registry, frame));

        var body = spectator.DequeueAll();
        Assert.Contains((ushort)ServerPacketId.SpectateFrames, PacketIds(body));
        // The raw frame bytes are relayed verbatim after the 7-byte header.
        Assert.Equal(frame, body[7..]);
        Assert.Empty(bystander.DequeueAll()); // non-spectators receive nothing
    }

    [Fact]
    public async Task Stop_detaches_and_notifies_host_and_fellows()
    {
        var registry = new PlayerRegistry();
        var host = AddPlayer(registry, 10, "Host");
        var leaver = AddPlayer(registry, 20, "Leaver");
        var stayer = AddPlayer(registry, 30, "Stayer");

        await new StartSpectatingHandler().HandleAsync(Context(leaver, registry, TargetId(host.UserId)));
        await new StartSpectatingHandler().HandleAsync(Context(stayer, registry, TargetId(host.UserId)));
        host.DequeueAll();
        stayer.DequeueAll();

        await new StopSpectatingHandler().HandleAsync(Context(leaver, registry, []));

        Assert.Null(leaver.SpectatingUserId);
        Assert.DoesNotContain(leaver.UserId, host.Spectators);
        Assert.True(Has(host, ServerPacketId.SpectatorLeft));            // host learns Leaver left
        Assert.True(Has(stayer, ServerPacketId.FellowSpectatorLeft));    // remaining spectator learns of it
    }

    [Fact]
    public async Task CantSpectate_relays_to_host_and_spectators()
    {
        var registry = new PlayerRegistry();
        var host = AddPlayer(registry, 10, "Host");
        var spectator = AddPlayer(registry, 20, "Spectator");

        await new StartSpectatingHandler().HandleAsync(Context(spectator, registry, TargetId(host.UserId)));
        host.DequeueAll();
        spectator.DequeueAll();

        await new CantSpectateHandler().HandleAsync(Context(spectator, registry, []));

        Assert.True(Has(host, ServerPacketId.SpectatorCantSpectate));       // host is told
        Assert.True(Has(spectator, ServerPacketId.SpectatorCantSpectate));  // the sender gets it too (renders "downloading")
    }

    [Fact]
    public async Task Restart_same_host_reannounces_without_double_adding()
    {
        var registry = new PlayerRegistry();
        var host = AddPlayer(registry, 10, "Host");
        var spectator = AddPlayer(registry, 20, "Spectator");

        await new StartSpectatingHandler().HandleAsync(Context(spectator, registry, TargetId(host.UserId)));
        host.DequeueAll();

        await new StartSpectatingHandler().HandleAsync(Context(spectator, registry, TargetId(host.UserId)));

        Assert.Equal(1, host.SpectatorCount);                    // not added twice
        Assert.True(Has(host, ServerPacketId.SpectatorJoined));  // re-announced to the host
    }

    [Fact]
    public async Task Cannot_spectate_self()
    {
        var registry = new PlayerRegistry();
        var host = AddPlayer(registry, 10, "Host");

        await new StartSpectatingHandler().HandleAsync(Context(host, registry, TargetId(host.UserId)));

        Assert.Null(host.SpectatingUserId);
        Assert.Equal(0, host.SpectatorCount);
    }

    [Fact]
    public async Task Logout_of_spectator_removes_it_from_host()
    {
        var registry = new PlayerRegistry();
        var host = AddPlayer(registry, 10, "Host");
        var spectator = AddPlayer(registry, 20, "Spectator");

        await new StartSpectatingHandler().HandleAsync(Context(spectator, registry, TargetId(host.UserId)));
        host.DequeueAll();

        await new LogoutHandler().HandleAsync(Context(spectator, registry, []));

        Assert.DoesNotContain(spectator.UserId, host.Spectators);
        Assert.True(Has(host, ServerPacketId.SpectatorLeft));
    }

    [Fact]
    public async Task Logout_of_host_clears_its_spectators_spectating_pointer()
    {
        var registry = new PlayerRegistry();
        var host = AddPlayer(registry, 10, "Host");
        var spectator = AddPlayer(registry, 20, "Spectator");

        await new StartSpectatingHandler().HandleAsync(Context(spectator, registry, TargetId(host.UserId)));

        await new LogoutHandler().HandleAsync(Context(host, registry, []));

        Assert.Null(spectator.SpectatingUserId); // no stale pointer to a gone host
        Assert.Equal(0, host.SpectatorCount);
    }

    // ---- #spectator ephemeral chat channel ----

    [Fact]
    public async Task First_spectator_opens_the_channel_for_both_host_and_spectator()
    {
        var registry = new PlayerRegistry();
        var host = AddPlayer(registry, 10, "Host");
        var spectator = AddPlayer(registry, 20, "Spectator");

        await new StartSpectatingHandler().HandleAsync(Context(spectator, registry, TargetId(host.UserId)));

        // Both are members of the host's channel and both were told to open the tab.
        Assert.True(host.SpectatorChannel.Contains(host.UserId));      // host joins its own channel
        Assert.True(host.SpectatorChannel.Contains(spectator.UserId));
        Assert.True(host.IsInChannel("#spectator"));
        Assert.True(spectator.IsInChannel("#spectator"));
        Assert.True(Has(host, ServerPacketId.ChannelJoinSuccess));
        Assert.True(Has(spectator, ServerPacketId.ChannelJoinSuccess));
    }

    [Fact]
    public async Task Chat_in_spectator_channel_reaches_host_and_fellows()
    {
        var registry = new PlayerRegistry();
        var host = AddPlayer(registry, 10, "Host");
        var first = AddPlayer(registry, 20, "First");
        var second = AddPlayer(registry, 30, "Second");

        await new StartSpectatingHandler().HandleAsync(Context(first, registry, TargetId(host.UserId)));
        await new StartSpectatingHandler().HandleAsync(Context(second, registry, TargetId(host.UserId)));
        host.DequeueAll();
        first.DequeueAll();
        second.DequeueAll();

        // The spectator "First" posts to #spectator; the message resolves via the host it watches.
        await new SendPublicMessageHandler(Bot()).HandleAsync(
            Context(first, registry, PublicMessage("hi all", "#spectator")));

        Assert.True(Has(host, ServerPacketId.SendMessage));    // host sees it
        Assert.True(Has(second, ServerPacketId.SendMessage));  // fellow spectator sees it
        Assert.False(Has(first, ServerPacketId.SendMessage));  // sender does not get its own echo
    }

    [Fact]
    public async Task Host_can_chat_in_its_own_spectator_channel()
    {
        var registry = new PlayerRegistry();
        var host = AddPlayer(registry, 10, "Host");
        var spectator = AddPlayer(registry, 20, "Spectator");

        await new StartSpectatingHandler().HandleAsync(Context(spectator, registry, TargetId(host.UserId)));
        host.DequeueAll();
        spectator.DequeueAll();

        // The host has no SpectatingUserId, so #spectator must resolve to its own channel.
        await new SendPublicMessageHandler(Bot()).HandleAsync(
            Context(host, registry, PublicMessage("welcome", "#spectator")));

        Assert.True(Has(spectator, ServerPacketId.SendMessage));
    }

    [Fact]
    public async Task Last_spectator_leaving_closes_the_channel_for_the_host()
    {
        var registry = new PlayerRegistry();
        var host = AddPlayer(registry, 10, "Host");
        var spectator = AddPlayer(registry, 20, "Spectator");

        await new StartSpectatingHandler().HandleAsync(Context(spectator, registry, TargetId(host.UserId)));
        host.DequeueAll();
        spectator.DequeueAll();

        await new StopSpectatingHandler().HandleAsync(Context(spectator, registry, []));

        // Both left the channel; both got a CHANNEL_KICK to close the tab.
        Assert.False(host.SpectatorChannel.Contains(host.UserId));       // host left when the last spectator did
        Assert.False(host.SpectatorChannel.Contains(spectator.UserId));
        Assert.False(host.IsInChannel("#spectator"));
        Assert.False(spectator.IsInChannel("#spectator"));
        Assert.True(Has(host, ServerPacketId.ChannelKick));
        Assert.True(Has(spectator, ServerPacketId.ChannelKick));
    }

    [Fact]
    public async Task Host_stays_in_channel_while_a_fellow_spectator_remains()
    {
        var registry = new PlayerRegistry();
        var host = AddPlayer(registry, 10, "Host");
        var leaver = AddPlayer(registry, 20, "Leaver");
        var stayer = AddPlayer(registry, 30, "Stayer");

        await new StartSpectatingHandler().HandleAsync(Context(leaver, registry, TargetId(host.UserId)));
        await new StartSpectatingHandler().HandleAsync(Context(stayer, registry, TargetId(host.UserId)));

        await new StopSpectatingHandler().HandleAsync(Context(leaver, registry, []));

        Assert.True(host.SpectatorChannel.Contains(host.UserId));         // host keeps the channel open
        Assert.True(host.SpectatorChannel.Contains(stayer.UserId));
        Assert.False(host.SpectatorChannel.Contains(leaver.UserId));      // only the leaver left
    }

    [Fact]
    public void Spectator_channel_is_not_advertised_in_the_login_channel_list()
    {
        // #spectator is instanced per host (hung off OnlinePlayer), so it must never appear in the
        // shared ChannelRegistry that the login burst advertises.
        var channels = new ChannelRegistry();
        Assert.Null(channels.Get("#spectator"));
        Assert.DoesNotContain(channels.All, c => c.Name == "#spectator");
    }
}
