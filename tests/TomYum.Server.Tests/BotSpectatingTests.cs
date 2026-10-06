using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using TomYum.Bancho;
using TomYum.Plugins.Abstractions;
using TomYum.Server.Bancho;
using TomYum.Server.Bancho.Packets;
using TomYum.Server.Bancho.Packets.Spectate;
using TomYum.Server.Plugins;

using Xunit;

namespace TomYum.Server.Tests;

/// <summary>
/// BanchoBot spectating for plugins (<see cref="IHostSpectating"/>, osu!dan): the player's client is
/// told BanchoBot joined, frames raise <see cref="SpectateFrameEvent"/> with the action read, nothing is
/// queued for the bot, and it mixes with human spectators.
/// </summary>
public class BotSpectatingTests
{
    private static (PlayerRegistry Registry, OnlinePlayer Bot) Registry()
    {
        var registry = new PlayerRegistry();
        var bot = registry.Add(BanchoBot.CreatePresence());
        return (registry, bot);
    }

    private static OnlinePlayer AddPlayer(PlayerRegistry registry, int userId, string name) =>
        registry.Add(new BanchoPresence { UserId = userId, Username = name });

    private static ClientPacketContext Context(OnlinePlayer player, PlayerRegistry registry, byte[] payload, IServiceProvider? services = null) =>
        new(player, registry, new ChannelRegistry(), new MatchRegistry(),
            services ?? new ServiceCollection().BuildServiceProvider(), payload, new BanchoPacketWriter(16));

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

    /// <summary>A frame bundle in today's layout: extra, count, frames, action, a 29-byte score frame, sequence.</summary>
    private static byte[] Bundle(byte action, int frames = 2)
    {
        var bundle = new List<byte>();
        bundle.AddRange(BitConverter.GetBytes(0));
        bundle.AddRange(BitConverter.GetBytes((ushort)frames));
        bundle.AddRange(new byte[frames * LegacyFrames.ReplayFrameLength]);
        bundle.Add(action);
        bundle.AddRange(new byte[29]);
        bundle.AddRange(BitConverter.GetBytes((ushort)1));
        return [.. bundle];
    }

    [Fact]
    public void AttachBot_tells_the_player_and_queues_nothing_for_the_bot()
    {
        var (registry, bot) = Registry();
        var host = AddPlayer(registry, 10, "Host");
        var spectating = new HostSpectating(registry);

        Assert.True(spectating.AttachBot(host.UserId));
        Assert.True(spectating.AttachBot(host.UserId)); // idempotent

        Assert.True(spectating.IsBotWatching(host.UserId));
        Assert.Equal(1, host.SpectatorCount);
        Assert.Contains(BanchoBot.UserId, host.Spectators);
        Assert.True(host.IsInChannel("#spectator"));
        var ids = PacketIds(host.DequeueAll());
        Assert.Single(ids, id => id == (ushort)ServerPacketId.SpectatorJoined);
        Assert.Null(bot.SpectatingUserId); // the bot keeps no link: it can watch many players
        Assert.Equal(0, bot.QueuedBytes);
    }

    [Fact]
    public void The_bot_watches_several_players_at_once()
    {
        var (registry, _) = Registry();
        var a = AddPlayer(registry, 10, "A");
        var b = AddPlayer(registry, 11, "B");
        var spectating = new HostSpectating(registry);

        Assert.True(spectating.AttachBot(a.UserId));
        Assert.True(spectating.AttachBot(b.UserId));

        Assert.True(spectating.IsBotWatching(a.UserId));
        Assert.True(spectating.IsBotWatching(b.UserId));
    }

    [Fact]
    public void DetachBot_tells_the_player_and_closes_the_channel()
    {
        var (registry, _) = Registry();
        var host = AddPlayer(registry, 10, "Host");
        var spectating = new HostSpectating(registry);
        spectating.AttachBot(host.UserId);
        host.DequeueAll();

        Assert.True(spectating.DetachBot(host.UserId));
        Assert.False(spectating.DetachBot(host.UserId));

        Assert.False(spectating.IsBotWatching(host.UserId));
        Assert.Equal(0, host.SpectatorCount);
        Assert.False(host.IsInChannel("#spectator"));
        Assert.True(Has(host, ServerPacketId.SpectatorLeft));
    }

    [Fact]
    public void Offline_irc_and_bot_targets_are_refused()
    {
        var (registry, _) = Registry();
        var irc = AddPlayer(registry, 12, "Irc");
        irc.IsIrc = true;
        var spectating = new HostSpectating(registry);

        Assert.False(spectating.AttachBot(99));
        Assert.False(spectating.AttachBot(irc.UserId));
        Assert.False(spectating.AttachBot(BanchoBot.UserId));
        Assert.False(spectating.IsBotWatching(99));
    }

    [Fact]
    public async Task A_human_spectator_alongside_the_bot_keeps_the_channel_until_both_leave()
    {
        var (registry, bot) = Registry();
        var host = AddPlayer(registry, 10, "Host");
        var human = AddPlayer(registry, 20, "Human");
        var spectating = new HostSpectating(registry);
        spectating.AttachBot(host.UserId);

        await new StartSpectatingHandler().HandleAsync(Context(human, registry, new BanchoPacketWriter(8).WriteInt32(host.UserId).ToArray()));
        Assert.Equal(2, host.SpectatorCount);
        Assert.True(Has(human, ServerPacketId.FellowSpectatorJoined)); // the human sees BanchoBot

        // Frames reach the human only; nothing piles up on the bot.
        await new SpectateFramesHandler().HandleAsync(Context(host, registry, Bundle(ReplayActions.Standard)));
        Assert.True(Has(human, ServerPacketId.SpectateFrames));
        Assert.Equal(0, bot.QueuedBytes);

        spectating.DetachBot(host.UserId);
        Assert.True(host.IsInChannel("#spectator")); // the human is still there
        await new StopSpectatingHandler().HandleAsync(Context(human, registry, []));
        Assert.False(host.IsInChannel("#spectator"));
    }

    [Fact]
    public async Task Logout_of_a_watched_player_drops_the_bot()
    {
        var (registry, _) = Registry();
        var host = AddPlayer(registry, 10, "Host");
        var spectating = new HostSpectating(registry);
        spectating.AttachBot(host.UserId);

        await new LogoutHandler().HandleAsync(Context(host, registry, []));

        Assert.Equal(0, host.SpectatorCount);
    }

    [Fact]
    public async Task Frames_with_only_the_bot_watching_raise_the_event_with_the_action()
    {
        var (registry, bot) = Registry();
        var host = AddPlayer(registry, 10, "Host");
        new HostSpectating(registry).AttachBot(host.UserId);

        var bus = new EventBus(NullLogger<EventBus>.Instance);
        var seen = new List<SpectateFrameEvent>();
        bus.Subscribe<SpectateFrameEvent>((e, _) => { seen.Add(e); return Task.CompletedTask; });
        var services = new ServiceCollection().AddSingleton<IEventBus>(bus).BuildServiceProvider();

        await new SpectateFramesHandler().HandleAsync(Context(host, registry, Bundle(ReplayActions.Pause), services));
        await new SpectateFramesHandler().HandleAsync(Context(host, registry, [1, 2, 3], services));

        Assert.Equal(2, seen.Count);
        Assert.Equal(host.UserId, seen[0].HostUserId);
        Assert.Equal(ReplayActions.Pause, seen[0].Action);
        Assert.Null(seen[1].Action); // unreadable bundle
        Assert.Equal(0, bot.QueuedBytes);
    }

    [Theory]
    [InlineData("b20240123.2", true)]
    [InlineData("b20140624", true)]
    [InlineData("b20130513", true)]
    [InlineData("b20130303", false)]
    [InlineData("b1122", false)]
    [InlineData("", true)]
    public void Checkable_frames_follow_the_build(string version, bool checkable) =>
        Assert.Equal(checkable, ClientBuild.Parse(version).HasCheckableFrames);

    [Fact]
    public void ReadAction_reads_todays_layout_and_refuses_short_or_odd_bundles()
    {
        Assert.Equal(ReplayActions.Unpause, LegacyFrames.ReadAction(Bundle(ReplayActions.Unpause, frames: 0)));
        Assert.Equal(ReplayActions.Pause, LegacyFrames.ReadAction(Bundle(ReplayActions.Pause, frames: 5)));
        Assert.Null(LegacyFrames.ReadAction(Bundle(200)));
        Assert.Null(LegacyFrames.ReadAction(Bundle(ReplayActions.Pause).AsSpan(0, 10))); // cut before the action
        Assert.Null(LegacyFrames.ReadAction([]));
    }
}
