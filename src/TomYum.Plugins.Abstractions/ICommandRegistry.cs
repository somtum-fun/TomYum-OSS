namespace TomYum.Plugins.Abstractions;

/// <summary>
/// Registry for chat/console commands contributed by plugins (e.g. <c>!dan</c>).
/// The host routes a matching command line to the registered handler.
/// </summary>
public interface ICommandRegistry
{
    /// <summary>
    /// Registers a command.
    /// </summary>
    /// <param name="name">Command name without prefix, e.g. <c>dan</c>.</param>
    /// <param name="description">Help text shown to users.</param>
    /// <param name="handler">Invoked with the parsed command invocation; returns the reply text.</param>
    /// <returns>A disposable that unregisters the command when disposed.</returns>
    IDisposable Register(
        string name,
        string description,
        Func<CommandInvocation, CancellationToken, Task<string?>> handler);
}

/// <summary>A single parsed command invocation handed to a command handler.</summary>
/// <param name="Name">The command name that matched.</param>
/// <param name="Arguments">Whitespace-split arguments following the command name.</param>
/// <param name="RawText">The full original command text.</param>
/// <param name="InvokerUserId">Id of the user who invoked the command, when known.</param>
/// <param name="Channel">Channel the command came from, or <c>null</c> for private/console.</param>
public sealed record CommandInvocation(
    string Name,
    IReadOnlyList<string> Arguments,
    string RawText,
    long? InvokerUserId,
    string? Channel);
