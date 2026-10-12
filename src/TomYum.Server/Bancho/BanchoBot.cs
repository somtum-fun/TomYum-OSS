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
    /// <summary>
    /// BanchoBot's user id when nothing else is set: 3, like the official server's (lazer shows no
    /// avatar for id 1, its guest user).
    /// </summary>
    public const int DefaultUserId = 3;

    /// <summary>
    /// BanchoBot's reserved user id. <see cref="DefaultUserId"/> unless the setting
    /// <c>Bancho:BotUserId</c> (<c>TOMYUM_BANCHO__BOTUSERID</c>) says otherwise: a server whose
    /// players were imported from another server with their ids (tools/ImportBanchoPy) may need
    /// the bot elsewhere, say 2, when an old player already holds 3. Set once at startup
    /// (<see cref="Configure"/>), before anything reads it; osu-web's <c>BANCHO_BOT_USER_ID</c>
    /// must say the same.
    /// </summary>
    public static int UserId { get; private set; } = DefaultUserId;

    /// <summary>The setting that holds the bot's id.</summary>
    public const string UserIdSetting = "Bancho:BotUserId";

    /// <summary>
    /// Sets <see cref="UserId"/> (and <see cref="Name"/>) for this process. Ids below 2 are refused: lazer treats ids up
    /// to 1 as no user.
    /// </summary>
    public static void Configure(int userId, string? name = null)
    {
        if (userId < 2)
            throw new ArgumentOutOfRangeException(nameof(userId), userId, $"{UserIdSetting} must be 2 or more.");
        UserId = userId;
        Name = string.IsNullOrWhiteSpace(name) ? DefaultName : name.Trim();
    }

    /// <summary>The default display name (osu!'s).</summary>
    public const string DefaultName = "BanchoBot";

    /// <summary>The setting that holds the bot's name.</summary>
    public const string NameSetting = "Bancho:BotName";

    /// <summary>
    /// The bot's display name, <c>Bancho:BotName</c> (<c>TOMYUM_BANCHO__BOTNAME</c>), set once at startup
    /// by <see cref="Configure(int, string?)"/>; a server moved from bancho.py can keep its bot's name.
    /// </summary>
    public static string Name { get; private set; } = DefaultName;

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
