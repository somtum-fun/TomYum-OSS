using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using TomYum.Plugins.Abstractions;

namespace TomYum.Server.Anticheat;

/// <summary>
/// The in-process challenge service exposed to plugins as <see cref="IChallengeService"/>.
/// Holds pending challenges in memory with the configured TTL and verifies HMAC-SHA256
/// signatures. When a per-session key has been issued for the sessionKey (via
/// <see cref="IssueSessionKey"/>, driven by the bancho login event), the signature is checked
/// against that key; otherwise verification falls back to
/// <see cref="AnticheatOptions.HandshakeSharedSecret"/> for backward compatibility with
/// pre-rollout clients that still carry the long-lived compile-time secret.
/// Dev-mode (<see cref="AnticheatOptions.DevMode"/>) makes every <see cref="Verify"/> accept.
/// </summary>
public sealed class ChallengeService : IChallengeService, IDisposable
{
    private readonly ConcurrentDictionary<string, PendingChallenge> _pending = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PendingSessionKey> _sessionKeys = new(StringComparer.Ordinal);
    private readonly AnticheatOptions _options;
    private readonly byte[] _secret;
    private readonly ILogger<ChallengeService>? _logger;
    private readonly Timer _sweep;

    /// <summary>Creates the service over the server anti-cheat options.</summary>
    public ChallengeService(IOptions<AnticheatOptions> options, ILogger<ChallengeService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _secret = string.IsNullOrEmpty(_options.HandshakeSharedSecret)
            ? []
            : Encoding.UTF8.GetBytes(_options.HandshakeSharedSecret);
        _logger = logger;
        _sweep = new Timer(_ => Sweep(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    /// <inheritdoc/>
    public bool DevMode => _options.DevMode;

    /// <inheritdoc/>
    public TimeSpan DefaultGrace => _options.HandshakeGrace;

    /// <inheritdoc/>
    public ChallengeTicket Issue(string sessionKey, TimeSpan? grace = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionKey);
        byte[] nonce = RandomNumberGenerator.GetBytes(32);
        var deadline = DateTimeOffset.UtcNow + (grace ?? _options.HandshakeGrace);
        _pending[sessionKey] = new PendingChallenge(nonce, deadline);
        return new ChallengeTicket(sessionKey, nonce, deadline);
    }

    /// <inheritdoc/>
    public ChallengeVerdict Verify(string sessionKey, ChallengeFlagPayload payload)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionKey);
        ArgumentNullException.ThrowIfNull(payload);

        // Dev-mode: accept everything but still clean up the pending entry so Issue + Verify stay
        // paired. Plugins should keep calling Issue/Verify in dev-mode to exercise the integration.
        if (_options.DevMode)
        {
            _pending.TryRemove(sessionKey, out _);
            return new ChallengeVerdict(true, null);
        }

        if (!_pending.TryRemove(sessionKey, out var pending))
            return new ChallengeVerdict(false, "no_pending_challenge");

        if (DateTimeOffset.UtcNow > pending.DeadlineUtc)
            return new ChallengeVerdict(false, "expired");

        if (!CryptographicOperations.FixedTimeEquals(pending.Nonce, payload.Nonce.Span))
            return new ChallengeVerdict(false, "nonce_mismatch");

        // Prefer the per-session HMAC key issued at login time; this is the key material pkms
        // obtained via POST /anticheat/key so it never has to ship a long-lived compile-time
        // secret. Fall back to the global shared secret for AC builds that pre-date the
        // per-session-key rollout — logged at Warn each time so we can see when the fallback
        // can be retired.
        // TODO(perssn-key-rollout): once no live AC build still signs with
        // HandshakeSharedSecret, delete the fallback branch below AND
        // AnticheatOptions.HandshakeSharedSecret entirely.
        byte[]? sessionKeyBytes = LookupSessionKeyInternal(sessionKey);
        byte[] hmacKey;
        if (sessionKeyBytes is not null)
        {
            hmacKey = sessionKeyBytes;
        }
        else if (_secret.Length != 0)
        {
            _logger?.LogWarning(
                "no session key for {SessionKey}; falling back to shared-secret verify", sessionKey);
            hmacKey = _secret;
        }
        else
        {
            return new ChallengeVerdict(false, "no_secret_configured");
        }

        byte[] expected = ComputeHmac(hmacKey, pending.Nonce, payload.BuildId, payload.ProcessMeasurement.Span);
        return CryptographicOperations.FixedTimeEquals(expected, payload.Signature.Span)
            ? new ChallengeVerdict(true, null)
            : new ChallengeVerdict(false, "bad_signature");
    }

    /// <inheritdoc/>
    public SessionKey IssueSessionKey(string sessionKey, TimeSpan? lifetime = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionKey);
        byte[] key = RandomNumberGenerator.GetBytes(32);
        var expires = DateTimeOffset.UtcNow + (lifetime ?? TimeSpan.FromHours(24));
        _sessionKeys[sessionKey] = new PendingSessionKey(key, expires);
        return new SessionKey(sessionKey, key, expires);
    }

    /// <inheritdoc/>
    public byte[]? LookupSessionKey(string sessionKey)
    {
        if (string.IsNullOrEmpty(sessionKey))
            return null;
        return LookupSessionKeyInternal(sessionKey);
    }

    private byte[]? LookupSessionKeyInternal(string sessionKey)
    {
        if (!_sessionKeys.TryGetValue(sessionKey, out var entry))
            return null;
        if (DateTimeOffset.UtcNow > entry.ExpiresUtc)
        {
            _sessionKeys.TryRemove(sessionKey, out _);
            return null;
        }
        return entry.Key;
    }

    /// <inheritdoc/>
    public void Cancel(string sessionKey)
    {
        if (string.IsNullOrEmpty(sessionKey))
            return;
        _pending.TryRemove(sessionKey, out _);
    }

    /// <summary>Disposes the background sweep timer.</summary>
    public void Dispose() => _sweep.Dispose();

    private static byte[] ComputeHmac(byte[] key, ReadOnlySpan<byte> nonce, string buildId, ReadOnlySpan<byte> measurement)
    {
        // Spec: HMAC-SHA256( nonce || build_id (utf-8) || process_measurement, key ).
        // Keep this ordering identical on the client AC — pad-krapow-mu-sap net.c, and the lazer
        // handshake plugin — or the signature will never match.
        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, key);
        hmac.AppendData(nonce);
        hmac.AppendData(Encoding.UTF8.GetBytes(buildId));
        hmac.AppendData(measurement);
        return hmac.GetHashAndReset();
    }

    private void Sweep()
    {
        var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(1);
        foreach (var kv in _pending)
        {
            if (kv.Value.DeadlineUtc < cutoff)
                _pending.TryRemove(kv.Key, out _);
        }
        var now = DateTimeOffset.UtcNow;
        foreach (var kv in _sessionKeys)
        {
            if (kv.Value.ExpiresUtc < now)
                _sessionKeys.TryRemove(kv.Key, out _);
        }
    }

    private sealed record PendingChallenge(byte[] Nonce, DateTimeOffset DeadlineUtc);

    private sealed record PendingSessionKey(byte[] Key, DateTimeOffset ExpiresUtc);
}
