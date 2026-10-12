# TomYum Plugins

This is the **runtime drop-in directory** for TomYum plugins. The server scans it on
startup (and, where supported, on change) and loads anything it finds. Plugins extend
the server without modifying core code.

See `memories/architecture/decisions/0007-plugin-model.md` and
`memories/features/plugins.md` for the full design, and
`memories/libraries/TomYum.Plugins.Abstractions.md` for the API reference.

## Two supported shapes

A plugin is anything that exposes a type implementing `TomYum.Plugins.Abstractions.IPlugin`.
The loader accepts it in either of two forms:

### 1. Compiled assembly (`.dll`)

Drop a built `.dll` (and any of its private dependencies) directly in this folder, or in
a subfolder:

```
plugins/
└── my-plugin/
    └── MyPlugin.dll
```

The `.dll` is loaded into an isolated `AssemblyLoadContext` so it can be unloaded/reloaded
without restarting the server. Build such a plugin against **only**
`TomYum.Plugins.Abstractions` — never reference core or infrastructure projects. See
`examples/CompiledPlugin/` for a minimal project.

### 2. Loose source (`folder/code.cs`)

Drop a folder containing one or more `.cs` files plus a `plugin.json` manifest. The loader
compiles the source at runtime with Roslyn and loads the result:

```
plugins/
└── hello-source/
    ├── plugin.json
    └── code.cs
```

This is ideal for quick operator tweaks and small plugins with no build step. See
`examples/LooseSourcePlugin/` for the shape.

## `plugin.json` manifest (loose-source plugins)

```json
{
  "id": "tomyum.example.hello-source",
  "name": "Hello (loose source)",
  "version": "0.1.0",
  "authors": "Phapoom Saksri",
  "description": "Minimal no-op loose-source example.",
  "entryPoint": "HelloSourcePlugin"
}
```

Compiled `.dll` plugins carry their manifest in code via `IPlugin.Manifest`, so they do
not need a `plugin.json`.

## Configuration

Each plugin gets a scoped config subtree under `plugins:<id>` (see `config/tomyum.yaml`).
Enable/disable a plugin and pass it settings there. Secrets (webhook URLs, tokens) come
from environment variables / k8s secrets, never committed config.

## Rules for plugin authors

- **Do** depend only on `TomYum.Plugins.Abstractions`.
- **Do** register hooks, commands, and routes in `InitializeAsync` and release them in `ShutdownAsync`.
- **Do** read config and secrets through `IPluginContext.Config`.
- **Don't** reference core/infrastructure projects or reach into server internals.
- **Don't** block the server: keep event handlers async and fast; offload heavy work.
- **Don't** assume a specific storage provider or database — go through host services.
