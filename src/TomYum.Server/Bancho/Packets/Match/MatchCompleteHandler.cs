using TomYum.Bancho;

namespace TomYum.Server.Bancho.Packets.Match;

/// <summary>
/// MATCH_COMPLETE (49): client finished the map. Mark the slot Complete; when every Playing
/// slot is done, broadcast MATCH_COMPLETE (58) and reset the match to idle.
/// </summary>
public sealed class MatchCompleteHandler : IClientPacketHandler
{
    public ClientPacketId PacketId => ClientPacketId.MatchComplete;

    public Task HandleAsync(ClientPacketContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var match = context.Matches.GetByUserId(context.Player.UserId);
        if (match is null) return Task.CompletedTask;

        bool allDone;
        lock (match.SyncRoot)
        {
            var idx = match.IndexOf(context.Player.UserId);
            if (idx >= 0) match.Slots[idx].Status = SlotStatus.Complete;
            allDone = match.AllComplete();
        }

        if (allDone)
            MatchBroadcast.CompleteMatch(match, context.Registry);

        return Task.CompletedTask;
    }
}
