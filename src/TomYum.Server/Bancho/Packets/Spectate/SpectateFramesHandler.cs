using TomYum.Bancho;

namespace TomYum.Server.Bancho.Packets.Spectate;

/// <summary>
/// SPECTATE_FRAMES (18): the host client sends a replay-frame bundle. Relay it verbatim to every
/// spectator, wrapped in a SPECTATE_FRAMES (15) server packet — a byte-for-byte fan-out (no
/// re-serialization), exactly like the multiplayer score-frame fastpath.
/// </summary>
public sealed class SpectateFramesHandler : IClientPacketHandler
{
    private const string LoggedLegacyFramesKey = "tomyum.legacy-frames-logged";

    /// <inheritdoc />
    public ClientPacketId PacketId => ClientPacketId.SpectateFrames;

    /// <inheritdoc />
    public async Task HandleAsync(ClientPacketContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var host = context.Player;

        var spectators = host.Spectators;
        if (spectators.Count == 0)
            return; // no audience — the client shouldn't be sending, but be safe

        // An old client's bundle (docs/old-clients.md) gets the fields today's clients expect at its end.
        var bundle = host.Build.HasFrameSequence
            ? context.Payload.ToArray()
            : LegacyFrames.SpectateFrames(context.Payload.Span, host.Build, host.NextFrameSequence());
        if (!host.Build.HasFrameSequence && host.GetState<bool>(LoggedLegacyFramesKey) is false)
        {
            // Once per session: what an old build sends, and whether it was changed (docs/old-clients.md).
            host.SetState(LoggedLegacyFramesKey, true);
            context.Services.GetService<ILoggerFactory>()?.CreateLogger<SpectateFramesHandler>().LogInformation(
                "Spectator frames from {User} on {Version}: {Received} bytes received, {Sent} sent.",
                host.UserId, host.Build.Version, context.Payload.Length, bundle.Length);
        }

        var writer = new BanchoPacketWriter(bundle.Length + 16);
        writer.WritePacket(ServerPacketId.SpectateFrames, w => w.WriteRaw(bundle));
        var framed = writer.ToArray();

        foreach (var spectatorId in spectators)
            foreach (var session in context.Registry.SpectatingSessions(spectatorId, context.Player.UserId))
                session.Enqueue(framed);

        // Plugins watching play (e.g. pause detection) get the raw bundle. A copy: the packet buffer
        // isn't the plugin's to keep.
        if (context.Services.GetService<TomYum.Plugins.Abstractions.IEventBus>() is { } events)
            await events.PublishAsync(new TomYum.Plugins.Abstractions.SpectateFrameEvent(host.UserId, context.Payload.ToArray()), cancellationToken).ConfigureAwait(false);
    }
}
