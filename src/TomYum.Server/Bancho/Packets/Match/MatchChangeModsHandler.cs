using TomYum.Bancho;

namespace TomYum.Server.Bancho.Packets.Match;

/// <summary>
/// MATCH_CHANGE_MODS (51): change mods. In free-mods mode only the player's per-slot mods
/// change; otherwise the host sets the global match mods. Payload: i32 mods.
/// </summary>
public sealed class MatchChangeModsHandler : IClientPacketHandler
{
    public ClientPacketId PacketId => ClientPacketId.MatchChangeMods;

    public Task HandleAsync(ClientPacketContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var match = context.Matches.GetByUserId(context.Player.UserId);
        if (match is null) return Task.CompletedTask;

        var reader = new BanchoPacketReader(context.Payload.Span);
        var mods   = reader.ReadUInt32();

        lock (match.SyncRoot)
        {
            if (match.FreeMods)
            {
                var idx = match.IndexOf(context.Player.UserId);
                if (idx >= 0) match.Slots[idx].Mods = mods;
            }
            else if (match.HostUserId == context.Player.UserId)
            {
                match.Mods = mods;
            }
        }

        MatchBroadcast.UpdateMatch(match, context.Registry);
        return Task.CompletedTask;
    }
}
