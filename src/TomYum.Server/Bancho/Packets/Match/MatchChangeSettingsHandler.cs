using TomYum.Bancho;

namespace TomYum.Server.Bancho.Packets.Match;

/// <summary>
/// MATCH_CHANGE_SETTINGS (41): host sends a full match composite with updated settings
/// (new beatmap, name, mods, team type, etc.). Apply and broadcast.
/// </summary>
public sealed class MatchChangeSettingsHandler : IClientPacketHandler
{
    public ClientPacketId PacketId => ClientPacketId.MatchChangeSettings;

    public Task HandleAsync(ClientPacketContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var match = context.Matches.GetByUserId(context.Player.UserId);
        if (match is null || match.HostUserId != context.Player.UserId) return Task.CompletedTask;

        var reader = new BanchoPacketReader(context.Payload.Span);
        lock (match.SyncRoot)
        {
            // Preserve player slot membership; only update settings fields.
            var prevSlotStatuses = new SlotStatus[16];
            var prevSlotUsers    = new int[16];
            var prevSlotTeams    = new byte[16];
            for (var i = 0; i < 16; i++)
            {
                prevSlotStatuses[i] = match.Slots[i].Status;
                prevSlotUsers[i]    = match.Slots[i].UserId;
                prevSlotTeams[i]    = match.Slots[i].Team;
            }

            MatchEncoder.ReadMatchComposite(reader, match);

            // Restore player occupancy — the client sends its own view of the slots which may
            // differ from the server's (e.g. the host doesn't know about concurrent changes).
            for (var i = 0; i < 16; i++)
            {
                if (prevSlotUsers[i] != 0)
                {
                    match.Slots[i].UserId = prevSlotUsers[i];
                    match.Slots[i].Team   = prevSlotTeams[i];
                    // Only restore status if the slot still has the player.
                    if (!match.Slots[i].HasPlayer)
                        match.Slots[i].Status = prevSlotStatuses[i];
                }
            }
        }

        MatchBroadcast.UpdateMatch(match, context.Registry);
        return Task.CompletedTask;
    }
}
