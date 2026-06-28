using System.Collections.Concurrent;
using System.Security.Cryptography;
using TomYum.Bancho;

namespace TomYum.Server.Bancho;

/// <summary>
/// In-memory registry of currently online players, indexed by both session token and
/// user id. Thread-safe for concurrent poll requests. This is the single-node online
/// state store; a Redis-backed cross-node registry replaces/augments it in a later
/// slice (ADR-0003).
/// </summary>
public sealed class PlayerRegistry
{
    private readonly ConcurrentDictionary<string, OnlinePlayer> _byToken = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<int, OnlinePlayer> _byUserId = new();

    /// <summary>Number of players currently online on this node.</summary>
    public int Count => _byToken.Count;

    /// <summary>A snapshot of all online players (safe to enumerate while others mutate).</summary>
    public IReadOnlyCollection<OnlinePlayer> All => _byToken.Values.ToArray();

    /// <summary>
    /// Registers <paramref name="presence"/> as a new online session, minting a fresh
    /// opaque token. If the user is already online elsewhere, the prior session is evicted
    /// first (one active bancho session per user, mirroring stable behaviour).
    /// </summary>
    public OnlinePlayer Add(BanchoPresence presence)
    {
        ArgumentNullException.ThrowIfNull(presence);

        if (_byUserId.TryRemove(presence.UserId, out var existing))
        {
            _byToken.TryRemove(existing.Token, out _);
        }

        var token = NewToken();
        var player = new OnlinePlayer(token, presence);
        _byToken[token] = player;
        _byUserId[presence.UserId] = player;
        return player;
    }

    /// <summary>Looks up an online player by session token, or <c>null</c> when not found.</summary>
    public OnlinePlayer? GetByToken(string token) =>
        _byToken.TryGetValue(token, out var player) ? player : null;

    /// <summary>Looks up an online player by user id, or <c>null</c> when not found.</summary>
    public OnlinePlayer? GetByUserId(int userId) =>
        _byUserId.TryGetValue(userId, out var player) ? player : null;

    /// <summary>Removes a session (logout or reap). Idempotent.</summary>
    public void Remove(string token)
    {
        if (_byToken.TryRemove(token, out var player))
        {
            _byUserId.TryRemove(player.UserId, out _);
        }
    }

    private static string NewToken() => Guid.NewGuid().ToString("N") +
        Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
}
