using System.Collections.Concurrent;

using TomYum.Plugins.Abstractions;

namespace TomYum.Server.Plugins;

/// <summary>
/// Shared store of plugin-contributed <c>!</c>commands. <c>BanchoBotCommands</c> consults it for any
/// command that isn't built in, so plugins add commands with no core change. Each
/// registration is tagged with the owning plugin id (for <c>!plugins</c> listing and unload).
/// </summary>
public sealed class PluginCommandRegistry
{
    private readonly ConcurrentDictionary<string, PluginCommand> _commands = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A snapshot of all registered plugin commands.</summary>
    public IReadOnlyCollection<PluginCommand> Commands => _commands.Values.ToArray();

    /// <summary>Looks up a command by name (without the <c>!</c> prefix).</summary>
    public bool TryGet(string name, out PluginCommand? command) => _commands.TryGetValue(name, out command);

    internal IDisposable Register(
        string pluginId,
        string name,
        string description,
        Func<CommandInvocation, CancellationToken, Task<string?>> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(handler);

        var normalized = name.TrimStart('!').ToLowerInvariant();
        if (Bancho.BanchoBotCommands.BuiltIn.Contains(normalized))
            throw new InvalidOperationException(
                $"plugin '{pluginId}' cannot register command '!{normalized}': it is a built-in command.");
        var command = new PluginCommand(pluginId, normalized, description ?? string.Empty, handler);
        if (!_commands.TryAdd(normalized, command))
            throw new InvalidOperationException(
                $"plugin '{pluginId}' cannot register command '!{normalized}': it is already registered.");

        return new Registration(this, normalized, command);
    }

    private void Remove(string name, PluginCommand expected)
    {
        // Only remove if it is still the same registration, so re-registration after unload is safe.
        if (_commands.TryGetValue(name, out var current) && ReferenceEquals(current, expected))
            _commands.TryRemove(name, out _);
    }

    private sealed class Registration : IDisposable
    {
        private readonly string _name;
        private readonly PluginCommand _command;
        private PluginCommandRegistry? _owner;

        public Registration(PluginCommandRegistry owner, string name, PluginCommand command)
        {
            _owner = owner;
            _name = name;
            _command = command;
        }

        public void Dispose()
        {
            _owner?.Remove(_name, _command);
            _owner = null;
        }
    }
}

/// <summary>A plugin-contributed chat command.</summary>
/// <param name="PluginId">Id of the plugin that owns the command.</param>
/// <param name="Name">Command name without the <c>!</c> prefix (lower-cased).</param>
/// <param name="Description">Help text.</param>
/// <param name="Handler">Invoked with the parsed invocation; returns the reply text (or null).</param>
public sealed record PluginCommand(
    string PluginId,
    string Name,
    string Description,
    Func<CommandInvocation, CancellationToken, Task<string?>> Handler);

/// <summary>
/// Per-plugin facade over <see cref="PluginCommandRegistry"/> that tags every registration with the
/// owning plugin id. Handed to a plugin as <see cref="IPluginContext.Commands"/>.
/// </summary>
internal sealed class ScopedCommandRegistry : ICommandRegistry
{
    private readonly PluginCommandRegistry _shared;
    private readonly string _pluginId;

    public ScopedCommandRegistry(PluginCommandRegistry shared, string pluginId)
    {
        _shared = shared;
        _pluginId = pluginId;
    }

    public IDisposable Register(
        string name,
        string description,
        Func<CommandInvocation, CancellationToken, Task<string?>> handler)
        => _shared.Register(_pluginId, name, description, handler);
}
