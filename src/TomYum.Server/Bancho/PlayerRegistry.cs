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
    public int Count => _byToken.Values.Count(p => !p.IsTournamentClient);

    /// <summary>
    /// A snapshot of all online players (safe to enumerate while others mutate). Tournament client
    /// windows aren't players of their own and aren't in it.
    /// </summary>
    public IReadOnlyCollection<OnlinePlayer> All => _byToken.Values.Where(p => !p.IsTournamentClient).ToArray();

    /// <summary>A snapshot of every session, tournament client windows included.</summary>
    public IReadOnlyCollection<OnlinePlayer> AllSessions => _byToken.Values.ToArray();

    /// <summary>The most tournament client windows one account has open at once (osu!'s client shows up to 16 players).</summary>
    public const int MaxTournamentClients = 16;

    private readonly Lock _tournamentGate = new();

    /// <summary>
    /// Adds a tournament client window for <paramref name="presence"/>'s account: its own token, next
    /// to (not instead of) the player's own session, which stays the one found by user id. Null when
    /// the account already has <see cref="MaxTournamentClients"/> windows open.
    /// </summary>
    public OnlinePlayer? AddTournamentClient(BanchoPresence presence)
    {
        ArgumentNullException.ThrowIfNull(presence);
        lock (_tournamentGate)
        {
            if (_byToken.Values.Count(p => p.IsTournamentClient && p.UserId == presence.UserId) >= MaxTournamentClients)
                return null;
            var token = NewToken();
            var player = new OnlinePlayer(token, presence) { IsTournamentClient = true };
            _byToken[token] = player;
            return player;
        }
    }

    /// <summary>The sessions of <paramref name="userId"/> (the game, tournament windows) spectating <paramref name="hostUserId"/>.</summary>
    public IEnumerable<OnlinePlayer> SpectatingSessions(int userId, int hostUserId) =>
        _byToken.Values.Where(p => p.UserId == userId && p.SpectatingUserId == hostUserId).ToArray();

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

    /// <summary>Looks up an online player by username (case-insensitive), or <c>null</c> when not found.</summary>
    public OnlinePlayer? GetByUsername(string username)
    {
        foreach (var p in _byToken.Values)
            if (!p.IsTournamentClient && string.Equals(p.Presence.Username, username, StringComparison.OrdinalIgnoreCase))
                return p;
        return null;
    }

    /// <summary>Removes a session (logout or reap). Idempotent.</summary>
    public void Remove(string token)
    {
        if (_byToken.TryRemove(token, out var player))
        {
            // Only the session found by user id: a tournament window leaving isn't the player leaving.
            _byUserId.TryRemove(new KeyValuePair<int, OnlinePlayer>(player.UserId, player));
        }
    }

    /// <summary>
    /// Queues already-framed packet bytes onto every online player's outbound queue,
    /// optionally skipping the player with <paramref name="exceptUserId"/> (typically the
    /// originator of a broadcast — e.g. a chat sender or a player whose own action changed).
    /// </summary>
    public void EnqueueToAll(ReadOnlySpan<byte> framedPackets, int exceptUserId = 0)
    {
        // Snapshot the values; ConcurrentDictionary enumeration is safe but we also avoid
        // holding any lock across the per-player enqueue (each player locks its own queue).
        foreach (var player in _byToken.Values)
        {
            if (player.UserId == exceptUserId)
            {
                continue;
            }

            player.Enqueue(framedPackets);
        }
    }

    /// <summary>
    /// Queues packets about <paramref name="subject"/> (presence, stats) to the players who can see
    /// them: everyone except <paramref name="exceptUserId"/>, or only the subject itself when it's
    /// restricted (restricted players are invisible to others). A status update (<paramref name="isUpdate"/>)
    /// also skips players whose filter doesn't want it (<see cref="PresenceFilter"/>); a login announcement
    /// goes to everyone, so their user list stays right.
    /// </summary>
    public void EnqueueAbout(OnlinePlayer subject, ReadOnlySpan<byte> framedPackets, int exceptUserId = 0, bool isUpdate = true)
    {
        ArgumentNullException.ThrowIfNull(subject);

        if (subject.IsRestricted)
        {
            if (subject.UserId != exceptUserId)
                subject.Enqueue(framedPackets);
            return;
        }

        foreach (var player in _byToken.Values)
        {
            if (player.UserId == exceptUserId)
                continue;
            if (isUpdate && player.UserId != subject.UserId && !Wants(player, subject.UserId))
                continue;
            player.Enqueue(framedPackets);
        }
    }

    private static bool Wants(OnlinePlayer viewer, int subjectUserId) => viewer.PresenceFilter switch
    {
        PresenceFilter.None => false,
        PresenceFilter.Friends => viewer.Friends.Contains(subjectUserId),
        _ => true,
    };

    /// <summary>Whether the player <paramref name="viewerUserId"/> may see <paramref name="subject"/> (restricted players only see themselves).</summary>
    public static bool IsVisibleTo(OnlinePlayer subject, int viewerUserId)
    {
        ArgumentNullException.ThrowIfNull(subject);
        return !subject.IsRestricted || subject.UserId == viewerUserId;
    }

    private static string NewToken() => Guid.NewGuid().ToString("N") +
        Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
}
