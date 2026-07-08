# TomYum plugin template

A starter for writing a **TomYum** plugin. Two shapes are shown — pick one:

| Shape | Folder | Deploy |
|-------|--------|--------|
| **Compiled** (`.dll`) | [`compiled/`](compiled/) | build → drop `MyPlugin.dll` into the server's `plugins/` |
| **Loose source** (`.cs`) | [`loose-source/`](loose-source/) | drop the whole folder into `plugins/`; the server compiles it at runtime |

A plugin references **only** `TomYum.Plugins.Abstractions` — never the server or infrastructure.

## Quick start (compiled)

1. Copy `compiled/` somewhere and rename `MyPlugin`.
2. Wire the `TomYum.Plugins.Abstractions` reference (see the comments in `MyPlugin.csproj`).
3. `dotnet build -c Release`
4. Copy `bin/Release/net10.0/MyPlugin.dll` into the server's `plugins/` directory and restart.
5. In-game: `!plugins` lists it; `!myplugin` runs its command.

## Quick start (loose source)

1. Copy `loose-source/` into the server's `plugins/` directory (e.g. `plugins/my-loose-plugin/`).
2. Edit `plugin.json` (`id`, `entryPoint`) and `code.cs`.
3. Restart the server — it compiles and loads the folder. Requires `Plugins:AllowLooseSource: true`.

## What a plugin can do

Everything flows through `IPluginContext`:

- `Config` — your settings, bound from `Plugins:<your-id>` in `config/tomyum.yaml`.
- `Logger` — structured logging scoped to your plugin id.
- `Commands` — register `!`commands (routed by BanchoBot).
- `Events` — subscribe to server events (`ActionChanged`, `ScoreSubmitting` (vetoable), …).
- `Http` — register HTTP routes, optionally on your own subdomain (Session-style frontends).
- `GetHostService<T>()` — inspect live state, e.g. `IHostPlayerView` (who's online, what they're doing).
- `Data` — raw DB access, **only** if the operator marks your plugin `trusted`.

See the full guide: **`docs/plugins.md`** in the TomYum repo.

## Making this a git submodule

The TomYum team ships this template as a standalone repo wired into TomYum as a submodule. To do that:

```bash
# from a copy of templates/plugin-template/
git init && git add . && git commit -m "TomYum plugin template"
gh repo create somtum-fun/tomyum-plugin-template --public --source=. --push

# then, inside the TomYum repo:
git submodule add https://github.com/somtum-fun/tomyum-plugin-template templates/plugin-template
git commit -m "chore: add plugin template as a submodule"
```
