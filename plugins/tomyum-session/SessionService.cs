// Session plugin — options, in-memory rate limiting, and the sessions-table data access.
//
// This is the clean-room reimplementation of bancho.py+prod's `session.py` auth core against
// TomYum's schema. Divergences from upstream (documented in docs/session-plugin.md):
//   * TomYum `users` columns are PascalCase (Id, SafeName, Email, PasswordBcrypt, Country,
//     Privileges); there is no `verified`/`silence_end` column yet.
//   * Password check is bcrypt(md5_hex(plaintext)) via BCrypt.Net (the same scheme the bancho
//     login uses); the website posts plaintext, so we md5 it first.
//   * No redis is handed to plugins, so rate limiting + last-active throttling are in-memory
//     (single-node). Cross-node parity waits on the Redis infra (ADR-0003).

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;

using TomYum.Plugins.Abstractions;

namespace TomYum.Plugins.Session;

/// <summary>Strongly-typed view over the plugin's <c>Plugins:tomyum.session</c> config subtree.</summary>
internal sealed class SessionOptions
{
    public int TtlHours { get; init; } = 24 * 30;          // 30 days, matching prod's SESSION_TTL_HOURS
    public bool BindClient { get; init; }                  // enforce IP-prefix + UA-hash binding
    public bool AllowBodyToken { get; init; } = true;      // accept token in a JSON body field
    public bool PasswordIsMd5 { get; init; }               // true if the frontend already md5s the password
    public bool RequireNormalPrivilege { get; init; } = true; // block accounts with the Normal bit cleared (restricted/banned)
    public string? CookieDomain { get; init; }
    public bool CookieSecure { get; init; } = true;
    public string CookieSameSite { get; init; } = "Lax";
    public string FrontendProxyToken { get; init; } = "";
    public int RateLimitWindowSeconds { get; init; } = 300;
    public int RateLimitMaxAttempts { get; init; } = 10;
    public int ReaperIntervalMinutes { get; init; } = 60;
    public IReadOnlyList<string> DisallowedPasswords { get; init; } = Array.Empty<string>();

    public int TtlSeconds => TtlHours * 3600;

    public static SessionOptions From(IPluginConfig config) => new()
    {
        TtlHours = config.Get("ttlHours", 24 * 30),
        BindClient = config.Get("bindClient", false),
        AllowBodyToken = config.Get("allowBodyToken", true),
        PasswordIsMd5 = config.Get("passwordIsMd5", false),
        RequireNormalPrivilege = config.Get("requireNormalPrivilege", true),
        CookieDomain = config.Get<string?>("cookieDomain", null),
        CookieSecure = config.Get("cookieSecure", true),
        CookieSameSite = config.Get("cookieSameSite", "Lax") ?? "Lax",
        FrontendProxyToken = config.Get("frontendProxyToken", "") ?? "",
        RateLimitWindowSeconds = config.Get("rateLimitWindowSeconds", 300),
        RateLimitMaxAttempts = config.Get("rateLimitMaxAttempts", 10),
        ReaperIntervalMinutes = config.Get("reaperIntervalMinutes", 60),
        DisallowedPasswords = config.Get<List<string>>("disallowedPasswords", null) ?? new List<string>(),
    };
}

/// <summary>The row shape read from <c>users</c> during login.</summary>
internal readonly record struct UserRow(int Id, string SafeName, string Email, string PasswordBcrypt, string Country, int Privileges);

/// <summary>One active session as shown on the Security page.</summary>
internal sealed record SessionListItem(
    string SessionToken,
    DateTime SessionDate,
    DateTime? ExpiresAt,
    string? ClientIp,
    DateTime? LastActive,
    string? UserAgent,
    string? Country);

/// <summary>
/// Owns the <c>sessions</c> table (a trusted plugin's own table) plus the in-memory rate-limit and
/// last-active-throttle state. All timestamps are UTC; expiry is compared against <c>UTC_TIMESTAMP()</c>.
/// </summary>
internal sealed class SessionService
{
    // bancho.py's dummy hash: verifying against it always fails but costs a real bcrypt round,
    // equalising response time between "unknown user" and "wrong password".
    private const string DummyBcrypt = "$2b$12$xG5QGrHUG75afGLOFR.7YuVCTNdVFSeeKR4m9ZQib.kxJFOqeIPI.";

    private readonly IPluginDataAccess _data;
    private readonly SessionOptions _options;
    private readonly IPluginLogger _log;

    private readonly ConcurrentDictionary<string, RateWindow> _rateLimits = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTime> _lastActiveThrottle = new(StringComparer.Ordinal);

    public SessionService(IPluginDataAccess data, SessionOptions options, IPluginLogger log)
    {
        _data = data;
        _options = options;
        _log = log;
    }

    public SessionOptions Options => _options;

    // --- schema --------------------------------------------------------------------------------

    public async Task EnsureSchemaAsync(CancellationToken ct)
    {
        await using var conn = await _data.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS sessions (
                SessionToken   VARCHAR(64)  NOT NULL,
                UserId         INT          NOT NULL,
                SessionDate    DATETIME     NOT NULL,
                ExpiresAt      DATETIME     NULL,
                ClientIp       VARCHAR(64)  NULL,
                UserAgentHash  CHAR(64)     NULL,
                UserAgent      VARCHAR(512) NULL,
                Country        VARCHAR(2)   NULL,
                LastActive     DATETIME     NULL,
                PRIMARY KEY (SessionToken),
                KEY IX_sessions_UserId (UserId),
                KEY IX_sessions_ExpiresAt (ExpiresAt)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
            """;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    // --- password verification -----------------------------------------------------------------

    /// <summary>Verifies a website login password against a stored <c>bcrypt(md5_hex)</c> hash.</summary>
    public bool VerifyPassword(string submitted, string storedBcrypt)
    {
        var md5Hex = _options.PasswordIsMd5 ? submitted.ToLowerInvariant() : SessionHttp.Md5Hex(submitted);
        return global::BCrypt.Net.BCrypt.Verify(md5Hex, storedBcrypt);
    }

    /// <summary>Burns a constant-time bcrypt round on the user-miss path so timing doesn't leak account existence.</summary>
    public static void BurnTiming(string submitted)
    {
        try { global::BCrypt.Net.BCrypt.Verify(submitted, DummyBcrypt); }
        catch { /* dummy hash is well-formed; ignore */ }
    }

    // --- rate limiting + throttle (in-memory) --------------------------------------------------

    /// <summary>INCR-with-window rate limiter. Returns true once the key exceeds the configured budget.</summary>
    public bool IsRateLimited(string key)
    {
        var now = DateTime.UtcNow;
        var window = TimeSpan.FromSeconds(_options.RateLimitWindowSeconds);

        var updated = _rateLimits.AddOrUpdate(
            key,
            _ => new RateWindow(now, 1),
            (_, existing) => now - existing.Start >= window ? new RateWindow(now, 1) : existing with { Count = existing.Count + 1 });

        return updated.Count > _options.RateLimitMaxAttempts;
    }

    /// <summary>True at most once per minute per token — the prod last-active write throttle, in memory.</summary>
    private bool ShouldBumpLastActive(string token)
    {
        var now = DateTime.UtcNow;
        var bump = false;
        _lastActiveThrottle.AddOrUpdate(
            token,
            _ => { bump = true; return now; },
            (_, previous) =>
            {
                if (now - previous >= TimeSpan.FromSeconds(60)) { bump = true; return now; }
                return previous;
            });
        return bump;
    }

    /// <summary>Drops rate-limit and throttle entries whose window has fully elapsed, so the maps don't grow unbounded.</summary>
    public void PruneMemory()
    {
        var now = DateTime.UtcNow;
        var window = TimeSpan.FromSeconds(_options.RateLimitWindowSeconds);
        foreach (var (key, value) in _rateLimits)
            if (now - value.Start >= window)
                _rateLimits.TryRemove(key, out _);
        foreach (var (key, value) in _lastActiveThrottle)
            if (now - value >= TimeSpan.FromMinutes(5))
                _lastActiveThrottle.TryRemove(key, out _);
    }

    // --- queries -------------------------------------------------------------------------------

    /// <summary>Looks a user up by safe-name (preferred) or email, mirroring prod's login lookup + ORDER BY.</summary>
    public async Task<UserRow?> FindUserAsync(string username, CancellationToken ct)
    {
        var safeName = SessionHttp.MakeSafeName(username);
        await using var conn = await _data.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Id, SafeName, Email, PasswordBcrypt, Country, Privileges
            FROM users
            WHERE SafeName = @safeName OR Email = @email
            ORDER BY (SafeName = @safeName) DESC
            LIMIT 1;
            """;
        AddParam(cmd, "@safeName", safeName);
        AddParam(cmd, "@email", username);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            return null;

        return new UserRow(
            reader.GetInt32(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? "xx" : reader.GetString(4),
            reader.GetInt32(5));
    }

    /// <summary>Inserts a freshly minted session row.</summary>
    public async Task InsertSessionAsync(
        string token, int userId, DateTime now, DateTime expiresAt,
        string? clientIp, string? uaHash, string? userAgent, string? country, CancellationToken ct)
    {
        await using var conn = await _data.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO sessions
                (SessionToken, UserId, SessionDate, ExpiresAt, ClientIp, UserAgentHash, UserAgent, Country, LastActive)
            VALUES
                (@token, @userId, @sessionDate, @expiresAt, @clientIp, @uaHash, @userAgent, @country, @lastActive);
            """;
        AddParam(cmd, "@token", token);
        AddParam(cmd, "@userId", userId);
        AddParam(cmd, "@sessionDate", now);
        AddParam(cmd, "@expiresAt", expiresAt);
        AddParam(cmd, "@clientIp", clientIp);
        AddParam(cmd, "@uaHash", uaHash);
        AddParam(cmd, "@userAgent", userAgent);
        AddParam(cmd, "@country", country);
        AddParam(cmd, "@lastActive", now);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>The user id for an unexpired token (a plain SELECT — does NOT bump last-active, like /check-session).</summary>
    public async Task<int?> GetUserIdForTokenAsync(string token, CancellationToken ct)
    {
        await using var conn = await _data.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT UserId FROM sessions
            WHERE SessionToken = @token AND (ExpiresAt IS NULL OR ExpiresAt > UTC_TIMESTAMP())
            LIMIT 1;
            """;
        AddParam(cmd, "@token", token);
        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is null || result is DBNull ? null : Convert.ToInt32(result);
    }

    /// <summary>
    /// The full <c>validate_session</c> primitive: existence + expiry, optional IP-prefix/UA-hash binding
    /// when <paramref name="headers"/> is supplied and binding is enabled, plus the throttled last-active
    /// bump. Returns the owning user id, or <c>null</c> when the token is missing/expired/failed binding.
    /// </summary>
    public async Task<int?> ValidateSessionAsync(string? token, IReadOnlyDictionary<string, string>? headers, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(token))
            return null;

        int userId;
        string? storedIp;
        string? storedUaHash;

        await using (var conn = await _data.OpenConnectionAsync(ct).ConfigureAwait(false))
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT UserId, ClientIp, UserAgentHash FROM sessions
                WHERE SessionToken = @token AND (ExpiresAt IS NULL OR ExpiresAt > UTC_TIMESTAMP())
                LIMIT 1;
                """;
            AddParam(cmd, "@token", token);

            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                return null;

            userId = reader.GetInt32(0);
            storedIp = reader.IsDBNull(1) ? null : reader.GetString(1);
            storedUaHash = reader.IsDBNull(2) ? null : reader.GetString(2);
        }

        if (_options.BindClient && headers is not null)
        {
            if (storedIp is not null
                && SessionHttp.ClientIpPrefix(SessionHttp.ResolveClientIp(headers, _options.FrontendProxyToken)) != storedIp)
                return null;
            if (storedUaHash is not null && SessionHttp.UserAgentHash(headers) != storedUaHash)
                return null;
        }

        if (ShouldBumpLastActive(token))
            await BumpLastActiveAsync(token, ct).ConfigureAwait(false);

        return userId;
    }

    private async Task BumpLastActiveAsync(string token, CancellationToken ct)
    {
        try
        {
            await using var conn = await _data.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE sessions SET LastActive = UTC_TIMESTAMP() WHERE SessionToken = @token;";
            AddParam(cmd, "@token", token);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Warn($"last-active bump failed: {ex.Message}"); // best-effort, never fail the request
        }
    }

    /// <summary>Deletes one session row by token. Returns rows affected.</summary>
    public async Task<int> DeleteTokenAsync(string token, CancellationToken ct)
    {
        await using var conn = await _data.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM sessions WHERE SessionToken = @token;";
        AddParam(cmd, "@token", token);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Lists a user's active sessions, most-recently-active first.</summary>
    public async Task<List<SessionListItem>> ListSessionsAsync(int userId, CancellationToken ct)
    {
        var items = new List<SessionListItem>();
        await using var conn = await _data.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT SessionToken, SessionDate, ExpiresAt, ClientIp, LastActive, UserAgent, Country
            FROM sessions
            WHERE UserId = @userId AND (ExpiresAt IS NULL OR ExpiresAt > UTC_TIMESTAMP())
            ORDER BY COALESCE(LastActive, SessionDate) DESC;
            """;
        AddParam(cmd, "@userId", userId);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            items.Add(new SessionListItem(
                reader.GetString(0),
                reader.GetDateTime(1),
                reader.IsDBNull(2) ? null : reader.GetDateTime(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetDateTime(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6)));
        }
        return items;
    }

    /// <summary>
    /// Resolves a revoke target (a full token or the masked last-8 suffix) to a full token owned by
    /// <paramref name="userId"/>. Scoped to the caller so one user can't revoke another's sessions.
    /// </summary>
    public async Task<string?> ResolveTargetTokenAsync(int userId, string target, CancellationToken ct)
    {
        await using var conn = await _data.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT SessionToken FROM sessions
            WHERE UserId = @userId AND (SessionToken = @target OR SessionToken LIKE @suffix)
            LIMIT 1;
            """;
        AddParam(cmd, "@userId", userId);
        AddParam(cmd, "@target", target);
        AddParam(cmd, "@suffix", "%" + target);
        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result as string;
    }

    /// <summary>Deletes every expired session. Returns rows removed. Run by the background reaper.</summary>
    public async Task<int> ReapExpiredAsync(CancellationToken ct)
    {
        await using var conn = await _data.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM sessions WHERE ExpiresAt IS NOT NULL AND ExpiresAt <= UTC_TIMESTAMP();";
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static void AddParam(DbCommand cmd, string name, object? value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value ?? DBNull.Value;
        cmd.Parameters.Add(p);
    }

    private readonly record struct RateWindow(DateTime Start, int Count);
}
