using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using TomYum.Persistence;
using TomYum.Plugins.Abstractions;
using TomYum.Plugins.Loader;

namespace TomYum.Server.Plugins;

/// <summary>
/// Drives the plugin lifecycle: on startup it loads every plugin from the configured directory,
/// builds each a scoped <see cref="IPluginContext"/>, and calls <see cref="IPlugin.InitializeAsync"/>;
/// on shutdown it calls <see cref="IPlugin.ShutdownAsync"/> and unloads each assembly context. A
/// plugin that throws during init is logged and skipped — it never blocks the others or the server.
/// </summary>
internal sealed class PluginHostService : IHostedService
{
    private readonly PluginHostOptions _options;
    private readonly IConfiguration _configuration;
    private readonly IHostEnvironment _environment;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<PluginHostService> _log;
    private readonly EventBus _events;
    private readonly PluginCommandRegistry _commands;
    private readonly PluginRouteTable _routes;
    private readonly HostServiceRegistry _hostServices;
    private readonly LoadedPluginRegistry _loaded;
    private readonly DatabaseOptions _database;
    private readonly IHostPlayerView _playerView;

    private readonly List<IPlugin> _plugins = [];
    private PluginLoader? _loader;

    public PluginHostService(
        PluginHostOptions options,
        IConfiguration configuration,
        IHostEnvironment environment,
        ILoggerFactory loggerFactory,
        EventBus events,
        PluginCommandRegistry commands,
        PluginRouteTable routes,
        HostServiceRegistry hostServices,
        LoadedPluginRegistry loaded,
        DatabaseOptions database,
        IHostPlayerView playerView)
    {
        _options = options;
        _configuration = configuration;
        _environment = environment;
        _loggerFactory = loggerFactory;
        _log = loggerFactory.CreateLogger<PluginHostService>();
        _events = events;
        _commands = commands;
        _routes = routes;
        _hostServices = hostServices;
        _loaded = loaded;
        _database = database;
        _playerView = playerView;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Publish the host services plugins can resolve via GetHostService<T>().
        _hostServices.Register(_playerView);

        var directory = ResolvePluginsDirectory(_options.Directory);
        _loader = new PluginLoader(
            _options.AllowLooseSource,
            (message, exception) => _log.LogWarning(exception, "{Message}", message));

        var discovered = await _loader.LoadAllAsync(directory, cancellationToken).ConfigureAwait(false);
        foreach (var plugin in discovered)
        {
            try
            {
                var context = BuildContext(plugin.Manifest);
                await plugin.InitializeAsync(context, cancellationToken).ConfigureAwait(false);
                _plugins.Add(plugin);
                _log.LogInformation(
                    "plugin loaded: {Id} v{Version} by {Authors}",
                    plugin.Manifest.Id, plugin.Manifest.Version, plugin.Manifest.Authors);
            }
#pragma warning disable CA1031 // one bad plugin must not stop the host
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _log.LogError(ex, "plugin {Id} failed to initialize; skipping", plugin.Manifest.Id);
                await _loader.UnloadAsync(plugin, cancellationToken).ConfigureAwait(false);
            }
        }

        _loaded.SetLoaded(_plugins.Select(p => p.Manifest));
        _log.LogInformation("plugin host: {Count} plugin(s) active from {Directory}", _plugins.Count, directory);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var plugin in _plugins)
        {
            try
            {
                await plugin.ShutdownAsync(cancellationToken).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // shutdown of one plugin must not stop the others
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _log.LogWarning(ex, "plugin {Id} shutdown threw", plugin.Manifest.Id);
            }

            if (_loader is not null)
                await _loader.UnloadAsync(plugin, cancellationToken).ConfigureAwait(false);
        }

        _plugins.Clear();
        _loaded.SetLoaded([]);
    }

    private PluginContext BuildContext(PluginManifest manifest)
    {
        var section = _configuration.GetSection($"{PluginHostOptions.SectionName}:{manifest.Id}");
        var config = new PluginConfig(section);
        var logger = new PluginLogger(_loggerFactory.CreateLogger($"plugin:{manifest.Id}"));
        var commands = new ScopedCommandRegistry(_commands, manifest.Id);
        var http = new ScopedHttpRouteRegistry(_routes, manifest.Id);

        IPluginDataAccess? data = null;
        if (_database.Enabled && _options.Trusted.Contains(manifest.Id, StringComparer.OrdinalIgnoreCase))
            data = new PluginDataAccess(_database.BuildConnectionString());

        return new PluginContext(config, logger, _events, commands, http, data, _hostServices);
    }

    private string ResolvePluginsDirectory(string configured)
    {
        if (Path.IsPathRooted(configured))
            return configured;

        // Mirror config resolution: walk up from the content root until the plugins dir is found,
        // so `dotnet run` (CWD = project dir) and a deployed layout both resolve it.
        for (var dir = new DirectoryInfo(_environment.ContentRootPath); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, configured);
            if (Directory.Exists(candidate))
                return candidate;
        }

        return Path.Combine(_environment.ContentRootPath, configured);
    }
}
