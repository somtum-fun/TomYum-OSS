using TomYum.Bancho;
using TomYum.Plugins.Abstractions;
using TomYum.Server.Bancho;

namespace TomYum.Server.Plugins;

/// <summary><see cref="IHostMessenger"/> over the live <see cref="PlayerRegistry"/>.</summary>
internal sealed class HostMessenger : IHostMessenger
{
    private readonly PlayerRegistry _players;

    public HostMessenger(PlayerRegistry players) => _players = players;

    public bool SendBotMessage(long userId, string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        if (Find(userId) is not { } player)
            return false;

        var writer = new BanchoPacketWriter(64 + text.Length * 3);
        ChatEncoder.WriteMessage(writer, new BanchoMessage(BanchoBot.Name, text, player.Presence.Username, BanchoBot.UserId));
        player.Enqueue(writer.ToArray());
        return true;
    }

    public bool SendNotification(long userId, string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        if (Find(userId) is not { } player)
            return false;

        var writer = new BanchoPacketWriter(16 + text.Length * 3);
        writer.WriteStringPacket(ServerPacketId.Notification, text);
        player.Enqueue(writer.ToArray());
        return true;
    }

    private OnlinePlayer? Find(long userId) =>
        userId is > 0 and <= int.MaxValue && userId != BanchoBot.UserId ? _players.GetByUserId((int)userId) : null;
}
