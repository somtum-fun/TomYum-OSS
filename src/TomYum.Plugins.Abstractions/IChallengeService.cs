namespace TomYum.Plugins.Abstractions;

/// <summary>
/// The server-side anti-cheat handshake surface. Plugins that gate a session on client-produced
/// proof-of-anti-cheat use this service via <see cref="IPluginContext.GetHostService{TService}"/>:
/// they ask the host to issue a nonce (<see cref="Issue"/>), the client's AC returns a signed
/// payload, the plugin hands it back via <see cref="Verify"/>. The crypto (HMAC-SHA256 over the
/// shared secret, constant-time comparison, pending-challenge TTL) lives in the host; plugins
/// never see the secret.
/// </summary>
/// <remarks>
/// <see cref="DevMode"/> is set from server config (<c>Anticheat:DevMode</c>) and makes
/// <see cref="Verify"/> accept every payload without checking the signature, so a development
/// client can connect without running the real AC. Plugins should still exercise the
/// <see cref="Issue"/>/<see cref="Verify"/> code path in dev-mode so the integration itself
/// is tested; use <see cref="DevMode"/> only to skip steps that would be impossible without a
/// running AC (e.g. closing the connection on verify-fail).
/// </remarks>
public interface IChallengeService
{
    /// <summary>When true, every <see cref="Verify"/> accepts without checking the signature.</summary>
    bool DevMode { get; }

    /// <summary>How long an issued challenge is valid for, from server config (default 10s).</summary>
    TimeSpan DefaultGrace { get; }

    /// <summary>Issues a fresh nonce for <paramref name="sessionKey"/> with the configured grace window.</summary>
    /// <param name="sessionKey">Opaque per-session key the plugin picks (bancho token, login attempt id, …).</param>
    /// <param name="grace">Overrides the default grace for this ticket. Null uses <see cref="DefaultGrace"/>.</param>
    /// <returns>The ticket to hand to the client as the challenge.</returns>
    ChallengeTicket Issue(string sessionKey, TimeSpan? grace = null);

    /// <summary>Verifies a client's signed flag against the pending challenge for <paramref name="sessionKey"/>.</summary>
    /// <param name="sessionKey">The session key passed to <see cref="Issue"/>.</param>
    /// <param name="payload">The payload the client's AC returned.</param>
    /// <returns><see cref="ChallengeVerdict.Ok"/> on success; otherwise a short reason suitable for staff logs.</returns>
    ChallengeVerdict Verify(string sessionKey, ChallengeFlagPayload payload);

    /// <summary>Drops any pending challenge for <paramref name="sessionKey"/> (session ended, plugin gave up).</summary>
    void Cancel(string sessionKey);

    /// <summary>Issues (and overwrites any existing) a per-session HMAC key for <paramref name="sessionKey"/>.
    /// Lifetime defaults to 24 hours; the key is used by <see cref="Verify"/> instead of the global
    /// shared secret when present. This is the per-session-key path that replaces the long-lived
    /// compile-time secret in the client AC — the plugin issues one from the bancho login event so
    /// the AC can fetch it (via <c>POST /anticheat/key</c>) and HMAC every subsequent flag with it.</summary>
    /// <param name="sessionKey">Opaque identifier the AC can later lookup (e.g. <c>stable:&lt;hwid-md5s&gt;</c>).</param>
    /// <param name="lifetime">Overrides the 24h default lifetime.</param>
    SessionKey IssueSessionKey(string sessionKey, TimeSpan? lifetime = null);

    /// <summary>Returns the live session key bytes for <paramref name="sessionKey"/> or null if none / expired.</summary>
    byte[]? LookupSessionKey(string sessionKey);
}

/// <summary>A per-session HMAC key issued for a client-chosen session identifier.</summary>
/// <param name="ClientId">Echoes the key passed to <see cref="IChallengeService.IssueSessionKey"/>.</param>
/// <param name="Key">32 random bytes used as the HMAC-SHA256 key for subsequent <c>/anticheat/flag</c> verifies.</param>
/// <param name="ExpiresUtc">UTC moment after which the key is no longer valid.</param>
public sealed record SessionKey(string ClientId, ReadOnlyMemory<byte> Key, DateTimeOffset ExpiresUtc);

/// <summary>A fresh anti-cheat challenge the server has issued for a session.</summary>
/// <param name="SessionKey">Echoes the key passed to <see cref="IChallengeService.Issue"/>.</param>
/// <param name="Nonce">32 random bytes the client's AC includes in the signed payload.</param>
/// <param name="DeadlineUtc">UTC moment after which the ticket is no longer valid.</param>
public sealed record ChallengeTicket(string SessionKey, ReadOnlyMemory<byte> Nonce, DateTimeOffset DeadlineUtc);

/// <summary>What the client's anti-cheat returns in response to a challenge.</summary>
/// <param name="Nonce">Must match the <see cref="ChallengeTicket.Nonce"/> that was issued.</param>
/// <param name="BuildId">Opaque identifier of the client build (per-release string).</param>
/// <param name="ProcessMeasurement">
/// Optional measurement of the AC module's code section; pass empty when the plugin issuing the
/// challenge does not require one.
/// </param>
/// <param name="Signature">HMAC-SHA256 over nonce || build_id (utf-8) || process_measurement, keyed with the shared secret.</param>
public sealed record ChallengeFlagPayload(
    ReadOnlyMemory<byte> Nonce,
    string BuildId,
    ReadOnlyMemory<byte> ProcessMeasurement,
    ReadOnlyMemory<byte> Signature);

/// <summary>The outcome of <see cref="IChallengeService.Verify"/>.</summary>
/// <param name="Ok">True when the signature matched and the ticket was still valid.</param>
/// <param name="Reason">
/// Short machine-readable reason when <paramref name="Ok"/> is false: <c>no_pending_challenge</c>,
/// <c>expired</c>, <c>nonce_mismatch</c>, <c>no_secret_configured</c>, or <c>bad_signature</c>.
/// </param>
public readonly record struct ChallengeVerdict(bool Ok, string? Reason);
