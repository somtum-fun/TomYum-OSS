using Microsoft.Extensions.DependencyInjection;
using TomYum.Bancho;
using TomYum.Domain.Repositories;
using TomYum.Plugins.Abstractions;

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
    /// <summary>The status text is cut to this (a map's "Artist - Title [Difficulty]" is well under it).</summary>
    internal const int MaxInfoLength = 512;

    /// <inheritdoc />
    public ClientPacketId PacketId => ClientPacketId.ChangeAction;

    /// <inheritdoc />
    public async Task HandleAsync(ClientPacketContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var reader = new BanchoPacketReader(context.Payload.Span);
        var presence = context.Player.Presence;

        presence.Action     = reader.ReadByte();
        presence.ActionInfo = ChatLimits.Cut(reader.ReadString(), MaxInfoLength);
        presence.BeatmapMd5 = ChatLimits.Cut(reader.ReadString(), 32);
        presence.Mods       = (Mods)reader.ReadUInt32();
        var newMode         = reader.ReadByte();
        presence.BeatmapId  = reader.ReadInt32();

        // The wire playmode stays vanilla (0-3); stats are keyed by the combined gamemode so
        // toggling Relax/Autopilot in song-select shows that variant's stats.
        presence.Mode = newMode;
        var newGameMode = presence.Mods.ToGameMode(newMode);

        // Reload stats when the combined mode changes (a mode switch OR a Relax/Autopilot toggle)
        // so the presence reflects the correct PP / accuracy / ranked score / rank.
        if (newGameMode != presence.GameMode)
        {
            presence.GameMode = newGameMode;
            var userStats = context.Services.GetRequiredService<IUserStatsRepository>();
            var stats = await userStats.GetOrCreateAsync(context.Player.UserId, newGameMode, cancellationToken).ConfigureAwait(false);
            var filler = context.Services.GetService<PresenceStats>() ?? new PresenceStats(userStats);
            await filler.FillAsync(presence, context.Player.UserId, newGameMode, stats, cancellationToken).ConfigureAwait(false);
        }

        // Encode the refreshed stats once, then fan out: everyone else gets it queued, and the
        // originator gets it in this poll's response so their own panel reflects the change.
        var writer = new BanchoPacketWriter(128);
        LoginResponseBuilder.WriteStats(writer, presence);
        var stats2 = writer.ToArray();

        context.Registry.EnqueueAbout(context.Player, stats2, exceptUserId: context.Player.UserId);
        context.Response.WriteRaw(stats2);

        // Notify plugins (DAN Mode watches this to start/stop bot-spectating). Cheap when no plugin
        // subscribes — the bus early-returns. A plugin fault is logged, never breaks the packet.
        await context.Services.GetRequiredService<IEventBus>()
            .PublishAsync(
                new ActionChangedEvent(
                    context.Player.UserId,
                    presence.Action,
                    presence.BeatmapMd5,
                    presence.BeatmapId,
                    (uint)presence.Mods,
                    (byte)presence.GameMode)
                {
                    ClientVersion = context.Player.Build.Version,
                    CanCheckFrames = !context.Player.IsIrc && context.Player.Build.HasCheckableFrames,
                },
                cancellationToken)
            .ConfigureAwait(false);
    }
}
