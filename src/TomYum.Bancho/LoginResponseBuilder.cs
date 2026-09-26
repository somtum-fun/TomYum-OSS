namespace TomYum.Bancho;

/// <summary>
/// Composes the server→client bancho packet bursts for login, both the failure reply and
/// the full successful-login sequence described in <c>memories/protocols/bancho-stable.md</c>
/// §3. The protocol version is pinned at <see cref="ProtocolVersion"/> (19), the canonical
/// value across the official 2016 server, bancho.py, BanchoNET, and Sunrise.
/// </summary>
/// <remarks>
/// This type owns only the wire encoding of the login burst. Deciding <em>whether</em> a
/// login succeeds (credential check, ban state, the player registry) belongs to the host
/// (<c>TomYum.Server</c>); this builder turns a decision into bytes.
/// </remarks>
public static class LoginResponseBuilder
{
    /// <summary>The canonical bancho protocol version sent in PROTOCOL_VERSION (id 75).</summary>
    public const int ProtocolVersion = 19;

    /// <summary>
    /// Builds the body for a failed login: a single USER_ID (id 5) packet carrying the
    /// negative <paramref name="reason"/>. The caller still sets a <c>cho-token</c> header
    /// (any value) on the HTTP response; the client only branches on this packet.
    /// </summary>
    public static byte[] BuildFailure(LoginFailureReason reason)
    {
        var writer = new BanchoPacketWriter(16);
        writer.WriteInt32Packet(ServerPacketId.UserId, (int)reason);
        return writer.ToArray();
    }

    /// <summary>
    /// Builds the successful-login burst for <paramref name="self"/>: protocol version,
    /// the user id, privileges, a welcome notification, the visible channel list, the main
    /// menu icon, friends list, silence-end, and the player's own presence + stats.
    /// </summary>
    /// <param name="self">The freshly authenticated player.</param>
    /// <param name="welcome">Welcome message shown as a NOTIFICATION (id 24).</param>
    /// <param name="writeChannels">
    /// Optional callback that writes CHANNEL_INFO (65) / CHANNEL_AUTO_JOIN (67) packets for the
    /// visible channels. Invoked before CHANNEL_INFO_END (89) so the client has the full list
    /// before the terminator. <c>null</c> advertises no channels (just the terminator).
    /// </param>
    public static byte[] BuildSuccess(BanchoPresence self, string welcome, Action<BanchoPacketWriter>? writeChannels = null)
    {
        ArgumentNullException.ThrowIfNull(self);

        var writer = new BanchoPacketWriter(512);

        // 1. Protocol version so the client knows we speak its dialect.
        writer.WriteInt32Packet(ServerPacketId.ProtocolVersion, ProtocolVersion);

        // 2. The login reply: a non-negative id means success.
        writer.WriteInt32Packet(ServerPacketId.UserId, self.UserId);

        // 3. Client-side privilege bitmask (drives UI: supporter, tournament, etc.).
        writer.WriteInt32Packet(ServerPacketId.Privileges, (int)self.Privileges);

        // 4. Welcome notification.
        writer.WriteStringPacket(ServerPacketId.Notification, welcome);

        // 5. Advertise the visible channels (CHANNEL_INFO / CHANNEL_AUTO_JOIN), then terminate
        //    the list so the client stops waiting for more CHANNEL_INFO packets.
        writeChannels?.Invoke(writer);
        writer.WritePacket(ServerPacketId.ChannelInfoEnd, null);

        // 6. Main menu icon: "imageUrl|clickUrl"; empty string => default osu! icon.
        writer.WriteStringPacket(ServerPacketId.MainMenuIcon, string.Empty);

        // 7. Friends list (empty until persistence lands).
        writer.WritePacket(ServerPacketId.FriendsList, w => w.WriteInt32List([]));

        // 8. Remaining silence in seconds (0 = not silenced).
        writer.WritePacket(ServerPacketId.SilenceEnd, w => w.WriteUInt32(0));

        // 9. The player's own presence + stats so their profile renders immediately.
        WritePresence(writer, self);
        WriteStats(writer, self);

        return writer.ToArray();
    }

    /// <summary>
    /// Writes a USER_PRESENCE (id 83) packet: static identity (name, country, privileges,
    /// timezone, location, global rank). Payload layout per protocol note §5.
    /// </summary>
    public static void WritePresence(BanchoPacketWriter writer, BanchoPresence p)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(p);

        writer.WritePacket(ServerPacketId.UserPresence, w =>
        {
            w.WriteInt32(p.UserId);
            w.WriteString(p.Username);
            w.WriteByte((byte)(p.UtcOffset + 24));
            w.WriteByte(p.CountryCode);
            // Low 5 bits = bancho privileges shown client-side, high 3 bits = game mode.
            w.WriteByte((byte)((int)p.Privileges & 0x1F | (p.Mode & 0x07) << 5));
            w.WriteSingle(p.Longitude);
            w.WriteSingle(p.Latitude);
            w.WriteInt32(p.GlobalRank);
        });
    }

    /// <summary>
    /// Writes a USER_STATS (id 11) packet: dynamic state (current action, beatmap, mods,
    /// scores, accuracy, pp, rank). Payload layout per protocol note §5.
    /// </summary>
    public static void WriteStats(BanchoPacketWriter writer, BanchoPresence p)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(p);

        writer.WritePacket(ServerPacketId.UserStats, w =>
        {
            w.WriteInt32(p.UserId);
            w.WriteByte(p.Action);
            w.WriteString(p.ActionInfo);
            w.WriteString(p.BeatmapMd5);
            w.WriteInt32((int)p.Mods);
            w.WriteByte((byte)p.Mode);
            w.WriteInt32(p.BeatmapId);
            w.WriteInt64(p.RankedScore);
            w.WriteSingle(p.Accuracy);
            w.WriteInt32(p.PlayCount);
            w.WriteInt64(p.TotalScore);
            w.WriteInt32(p.GlobalRank);
            w.WriteInt16(p.Performance);
        });
    }
}
