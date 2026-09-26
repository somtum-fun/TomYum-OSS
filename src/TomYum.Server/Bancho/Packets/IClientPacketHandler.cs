using TomYum.Bancho;

namespace TomYum.Server.Bancho.Packets;

/// <summary>
/// Handles a single client→server bancho packet type. One implementation per
/// <see cref="ClientPacketId"/>, discovered via DI and indexed by <see cref="PacketId"/>
/// in <see cref="BanchoPacketDispatcher"/> (mirrors the bancho.py <c>@register</c> /
/// Sunrise <c>[PacketHandler]</c> pattern, but as explicit DI registrations).
/// </summary>
public interface IClientPacketHandler
{
    /// <summary>The client packet id this handler is responsible for.</summary>
    ClientPacketId PacketId { get; }

    /// <summary>
    /// Handles one packet. Reads fields from <see cref="ClientPacketContext.Payload"/>,
    /// mutates online state, and queues any resulting packets (to the originator via
    /// <see cref="ClientPacketContext.Response"/>, to others via the registry). Async so a
    /// handler may await a repository or other I/O (e.g. an admin command persisting a change).
    /// </summary>
    Task HandleAsync(ClientPacketContext context, CancellationToken cancellationToken = default);
}
