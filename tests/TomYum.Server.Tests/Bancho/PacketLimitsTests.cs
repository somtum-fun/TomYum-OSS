using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TomYum.Bancho;
using TomYum.Domain.Entities;
using TomYum.Server.Bancho;
using TomYum.Server.Bancho.Packets;
using TomYum.Server.Beatmaps;
using Xunit;

namespace TomYum.Server.Tests.Bancho;

/// <summary>
/// What a client can't make the dispatcher do: read past the request, run more packets than a poll
/// has, or stop the rest of a request with one packet that throws.
/// </summary>
public class PacketLimitsTests
{
    /// <summary>Counts the packets it's given; throws on every one when told to.</summary>
    private sealed class CountingHandler(ClientPacketId id, Exception? failure = null) : IClientPacketHandler
    {
        public int Count { get; private set; }

        public ClientPacketId PacketId => id;

        public Task HandleAsync(ClientPacketContext context, CancellationToken cancellationToken = default)
        {
            Count++;
            return failure is null ? Task.CompletedTask : throw failure;
        }
    }

    private static byte[] Frame(ClientPacketId id, uint declaredLength, byte[] payload)
    {
        var frame = new byte[PacketHeader.Size + payload.Length];
        BitConverter.GetBytes((ushort)id).CopyTo(frame, 0);
        BitConverter.GetBytes(declaredLength).CopyTo(frame, 3);
        payload.CopyTo(frame, PacketHeader.Size);
        return frame;
    }

    private static Task<byte[]> DispatchAsync(BanchoPacketDispatcher dispatcher, byte[] body)
    {
        var registry = new PlayerRegistry();
        var player = registry.Add(new BanchoPresence { UserId = 2, Username = "TomYum" });
        return dispatcher.DispatchAsync(player, registry, new ChannelRegistry(), new MatchRegistry(),
            new ServiceCollection().BuildServiceProvider(), body);
    }

    [Theory]
    [InlineData(uint.MaxValue)]          // was an OverflowException
    [InlineData(int.MaxValue)]           // offset + length wrapped around past the bounds check
    [InlineData(int.MaxValue - 6)]
    [InlineData(4u * 1024 * 1024)]       // more than any packet a client sends
    public async Task AnAbsurdLength_EndsTheRequest_WithoutThrowing(uint length)
    {
        var ping = new CountingHandler(ClientPacketId.Ping);
        var dispatcher = new BanchoPacketDispatcher([ping], NullLogger<BanchoPacketDispatcher>.Instance);
        byte[] body = [.. Frame(ClientPacketId.Ping, 0, []), .. Frame(ClientPacketId.Ping, length, [1, 2, 3]), .. Frame(ClientPacketId.Ping, 0, [])];

        await DispatchAsync(dispatcher, body);

        Assert.Equal(1, ping.Count);
    }

    [Fact]
    public async Task OnlySoManyPackets_AreRunFromOneRequest()
    {
        var ping = new CountingHandler(ClientPacketId.Ping);
        var dispatcher = new BanchoPacketDispatcher([ping], NullLogger<BanchoPacketDispatcher>.Instance);
        var one = Frame(ClientPacketId.Ping, 0, []);
        var body = Enumerable.Repeat(one, BanchoPacketDispatcher.MaxPacketsPerRequest + 100).SelectMany(b => b).ToArray();

        await DispatchAsync(dispatcher, body);

        Assert.Equal(BanchoPacketDispatcher.MaxPacketsPerRequest, ping.Count);
    }

    [Fact]
    public async Task AHandlerThatThrows_DoesntStopTheRestOfTheRequest()
    {
        var broken = new CountingHandler(ClientPacketId.ChangeAction, new KeyNotFoundException());
        var ping = new CountingHandler(ClientPacketId.Ping);
        var dispatcher = new BanchoPacketDispatcher([broken, ping], NullLogger<BanchoPacketDispatcher>.Instance);
        byte[] body = [.. Frame(ClientPacketId.ChangeAction, 0, []), .. Frame(ClientPacketId.Ping, 0, [])];

        await DispatchAsync(dispatcher, body);

        Assert.Equal(1, broken.Count);
        Assert.Equal(1, ping.Count);
    }

    [Theory]
    [InlineData("!roll 2147483647", "(1-1000000)")]
    [InlineData("!roll 5", "(1-5)")]
    [InlineData("!roll", "(1-100)")]
    public async Task Roll_IsCapped(string command, string range)
    {
        var commands = new BanchoBotCommands(
            new TomYumVersion(), new PlayerRegistry(), new ChannelRegistry(), new MatchRegistry(), new BeatmapService(new NoMaps()),
            TestScopeFactory.Create(new InMemoryUserRepository()),
            new Plugins.PluginCommandRegistry(), new Plugins.LoadedPluginRegistry());
        var caller = new PlayerRegistry().Add(new BanchoPresence { UserId = 2, Username = "TomYum" });

        var reply = await commands.TryHandleAsync(caller, command, BanchoPrivileges.Player);

        Assert.EndsWith(range, reply);
    }

    private sealed class NoMaps : IOsuApiClient
    {
        public Task<Beatmap?> GetByMd5Async(string md5, CancellationToken cancellationToken = default) => Task.FromResult<Beatmap?>(null);

        public Task<IReadOnlyList<Beatmap>> GetSetAsync(int setId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Beatmap>>([]);

        public Task<IReadOnlyList<Beatmap>> GetSinceAsync(DateTimeOffset since, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Beatmap>>([]);

        public bool SupportsSince => false;
    }
}
