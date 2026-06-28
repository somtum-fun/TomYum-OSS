using TomYum.Bancho;

namespace TomYum.Server.Bancho.Packets;

/// <summary>
/// CHANGE_ACTION (id 0): the client reports a new status (idle, playing, watching, …).
/// Payload: u8 action + string info_text + string map_md5 + u32 mods + u8 mode + i32 map_id.
/// We fold it into the player's <see cref="BanchoPresence"/> and push the refreshed USER_STATS
/// (id 11) to every other online player so their user panels update, plus echo it back to the
/// originator so their own panel stays in sync.
/// </summary>
public sealed class ChangeActionHandler : IClientPacketHandler
{
    /// <inheritdoc />
    public ClientPacketId PacketId => ClientPacketId.ChangeAction;

    /// <inheritdoc />
    public void Handle(ClientPacketContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var reader = new BanchoPacketReader(context.Payload.Span);
        var presence = context.Player.Presence;

        presence.Action = reader.ReadByte();
        presence.ActionInfo = reader.ReadString();
        presence.BeatmapMd5 = reader.ReadString();
        presence.Mods = (Mods)reader.ReadUInt32();
        presence.Mode = reader.ReadByte();
        presence.BeatmapId = reader.ReadInt32();

        // Encode the refreshed stats once, then fan out: everyone else gets it queued, and the
        // originator gets it in this poll's response so their own panel reflects the change.
        var writer = new BanchoPacketWriter(128);
        LoginResponseBuilder.WriteStats(writer, presence);
        var stats = writer.ToArray();

        context.Registry.EnqueueToAll(stats, exceptUserId: context.Player.UserId);
        context.Response.WriteRaw(stats);
    }
}
