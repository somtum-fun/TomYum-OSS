using TomYum.Bancho;
using TomYum.Server.Bancho.Packets.Match;

namespace TomYum.Server.Bancho.Packets;

/// <summary>TOURNAMENT_MATCH_INFO_REQUEST (id 93): a tournament client asks for a match's state (payload: i32 match id).</summary>
public sealed class TournamentMatchInfoHandler : IClientPacketHandler
{
    /// <inheritdoc />
    public ClientPacketId PacketId => ClientPacketId.TournamentMatchInfoRequest;

    /// <inheritdoc />
    public Task HandleAsync(ClientPacketContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (TournamentWatching.Find(context) is { } match)
            context.Response.WriteRaw(MatchBroadcast.EncodeMatch(ServerPacketId.UpdateMatch, match, sendPassword: false));
        return Task.CompletedTask;
    }
}

/// <summary>
/// TOURNAMENT_JOIN_MATCH_CHANNEL (id 108): a tournament client follows a match: its chat, and its
/// state from then on, password included (payload: i32 match id). Only tournament client windows may:
/// from the game it would let anyone read any room's chat and password.
/// </summary>
public sealed class TournamentJoinMatchChannelHandler : IClientPacketHandler
{
    /// <inheritdoc />
    public ClientPacketId PacketId => ClientPacketId.TournamentJoinMatchChannel;

    /// <inheritdoc />
    public Task HandleAsync(ClientPacketContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!context.Player.IsTournamentClient || TournamentWatching.Find(context) is not { } match)
            return Task.CompletedTask;

        TournamentWatching.Stop(context.Player, context.Matches);
        lock (match.SyncRoot)
            match.TournamentWatchers.Add(context.Player);
        context.Player.WatchingMatchId = match.Id;
        match.Channel.Join(context.Player);
        context.Player.JoinChannel("#multiplayer");
        ChatEncoder.WriteChannelJoinSuccess(context.Response, "#multiplayer");
        return Task.CompletedTask;
    }
}

/// <summary>TOURNAMENT_LEAVE_MATCH_CHANNEL (id 109): a tournament client stops following a match (payload: i32 match id).</summary>
public sealed class TournamentLeaveMatchChannelHandler : IClientPacketHandler
{
    /// <inheritdoc />
    public ClientPacketId PacketId => ClientPacketId.TournamentLeaveMatchChannel;

    /// <inheritdoc />
    public Task HandleAsync(ClientPacketContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (TournamentWatching.Stop(context.Player, context.Matches))
            ChatEncoder.WriteChannelKick(context.Response, "#multiplayer");
        return Task.CompletedTask;
    }
}

/// <summary>Following a match from a tournament client.</summary>
internal static class TournamentWatching
{
    /// <summary>The match in the packet (i32 id), or null.</summary>
    public static Bancho.Match? Find(ClientPacketContext context)
    {
        var payload = context.Payload.Span;
        if (payload.Length < 4)
            return null;
        var id = new BanchoPacketReader(payload).ReadInt32();
        return id is > 0 and <= ushort.MaxValue ? context.Matches.Get((ushort)id) : null;
    }

    /// <summary>Stops following the watched match (its state and chat); false when it followed none.</summary>
    public static bool Stop(OnlinePlayer player, MatchRegistry matches)
    {
        if (player.WatchingMatchId is not { } id)
            return false;
        player.WatchingMatchId = null;
        player.LeaveChannel("#multiplayer");
        if (matches.Get(id) is { } match)
        {
            lock (match.SyncRoot)
                match.TournamentWatchers.Remove(player);
            // The chat knows members by account: keep the account's place if it plays in the match.
            if (!MatchBroadcast.IsInMatch(player.UserId, match))
                match.Channel.Leave(player.UserId);
        }
        return true;
    }
}
