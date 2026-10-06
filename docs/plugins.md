# Writing TomYum plugins (human guide)

> Audience: **people** writing or deploying plugins. The host's internals are in
> `src/TomYum.Server/Plugins/` and `src/TomYum.Plugins.Loader/`.

TomYum loads plugins at startup from a `plugins/` directory. A plugin is a small piece of code that
hooks into the server through one interface — `IPluginContext` — and can add commands, react to
events, serve HTTP, and inspect live state. **DAN Mode** and the **Session** frontend ship as plugins
on this same system.

The fastest way to start is the plugin template repo:
**[somtum-fun/tomyum-plugin-template](https://github.com/somtum-fun/tomyum-plugin-template)** — compiled
(`.dll`) and loose-source (`.cs`) starter shapes plus an authoring guide.

## Two shapes

| Shape | You ship | How it loads |
|-------|----------|--------------|
| **Compiled** | a built `.dll` dropped into `plugins/` | loaded into an isolated, unloadable `AssemblyLoadContext` |
| **Loose source** | a folder with `plugin.json` + `.cs` files | compiled at runtime with Roslyn, then loaded the same way |

Both end up as the same thing at runtime. Compiled is better for real plugins (tooling, tests);
loose source is great for quick tweaks with no build step. Loose source requires
`Plugins:AllowLooseSource: true` in config.

## The contract

Every plugin implements `IPlugin`:

```csharp
public interface IPlugin
{
    PluginManifest Manifest { get; }                                     // id, name, version, authors, description
    Task InitializeAsync(IPluginContext context, CancellationToken ct);  // register everything here
    Task ShutdownAsync(CancellationToken ct);                            // dispose everything here
}
```

The `Id` in your manifest is your identity **and** your config key. Keep it stable and unique
(e.g. `tomyum.dan`). Dispose in `ShutdownAsync` exactly what you registered in `InitializeAsync`
so the plugin can be cleanly unloaded.

## What you get: `IPluginContext`

| Member | Use it for |
|--------|-----------|
| `Config` | Your settings, bound from `Plugins:<your-id>` in `config/tomyum.yaml`. `Get<T>(key, default)` or `Bind<TOptions>()`. |
| `Logger` | `Info` / `Warn` / `Error`, tagged with your plugin id. |
| `Commands` | `Register(name, description, handler)` — adds a `!`command. Returns an `IDisposable`; dispose it on shutdown. |
| `Events` | `Subscribe<TEvent>(handler)` — react to server events. Some are **vetoable** (you can reject the operation). |
| `Http` | `Map(method, pattern, handler, requireAuth, subdomain)` — serve HTTP, optionally on your own subdomain. |
| `GetHostService<T>()` | Inspect live state and reach players. Today: `IHostPlayerView` (who's online, action, map, mods), `IHostOnlineCounts` (players online by client: stable, IRC, lazer; open multiplayer rooms) and `IHostMessenger` (a BanchoBot message or a notification to an online osu!(stable) player) and `IHostSpectating` (BanchoBot spectates an osu!stable player, so their client streams frames and `SpectateFrameEvent` fires without a human spectator; nothing is queued for the bot, and it can watch many players at once). |
| `Data` | Raw ADO.NET `DbConnection` — **only** if the operator lists your id under `Plugins:Trusted`. Otherwise `null`. |

### Events you can subscribe to

All implement `IPluginEvent`; `ScoreSubmitting` also implements `IVetoableEvent` (call `Veto(reason)`
to reject the score):

- `PlayerLoginEvent` — a player logged in.
- `PlayerLoginRefusedEvent` — an osu!stable login was refused (`Held` when it's only for now: maintenance, or a device to verify).
- `ChatMessageEvent` — a player sent a message.
- `ActionChangedEvent` — a player's status changed (idle/playing/…, with map + mods). `ClientVersion` is the client's
  build; `CanCheckFrames` says whether the server can read its spectator frames (osu!stable from b20130513 on).
- `SpectateFrameEvent` — a raw replay-frame bundle from a spectated host. `Action` is the bundle's replay action
  (`ReplayActions.Pause`, `NewSong`, …) read by the server whatever the build's layout, or null when it couldn't.
- `ScoreSubmittingEvent` *(vetoable)* — a score is about to be persisted (with `BeatmapId`, `BeatmapSetId` and
  `BeatmapVersion`, the difficulty name).
- `ScoreSubmittedEvent` — an osu!stable score was stored (failed plays too): its score id, map, mods, accuracy,
  grade, misses, pp, client version and whether the player is restricted. Not raised for a vetoed or refused score,
  nor for osu!lazer (scores go to the website).

> Some raise-sites are wired as their consuming features land (`ActionChanged` is live today;
> score-veto and spectate-frame arrive with DAN/spectating). Subscribing is always safe.

## Example

```csharp
public Task InitializeAsync(IPluginContext context, CancellationToken ct = default)
{
    var greeting = context.Config.Get("greeting", "hello");

    _command = context.Commands.Register("myplugin", "example", (invocation, ct) =>
    {
        var online = context.GetHostService<IHostPlayerView>()?.OnlineCount ?? 0;
        return Task.FromResult<string?>($"{greeting}! {online} online.");
    });

    _hook = context.Events.Subscribe<ActionChangedEvent>((e, ct) =>
    {
        context.Logger.Info($"user {e.UserId} -> action {e.Action}");
        return Task.CompletedTask;
    });

    return Task.CompletedTask;
}
```

## Configuration

Per-plugin config lives under your id in `config/tomyum.yaml`:

```yaml
plugins:
  directory: plugins
  allowLooseSource: true
  trusted:
    - tomyum.session          # only trusted plugins get IPluginContext.Data
  example.my-plugin:          # <- your manifest Id; becomes IPluginContext.Config
    greeting: "sawasdee"
```

## Deploying

1. Put your `.dll` (or loose-source folder) in the server's `plugins/` directory.
2. Restart the server. The startup log shows `plugin loaded: <id> …` and `plugin host: N plugin(s) active`.
3. In-game, `!plugins` lists everything loaded (name, version, author).

## Rules of the road

- Reference **only** `TomYum.Plugins.Abstractions`. Referencing the server or infra breaks isolation
  and unloadability.
- Keep event handlers cheap — some run on the score/status hot path.
- A plugin that throws during init is logged and skipped; it never takes down the server or other
  plugins. But test yours.
- Trusted DB access is powerful and unguarded — only ask the operator for it if you truly own tables.

See also: the plugin template, [somtum-fun/tomyum-plugin-template](https://github.com/somtum-fun/tomyum-plugin-template).
