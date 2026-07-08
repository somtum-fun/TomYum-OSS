using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using TomYum.Plugins.Abstractions;
using TomYum.Plugins.Loader;

using Xunit;

namespace TomYum.Server.Tests;

/// <summary>
/// Guards the loose-source <c>tomyum.session</c> plugin (plugins/tomyum-session): it must compile
/// under Roslyn, load into a collectible context with the right manifest, register its full route
/// set, and stay inert-but-safe when it isn't granted trusted DB access. The real plugin source is
/// copied into a temp root so only this plugin is compiled (not the example plugins next to it).
/// </summary>
public class SessionPluginTests
{
    [Fact]
    public async Task Loose_source_compiles_loads_and_exposes_the_session_manifest()
    {
        var (root, plugin, loader) = await LoadRealPluginAsync();
        try
        {
            Assert.Equal("tomyum.session", plugin.Manifest.Id);
            Assert.Equal("Session (web auth)", plugin.Manifest.Name);
            Assert.Equal("0.1.0", plugin.Manifest.Version);
        }
        finally
        {
            await loader.UnloadAsync(plugin);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Without_trusted_db_it_registers_all_routes_warns_and_shuts_down_cleanly()
    {
        var (root, plugin, loader) = await LoadRealPluginAsync();
        var http = new RecordingHttpRoutes();
        var logger = new RecordingLogger();
        var context = new FakeContext(http, logger, data: null);
        try
        {
            await plugin.InitializeAsync(context);

            // Six routes, all on the session subdomain, none relying on the host auth gate.
            Assert.Equal(6, http.Routes.Count);
            Assert.All(http.Routes, r => Assert.Equal("session", r.Subdomain));
            Assert.All(http.Routes, r => Assert.False(r.RequireAuth));
            Assert.Contains(http.Routes, r => r is { Method: "POST", Pattern: "/login" });
            Assert.Contains(http.Routes, r => r is { Method: "GET", Pattern: "/health" });
            Assert.Contains(http.Routes, r => r is { Method: "DELETE", Pattern: "/user/sessions/web/{target_token}" });

            // Data == null → it must loudly tell the operator it needs the trusted grant, not fail silently.
            Assert.Contains(logger.Errors, e => e.Contains("Plugins:Trusted"));

            await plugin.ShutdownAsync();
            Assert.All(http.Routes, r => Assert.True(r.Disposed)); // routes released on shutdown
        }
        finally
        {
            await loader.UnloadAsync(plugin);
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<(string Root, IPlugin Plugin, PluginLoader Loader)> LoadRealPluginAsync()
    {
        var source = FindRealPluginDir();
        var root = Path.Combine(Path.GetTempPath(), "tomyum-session-" + Guid.NewGuid().ToString("N"));
        var dest = Path.Combine(root, "tomyum-session");
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
            var candidate = Path.Combine(dir.FullName, "plugins", "tomyum-session");
            if (File.Exists(Path.Combine(candidate, "plugin.json")))
                return candidate;
        }

        throw new DirectoryNotFoundException("Could not locate plugins/tomyum-session from the test base directory.");
    }

    // --- minimal fakes for the Data==null lifecycle path ---------------------------------------

    private sealed record RecordedRoute(string Method, string Pattern, bool RequireAuth, string? Subdomain)
    {
        public bool Disposed { get; set; }
    }

    private sealed class RecordingHttpRoutes : IHttpRouteRegistry
    {
        public List<RecordedRoute> Routes { get; } = new();

        public IDisposable Map(
            string method,
            string pattern,
            Func<PluginHttpRequest, CancellationToken, Task<PluginHttpResponse>> handler,
            bool requireAuth = false,
            string? subdomain = null)
        {
            var route = new RecordedRoute(method, pattern, requireAuth, subdomain);
            Routes.Add(route);
            return new Handle(route);
        }

        private sealed class Handle(RecordedRoute route) : IDisposable
        {
            public void Dispose() => route.Disposed = true;
        }
    }

    private sealed class RecordingLogger : IPluginLogger
    {
        public List<string> Errors { get; } = new();
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? exception = null) => Errors.Add(message);
    }

    private sealed class EmptyConfig : IPluginConfig
    {
        public T? Get<T>(string key, T? defaultValue = default) => defaultValue;
        public TOptions Bind<TOptions>() where TOptions : new() => new();
    }

    private sealed class FakeContext(IHttpRouteRegistry http, IPluginLogger logger, IPluginDataAccess? data)
        : IPluginContext
    {
        public IPluginConfig Config { get; } = new EmptyConfig();
        public IPluginLogger Logger { get; } = logger;
        public IEventBus Events => throw new NotSupportedException();
        public ICommandRegistry Commands => throw new NotSupportedException();
        public IHttpRouteRegistry Http { get; } = http;
        public IPluginDataAccess? Data { get; } = data;
        public TService? GetHostService<TService>() where TService : class => null;
    }
}
