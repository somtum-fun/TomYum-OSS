namespace TomYum.Plugins.Abstractions;

/// <summary>Messages to online osu!(stable) players, for plugins (e.g. a vote's thank-you).</summary>
public interface IHostMessenger
{
    /// <summary>A private message from BanchoBot. False when the player isn't online.</summary>
    bool SendBotMessage(long userId, string text);

    /// <summary>A notification (the client's pop-up). False when the player isn't online.</summary>
    bool SendNotification(long userId, string text);
}
