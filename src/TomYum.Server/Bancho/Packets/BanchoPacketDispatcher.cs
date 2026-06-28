using System.Collections.Frozen;
using TomYum.Bancho;

namespace TomYum.Server.Bancho.Packets;

/// <summary>
/// Decodes the inbound osu!stable packet stream on an authenticated <c>POST /</c> poll and
/// routes each packet to its <see cref="IClientPacketHandler"/>. One request body may carry
/// many packets back-to-back; the dispatcher loops until the stream is exhausted, skipping
/// any packet whose id has no registered handler (forward-compatible with packets we do not
/// implement yet).
/// </summary>
/// <remarks>
/// Handlers are resolved once into a <see cref="FrozenDictionary{TKey,TValue}"/> keyed by
/// <see cref="ClientPacketId"/> for allocation-free lookup on the hot path. The dispatcher is
/// a singleton; per-poll state lives in the <see cref="ClientPacketContext"/> it builds.
/// </remarks>
public sealed class BanchoPacketDispatcher
{
    private readonly FrozenDictionary<ClientPacketId, IClientPacketHandler> _handlers;
    private readonly ILogger<BanchoPacketDispatcher> _logger;

    /// <summary>Builds the dispatcher from every registered <see cref="IClientPacketHandler"/>.</summary>
    public BanchoPacketDispatcher(
        IEnumerable<IClientPacketHandler> handlers,
        ILogger<BanchoPacketDispatcher> logger)
    {
        ArgumentNullException.ThrowIfNull(handlers);
        _handlers = handlers.ToFrozenDictionary(h => h.PacketId);
        _logger = logger;
    }

    /// <summary>
    /// Decodes and dispatches every packet in <paramref name="body"/> on behalf of
    /// <paramref name="player"/>, returning the framed bytes any handler queued for the
    /// originator. Packets for other players are queued on them via the registry.
    /// </summary>
    public byte[] Dispatch(OnlinePlayer player, PlayerRegistry registry, ReadOnlyMemory<byte> body)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(registry);

        var response = new BanchoPacketWriter(256);

        // Read header + payload per packet. A ref struct reader cannot cross the handler call
        // (handlers take ReadOnlyMemory), so slice the payload here and advance manually.
        var offset = 0;
        var span = body.Span;
        while (span.Length - offset >= PacketHeader.Size)
        {
            var reader = new BanchoPacketReader(span[offset..]);
            var header = reader.ReadHeader();
            var payloadLength = checked((int)header.Length);
            var payloadStart = offset + PacketHeader.Size;

            if (payloadStart + payloadLength > span.Length)
            {
                _logger.LogWarning(
                    "Truncated packet {PacketId} from user {UserId}: declared {Length} bytes, {Remaining} remaining.",
                    header.Id, player.UserId, payloadLength, span.Length - payloadStart);
                break;
            }

            var payload = body.Slice(payloadStart, payloadLength);
            offset = payloadStart + payloadLength;

            if (_handlers.TryGetValue(header.Id, out var handler))
            {
                var context = new ClientPacketContext(player, registry, payload, response);
                try
                {
                    handler.Handle(context);
                }
                catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException)
                {
                    _logger.LogWarning(ex, "Handler for {PacketId} (user {UserId}) failed.", header.Id, player.UserId);
                }
            }
            else
            {
                _logger.LogDebug("No handler for client packet {PacketId} (user {UserId}); skipping.", header.Id, player.UserId);
            }
        }

        player.LastActivityUtc = DateTimeOffset.UtcNow;
        return response.Length == 0 ? [] : response.ToArray();
    }
}
