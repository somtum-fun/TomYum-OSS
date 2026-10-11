using Microsoft.Extensions.DependencyInjection;
using TomYum.Bancho;
using TomYum.Server.Bancho;
using TomYum.Server.Bancho.Packets;
using Xunit;

namespace TomYum.Server.Tests;

/// <summary>When a stable session ends: osu!'s stray LOGOUT after a login, clients that stopped polling, the welcome.</summary>
public class SessionLifetimeTests
{
    private static OnlinePlayer Add(PlayerRegistry registry, int id) =>
        registry.Add(new BanchoPresence { UserId = id, Username = $"Player{id}" });

    private static ClientPacketContext Context(OnlinePlayer player, PlayerRegistry registry) =>
        new(player, registry, new ChannelRegistry(), new MatchRegistry(), new ServiceCollection().BuildServiceProvider(), ReadOnlyMemory<byte>.Empty, new BanchoPacketWriter(16));

    [Fact]
    public async Task A_logout_right_after_login_is_ignored()
    {
        var registry = new PlayerRegistry();
        var player = Add(registry, 2);

        await new LogoutHandler().HandleAsync(Context(player, registry));

        Assert.NotNull(registry.GetByToken(player.Token));
    }

    [Fact]
    public async Task A_logout_after_that_ends_the_session()
    {
        var registry = new PlayerRegistry();
        var player = Add(registry, 2);
        player.LoggedInUtc = DateTimeOffset.UtcNow - LogoutHandler.IgnoreAfterLogin - TimeSpan.FromSeconds(1);

        await new LogoutHandler().HandleAsync(Context(player, registry));

        Assert.Null(registry.GetByToken(player.Token));
    }

    [Fact]
    public async Task The_server_ending_a_session_is_never_ignored()
    {
        var registry = new PlayerRegistry();
        var player = Add(registry, 2);
        player.EndedByServer = true;

        await new LogoutHandler().HandleAsync(Context(player, registry));

        Assert.Null(registry.GetByToken(player.Token));
    }

    [Fact]
    public void Sessions_silent_for_a_minute_are_reaped_but_not_irc_or_banchobot()
    {
        var registry = new PlayerRegistry();
        var now = DateTimeOffset.UtcNow;
        var quiet = Add(registry, 2);
        quiet.LastActivityUtc = now - IdleSessionReaper.IdleLimit - TimeSpan.FromSeconds(1);
        var polling = Add(registry, 3);
        polling.LastActivityUtc = now - TimeSpan.FromSeconds(20);
        var irc = Add(registry, 4);
        irc.IsIrc = true;
        irc.LastActivityUtc = quiet.LastActivityUtc;
        var bot = Add(registry, BanchoBot.UserId);
        bot.LastActivityUtc = quiet.LastActivityUtc;

        Assert.Equal([quiet], IdleSessionReaper.Idle(registry.AllSessions, now));
    }

    [Fact]
    public void A_reconnect_request_queues_restart_and_marks_the_session()
    {
        var player = Add(new PlayerRegistry(), 2);

        player.RequestReconnect();

        Assert.True(player.ReconnectRequested);
        Assert.Equal([ServerPacketId.Restart], PacketIds(player.DequeueAll()));
    }

    [Theory]
    [InlineData(null, null, true, "Welcome to TomYum, Blue!")]
    [InlineData("0", null, false, "Welcome to TomYum, Blue!")]
    [InlineData("1", "Hi {username}, have fun on somtum.fun", true, "Hi Blue, have fun on somtum.fun")]
    [InlineData("1", "   ", true, "Welcome to TomYum, Blue!")]
    public void Welcome_comes_from_the_admin_panel(string? enabled, string? message, bool on, string text)
    {
        var settings = new Dictionary<string, string?>();
        if (enabled is not null)
            settings[BanchoWelcome.EnabledSetting] = enabled;
        if (message is not null)
            settings[BanchoWelcome.MessageSetting] = message;

        var (isOn, template) = BanchoWelcome.FromSettings(settings);

        Assert.Equal(on, isOn);
        Assert.Equal(text, BanchoWelcome.Format(template, "Blue"));
    }

    [Fact]
    public void No_welcome_sends_no_notification()
    {
        var body = LoginResponseBuilder.BuildSuccess(new BanchoPresence { UserId = 2, Username = "Blue" }, string.Empty);

        Assert.DoesNotContain(ServerPacketId.Notification, PacketIds(body));
    }

    private static List<ServerPacketId> PacketIds(byte[] body)
    {
        var ids = new List<ServerPacketId>();
        var offset = 0;
        while (offset + 7 <= body.Length)
        {
            ids.Add((ServerPacketId)BitConverter.ToUInt16(body, offset));
            offset += 7 + BitConverter.ToInt32(body, offset + 3);
        }
        return ids;
    }
}
