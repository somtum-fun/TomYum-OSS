using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using TomYum.Bancho;
using TomYum.Domain.Entities;
using TomYum.Plugins.Abstractions;
using TomYum.Server.Bancho;
using TomYum.Server.Beatmaps;
using TomYum.Server.Plugins;

using Xunit;

namespace TomYum.Server.Tests;

/// <summary>
/// The plugin API's additions (docs/plugins.md): command options, the trusted-only host services, the veto's player
/// message, the state store, the scheduler, server errors and chat posts.
/// </summary>
public class PluginApiTests
{
    private static OnlinePlayer Caller(string name, int userId = 50, BanchoPrivileges privileges = BanchoPrivileges.Player) =>
        new("token-" + name, new BanchoPresence { UserId = userId, Username = name, Privileges = privileges });

    private static (BanchoBot Bot, PluginCommandRegistry Commands) NewBot()
    {
        var commands = new PluginCommandRegistry();
        var bot = new BanchoBot(new BanchoBotCommands(
            new TomYumVersion(),
            new PlayerRegistry(),
            new ChannelRegistry(),
            new MatchRegistry(),
            new BeatmapService(new StubApiClient()),
            TestScopeFactory.Create(new InMemoryUserRepository()),
            commands,
            new LoadedPluginRegistry()));
        return (bot, commands);
    }

    [Fact]
    public async Task A_command_with_required_groups_refuses_others_but_not_staff()
    {
        var (bot, commands) = NewBot();
        var scoped = new ScopedCommandRegistry(commands, "test.plugin");
        using var _ = scoped.Register("judge", "staff only", (_, _) => Task.FromResult<string?>("ran"), new CommandOptions { RequiredGroups = ["gmt"] });

        Assert.Equal("You can't use that command.", await bot.TryHandleCommandAsync(Caller("Someone"), "!judge", BanchoPrivileges.Player));
        Assert.Equal("ran", await bot.TryHandleCommandAsync(Caller("Owner", privileges: BanchoPrivileges.Owner), "!judge", BanchoPrivileges.Owner));
    }

    [Fact]
    public async Task A_plugin_command_gets_the_callers_name_and_help_shows_usage_and_leaves_hidden_ones_out()
    {
        var (bot, commands) = NewBot();
        var scoped = new ScopedCommandRegistry(commands, "test.plugin");
        CommandInvocation? seen = null;
        using var shown = scoped.Register("greet", "says hi", (i, _) =>
        {
            seen = i;
            return Task.FromResult<string?>("hi");
        }, new CommandOptions { Usage = "<player>" });
        using var hidden = scoped.Register("secret", "not listed", (_, _) => Task.FromResult<string?>("shh"), new CommandOptions { Hidden = true });

        Assert.Equal("hi", await bot.TryHandleCommandAsync(Caller("Someone"), "!greet peppy", BanchoPrivileges.Player));
        Assert.Equal("Someone", seen!.InvokerUsername);
        Assert.Empty(seen.InvokerGroups);

        var help = await bot.TryHandleCommandAsync(Caller("Someone"), "!help", BanchoPrivileges.Player);
        Assert.Contains("!greet <player> (says hi)", help, StringComparison.Ordinal);
        Assert.DoesNotContain("!secret", help, StringComparison.Ordinal);
        Assert.Equal("shh", await bot.TryHandleCommandAsync(Caller("Someone"), "!secret", BanchoPrivileges.Player));
    }

    [Fact]
    public void Only_trusted_plugins_get_the_services_that_act_as_a_player()
    {
        var registry = new HostServiceRegistry();
        registry.Register<IHostCommands>(new HostCommands(new EmptyServices()));
        IPluginContext Context(IPluginDataAccess? data) => new PluginContext(
            new PluginConfig(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()), new PluginLogger(NullLogger.Instance),
            new EventBus(NullLogger<EventBus>.Instance), new ScopedCommandRegistry(new PluginCommandRegistry(), "p"),
            new NoRoutes(), data, registry);

        Assert.Null(Context(null).GetHostService<IHostCommands>());
        Assert.NotNull(Context(new PluginDataAccess("Server=localhost")).GetHostService<IHostCommands>());
        Assert.Throws<ArgumentException>(() => Context(new PluginDataAccess("Server=localhost")).PublishHostService<IHostCommands>(new HostCommands(new EmptyServices())));
    }

    [Fact]
    public void A_veto_can_carry_a_message_for_the_player_and_the_first_one_wins()
    {
        var e = new ScoreSubmittingEvent(1, "md5", 3, 0, 0.95, 100, true, "map");
        e.Veto("dan: paused", "Your play wasn't submitted: you paused.");
        e.Veto("other", "something else");

        Assert.True(e.IsVetoed);
        Assert.Equal("dan: paused", e.VetoReason);
        Assert.Equal("Your play wasn't submitted: you paused.", e.PlayerMessage);
    }

    [Fact]
    public async Task The_state_store_keeps_values_until_they_expire_or_are_removed()
    {
        var time = new ManualTime(DateTimeOffset.Parse("2026-10-25T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var store = new PluginStateStore("test.plugin", redis: null, time);

        await store.SetAsync("mark", "42", TimeSpan.FromMinutes(1));
        await store.SetAsync("forever", "yes");
        Assert.Equal("42", await store.GetAsync("mark"));

        time.Now += TimeSpan.FromMinutes(2);
        Assert.Null(await store.GetAsync("mark"));
        Assert.Equal("yes", await store.GetAsync("forever"));
        Assert.True(await store.RemoveAsync("forever"));
        Assert.Null(await store.GetAsync("forever"));
    }

    [Fact]
    public async Task The_scheduler_runs_a_job_and_stops_it_with_the_plugin()
    {
        var scheduler = new PluginScheduler("test.plugin", NullLogger.Instance);
        var runs = 0;
        var ran = new TaskCompletionSource();
        scheduler.Every(TimeSpan.FromSeconds(1), _ =>
        {
            if (Interlocked.Increment(ref runs) == 1)
                ran.SetResult();
            throw new InvalidOperationException("a failing job runs again");
        });

        await ran.Task.WaitAsync(TimeSpan.FromSeconds(10));
        scheduler.Dispose();
        var after = runs;
        await Task.Delay(1500);
        Assert.Equal(after, runs);
        Assert.Throws<ObjectDisposedException>(() => scheduler.Every(TimeSpan.FromSeconds(1), _ => Task.CompletedTask));
    }

    [Fact]
    public async Task Server_errors_are_raised_once_a_minute_per_category_with_the_rest_counted()
    {
        var time = new ManualTime(DateTimeOffset.Parse("2026-10-25T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var bus = new EventBus(NullLogger<EventBus>.Instance);
        var raised = new List<ServerErrorEvent>();
        var arrived = new SemaphoreSlim(0);
        using var _ = bus.Subscribe<ServerErrorEvent>((e, _) =>
        {
            lock (raised)
                raised.Add(e);
            arrived.Release();
            return Task.CompletedTask;
        });
        using var provider = new PluginErrorLoggerProvider(time);
        provider.Attach(bus);
        var logger = provider.CreateLogger("TomYum.Test");

        logger.LogWarning("not an error");
        logger.LogError(new InvalidOperationException("boom"), "first");
        logger.LogError("second");
        logger.LogError("third");
        Assert.True(await arrived.WaitAsync(TimeSpan.FromSeconds(5)));

        time.Now += TimeSpan.FromMinutes(1);
        logger.LogError("fourth");
        Assert.True(await arrived.WaitAsync(TimeSpan.FromSeconds(5)));

        lock (raised)
        {
            Assert.Equal(2, raised.Count);
            Assert.Equal("first", raised[0].Message);
            Assert.Equal("InvalidOperationException: boom", raised[0].Exception);
            Assert.Equal("fourth", raised[1].Message);
            Assert.Equal(2, raised[1].Suppressed);
        }
    }

    [Fact]
    public async Task A_chat_post_reaches_the_channel_and_the_chat_log_unless_the_account_is_muted()
    {
        var users = new InMemoryUserRepository();
        var speaker = await users.AddAsync(new User { Username = "Relay", SafeName = "relay", Email = "r@x.invalid" });
        var restricted = await users.AddAsync(new User { Username = "Bad", SafeName = "bad", Email = "b@x.invalid", IsRestricted = true });
        var scopes = TestScopeFactory.Create(users);
        var channels = new ChannelRegistry();
        var listener = Caller("Listener", userId: 900);
        var channel = channels.Get("#osu")!;
        channel.Join(listener);
        var chatLog = new ChatLog(scopes, TimeProvider.System, NullLogger<ChatLog>.Instance);
        string? origin = null;
        chatLog.RecordedFrom += (_, o) => origin = o;
        var chat = new HostChat(scopes, channels, chatLog);

        Assert.Equal(ChatPostResult.Posted, await chat.PostAsync("#osu", speaker.Id, "hello\nthere", "Discord"));
        Assert.NotEmpty(listener.DequeueAll());
        Assert.Equal("discord", origin);
        Assert.Equal(ChatPostResult.Muted, await chat.PostAsync("#osu", restricted.Id, "hi", "discord"));
        Assert.Equal(ChatPostResult.NoChannel, await chat.PostAsync("#nowhere", speaker.Id, "hi", "discord"));
        Assert.Equal(ChatPostResult.Empty, await chat.PostAsync("#osu", speaker.Id, " \n ", "discord"));
        Assert.Equal(ChatPostResult.NoAccount, await chat.PostAsync("#osu", 123456, "hi", "discord"));
    }

    private sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class EmptyServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    private sealed class NoRoutes : IHttpRouteRegistry
    {
        public IDisposable Map(string method, string pattern, Func<PluginHttpRequest, CancellationToken, Task<PluginHttpResponse>> handler, bool requireAuth = false, string? subdomain = null) =>
            throw new NotSupportedException();
    }

    private sealed class StubApiClient : IOsuApiClient
    {
        public bool SupportsSince => false;

        public Task<Beatmap?> GetByMd5Async(string md5, CancellationToken cancellationToken = default) => Task.FromResult<Beatmap?>(null);

        public Task<IReadOnlyList<Beatmap>> GetSetAsync(int setId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Beatmap>>([]);

        public Task<IReadOnlyList<Beatmap>> GetSinceAsync(DateTimeOffset since, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Beatmap>>([]);
    }
}
