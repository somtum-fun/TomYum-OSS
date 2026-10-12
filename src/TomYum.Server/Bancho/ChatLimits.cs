namespace TomYum.Server.Bancho;

/// <summary>How much players may say in chat: a message's length (as long as the chat log keeps) and how often.</summary>
public static class ChatLimits
{
    /// <summary>Longer messages are cut to this many characters (the chat log keeps as many).</summary>
    public const int MaxMessageLength = 2048;

    /// <summary>Messages a player may send in <see cref="Window"/>; more are dropped (and they're told).</summary>
    public const int MessagesPerWindow = 10;

    /// <summary>The window <see cref="MessagesPerWindow"/> counts over.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(5);

    /// <summary>What BanchoBot tells a player whose message was dropped for coming too fast.</summary>
    public const string TooFast = "You're sending messages a little too fast. Wait a few seconds, then try again.";

    /// <summary><paramref name="text"/> cut to <paramref name="length"/> characters.</summary>
    public static string Cut(string text, int length)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Length <= length ? text : text[..length];
    }
}
