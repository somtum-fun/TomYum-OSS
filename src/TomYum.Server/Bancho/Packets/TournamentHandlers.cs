using Microsoft.Extensions.DependencyInjection;
using TomYum.Bancho;
using TomYum.Persistence.OsuWeb;
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
/// state from then on (payload: i32 match id). Only tournament client windows may, and only for
/// someone allowed to (<see cref="TournamentWatching.MayFollowAsync"/>): a tournament client is
/// whatever says so in its version string, so this alone would let anyone read any room's chat.
/// </summary>
public sealed class TournamentJoinMatchChannelHandler : IClientPacketHandler
{
    /// <inheritdoc />
    public ClientPacketId PacketId => ClientPacketId.TournamentJoinMatchChannel;

    /// <inheritdoc />
    public async Task HandleAsync(ClientPacketContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!context.Player.IsTournamentClient || TournamentWatching.Find(context) is not { } match
            || !await TournamentWatching.MayFollowAsync(context, match, cancellationToken).ConfigureAwait(false))
            return;

        TournamentWatching.Stop(context.Player, context.Matches);
        lock (match.SyncRoot)
            match.TournamentWatchers.Add(context.Player);
        context.Player.WatchingMatchId = match.Id;
        match.Channel.Join(context.Player);
        context.Player.JoinChannel("#multiplayer");
        ChatEncoder.WriteChannelJoinSuccess(context.Response, "#multiplayer");
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
    /// <summary>The website group that runs tournaments (Tournament Committee): may follow any match.</summary>
    public const string TournamentGroup = "tc";

    /// <summary>
    /// Whether the player may follow <paramref name="match"/> from a tournament client: staff, the
    /// Tournament Committee, or someone in the room (its host or a player). Never a restricted account.
    /// </summary>
    /// <param name="context">The packet's context (the player, and the services to look up their groups).</param>
    /// <param name="match">The match to follow.</param>
    /// <param name="cancellationToken">Cancels the group lookup.</param>
    public static async Task<bool> MayFollowAsync(ClientPacketContext context, Bancho.Match match, CancellationToken cancellationToken = default)
    {
        var player = context.Player;
        if (player.IsRestricted)
            return false;
        if ((player.Presence.Privileges & (BanchoPrivileges.Moderator | BanchoPrivileges.Developer | BanchoPrivileges.Owner)) != 0)
            return true;
        lock (match.SyncRoot)
        {
            if (match.HostUserId == player.UserId || MatchBroadcast.IsInMatch(player.UserId, match))
                return true;
        }
        await using var scope = context.Services.CreateAsyncScope();
        return scope.ServiceProvider.GetService<OsuWebCommunity>() is { } community
            && (await community.GetGroupsAsync(player.UserId, cancellationToken).ConfigureAwait(false)).Contains(TournamentGroup);
    }

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
