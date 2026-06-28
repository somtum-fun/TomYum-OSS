// LooseSourcePlugin — the "folder/code.cs" plugin shape.
//
// The Roslyn-based loader (TomYum.Plugins.Loader) compiles every .cs file in this
// folder at runtime, references ONLY TomYum.Plugins.Abstractions, finds the single
// IPlugin implementation named by plugin.json's entryPoint, and drives its lifecycle.
//
// No .csproj, no build step — drop the folder into plugins/ and restart (or hot-load).
// See plugins/README.md and memories/architecture/decisions/0007-plugin-model.md.

using System;
using System.Threading;
using System.Threading.Tasks;
using TomYum.Plugins.Abstractions;

namespace TomYum.Examples.LooseSource;

/// <summary>A no-op example proving the loose-source plugin shape loads and runs.</summary>
public sealed class HelloSourcePlugin : IPlugin
{
    public PluginManifest Manifest { get; } = new(
        Id: "tomyum.example.hello-source",
        Name: "Hello (loose source)",
        Version: "0.1.0",
        Authors: "Phapoom Saksri",
        Description: "Minimal no-op loose-source example compiled at runtime.");

    public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken = default)
    {
        context.Logger.Info("hello from the loose-source example plugin");

        // Example hook registration (commented — events land when the host raises them):
        // context.Events.Subscribe<SomeEvent>((e, ct) => Task.CompletedTask);
        // context.Commands.Register("hello", "say hello", (cmd, ct) => Task.FromResult<string?>("hi!"));

        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        context_LogShutdown();
        return Task.CompletedTask;

        static void context_LogShutdown()
        {
            // nothing to release in this example
        }
    }
}
