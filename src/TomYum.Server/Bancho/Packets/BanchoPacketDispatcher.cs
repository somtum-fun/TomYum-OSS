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
    public async Task<byte[]> DispatchAsync(OnlinePlayer player, PlayerRegistry registry, ChannelRegistry channels, ReadOnlyMemory<byte> body)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(channels);

        var response = new BanchoPacketWriter(256);

        // Decode one packet's header + payload, then dispatch. The ref struct reader is fully
        // consumed before any await (it never crosses the handler call), so async handlers are safe.
        var offset = 0;
        while (body.Length - offset >= PacketHeader.Size)
        {
            // The ref struct reader cannot live in an async method, so parse the header in a
            // synchronous helper that returns plain values before any await.
            var header = ReadHeader(body.Span.Slice(offset, PacketHeader.Size));
            var id = header.Id;
            var payloadLength = checked((int)header.Length);

            var payloadStart = offset + PacketHeader.Size;
            if (payloadStart + payloadLength > body.Length)
            {
                _logger.LogWarning(
                    "Truncated packet {PacketId} from user {UserId}: declared {Length} bytes, {Remaining} remaining.",
                    id, player.UserId, payloadLength, body.Length - payloadStart);
                break;
            }

            var payload = body.Slice(payloadStart, payloadLength);
            offset = payloadStart + payloadLength;

            if (_handlers.TryGetValue(id, out var handler))
            {
                var context = new ClientPacketContext(player, registry, channels, payload, response);
                try
                {
                    await handler.HandleAsync(context).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException)
                {
                    _logger.LogWarning(ex, "Handler for {PacketId} (user {UserId}) failed.", id, player.UserId);
                }
            }
            else
            {
                _logger.LogDebug("No handler for client packet {PacketId} (user {UserId}); skipping.", id, player.UserId);
            }
        }

        player.LastActivityUtc = DateTimeOffset.UtcNow;
        return response.Length == 0 ? [] : response.ToArray();
    }

    /// <summary>
    /// Reads a packet header from a 7-byte span. Kept synchronous and separate from
    /// <see cref="DispatchAsync"/> because the <see cref="BanchoPacketReader"/> ref struct cannot
    /// appear in an async method.
    /// </summary>
    private static PacketHeader ReadHeader(ReadOnlySpan<byte> headerBytes)
    {
        var reader = new BanchoPacketReader(headerBytes);
        return reader.ReadHeader();
    }
}
