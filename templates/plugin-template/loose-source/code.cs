// A loose-source ("folder/code.cs") TomYum plugin.
//
// No .csproj, no build step: the server compiles every .cs in this folder at runtime with Roslyn
// (referencing the host's assemblies), finds the class named by plugin.json's "entryPoint", and
// drives its lifecycle. Drop this whole folder into the server's plugins/ directory.

using System.Threading;
using System.Threading.Tasks;

using TomYum.Plugins.Abstractions;

public sealed class MyLoosePlugin : IPlugin
{
    private IDisposable? _command;

    public PluginManifest Manifest { get; } = new(
        "example.my-loose-plugin",
        "My Loose Plugin",
        "0.1.0",
        "your name here",
        "A starter loose-source TomYum plugin.");

    public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken = default)
    {
        _command = context.Commands.Register(
            "myloose",
            "Example command from the loose-source template.",
            (invocation, ct) => Task.FromResult<string?>("hello from a loose-source plugin!"));

        context.Logger.Info("my-loose-plugin initialized");
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        _command?.Dispose();
        return Task.CompletedTask;
    }
}
