using TomYum.Plugins.Abstractions;

namespace MyTomYumPlugin;

/// <summary>
/// A starter TomYum plugin. Shows the four things a plugin can do through <see cref="IPluginContext"/>:
/// read config, register a chat command, subscribe to a server event, and inspect live server state.
/// Rename it, change the manifest, and build the .dll to deploy.
/// </summary>
public sealed class MyPlugin : IPlugin
{
    private IDisposable? _command;
    private IDisposable? _actionHook;

    public PluginManifest Manifest { get; } = new(
        Id: "example.my-plugin",          // stable, unique; also the config key (Plugins:example.my-plugin)
        Name: "My Plugin",
        Version: "0.1.0",
        Authors: "your name here",
        Description: "A starter TomYum plugin.");

    public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken = default)
    {
        // 1) Config — bound from Plugins:example.my-plugin in config/tomyum.yaml.
        var greeting = context.Config.Get("greeting", "hello");

        // 2) A chat command — players type !myplugin in-game.
        _command = context.Commands.Register(
            name: "myplugin",
            description: "Example command from the plugin template.",
            handler: (invocation, ct) =>
            {
                // 4) Inspect live server state through a host service.
                var players = context.GetHostService<IHostPlayerView>();
                var online = players?.OnlineCount ?? 0;
                return Task.FromResult<string?>($"{greeting}! {online} player(s) online.");
            });

        // 3) A server event hook — react whenever a player changes what they are doing.
        _actionHook = context.Events.Subscribe<ActionChangedEvent>((e, ct) =>
        {
            context.Logger.Info($"user {e.UserId} action={e.Action} map={e.BeatmapMd5}");
            return Task.CompletedTask;
        });

        context.Logger.Info("my-plugin initialized");
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        // Dispose everything registered in Initialize so the plugin can be cleanly unloaded.
        _command?.Dispose();
        _actionHook?.Dispose();
        return Task.CompletedTask;
    }
}
