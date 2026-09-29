using TomYum.Bancho;
using TomYum.Domain.Repositories;

namespace TomYum.Server.Bancho.Packets;

/// <summary>FRIEND_ADD (id 73): the player adds a friend (payload: i32 user id).</summary>
public sealed class FriendAddHandler : IClientPacketHandler
{
    /// <inheritdoc />
    public ClientPacketId PacketId => ClientPacketId.FriendAdd;

    /// <inheritdoc />
    public async Task HandleAsync(ClientPacketContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var friendId = FriendPackets.ReadUserId(context.Payload.Span);
        if (friendId <= 0 || friendId == context.Player.UserId || context.Player.Friends.Contains(friendId))
            return;
        if (await context.Services.GetRequiredService<IUserRepository>().GetByIdAsync(friendId, cancellationToken).ConfigureAwait(false) is null)
            return;

        await context.Services.GetRequiredService<IFriendRepository>().AddAsync(context.Player.UserId, friendId, cancellationToken).ConfigureAwait(false);
        context.Player.Friends = context.Player.Friends.Add(friendId);
    }
}

/// <summary>FRIEND_REMOVE (id 74): the player removes a friend (payload: i32 user id).</summary>
public sealed class FriendRemoveHandler : IClientPacketHandler
{
    /// <inheritdoc />
    public ClientPacketId PacketId => ClientPacketId.FriendRemove;

    /// <inheritdoc />
    public async Task HandleAsync(ClientPacketContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var friendId = FriendPackets.ReadUserId(context.Payload.Span);
        if (friendId <= 0)
            return;

        await context.Services.GetRequiredService<IFriendRepository>().RemoveAsync(context.Player.UserId, friendId, cancellationToken).ConfigureAwait(false);
        context.Player.Friends = context.Player.Friends.Remove(friendId);
    }
}

/// <summary>SET_AWAY_MESSAGE (id 82): the client's /away (payload: a message; empty text clears it).</summary>
public sealed class SetAwayMessageHandler : IClientPacketHandler
{
    /// <inheritdoc />
    public ClientPacketId PacketId => ClientPacketId.SetAwayMessage;

    /// <inheritdoc />
    public Task HandleAsync(ClientPacketContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var text = FriendPackets.ReadMessageText(context.Payload.Span).Trim();
        context.Player.AwayMessage = text.Length == 0 ? null : text;
        return Task.CompletedTask;
    }
}

/// <summary>TOGGLE_BLOCK_NON_FRIEND_DMS (id 99): whether only friends may PM the player (payload: i32, 1 = block).</summary>
public sealed class ToggleBlockNonFriendDmsHandler : IClientPacketHandler
{
    /// <inheritdoc />
    public ClientPacketId PacketId => ClientPacketId.ToggleBlockNonFriendDms;

    /// <inheritdoc />
    public Task HandleAsync(ClientPacketContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Player.BlocksNonFriendDms = FriendPackets.ReadUserId(context.Payload.Span) != 0;
        return Task.CompletedTask;
    }
}

/// <summary>RECEIVE_UPDATES (id 79): whose status updates the client wants (payload: i32, 0 none / 1 all / 2 friends).</summary>
public sealed class ReceiveUpdatesHandler : IClientPacketHandler
{
    /// <inheritdoc />
    public ClientPacketId PacketId => ClientPacketId.ReceiveUpdates;

    /// <inheritdoc />
    public Task HandleAsync(ClientPacketContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var filter = (PresenceFilter)FriendPackets.ReadUserId(context.Payload.Span);
        context.Player.PresenceFilter = Enum.IsDefined(filter) ? filter : PresenceFilter.All;
        return Task.CompletedTask;
    }
}

/// <summary>Payload readers (sync: <see cref="BanchoPacketReader"/> is a ref struct).</summary>
internal static class FriendPackets
{
    public static int ReadUserId(ReadOnlySpan<byte> payload) =>
        payload.Length >= 4 ? new BanchoPacketReader(payload).ReadInt32() : 0;

    public static string ReadMessageText(ReadOnlySpan<byte> payload)
    {
        var reader = new BanchoPacketReader(payload);
        _ = reader.ReadString(); // sender
        return reader.ReadString();
    }
}
