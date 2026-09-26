using TomYum.Bancho;

namespace TomYum.Server.Bancho.Packets.Spectate;

/// <summary>
/// SPECTATE_FRAMES (18): the host client sends a replay-frame bundle. Relay it verbatim to every
/// spectator, wrapped in a SPECTATE_FRAMES (15) server packet — a byte-for-byte fan-out (no
/// re-serialization), exactly like the multiplayer score-frame fastpath.
/// </summary>
public sealed class SpectateFramesHandler : IClientPacketHandler
{
    /// <inheritdoc />
    public ClientPacketId PacketId => ClientPacketId.SpectateFrames;

    /// <inheritdoc />
    public Task HandleAsync(ClientPacketContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var host = context.Player;

        var spectators = host.Spectators;
        if (spectators.Count == 0)
            return Task.CompletedTask; // no audience — the client shouldn't be sending, but be safe

        var writer = new BanchoPacketWriter(context.Payload.Length + 16);
        writer.WritePacket(ServerPacketId.SpectateFrames, w => w.WriteRaw(context.Payload.Span));
        var framed = writer.ToArray();

        foreach (var spectatorId in spectators)
            context.Registry.GetByUserId(spectatorId)?.Enqueue(framed);

        // TODO(Phase 4 — DAN): publish SpectateFrameEvent(host.UserId, context.Payload) on the plugin
        // event bus here so the DAN plugin can watch for pause frames. Deferred until its consumer exists.
        return Task.CompletedTask;
    }
}
