using Microsoft.Extensions.DependencyInjection;
using TomYum.Bancho;
using TomYum.Server.Bancho;
using TomYum.Server.Bancho.Packets;
using TomYum.Server.Beatmaps;
using Xunit;

namespace TomYum.Server.Tests;

/// <summary><c>!lock</c>/<c>!unlock</c>: staff lock a channel, players' messages are dropped without a reply.</summary>
public class ChannelLockTests
{
    private static BanchoBot NewBot() => new(
        new BanchoBotCommands(
            new TomYumVersion(),
            new PlayerRegistry(),
            new ChannelRegistry(),
            new MatchRegistry(),
            new BeatmapService(new NoMaps()),
            TestScopeFactory.Create(new InMemoryUserRepository()),
            new Plugins.PluginCommandRegistry(),
            new Plugins.LoadedPluginRegistry()));

    private sealed class NoMaps : IOsuApiClient
    {
        public Task<TomYum.Domain.Entities.Beatmap?> GetByMd5Async(string md5, CancellationToken cancellationToken = default) =>
            Task.FromResult<TomYum.Domain.Entities.Beatmap?>(null);

        public Task<IReadOnlyList<TomYum.Domain.Entities.Beatmap>> GetSetAsync(int setId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TomYum.Domain.Entities.Beatmap>>([]);

        public Task<IReadOnlyList<TomYum.Domain.Entities.Beatmap>> GetSinceAsync(DateTimeOffset since, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TomYum.Domain.Entities.Beatmap>>([]);

        public bool SupportsSince => false;
    }

    private static readonly BanchoPrivileges Mod = BanchoPrivileges.Player | BanchoPrivileges.Moderator;

    private static byte[] Message(string text, string target)
    {
        var w = new BanchoPacketWriter(64);
        w.WriteString(string.Empty);
        w.WriteString(text);
        w.WriteString(target);
        w.WriteInt32(0);
        return w.ToArray();
    }

    private static async Task<byte[]> Say(OnlinePlayer from, PlayerRegistry players, ChannelRegistry channels, string text, string target)
    {
        var response = new BanchoPacketWriter(64);
        await new SendPublicMessageHandler(NewBot()).HandleAsync(
            new ClientPacketContext(from, players, channels, new MatchRegistry(), new ServiceCollection().BuildServiceProvider(), Message(text, target), response));
        return response.ToArray();
    }

    private static List<string> Texts(byte[] body)
    {
        var texts = new List<string>();
        var reader = new BanchoPacketReader(body);
        while (reader.CanReadHeader)
        {
            var header = reader.ReadHeader();
            if ((ushort)header.Id == (ushort)ServerPacketId.SendMessage)
            {
                _ = reader.ReadString();
                texts.Add(reader.ReadString());
                _ = reader.ReadString();
                _ = reader.ReadInt32();
            }
            else
            {
                reader.Skip((int)header.Length);
            }
        }
        return texts;
    }

    [Fact]
    public async Task Locked_ServerChannel_DropsPlayers_Silently_StaffStillTalk()
    {
        var players = new PlayerRegistry();
        var channels = new ChannelRegistry();
        var player = players.Add(new BanchoPresence { UserId = 2, Username = "Spammer" });
        var staff = players.Add(new BanchoPresence { UserId = 3, Username = "Gmt", Privileges = Mod });
        var listener = players.Add(new BanchoPresence { UserId = 4, Username = "Listener" });
        var osu = channels.Get("#osu")!;
        foreach (var p in new[] { player, staff, listener })
            osu.Join(p);

        await Say(staff, players, channels, "!lock", "#osu");
        Assert.True(osu.Locked);
        Assert.Contains(ChannelLocks.Reply("#osu", true), Texts(listener.DequeueAll()));

        var reply = await Say(player, players, channels, "spam spam spam", "#osu");
        Assert.Empty(reply);                         // no notice to the sender
        Assert.Empty(Texts(listener.DequeueAll()));  // and nobody sees it

        await Say(staff, players, channels, "calm down please", "#osu");
        Assert.Contains("calm down please", Texts(listener.DequeueAll()));

        await Say(staff, players, channels, "!unlock", "#osu");
        Assert.False(osu.Locked);
        listener.DequeueAll();
        await Say(player, players, channels, "sorry", "#osu");
        Assert.Contains("sorry", Texts(listener.DequeueAll()));
    }

    [Fact]
    public async Task Players_CantLock()
    {
        var players = new PlayerRegistry();
        var channels = new ChannelRegistry();
        var player = players.Add(new BanchoPresence { UserId = 2, Username = "Player" });
        channels.Get("#osu")!.Join(player);

        await Say(player, players, channels, "!lock", "#osu");

        Assert.False(channels.Get("#osu")!.Locked);
    }

    [Fact]
    public async Task Locked_Spectator_DropsSpectators()
    {
        var players = new PlayerRegistry();
        var channels = new ChannelRegistry();
        var host = players.Add(new BanchoPresence { UserId = 2, Username = "Host" });
        var staff = players.Add(new BanchoPresence { UserId = 3, Username = "Gmt", Privileges = Mod });
        var spectator = players.Add(new BanchoPresence { UserId = 4, Username = "Watcher" });
        foreach (var p in new[] { host, staff, spectator })
            host.SpectatorChannel.Join(p);
        foreach (var p in new[] { staff, spectator })
        {
            host.AddSpectator(p.UserId);
            p.SpectatingUserId = host.UserId;
        }

        await Say(staff, players, channels, "!lock", "#spectator");
        Assert.True(host.SpectatorChannel.Locked);
        host.DequeueAll();

        Assert.Empty(await Say(spectator, players, channels, "omg omg omg", "#spectator"));
        Assert.Empty(Texts(host.DequeueAll()));
    }

    [Fact]
    public async Task ASpectatorLock_EndsWhenTheLastSpectatorLeaves()
    {
        var players = new PlayerRegistry();
        var channels = new ChannelRegistry();
        var host = players.Add(new BanchoPresence { UserId = 2, Username = "Host" });
        var staff = players.Add(new BanchoPresence { UserId = 3, Username = "Gmt", Privileges = Mod });
        TomYum.Server.Bancho.Packets.Spectate.SpectatorManager.Attach(host, staff, players);
        await Say(staff, players, channels, "!lock", "#spectator");
        Assert.True(host.SpectatorChannel.Locked);

        TomYum.Server.Bancho.Packets.Spectate.SpectatorManager.Detach(host, staff, players);

        Assert.False(host.SpectatorChannel.Locked); // the host's next spectating starts open
    }

    [Fact]
    public async Task Bot_ExplainsLock_InAPm_AndRefusesPlayers()
    {
        var bot = NewBot();
        var player = new OnlinePlayer("t", new BanchoPresence { UserId = 2, Username = "P" });
        var staff = new OnlinePlayer("t", new BanchoPresence { UserId = 3, Username = "S", Privileges = Mod });

        Assert.Equal("That command is staff-only.", await bot.TryHandleCommandAsync(player, "!lock", BanchoPrivileges.Player));
        Assert.Contains("in the channel", await bot.TryHandleCommandAsync(staff, "!lock", Mod), StringComparison.Ordinal);
    }
}
