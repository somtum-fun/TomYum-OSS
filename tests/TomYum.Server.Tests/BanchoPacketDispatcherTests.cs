using Microsoft.Extensions.Logging.Abstractions;
using TomYum.Bancho;
using TomYum.Domain.Entities;
using TomYum.Server.Bancho;
using TomYum.Server.Bancho.Packets;
using TomYum.Server.Beatmaps;
using Xunit;

namespace TomYum.Server.Tests;

/// <summary>
/// Tests the post-login packet dispatch path: a poll body of client packets is decoded and
/// routed to handlers, which mutate online state and fan packets out to the originator (the
/// poll response) and to other online players (their outbound queues).
/// </summary>
public class BanchoPacketDispatcherTests
{
    private static readonly BanchoBot Bot = new(new BanchoBotCommands(
        new TomYumVersion(), new PlayerRegistry(), new ChannelRegistry(), new BeatmapService(new StubApiClient()),
        TestScopeFactory.Create(new InMemoryUserRepository())));

    /// <summary>An <see cref="IOsuApiClient"/> that knows no maps — enough to construct BeatmapService.</summary>
    private sealed class StubApiClient : IOsuApiClient
    {
        public Task<Beatmap?> GetByMd5Async(string md5, CancellationToken cancellationToken = default) =>
            Task.FromResult<Beatmap?>(null);
    }

    private static BanchoPacketDispatcher NewDispatcher() => new(
        [
            new PingHandler(),
            new ChangeActionHandler(),
            new RequestStatusUpdateHandler(),
            new UserStatsRequestHandler(),
            new UserPresenceRequestHandler(),
            new UserPresenceRequestAllHandler(),
            new LogoutHandler(),
            new SendPublicMessageHandler(Bot),
            new SendPrivateMessageHandler(Bot),
            new ChannelJoinHandler(),
            new ChannelPartHandler(),
        ],
        NullLogger<BanchoPacketDispatcher>.Instance);

    private static OnlinePlayer AddPlayer(PlayerRegistry registry, int userId, string name) =>
        registry.Add(new BanchoPresence { UserId = userId, Username = name });

    /// <summary>Frames a single client packet (u16 id + pad + u32 length + payload).</summary>
    private static byte[] FrameClientPacket(ClientPacketId id, ReadOnlySpan<byte> payload)
    {
        var buffer = new byte[PacketHeader.Size + payload.Length];
        buffer[0] = (byte)((ushort)id & 0xFF);
        buffer[1] = (byte)((ushort)id >> 8);
        // buffer[2] = compression/pad = 0
        var len = (uint)payload.Length;
        buffer[3] = (byte)(len & 0xFF);
        buffer[4] = (byte)((len >> 8) & 0xFF);
        buffer[5] = (byte)((len >> 16) & 0xFF);
        buffer[6] = (byte)((len >> 24) & 0xFF);
        payload.CopyTo(buffer.AsSpan(PacketHeader.Size));
        return buffer;
    }

    /// <summary>Collects the ids of every framed packet in a server→client body. Synchronous
    /// because <see cref="BanchoPacketReader"/> is a ref struct.</summary>
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

    [Fact]
    public async Task ChangeAction_UpdatesPresence_AndBroadcastsStatsToOthers()
    {
        var registry = new PlayerRegistry();
        var dispatcher = NewDispatcher();
        var sender = AddPlayer(registry, 2, "TomYum");
        var observer = AddPlayer(registry, 3, "Observer");

        // CHANGE_ACTION: u8 action + string info + string md5 + u32 mods + u8 mode + i32 map_id.
        var payload = new BanchoPacketWriter(32);
        payload.WriteByte(2); // action = Playing
        payload.WriteString("Some Map [Insane]");
        payload.WriteString("abc123md5");
        payload.WriteUInt32(0);
        payload.WriteByte(0); // mode = osu!
        payload.WriteInt32(42);
        var body = FrameClientPacket(ClientPacketId.ChangeAction, payload.ToArray());

        var produced = await dispatcher.DispatchAsync(sender, registry, new ChannelRegistry(), body);

        // Presence was folded in.
        Assert.Equal(2, sender.Presence.Action);
        Assert.Equal("Some Map [Insane]", sender.Presence.ActionInfo);
        Assert.Equal(42, sender.Presence.BeatmapId);

        // Originator gets its own refreshed stats echoed back in the response.
        Assert.Contains((ushort)ServerPacketId.UserStats, PacketIds(produced));

        // The observer received the sender's UserStats on its queue.
        Assert.Contains((ushort)ServerPacketId.UserStats, PacketIds(observer.DequeueAll()));
    }

    [Fact]
    public async Task UserStatsRequest_RepliesWithStatsForOnlineIds()
    {
        var registry = new PlayerRegistry();
        var dispatcher = NewDispatcher();
        var requester = AddPlayer(registry, 2, "TomYum");
        AddPlayer(registry, 3, "Target");

        // USER_STATS_REQUEST: i32 list (u16 count + ids). Request id 3 (online) and 99 (offline).
        var payload = new BanchoPacketWriter(16);
        payload.WriteInt32List([3, 99]);
        var body = FrameClientPacket(ClientPacketId.UserStatsRequest, payload.ToArray());

        var produced = await dispatcher.DispatchAsync(requester, registry, new ChannelRegistry(), body);

        // Exactly one UserStats (for id 3); the offline id 99 is skipped.
        Assert.Equal(1, PacketIds(produced).Count(id => id == (ushort)ServerPacketId.UserStats));
    }

    [Fact]
    public async Task RequestStatusUpdate_RepliesWithOwnStats()
    {
        var registry = new PlayerRegistry();
        var dispatcher = NewDispatcher();
        var player = AddPlayer(registry, 2, "TomYum");

        var body = FrameClientPacket(ClientPacketId.RequestStatusUpdate, []);
        var produced = await dispatcher.DispatchAsync(player, registry, new ChannelRegistry(), body);

        Assert.Contains((ushort)ServerPacketId.UserStats, PacketIds(produced));
    }

    [Fact]
    public async Task UserPresenceRequestAll_RepliesWithPresenceForEveryOnlinePlayer()
    {
        var registry = new PlayerRegistry();
        var dispatcher = NewDispatcher();
        var requester = AddPlayer(registry, 2, "TomYum");
        AddPlayer(registry, 3, "Other");

        var body = FrameClientPacket(ClientPacketId.UserPresenceRequestAll, []);
        var produced = await dispatcher.DispatchAsync(requester, registry, new ChannelRegistry(), body);

        Assert.Equal(2, PacketIds(produced).Count(id => id == (ushort)ServerPacketId.UserPresence));
    }

    [Fact]
    public async Task Logout_RemovesPlayer_AndBroadcastsUserLogout()
    {
        var registry = new PlayerRegistry();
        var dispatcher = NewDispatcher();
        var leaver = AddPlayer(registry, 2, "TomYum");
        var observer = AddPlayer(registry, 3, "Observer");

        // LOGOUT carries a trailing i32 the handler ignores.
        var payload = new BanchoPacketWriter(8);
        payload.WriteInt32(0);
        var body = FrameClientPacket(ClientPacketId.Logout, payload.ToArray());

        await dispatcher.DispatchAsync(leaver, registry, new ChannelRegistry(), body);

        Assert.Null(registry.GetByUserId(2));
        Assert.Equal(1, registry.Count);
        Assert.Contains((ushort)ServerPacketId.UserLogout, PacketIds(observer.DequeueAll()));
    }

    [Fact]
    public async Task MultiplePacketsInOneBody_AreAllDispatched()
    {
        var registry = new PlayerRegistry();
        var dispatcher = NewDispatcher();
        var player = AddPlayer(registry, 2, "TomYum");

        // Ping (no-op) followed by RequestStatusUpdate (replies with own stats).
        var ping = FrameClientPacket(ClientPacketId.Ping, []);
        var status = FrameClientPacket(ClientPacketId.RequestStatusUpdate, []);
        var body = new byte[ping.Length + status.Length];
        ping.CopyTo(body, 0);
        status.CopyTo(body, ping.Length);

        var produced = await dispatcher.DispatchAsync(player, registry, new ChannelRegistry(), body);

        Assert.Contains((ushort)ServerPacketId.UserStats, PacketIds(produced));
    }

    [Fact]
    public async Task UnknownPacketId_IsSkipped_WithoutThrowing()
    {
        var registry = new PlayerRegistry();
        var dispatcher = NewDispatcher();
        var player = AddPlayer(registry, 2, "TomYum");

        // Id 105 (RTX) has no handler; a following Ping must still process.
        var unknown = FrameClientPacket((ClientPacketId)105, [1, 2, 3]);
        var ping = FrameClientPacket(ClientPacketId.Ping, []);
        var body = new byte[unknown.Length + ping.Length];
        unknown.CopyTo(body, 0);
        ping.CopyTo(body, unknown.Length);

        var produced = await dispatcher.DispatchAsync(player, registry, new ChannelRegistry(), body);

        // No reply expected, but the call must not throw and must consume the whole stream.
        Assert.Empty(produced);
    }
}
