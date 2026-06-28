using System.Globalization;
using TomYum.Bancho;

namespace TomYum.Server.Bancho;

/// <summary>
/// The server's resident chat bot. <c>BanchoBot</c> is a virtual player (user id
/// <see cref="UserId"/>) that is always online, never logs in, is never reaped, and is a
/// permanent member of the default channels. It answers <c>!</c>-prefixed commands sent to it
/// (publicly in a channel or as a private message) and is the voice the server speaks with.
/// </summary>
/// <remarks>
/// Seeded into the <see cref="PlayerRegistry"/> at startup so it shows up in every client's
/// user list and its messages carry a real presence. Command handling is deliberately tiny for
/// this slice (<c>!help</c>, <c>!roll</c>, <c>!ping</c>); a richer command framework with
/// privilege gating is a later slice.
/// </remarks>
public sealed class BanchoBot
{
    /// <summary>BanchoBot's reserved user id (1, matching bancho.py / the official server).</summary>
    public const int UserId = 1;

    /// <summary>BanchoBot's display name.</summary>
    public const string Name = "BanchoBot";

    private readonly TomYumVersion _version;

    /// <summary>Creates the bot over the running server version (used by <c>!version</c>/<c>!help</c>).</summary>
    public BanchoBot(TomYumVersion version)
    {
        _version = version;
    }

    /// <summary>
    /// Builds the bot's <see cref="BanchoPresence"/>: a bot-flagged, always-idle presence so it
    /// renders in the user list. Country/location are left at zero (the bot has no real locale).
    /// </summary>
    public static BanchoPresence CreatePresence() => new()
    {
        UserId = UserId,
        Username = Name,
        Privileges = BanchoPrivileges.Player,
        // Action 0 = idle; the bot is never "playing".
        Action = 0,
        ActionInfo = "Looking after TomYum",
    };

    /// <summary>
    /// Produces the bot's reply to a command, or <c>null</c> when the input is not a command the
    /// bot recognises (so callers can stay silent rather than echo an error for ordinary chat).
    /// </summary>
    /// <param name="sender">The display name of the player who invoked the command.</param>
    /// <param name="message">The raw message text (may or may not start with <c>!</c>).</param>
    public string? TryHandleCommand(string sender, string message)
    {
        if (string.IsNullOrWhiteSpace(message) || message[0] != '!')
        {
            return null;
        }

        var parts = message[1..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return null;
        }

        var command = parts[0].ToLowerInvariant();
        return command switch
        {
            "help" => "Commands: !help, !ping, !roll [max], !version. More to come.",
            "ping" => "pong!",
            "version" => string.Create(CultureInfo.InvariantCulture, $"TomYum {_version.Display}"),
            "roll" => Roll(sender, parts),
            _ => string.Create(CultureInfo.InvariantCulture, $"Unknown command \"{command}\". Try !help."),
        };
    }

    private static string Roll(string sender, string[] parts)
    {
        var max = 100;
        if (parts.Length > 1 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var requested) && requested > 0)
        {
            max = requested;
        }

        var value = Random.Shared.Next(1, max + 1);
        return string.Create(CultureInfo.InvariantCulture, $"{sender} rolls {value} point(s)! (1-{max})");
    }
}
