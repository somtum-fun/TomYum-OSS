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
/// How much one session can make the server hold or send: message length and rate, status text,
/// the id lists of stats and presence requests, and its outbound queue.
/// </summary>
public class SessionLimitsTests
{
    private static BanchoBot NewBot() => new(new BanchoBotCommands(
        new TomYumVersion(), new PlayerRegistry(), new ChannelRegistry(), new MatchRegistry(), new BeatmapService(new NoMaps()),
        TestScopeFactory.Create(new InMemoryUserRepository()),
        new Plugins.PluginCommandRegistry(), new Plugins.LoadedPluginRegistry()));

    private sealed class NoMaps : IOsuApiClient
    {
        public Task<Beatmap?> GetByMd5Async(string md5, CancellationToken cancellationToken = default) => Task.FromResult<Beatmap?>(null);

        public Task<IReadOnlyList<Beatmap>> GetSetAsync(int setId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Beatmap>>([]);

        public Task<IReadOnlyList<Beatmap>> GetSinceAsync(DateTimeOffset since, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Beatmap>>([]);

        public bool SupportsSince => false;
    }

    private static byte[] Message(string text, string target) =>
        new BanchoPacketWriter(64).WriteString(string.Empty).WriteString(text).WriteString(target).WriteInt32(0).ToArray();

    private static ClientPacketContext Context(OnlinePlayer player, PlayerRegistry registry, ChannelRegistry channels, byte[] payload, BanchoPacketWriter response) =>
        new(player, registry, channels, new MatchRegistry(), new ServiceCollection().BuildServiceProvider(), payload, response);

    /// <summary>The texts of the chat messages in a server→client body.</summary>
    private static List<string> Texts(byte[] body)
    {
        var texts = new List<string>();
        var reader = new BanchoPacketReader(body);
        while (reader.CanReadHeader)
        {
            var header = reader.ReadHeader();
            var payload = new BanchoPacketReader(reader.ReadBytes((int)header.Length));
            if (header.Id == (ClientPacketId)(ushort)ServerPacketId.SendMessage)
            {
                _ = payload.ReadString();
                texts.Add(payload.ReadString());
            }
        }
        return texts;
    }

    private static int Count(byte[] body, ServerPacketId id)
    {
        var count = 0;
        var reader = new BanchoPacketReader(body);
        while (reader.CanReadHeader)
        {
            var header = reader.ReadHeader();
            reader.Skip((int)header.Length);
            if ((ushort)header.Id == (ushort)id)
                count++;
        }
        return count;
    }

    [Fact]
    public async Task LongMessages_AreCut()
    {
        var registry = new PlayerRegistry();
        var channels = new ChannelRegistry();
        var sender = registry.Add(new BanchoPresence { UserId = 2, Username = "Sender" });
        var listener = registry.Add(new BanchoPresence { UserId = 3, Username = "Listener" });
        channels.Get("#osu")!.Join(sender);
        channels.Get("#osu")!.Join(listener);

        await new SendPublicMessageHandler(NewBot()).HandleAsync(Context(sender, registry, channels, Message(new string('a', 100_000), "#osu"), new BanchoPacketWriter()));
        await new SendPrivateMessageHandler(NewBot()).HandleAsync(Context(sender, registry, channels, Message(new string('b', 100_000), "Listener"), new BanchoPacketWriter()));

        var texts = Texts(listener.DequeueAll());
        Assert.Equal([ChatLimits.MaxMessageLength, ChatLimits.MaxMessageLength], texts.Select(t => t.Length));
    }

    [Fact]
    public async Task TooManyMessagesTooFast_AreDropped_AndTheSenderIsTold()
    {
        var registry = new PlayerRegistry();
        var channels = new ChannelRegistry();
        var sender = registry.Add(new BanchoPresence { UserId = 2, Username = "Sender" });
        var listener = registry.Add(new BanchoPresence { UserId = 3, Username = "Listener" });
        channels.Get("#osu")!.Join(sender);
        channels.Get("#osu")!.Join(listener);

        var replies = new List<string>();
        for (var i = 0; i < ChatLimits.MessagesPerWindow + 5; i++)
        {
            var response = new BanchoPacketWriter();
            var handler = i % 2 == 0 ? (IClientPacketHandler)new SendPublicMessageHandler(NewBot()) : new SendPrivateMessageHandler(NewBot());
            await handler.HandleAsync(Context(sender, registry, channels, Message("spam " + i, i % 2 == 0 ? "#osu" : "Listener"), response));
            replies.AddRange(Texts(response.ToArray()));
        }

        Assert.Equal(ChatLimits.MessagesPerWindow, Texts(listener.DequeueAll()).Count);
        Assert.Equal(5, replies.Count(r => r == ChatLimits.TooFast));
    }

    [Fact]
    public void ARateLimit_LetsMoreThroughOnceTheWindowHasPassed()
    {
        var limit = new RateLimit(2, TimeSpan.FromSeconds(5));
        var start = DateTimeOffset.UnixEpoch;

        Assert.True(limit.TryTake(start));
        Assert.True(limit.TryTake(start.AddSeconds(1)));
        Assert.False(limit.TryTake(start.AddSeconds(4)));
        Assert.True(limit.TryTake(start.AddSeconds(5)));
        Assert.False(limit.TryTake(start.AddSeconds(5.5)));
        Assert.True(limit.TryTake(start.AddSeconds(6)));
    }

    [Fact]
    public async Task StatusText_IsCut()
    {
        var registry = new PlayerRegistry();
        var player = registry.Add(new BanchoPresence { UserId = 2, Username = "Player" });
        var payload = new BanchoPacketWriter().WriteByte(2).WriteString(new string('x', 100_000)).WriteString(new string('f', 1000))
            .WriteUInt32(0).WriteByte(0).WriteInt32(1).ToArray();
        var services = new ServiceCollection().AddSingleton<TomYum.Plugins.Abstractions.IEventBus>(
            new Plugins.EventBus(NullLogger<Plugins.EventBus>.Instance)).BuildServiceProvider();

        await new ChangeActionHandler().HandleAsync(new ClientPacketContext(player, registry, new ChannelRegistry(), new MatchRegistry(), services, payload, new BanchoPacketWriter()));

        Assert.Equal(512, player.Presence.ActionInfo.Length);
        Assert.Equal(32, player.Presence.BeatmapMd5.Length);
    }

    [Fact]
    public async Task StatsAndPresenceRequests_AnswerEachPlayerOnce()
    {
        var registry = new PlayerRegistry();
        var asker = registry.Add(new BanchoPresence { UserId = 2, Username = "Asker" });
        registry.Add(new BanchoPresence { UserId = 3, Username = "Other" });
        var ids = new BanchoPacketWriter().WriteUInt16(60_000);
        for (var i = 0; i < 60_000; i++)
            ids.WriteInt32(i % 2 == 0 ? 3 : 2);

        var stats = new BanchoPacketWriter();
        await new UserStatsRequestHandler().HandleAsync(Context(asker, registry, new ChannelRegistry(), ids.ToArray(), stats));
        var presence = new BanchoPacketWriter();
        await new UserPresenceRequestHandler().HandleAsync(Context(asker, registry, new ChannelRegistry(), ids.ToArray(), presence));

        Assert.Equal(2, Count(stats.ToArray(), ServerPacketId.UserStats));
        Assert.Equal(2, Count(presence.ToArray(), ServerPacketId.UserPresence));
    }

    [Fact]
    public void AQueuePastItsLimit_IsDropped_AndTheSessionMarked()
    {
        var player = new PlayerRegistry().Add(new BanchoPresence { UserId = 2, Username = "Away" });
        var chunk = new byte[64 * 1024];

        for (var i = 0; i < OnlinePlayer.MaxQueuedBytes / chunk.Length; i++)
            player.Enqueue(chunk);
        Assert.False(player.QueueOverflowed);
        Assert.Equal(OnlinePlayer.MaxQueuedBytes, player.QueuedBytes);

        player.Enqueue(chunk);
        player.Enqueue(chunk);

        Assert.True(player.QueueOverflowed);
        Assert.True(player.HasQueued);
        Assert.Equal(0, player.QueuedBytes);
    }

    [Fact]
    public async Task ASessionWhoseQueueOverflowed_IsLoggedOut_AndToldToReconnect()
    {
        var registry = new PlayerRegistry();
        var channels = new ChannelRegistry();
        var away = registry.Add(new BanchoPresence { UserId = 2, Username = "Away" });
        var other = registry.Add(new BanchoPresence { UserId = 3, Username = "Other" });
        channels.Get("#osu")!.Join(away);
        var dispatcher = new BanchoPacketDispatcher([new LogoutHandler()], NullLogger<BanchoPacketDispatcher>.Instance);
        for (var i = 0; i <= OnlinePlayer.MaxQueuedBytes / 1024; i++)
            away.Enqueue(new byte[1024]);

        var matches = new MatchRegistry();
        var services = new ServiceCollection().AddSingleton(registry).AddSingleton(channels).AddSingleton(matches).AddSingleton(dispatcher)
            .BuildServiceProvider();

        var response = await BanchoExchange.RunAsync(away, [], dispatcher, registry, channels, matches, services);

        Assert.Equal(1, Count(response, ServerPacketId.Restart));
        Assert.Null(registry.GetByToken(away.Token));
        Assert.False(channels.Get("#osu")!.Contains(2));
        Assert.Equal(1, Count(other.DequeueAll(), ServerPacketId.UserLogout));
    }
}
