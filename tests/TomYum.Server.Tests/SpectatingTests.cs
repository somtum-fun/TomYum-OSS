using Microsoft.Extensions.DependencyInjection;

using TomYum.Bancho;
using TomYum.Server.Bancho;
using TomYum.Server.Bancho.Packets;
using TomYum.Server.Bancho.Packets.Spectate;

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
}
