using TomYum.Bancho;

namespace TomYum.Server.Bancho.Packets.Match;

/// <summary>
/// MATCH_LOAD_COMPLETE (52): client finished loading the map. Once every Playing slot has
/// loaded, broadcast MATCH_ALL_PLAYERS_LOADED (53) so everyone starts simultaneously.
/// </summary>
public sealed class MatchLoadCompleteHandler : IClientPacketHandler
{
    public ClientPacketId PacketId => ClientPacketId.MatchLoadComplete;

    public Task HandleAsync(ClientPacketContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var match = context.Matches.GetByUserId(context.Player.UserId);
        if (match is null) return Task.CompletedTask;

        bool allLoaded;
        lock (match.SyncRoot)
        {
            match.LoadedPlayers.Add(context.Player.UserId);
            var playing = 0;
            foreach (var s in match.Slots)
                if (s.Status == SlotStatus.Playing) playing++;
            allLoaded = match.LoadedPlayers.Count >= playing;
            if (allLoaded) match.LoadedPlayers.Clear();
        }

        if (allLoaded)
        {
            var bytes = new BanchoPacketWriter(16)
                .WritePacket(ServerPacketId.MatchAllPlayersLoaded, null)
                .ToArray();
            MatchBroadcast.ToMatch(match, context.Registry, bytes);
        }

        return Task.CompletedTask;
    }
}
