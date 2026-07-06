using Microsoft.Extensions.DependencyInjection;
using TomYum.Bancho;
using TomYum.Domain.Repositories;

namespace TomYum.Server.Bancho.Packets;

/// <summary>
/// CHANGE_ACTION (id 0): the client reports a new status (idle, playing, watching, …).
/// Payload: u8 action + string info_text + string map_md5 + u32 mods + u8 mode + i32 map_id.
/// We fold it into the player's <see cref="BanchoPresence"/> and push the refreshed USER_STATS
/// (id 11) to every other online player so their user panels update, plus echo it back to the
/// originator so their own panel stays in sync.
/// When the mode changes, stats (PP, accuracy, ranked score, rank) are reloaded from the DB
/// for the new mode before the broadcast.
/// </summary>
public sealed class ChangeActionHandler : IClientPacketHandler
{
    /// <inheritdoc />
    public ClientPacketId PacketId => ClientPacketId.ChangeAction;

    /// <inheritdoc />
    public async Task HandleAsync(ClientPacketContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var reader = new BanchoPacketReader(context.Payload.Span);
        var presence = context.Player.Presence;

        presence.Action     = reader.ReadByte();
        presence.ActionInfo = reader.ReadString();
        presence.BeatmapMd5 = reader.ReadString();
        presence.Mods       = (Mods)reader.ReadUInt32();
        var newMode         = reader.ReadByte();
        presence.BeatmapId  = reader.ReadInt32();

        // If the player switched modes, reload their stats for the new mode from the DB
        // so the presence reflects the correct PP / accuracy / ranked score / rank.
        if (newMode != presence.Mode)
        {
            presence.Mode = newMode;
            var userStats = context.Services.GetRequiredService<IUserStatsRepository>();
            var stats = await userStats.GetOrCreateAsync(context.Player.UserId, newMode, cancellationToken).ConfigureAwait(false);
            var rank  = await userStats.GetGlobalRankAsync(context.Player.UserId, newMode, cancellationToken).ConfigureAwait(false);

            presence.RankedScore = stats.RankedScore;
            presence.Accuracy    = stats.Accuracy;
            presence.PlayCount   = stats.PlayCount;
            presence.Performance = (short)Math.Clamp((int)stats.Pp, short.MinValue, short.MaxValue);
            presence.GlobalRank  = rank;
        }
        else
        {
            presence.Mode = newMode;
        }

        // Encode the refreshed stats once, then fan out: everyone else gets it queued, and the
        // originator gets it in this poll's response so their own panel reflects the change.
        var writer = new BanchoPacketWriter(128);
        LoginResponseBuilder.WriteStats(writer, presence);
        var stats2 = writer.ToArray();

        context.Registry.EnqueueToAll(stats2, exceptUserId: context.Player.UserId);
        context.Response.WriteRaw(stats2);
    }
}
