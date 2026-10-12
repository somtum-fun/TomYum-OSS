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
    /// <summary>
    /// The most packets one request is read for: a client sends a handful a poll. The rest of a
    /// request with more is dropped.
    /// </summary>
    internal const int MaxPacketsPerRequest = 512;

    /// <summary>
    /// The most one packet's payload may be. The largest a client sends is an old build's beatmap info
    /// request, about a megabyte at its 4,500 maps; a packet that says it's longer ends the request.
    /// </summary>
    internal const int MaxPayloadBytes = 2 * 1024 * 1024;

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
    public async Task<byte[]> DispatchAsync(OnlinePlayer player, PlayerRegistry registry, ChannelRegistry channels, MatchRegistry matches, IServiceProvider services, ReadOnlyMemory<byte> body)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(channels);
        ArgumentNullException.ThrowIfNull(matches);

        var response = new BanchoPacketWriter(256);

        // Decode one packet's header + payload, then dispatch. The ref struct reader is fully
        // consumed before any await (it never crosses the handler call), so async handlers are safe.
        var offset = 0;
        var count = 0;
        while (body.Length - offset >= PacketHeader.Size)
        {
            if (++count > MaxPacketsPerRequest)
            {
                _logger.LogWarning("User {UserId} sent more than {Max} packets at once; the rest are dropped.", player.UserId, MaxPacketsPerRequest);
                break;
            }

            // The ref struct reader cannot live in an async method, so parse the header in a
            // synchronous helper that returns plain values before any await.
            var header = ReadHeader(body.Span.Slice(offset, PacketHeader.Size));
            var id = header.Id;
            long payloadLength = header.Length;

            // As longs: a length near 4 GB must not wrap around past the bounds check.
            var payloadStart = offset + PacketHeader.Size;
            if (payloadLength > MaxPayloadBytes || payloadStart + payloadLength > body.Length)
            {
                _logger.LogWarning(
                    "Truncated or oversized packet {PacketId} from user {UserId}: declared {Length} bytes, {Remaining} remaining.",
                    id, player.UserId, payloadLength, body.Length - payloadStart);
                break;
            }

            var payload = body.Slice(payloadStart, (int)payloadLength);
            offset = payloadStart + (int)payloadLength;

            if (_handlers.TryGetValue(id, out var handler))
            {
                var context = new ClientPacketContext(player, registry, channels, matches, services, payload, response);
                try
                {
                    await handler.HandleAsync(context).ConfigureAwait(false);
                }
#pragma warning disable CA1031 // one bad packet (or a handler's bug) must not end the rest of the request
                catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
#pragma warning restore CA1031
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
