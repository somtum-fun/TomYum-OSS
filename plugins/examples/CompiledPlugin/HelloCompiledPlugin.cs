// CompiledPlugin — the ".dll" plugin shape.
//
// Compiled out-of-tree into a single assembly and dropped into plugins/ as a .dll.
// The loader scans the assembly for an IPlugin implementation, instantiates it inside
// a dedicated AssemblyLoadContext (so it can be unloaded), and drives its lifecycle.
// See plugins/README.md and memories/architecture/decisions/0007-plugin-model.md.

using System;
using System.Threading;
using System.Threading.Tasks;
using TomYum.Plugins.Abstractions;

namespace TomYum.Examples.Compiled;

/// <summary>A no-op example proving the compiled-assembly plugin shape loads and runs.</summary>
public sealed class HelloCompiledPlugin : IPlugin
{
    private IDisposable? commandRegistration;

    public PluginManifest Manifest { get; } = new(
        Id: "tomyum.example.hello-compiled",
        Name: "Hello (compiled)",
        Version: "0.1.0",
        Authors: "TomYum contributors",
        Description: "Minimal no-op compiled-assembly example.");

    public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken = default)
    {
        context.Logger.Info("hello from the compiled example plugin");

        // Example command registration; the returned disposable is released on shutdown.
        commandRegistration = context.Commands.Register(
            name: "hellocompiled",
            description: "Replies with a greeting from the compiled example plugin.",
            handler: (invocation, ct) => Task.FromResult<string?>("hi from CompiledPlugin!"));

        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        commandRegistration?.Dispose();
        commandRegistration = null;
        return Task.CompletedTask;
    }
}
