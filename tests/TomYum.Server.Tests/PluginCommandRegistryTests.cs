using TomYum.Plugins.Abstractions;
using TomYum.Server.Plugins;

using Xunit;

namespace TomYum.Server.Tests;

/// <summary>
/// Plugin commands register through the per-plugin facade, are found by name (case-insensitive),
/// reject duplicates, and unregister on dispose — the surface <c>BanchoBotCommands</c> bridges to.
/// </summary>
public class PluginCommandRegistryTests
{
    private static readonly Func<CommandInvocation, CancellationToken, Task<string?>> Reply =
        (_, _) => Task.FromResult<string?>("ok");

    [Fact]
    public void Registered_command_is_found_by_name()
    {
        var registry = new PluginCommandRegistry();
        var scoped = new ScopedCommandRegistry(registry, "test.plugin");

        using var _ = scoped.Register("hello", "say hi", Reply);

        Assert.True(registry.TryGet("HELLO", out var command));
        Assert.NotNull(command);
        Assert.Equal("test.plugin", command!.PluginId);
        Assert.Equal("hello", command.Name);
    }

    [Fact]
    public void Duplicate_registration_throws()
    {
        var registry = new PluginCommandRegistry();
        var scoped = new ScopedCommandRegistry(registry, "test.plugin");
        using var _ = scoped.Register("dup", "first", Reply);

        Assert.Throws<InvalidOperationException>(() => scoped.Register("dup", "second", Reply));
    }

    [Fact]
    public void Dispose_unregisters_the_command()
    {
        var registry = new PluginCommandRegistry();
        var scoped = new ScopedCommandRegistry(registry, "test.plugin");

        var registration = scoped.Register("temp", "temp", Reply);
        Assert.True(registry.TryGet("temp", out _));

        registration.Dispose();
        Assert.False(registry.TryGet("temp", out _));
    }
}
