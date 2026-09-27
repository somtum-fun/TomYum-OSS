using TomYum.Bancho;

namespace TomYum.Server.Bancho;

/// <summary>Bancho packets for restrictions and silences.</summary>
public static class ModerationPackets
{
    /// <summary>Tells the client its account is restricted (it shows the restricted notice).</summary>
    public static void WriteRestricted(BanchoPacketWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteStringPacket(ServerPacketId.Notification,
            "Your account is restricted: you can still play, but you're hidden from other players and rankings. Contact staff if you think this is a mistake.");
        writer.WritePacket(ServerPacketId.AccountRestricted, null);
    }

    /// <summary>Tells the client how long it's silenced (0 lifts a silence).</summary>
    public static void WriteSilenceEnd(BanchoPacketWriter writer, uint seconds)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WritePacket(ServerPacketId.SilenceEnd, w => w.WriteUInt32(seconds));
    }

    /// <summary>Tells other clients a user was silenced (they hide that user's messages).</summary>
    public static void WriteUserSilenced(BanchoPacketWriter writer, int userId)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteInt32Packet(ServerPacketId.UserSilenced, userId);
    }

    /// <summary>The reply to a message from a silenced player.</summary>
    public static string SilencedReply(uint secondsLeft)
    {
        var left = TimeSpan.FromSeconds(secondsLeft);
        var readable = left.TotalHours >= 1
            ? $"{(int)left.TotalHours}h {left.Minutes}m"
            : left.TotalMinutes >= 1 ? $"{(int)left.TotalMinutes}m {left.Seconds}s" : $"{left.Seconds}s";
        return $"You're silenced for another {readable}; your message wasn't sent.";
    }
}
