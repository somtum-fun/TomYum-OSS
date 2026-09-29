using TomYum.Bancho;

namespace TomYum.Server.Bancho;

/// <summary>
/// The server's resident chat bot. <c>BanchoBot</c> is a virtual player (user id
/// <see cref="UserId"/>) that is always online, never logs in, is never reaped, and is a
/// permanent member of the default channels. It is the voice the server speaks with; the actual
/// <c>!</c>-command parsing lives in <see cref="BanchoBotCommands"/>.
/// </summary>
/// <remarks>
/// Seeded into the <see cref="PlayerRegistry"/> at startup so it shows up in every client's user
/// list and its messages carry a real presence. This type stays small on purpose — its identity
/// (id, name, presence) plus a thin delegate to the command set. New commands go in
/// <see cref="BanchoBotCommands"/>, not here.
/// </remarks>
public sealed class BanchoBot
{
    /// <summary>BanchoBot's reserved user id (1, matching bancho.py / the official server).</summary>
    public const int UserId = 1;

    /// <summary>BanchoBot's display name.</summary>
    public const string Name = "BanchoBot";

    private readonly BanchoBotCommands _commands;

    /// <summary>Creates the bot over its command set.</summary>
    public BanchoBot(BanchoBotCommands commands) => _commands = commands;

    /// <summary>
    /// Builds the bot's <see cref="BanchoPresence"/>: a bot-flagged, always-idle presence so it
    /// renders in the user list, from Saint Helena like osu!'s BanchoBot.
    /// </summary>
    public static BanchoPresence CreatePresence() => new()
    {
        UserId = UserId,
        Username = Name,
        Privileges = BanchoPrivileges.Player,
        CountryCode = CountryCodes.ToNumeric("sh"),
        // Action 0 = idle; the bot is never "playing".
        Action = 0,
        ActionInfo = "Looking after TomYum",
    };

    /// <summary>
    /// Produces the bot's reply to a command, or <c>null</c> when the input is not a command the
    /// bot recognises (so callers stay silent rather than echo an error for ordinary chat).
    /// Delegates to <see cref="BanchoBotCommands"/>. Async because some commands (e.g.
    /// <c>!setpriv</c>) persist a change through the repository.
    /// </summary>
    /// <param name="caller">The player who invoked the command (acting user for staff commands).</param>
    /// <param name="message">The raw message text (may or may not start with <c>!</c>).</param>
    /// <param name="privileges">The caller's client privileges, used to gate staff-only commands.</param>
    /// <param name="cancellationToken">Cancels any repository work a command performs.</param>
    public Task<string?> TryHandleCommandAsync(
        OnlinePlayer caller,
        string message,
        BanchoPrivileges privileges,
        CancellationToken cancellationToken = default) =>
        _commands.TryHandleAsync(caller, message, privileges, cancellationToken);
}
