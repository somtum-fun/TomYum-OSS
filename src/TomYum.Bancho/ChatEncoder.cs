namespace TomYum.Bancho;

/// <summary>
/// Encodes the server→client chat and channel packets: SEND_MESSAGE (7), CHANNEL_INFO (65),
/// CHANNEL_INFO_END (89), CHANNEL_JOIN_SUCCESS (64), CHANNEL_AUTO_JOIN (67), and CHANNEL_KICK
/// (66). Mirrors <see cref="LoginResponseBuilder"/>: this type owns only the wire encoding;
/// channel membership and routing decisions live in the host (<c>TomYum.Server</c>).
/// </summary>
/// <remarks>
/// Composite layouts (see <c>memories/protocols/bancho-stable.md</c> §8):
/// <list type="bullet">
/// <item><c>message</c> = string sender + string text + string target + i32 sender_id.</item>
/// <item><c>channel</c> = string name + string topic + u16 player_count.</item>
/// </list>
/// </remarks>
public static class ChatEncoder
{
    /// <summary>Writes a SEND_MESSAGE (id 7) packet carrying <paramref name="message"/>.</summary>
    public static void WriteMessage(BanchoPacketWriter writer, BanchoMessage message)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WritePacket(ServerPacketId.SendMessage, w =>
        {
            w.WriteString(message.Sender);
            w.WriteString(message.Text);
            w.WriteString(message.Target);
            w.WriteInt32(message.SenderId);
        });
    }

    /// <summary>
    /// Writes a CHANNEL_INFO (id 65) packet advertising a channel's metadata: name, topic, and
    /// current member count. Sent for each visible channel during the login burst.
    /// </summary>
    public static void WriteChannelInfo(BanchoPacketWriter writer, string name, string topic, int memberCount)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WritePacket(ServerPacketId.ChannelInfo, w =>
        {
            w.WriteString(name);
            w.WriteString(topic);
            w.WriteUInt16((ushort)memberCount);
        });
    }

    /// <summary>
    /// Writes a CHANNEL_AUTO_JOIN (id 67) packet, structurally identical to CHANNEL_INFO but
    /// instructing the client to auto-open the channel (e.g. <c>#osu</c>).
    /// </summary>
    public static void WriteChannelAutoJoin(BanchoPacketWriter writer, string name, string topic, int memberCount)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WritePacket(ServerPacketId.ChannelAutoJoin, w =>
        {
            w.WriteString(name);
            w.WriteString(topic);
            w.WriteUInt16((ushort)memberCount);
        });
    }

    /// <summary>Writes CHANNEL_INFO_END (id 89): marks the end of the initial channel-info burst.</summary>
    public static void WriteChannelInfoEnd(BanchoPacketWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WritePacket(ServerPacketId.ChannelInfoEnd, null);
    }

    /// <summary>
    /// Writes CHANNEL_JOIN_SUCCESS (id 64): confirms to the client that it joined
    /// <paramref name="channelName"/>, so the client opens the tab.
    /// </summary>
    public static void WriteChannelJoinSuccess(BanchoPacketWriter writer, string channelName)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringPacket(ServerPacketId.ChannelJoinSuccess, channelName);
    }

    /// <summary>
    /// Writes CHANNEL_KICK (id 66): tells the client it was removed from
    /// <paramref name="channelName"/> (or that a requested join was refused), closing the tab.
    /// </summary>
    public static void WriteChannelKick(BanchoPacketWriter writer, string channelName)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringPacket(ServerPacketId.ChannelKick, channelName);
    }
}
