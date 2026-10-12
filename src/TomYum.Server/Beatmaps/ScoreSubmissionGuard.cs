using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using StackExchange.Redis;

namespace TomYum.Server.Beatmaps;

/// <summary>
/// Keeps stable score submission to one count per play: the same score sent twice (a resend, or a
/// replayed request) is taken once, and a player's plays on one map and mode are stored one at a time,
/// so two at once can't both become their best. Scores are told apart by their whole score line (it
/// has the play's checksum, and from b595b its date), remembered for <see cref="RememberedFor"/> in Redis
/// (<c>tomyum:score-seen:{hash}</c>, shared by every server on it), or in memory without Redis.
/// </summary>
public sealed class ScoreSubmissionGuard
{
    /// <summary>How long a score is remembered.</summary>
    public static readonly TimeSpan RememberedFor = TimeSpan.FromDays(1);

    private static readonly TimeSpan SweepEvery = TimeSpan.FromMinutes(10);

    private readonly IConnectionMultiplexer? _redis;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _seen = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Gate> _gates = new(StringComparer.Ordinal);
    private DateTimeOffset _nextSweep;

    public ScoreSubmissionGuard(TimeProvider time, IConnectionMultiplexer? redis = null)
    {
        ArgumentNullException.ThrowIfNull(time);
        _time = time;
        _redis = redis;
    }

    /// <summary>The score's fingerprint: a hash of its decrypted score line.</summary>
    /// <param name="scoreLine">The decrypted score line.</param>
    public static string Fingerprint(string scoreLine)
    {
        ArgumentNullException.ThrowIfNull(scoreLine);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scoreLine)));
    }

    /// <summary>What <see cref="LockAsync"/> is held on for a play: the player, the map and the mode.</summary>
    /// <param name="userId">The player.</param>
    /// <param name="beatmapMd5">The map.</param>
    /// <param name="gameMode">The combined mode (Relax and Autopilot apart).</param>
    public static string BestKey(int userId, string beatmapMd5, int gameMode) =>
        string.Create(CultureInfo.InvariantCulture, $"{userId}:{beatmapMd5}:{gameMode}");

    /// <summary>Claims a score: true the first time it's seen, false when it was already taken.</summary>
    /// <param name="fingerprint">The score's <see cref="Fingerprint"/>.</param>
    public async Task<bool> TryClaimAsync(string fingerprint)
    {
        if (_redis is not null)
        {
            try
            {
                return await _redis.GetDatabase().StringSetAsync(Key(fingerprint), 1, RememberedFor, When.NotExists).ConfigureAwait(false);
            }
            catch (RedisException)
            {
                // Redis is away: this server's memory stands in.
            }
        }

        var now = _time.GetUtcNow();
        Sweep(now);
        var until = now + RememberedFor;
        while (true)
        {
            if (_seen.TryAdd(fingerprint, until))
                return true;
            if (!_seen.TryGetValue(fingerprint, out var expires))
                continue;
            if (expires > now)
                return false;
            if (_seen.TryUpdate(fingerprint, until, expires))
                return true;
        }
    }

    /// <summary>Forgets a claimed score, so it can be sent again (it wasn't stored).</summary>
    /// <param name="fingerprint">The score's <see cref="Fingerprint"/>.</param>
    public async Task ReleaseAsync(string fingerprint)
    {
        _seen.TryRemove(fingerprint, out _);
        if (_redis is null)
            return;
        try
        {
            await _redis.GetDatabase().KeyDeleteAsync(Key(fingerprint)).ConfigureAwait(false);
        }
        catch (RedisException)
        {
            // It expires on its own.
        }
    }

    /// <summary>Waits for <paramref name="key"/> (<see cref="BestKey"/>) to be free and holds it until disposed.</summary>
    /// <param name="key">What to hold.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<IDisposable> LockAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        Gate gate;
        lock (_gates)
        {
            if (!_gates.TryGetValue(key, out gate!))
                _gates[key] = gate = new Gate();
            gate.Users++;
        }

        try
        {
            await gate.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Leave(key, gate);
            throw;
        }
        return new Holder(this, key, gate);
    }

    /// <summary>How many keys are held or waited on (for tests).</summary>
    internal int HeldKeys
    {
        get
        {
            lock (_gates)
                return _gates.Count;
        }
    }

    private void Leave(string key, Gate gate)
    {
        lock (_gates)
        {
            if (--gate.Users == 0)
            {
                _gates.Remove(key);
                gate.Semaphore.Dispose();
            }
        }
    }

    private void Sweep(DateTimeOffset now)
    {
        if (now < _nextSweep)
            return;
        _nextSweep = now + SweepEvery;
        foreach (var (fingerprint, expires) in _seen)
        {
            if (expires <= now)
                _seen.TryRemove(new KeyValuePair<string, DateTimeOffset>(fingerprint, expires));
        }
    }

    private static RedisKey Key(string fingerprint) => "tomyum:score-seen:" + fingerprint;

    private sealed class Gate
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        public int Users { get; set; }
    }

    private sealed class Holder(ScoreSubmissionGuard guard, string key, Gate gate) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            gate.Semaphore.Release();
            guard.Leave(key, gate);
        }
    }
}
