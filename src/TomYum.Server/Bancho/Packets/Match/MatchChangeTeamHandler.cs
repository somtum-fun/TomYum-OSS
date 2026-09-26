using TomYum.Bancho;

namespace TomYum.Server.Bancho.Packets.Match;

/// <summary>MATCH_CHANGE_TEAM (77): toggle player's team (Blue ↔ Red). No payload.</summary>
public sealed class MatchChangeTeamHandler : IClientPacketHandler
{
    public ClientPacketId PacketId => ClientPacketId.MatchChangeTeam;

    public Task HandleAsync(ClientPacketContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var match = context.Matches.GetByUserId(context.Player.UserId);
        if (match is null) return Task.CompletedTask;

        lock (match.SyncRoot)
        {
            var idx = match.IndexOf(context.Player.UserId);
            if (idx >= 0)
                match.Slots[idx].Team = match.Slots[idx].Team == 1 ? (byte)2 : (byte)1;
        }

        MatchBroadcast.UpdateMatch(match, context.Registry);
        return Task.CompletedTask;
    }
}
