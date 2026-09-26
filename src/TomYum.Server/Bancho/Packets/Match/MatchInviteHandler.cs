using TomYum.Bancho;

namespace TomYum.Server.Bancho.Packets.Match;

/// <summary>
/// MATCH_INVITE (87): invite another online player to the current match. Payload: i32 target_user_id.
/// Delivers MATCH_INVITE (88) as a message composite so the target sees an invite link.
/// </summary>
public sealed class MatchInviteHandler : IClientPacketHandler
{
    public ClientPacketId PacketId => ClientPacketId.MatchInvite;

    public Task HandleAsync(ClientPacketContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var match = context.Matches.GetByUserId(context.Player.UserId);
        if (match is null) return Task.CompletedTask;

        var reader   = new BanchoPacketReader(context.Payload.Span);
        var targetId = reader.ReadInt32();
        var target   = context.Registry.GetByUserId(targetId);
        if (target is null) return Task.CompletedTask;

        // The invite is a SEND_MESSAGE (7) from the inviting player whose text is the match URL.
        var inviteText = $"Come join my multiplayer game: [osump://{match.Id}/{Uri.EscapeDataString(match.Password)} {match.Name}]";
        var sender     = context.Player.Presence.Username;
        var message    = new BanchoMessage(sender, inviteText, target.Presence.Username, context.Player.UserId);

        var w = new BanchoPacketWriter(256);
        ChatEncoder.WriteMessage(w, message);
        target.Enqueue(w.ToArray());

        return Task.CompletedTask;
    }
}
