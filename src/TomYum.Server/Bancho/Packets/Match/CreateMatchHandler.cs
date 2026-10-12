using TomYum.Bancho;

namespace TomYum.Server.Bancho.Packets.Match;

/// <summary>
/// CREATE_MATCH (31): host creates a new room. Parse the match composite, create it in the
/// registry, put the host in slot 0, and confirm with MATCH_JOIN_SUCCESS (36). Notify every
/// lobby viewer via NEW_MATCH (27).
/// </summary>
public sealed class CreateMatchHandler : IClientPacketHandler
{
    public ClientPacketId PacketId => ClientPacketId.CreateMatch;

    public Task HandleAsync(ClientPacketContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Player.IsRestricted)
        {
            ModerationPackets.WriteNoMultiplayer(context.Response);
            return Task.CompletedTask;
        }

        MatchCreateArgs args;
        try { args = MatchEncoder.ReadCreateArgs(context.Payload.Span); }
        catch (InvalidDataException) { return Task.CompletedTask; }

        if (!context.Player.RoomRate.TryTake(DateTimeOffset.UtcNow))
        {
            context.Response.WriteStringPacket(ServerPacketId.Notification, "You're making rooms a little too fast. Wait a minute, then try again.");
            context.Response.WritePacket(ServerPacketId.MatchJoinFail, null);
            return Task.CompletedTask;
        }

        // One room at a time: the one the player is in is left first.
        if (context.Matches.GetByUserId(context.Player.UserId) is { } current)
            MatchBroadcast.RemovePlayer(current, context.Player, context.Registry, context.Matches);

        var match = context.Matches.Create(
            context.Player.UserId, args.Name, args.Password,
            args.MapName, args.MapId, args.MapMd5,
            args.Mode, args.WinCondition, args.TeamType,
            args.FreeMods, args.Mods, args.Seed);

        lock (match.SyncRoot)
        {
            match.Slots[0].Join(context.Player.UserId);
        }

        match.Channel.Join(context.Player);
        context.Player.JoinChannel("#multiplayer");
        match.History?.Created(match);

        // Tell the creating player they've joined successfully.
        MatchEncoder.WriteMatchPacket(context.Response, ServerPacketId.MatchJoinSuccess, match, sendPassword: true);

        // The client also expects a CHANNEL_JOIN_SUCCESS for the match-specific chat.
        context.Response.WriteStringPacket(ServerPacketId.ChannelJoinSuccess, "#multiplayer");

        // Announce to everyone in the lobby.
        var lobbyPacket = MatchBroadcast.EncodeMatch(ServerPacketId.NewMatch, match, sendPassword: false);
        MatchBroadcast.ToLobby(context.Registry, lobbyPacket);

        return Task.CompletedTask;
    }
}
