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

        Send(match, context.Player, target);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Invites <paramref name="target"/> to <paramref name="match"/> from <paramref name="from"/> (also
    /// BanchoBot's <c>!mp invite</c>). Old builds (docs/old-clients.md) take an invite only as
    /// MATCH_INVITE (88), with the inviter as the target, as osuTitanic and bancho.py send it; today's
    /// client gets the same text as a plain message, whose link it shows.
    /// </summary>
    public static void Send(TomYum.Server.Bancho.Match match, OnlinePlayer from, OnlinePlayer target)
    {
        ArgumentNullException.ThrowIfNull(match);
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(target);

        var inviteText = $"Come join my multiplayer game: [osump://{match.Id}/{Uri.EscapeDataString(match.Password)} {match.Name}]";
        var sender     = from.Presence.Username;
        var message    = new BanchoMessage(sender, inviteText, target.Presence.Username, from.UserId);

        var w = new BanchoPacketWriter(256);
        if (target.Build.IsLegacy)
        {
            w.WritePacket(ServerPacketId.MatchInvite, pw =>
            {
                pw.WriteString(message.Sender);
                pw.WriteString(message.Text);
                pw.WriteString(message.Sender);
                pw.WriteInt32(message.SenderId);
            });
        }
        else
        {
            ChatEncoder.WriteMessage(w, message);
        }
        target.Enqueue(w.ToArray());
    }
}
