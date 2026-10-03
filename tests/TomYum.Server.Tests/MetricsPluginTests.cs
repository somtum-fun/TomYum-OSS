using System.Globalization;
using System.Text;

using Microsoft.Extensions.Logging.Abstractions;

using TomYum.Bancho;
using TomYum.Plugins.Abstractions;
using TomYum.Plugins.Loader;
using TomYum.Server.Bancho;
using TomYum.Server.Plugins;

using Xunit;

namespace TomYum.Server.Tests;

/// <summary>
/// Guards the loose-source <c>tomyum.metrics</c> plugin (plugins/tomyum-metrics), which serves the
/// status page's Prometheus numbers: it must compile under Roslyn, answer <c>/metrics</c> only on
/// internal host names, count scores and logins from the host's events, and keep the metric names
/// the status page queries. Also covers the two host hooks it uses (online counts, refused logins).
/// </summary>
public class MetricsPluginTests
{
    private const string InternalHost = "127.0.0.1:8091";

    [Fact]
    public async Task Loose_source_compiles_and_registers_one_internal_route()
    {
        var (root, plugin, loader) = await LoadRealPluginAsync();
        var http = new RecordingHttpRoutes();
        var events = new RecordingEventBus();
        var logger = new RecordingLogger();
        try
        {
            Assert.Equal("tomyum.metrics", plugin.Manifest.Id);
            await plugin.InitializeAsync(new FakeContext(http, events, logger, online: null));

            var route = Assert.Single(http.Routes);
            Assert.Equal("GET", route.Method);
            Assert.Equal("/metrics", route.Pattern);
            Assert.Null(route.Subdomain);
            Assert.False(route.RequireAuth);
            Assert.Equal(3, events.Count);
            // Without trusted DB access it says what's missing and how to get it.
            Assert.Contains(logger.Warnings, w => w.Contains("Plugins:Trusted", StringComparison.Ordinal));

            await plugin.ShutdownAsync();
            Assert.True(route.Disposed);
            Assert.Equal(0, events.Count);
        }
        finally
        {
            await loader.UnloadAsync(plugin);
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("osu.example.com")]
    [InlineData("c.example.com")]
    [InlineData("127.0.0.1:8080")]
    [InlineData(null)]
    public async Task Public_hosts_get_a_404(string? host)
    {
        var (root, plugin, loader) = await LoadRealPluginAsync();
        var http = new RecordingHttpRoutes();
        try
        {
            await plugin.InitializeAsync(new FakeContext(http, new RecordingEventBus(), new RecordingLogger(), online: null));
            var response = await GetAsync(http, host);
            Assert.Equal(404, response.StatusCode);
            Assert.DoesNotContain("tomyum_", Encoding.UTF8.GetString(response.Body.Span), StringComparison.Ordinal);
            await plugin.ShutdownAsync();
        }
        finally
        {
            await loader.UnloadAsync(plugin);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Serves_players_scores_and_logins_with_the_status_page_names()
    {
        var (root, plugin, loader) = await LoadRealPluginAsync();
        var http = new RecordingHttpRoutes();
        var events = new RecordingEventBus();
        var online = new FakeOnlineCounts { Counts = new HostOnlineCounts(Stable: 2, Irc: 1, Lazer: 4, MultiplayerMatches: 3) };
        try
        {
            await plugin.InitializeAsync(new FakeContext(http, events, new RecordingLogger(), online));

            await events.PublishAsync(Score());
            var vetoed = Score();
            vetoed.Veto("a plugin said no");
            await events.PublishAsync(vetoed);
            await events.PublishAsync(new PlayerLoginEvent(10, "Someone", "th"));
            await events.PublishAsync(new PlayerLoginRefusedEvent("Someone", Held: true));
            await events.PublishAsync(new PlayerLoginRefusedEvent("nobody", Held: false));
            await events.PublishAsync(new PlayerLoginRefusedEvent("nobody", Held: false));

            var response = await GetAsync(http, InternalHost);
            Assert.Equal(200, response.StatusCode);
            Assert.StartsWith("text/plain; version=0.0.4", response.ContentType, StringComparison.Ordinal);
            var lines = Lines(response);

            Assert.Contains("# TYPE tomyum_online_players gauge", lines);
            Assert.Contains("tomyum_online_players{client=\"stable\"} 2", lines);
            Assert.Contains("tomyum_online_players{client=\"lazer\"} 4", lines);
            Assert.Contains("tomyum_online_players{client=\"irc\"} 1", lines);
            Assert.Contains("tomyum_multiplayer_matches 3", lines);
            Assert.Contains("# TYPE tomyum_scores_submitted_total counter", lines);
            Assert.Contains("tomyum_scores_submitted_total{client=\"stable\"} 1", lines); // the vetoed one isn't counted
            Assert.Contains("tomyum_scores_submitted_total{client=\"lazer\"} 0", lines);
            Assert.Contains("tomyum_logins_total{client=\"stable\",result=\"ok\"} 1", lines);
            Assert.Contains("tomyum_logins_total{client=\"stable\",result=\"held\"} 1", lines);
            Assert.Contains("tomyum_logins_total{client=\"stable\",result=\"failed\"} 2", lines);
            // Not counted yet (no database here): left out rather than reported as 0.
            Assert.DoesNotContain(lines, l => l.StartsWith("tomyum_registered_users", StringComparison.Ordinal));
            Assert.Contains(lines, l => l.StartsWith("process_cpu_seconds_total ", StringComparison.Ordinal));
            Assert.Contains(lines, l => l.StartsWith("process_resident_memory_bytes ", StringComparison.Ordinal));
            // Every sample is "name{labels} number".
            foreach (var line in lines.Where(l => !l.StartsWith('#')))
                Assert.True(double.TryParse(line[(line.LastIndexOf(' ') + 1)..], CultureInfo.InvariantCulture, out _), line);

            // While the lazer feed can't be read, lazer is left out instead of dropping to 0.
            online.Counts = new HostOnlineCounts(Stable: 2, Irc: 1, Lazer: null, MultiplayerMatches: 0);
            lines = Lines(await GetAsync(http, InternalHost));
            Assert.Contains("tomyum_online_players{client=\"stable\"} 2", lines);
            Assert.DoesNotContain(lines, l => l.StartsWith("tomyum_online_players{client=\"lazer\"}", StringComparison.Ordinal));

            await plugin.ShutdownAsync();
        }
        finally
        {
            await loader.UnloadAsync(plugin);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Host_online_counts_split_stable_and_irc_without_BanchoBot()
    {
        var players = new PlayerRegistry();
        players.Add(BanchoBot.CreatePresence());
        players.Add(new BanchoPresence { UserId = 10, Username = "Stable One" });
        players.Add(new BanchoPresence { UserId = 11, Username = "Stable Two" });
        players.Add(new BanchoPresence { UserId = 12, Username = "Irc Person" }).IsIrc = true;

        var counts = await new HostOnlineCountsView(players, new MatchRegistry()).GetAsync();

        Assert.Equal(2, counts.Stable);
        Assert.Equal(1, counts.Irc);
        Assert.Null(counts.Lazer); // no Redis: unknown, not 0
        Assert.Equal(0, counts.MultiplayerMatches);
    }

    [Fact]
    public async Task Refused_logins_raise_an_event_for_plugins()
    {
        var events = new RecordingEventBus();
        var refused = new List<PlayerLoginRefusedEvent>();
        using var subscription = events.Subscribe<PlayerLoginRefusedEvent>((e, _) =>
        {
            refused.Add(e);
            return Task.CompletedTask;
        });
        var handler = new BanchoLoginHandler(new InMemoryUserRepository(), new BcryptPasswordHasher(), new InMemoryUserStatsRepository(),
            new PlayerRegistry(), new ChannelRegistry(), NullLogger<BanchoLoginHandler>.Instance, events);

        var outcome = await handler.HandleAsync("nobody\n0123456789abcdef0123456789abcdef\nb20231228.1|0|0|a:b:c:d:e:|0\n", "TH", CancellationToken.None);

        Assert.Null(outcome.Token);
        var e = Assert.Single(refused);
        Assert.Equal("nobody", e.Username);
        Assert.False(e.Held);
    }

    private static ScoreSubmittingEvent Score() => new(10, "0123456789abcdef0123456789abcdef", 0, 0, 0.98, 500, true, "Artist - Title [Hard]");

    private static string[] Lines(PluginHttpResponse response) =>
        Encoding.UTF8.GetString(response.Body.Span).Split('\n', StringSplitOptions.RemoveEmptyEntries);

    private static Task<PluginHttpResponse> GetAsync(RecordingHttpRoutes http, string? host)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (host is not null)
            headers["Host"] = host;
        var request = new PluginHttpRequest("GET", "/metrics", new Dictionary<string, string>(), new Dictionary<string, string>(), headers, null, null);
        return http.Routes.Single(r => r.Pattern == "/metrics").Handler(request, CancellationToken.None);
    }

    private static async Task<(string Root, IPlugin Plugin, PluginLoader Loader)> LoadRealPluginAsync()
    {
        var source = FindRealPluginDir();
        var root = Path.Combine(Path.GetTempPath(), "tomyum-metrics-" + Guid.NewGuid().ToString("N"));
        var dest = Path.Combine(root, "tomyum-metrics");
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)));

        var loader = new PluginLoader(allowLooseSource: true);
        var plugins = await loader.LoadAllAsync(root);
        var plugin = Assert.Single(plugins);
        return (root, plugin, loader);
    }

    private static string FindRealPluginDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "plugins", "tomyum-metrics");
            if (File.Exists(Path.Combine(candidate, "plugin.json")))
                return candidate;
        }

        throw new DirectoryNotFoundException("Could not locate plugins/tomyum-metrics from the test base directory.");
    }

    // --- fakes ---------------------------------------------------------------------------------

    private sealed record RecordedRoute(
        string Method, string Pattern, bool RequireAuth, string? Subdomain,
        Func<PluginHttpRequest, CancellationToken, Task<PluginHttpResponse>> Handler)
    {
        public bool Disposed { get; set; }
    }

    private sealed class RecordingHttpRoutes : IHttpRouteRegistry
    {
        public List<RecordedRoute> Routes { get; } = [];

        public IDisposable Map(
            string method,
            string pattern,
            Func<PluginHttpRequest, CancellationToken, Task<PluginHttpResponse>> handler,
            bool requireAuth = false,
            string? subdomain = null)
        {
            var route = new RecordedRoute(method, pattern, requireAuth, subdomain, handler);
            Routes.Add(route);
            return new Handle(() => route.Disposed = true);
        }
    }

    /// <summary>A minimal in-order event bus (the host's own is internal and needs a logger).</summary>
    private sealed class RecordingEventBus : IEventBus
    {
        private readonly List<(Type Type, Func<IPluginEvent, CancellationToken, Task> Handler)> _handlers = [];

        public int Count => _handlers.Count;

        public IDisposable Subscribe<TEvent>(Func<TEvent, CancellationToken, Task> handler)
            where TEvent : IPluginEvent
        {
            var entry = (typeof(TEvent), (Func<IPluginEvent, CancellationToken, Task>)((e, ct) => handler((TEvent)e, ct)));
            _handlers.Add(entry);
            return new Handle(() => _handlers.Remove(entry));
        }

        public async Task PublishAsync<TEvent>(TEvent @event, CancellationToken cancellationToken = default)
            where TEvent : IPluginEvent
        {
            foreach (var (type, handler) in _handlers.ToArray())
            {
                if (type == typeof(TEvent))
                    await handler(@event, cancellationToken);
                if (@event is IVetoableEvent { IsVetoed: true })
                    break;
            }
        }
    }

    private sealed class Handle(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }

    private sealed class FakeOnlineCounts : IHostOnlineCounts
    {
        public HostOnlineCounts Counts { get; set; } = new(0, 0, null, 0);

        public Task<HostOnlineCounts> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult(Counts);
    }

    private sealed class RecordingLogger : IPluginLogger
    {
        public List<string> Warnings { get; } = [];

        public void Info(string message)
        {
        }

        public void Warn(string message) => Warnings.Add(message);

        public void Error(string message, Exception? exception = null) => Warnings.Add(message);
    }

    private sealed class EmptyConfig : IPluginConfig
    {
        public T? Get<T>(string key, T? defaultValue = default) => defaultValue;

        public TOptions Bind<TOptions>() where TOptions : new() => new();
    }

    private sealed class FakeContext(IHttpRouteRegistry http, IEventBus events, IPluginLogger logger, IHostOnlineCounts? online)
        : IPluginContext
    {
        public IPluginConfig Config { get; } = new EmptyConfig();

        public IPluginLogger Logger { get; } = logger;

        public IEventBus Events { get; } = events;

        public ICommandRegistry Commands => throw new NotSupportedException();

        public IHttpRouteRegistry Http { get; } = http;

        public IPluginDataAccess? Data => null;

        public TService? GetHostService<TService>() where TService : class =>
            typeof(TService) == typeof(IHostOnlineCounts) ? online as TService : null;
    }
}
