using System.Collections.Concurrent;

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
    private readonly HashSet<string> _channels = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _channelsLock = new();
    private readonly HashSet<int> _spectators = new();
    private readonly object _spectatorsLock = new();
    private readonly ConcurrentDictionary<string, object?> _state = new(StringComparer.Ordinal);

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

    /// <summary>Whether this player currently has the multiplayer lobby open.</summary>
    public bool InLobby { get; set; }

    /// <summary>
    /// The user id of the host this player is currently spectating, or <c>null</c> when not
    /// spectating anyone. Set by <c>START_SPECTATING</c>, cleared by <c>STOP_SPECTATING</c>/logout.
    /// </summary>
    public int? SpectatingUserId { get; set; }

    /// <summary>Appends already-framed packet bytes to this player's outbound queue.</summary>
    public void Enqueue(ReadOnlySpan<byte> framedPackets)
    {
        lock (_queueLock)
        {
            _outbound.AddRange(framedPackets);
        }
    }

    /// <summary>
    /// Bytes currently buffered in this player's outbound queue. A steadily growing value for a
    /// player means the client has stopped polling but the session has not yet been reaped — the
    /// main per-session memory-bloat signal (surfaced by the <c>!stats</c> command).
    /// </summary>
    public int QueuedBytes
    {
        get
        {
            lock (_queueLock)
            {
                return _outbound.Count;
            }
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

    /// <summary>A snapshot of the channel names this player has joined.</summary>
    public IReadOnlyCollection<string> Channels
    {
        get
        {
            lock (_channelsLock)
            {
                return _channels.ToArray();
            }
        }
    }

    /// <summary>Records that this player joined <paramref name="channelName"/>. Idempotent.</summary>
    public void JoinChannel(string channelName)
    {
        lock (_channelsLock)
        {
            _channels.Add(channelName);
        }
    }

    /// <summary>Records that this player left <paramref name="channelName"/>. Idempotent.</summary>
    public void LeaveChannel(string channelName)
    {
        lock (_channelsLock)
        {
            _channels.Remove(channelName);
        }
    }

    /// <summary>Whether this player has joined <paramref name="channelName"/>.</summary>
    public bool IsInChannel(string channelName)
    {
        lock (_channelsLock)
        {
            return _channels.Contains(channelName);
        }
    }

    /// <summary>
    /// A snapshot of the user ids currently spectating this player (i.e. this player is their host).
    /// </summary>
    public IReadOnlyCollection<int> Spectators
    {
        get
        {
            lock (_spectatorsLock)
            {
                return _spectators.ToArray();
            }
        }
    }

    /// <summary>How many players are currently spectating this player.</summary>
    public int SpectatorCount
    {
        get
        {
            lock (_spectatorsLock)
            {
                return _spectators.Count;
            }
        }
    }

    /// <summary>Records that <paramref name="userId"/> started spectating this player. Returns <c>false</c> if already tracked.</summary>
    public bool AddSpectator(int userId)
    {
        lock (_spectatorsLock)
        {
            return _spectators.Add(userId);
        }
    }

    /// <summary>Records that <paramref name="userId"/> stopped spectating this player. Returns <c>false</c> if it wasn't tracked.</summary>
    public bool RemoveSpectator(int userId)
    {
        lock (_spectatorsLock)
        {
            return _spectators.Remove(userId);
        }
    }

    /// <summary>
    /// This player's ephemeral <c>#spectator</c> chat channel — the shared tab its spectators (and the
    /// host itself) chat in while being watched. The client always sees it as <c>#spectator</c>, but
    /// each host owns its own instance, exactly like <see cref="Match"/> owns <c>#multiplayer</c>; the
    /// host joins it when the first spectator arrives and leaves when the last one goes, so it never
    /// appears in the login channel list (which advertises only the shared <see cref="ChannelRegistry"/>).
    /// </summary>
    public ChatChannel SpectatorChannel { get; } = new("#spectator", "Spectator chat", autoJoin: false);

    /// <summary>
    /// Transient per-session state bag for plugins (e.g. DAN Mode stashing per-login flags such as
    /// <c>dan.watched</c>/<c>dan.tainted</c>). Keys are plugin-namespaced by convention. The whole
    /// bag is dropped when the session ends, so plugins never have to clean up on logout.
    /// </summary>
    public void SetState(string key, object? value) => _state[key] = value;

    /// <summary>Reads a state value as <typeparamref name="T"/>, or <c>default</c> when absent/mistyped.</summary>
    public T? GetState<T>(string key) => _state.TryGetValue(key, out var value) && value is T typed ? typed : default;

    /// <summary>Removes a state value. Returns <c>true</c> if it was present.</summary>
    public bool RemoveState(string key) => _state.TryRemove(key, out _);

    /// <summary>Clears all plugin state for this session (called when the session ends).</summary>
    public void ClearState() => _state.Clear();
}
