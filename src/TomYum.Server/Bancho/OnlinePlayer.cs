using TomYum.Bancho;

namespace TomYum.Server.Bancho;

/// <summary>
/// A logged-in player's live server-side session: the bancho session token, the
/// client-facing <see cref="BanchoPresence"/>, and the per-player outbound packet queue
/// that the next HTTP poll flushes into its response body.
/// </summary>
/// <remarks>
/// This is in-memory online state for a single web node. Cross-node fan-out (presence,
/// chat, multiplayer) over Redis is a later slice — see ADR-0003. For now the registry
/// holds everything in process.
/// </remarks>
public sealed class OnlinePlayer
{
    private readonly object _queueLock = new();
    private readonly List<byte> _outbound = [];

    /// <summary>Creates a session for <paramref name="presence"/> identified by <paramref name="token"/>.</summary>
    public OnlinePlayer(string token, BanchoPresence presence)
    {
        Token = token;
        Presence = presence;
        LastActivityUtc = DateTimeOffset.UtcNow;
    }

    /// <summary>The opaque session token returned to the client as <c>cho-token</c>.</summary>
    public string Token { get; }

    /// <summary>The player's osu! user id.</summary>
    public int UserId => Presence.UserId;

    /// <summary>The client-facing presence/stats snapshot for this player.</summary>
    public BanchoPresence Presence { get; }

    /// <summary>When this session last sent or was sent data; used to reap dead sessions.</summary>
    public DateTimeOffset LastActivityUtc { get; set; }

    /// <summary>Appends already-framed packet bytes to this player's outbound queue.</summary>
    public void Enqueue(ReadOnlySpan<byte> framedPackets)
    {
        lock (_queueLock)
        {
            _outbound.AddRange(framedPackets);
        }
    }

    /// <summary>
    /// Atomically takes everything queued so far, clearing the queue. Returns an empty
    /// array when nothing is pending. Called when flushing a poll response.
    /// </summary>
    public byte[] DequeueAll()
    {
        lock (_queueLock)
        {
            if (_outbound.Count == 0)
            {
                return [];
            }

            var bytes = _outbound.ToArray();
            _outbound.Clear();
            return bytes;
        }
    }
}
