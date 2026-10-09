// Session plugin — entry point, HTTP endpoints, and the expiry reaper.
//
// Clean-room port of a bancho.py fork's `session.py` web-auth core to the TomYum plugin ABI.
// Ships as ONE trusted plugin mounted on session.<domain>. It resolves auth itself inside each
// handler (the host's requireAuth is a hard 401 today — no auth-resolver hook exists), owns its
// own `sessions` table via IPluginContext.Data, and needs only TomYum.Plugins.Abstractions plus
// BCrypt.Net (both resolvable at compile + runtime from the host TPA).
//
// Human spec: docs/session-plugin.md. Agent notes: memories/features/session.md.

using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using TomYum.Plugins.Abstractions;

namespace TomYum.Plugins.Session;

/// <summary>The Session web-auth plugin: login/token/logout + session management on the <c>session</c> subdomain.</summary>
public sealed class SessionPlugin : IPlugin
{
    /// <summary>The <see cref="UserPrivileges.Normal"/> bit (1&lt;&lt;0); cleared on a restricted/banned account.</summary>
    private const int PrivNormal = 1;

    private readonly List<IDisposable> _routes = new();
    private IPluginLogger? _logger;
    private SessionService? _service;
    private CancellationTokenSource? _cts;
    private Task? _reaperTask;
    private int _reaperMinutes = 60;

    public PluginManifest Manifest { get; } = new(
        Id: "tomyum.session",
        Name: "Session (web auth)",
        Version: "0.1.0",
        Authors: "Phapoom Saksri",
        Description: "Website session/auth layer (login, tokens, session management) on session.<domain>. Port of a bancho.py fork's session.py.");

    public async Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken = default)
    {
        _logger = context.Logger;
        var options = SessionOptions.From(context.Config);
        _reaperMinutes = options.ReaperIntervalMinutes;

        // Register routes unconditionally so a missing trusted-DB grant surfaces as a clear 503,
        // not a silently-absent endpoint. All routes live on the session subdomain.
        Map(context, "POST", "/login", LoginAsync);
        Map(context, "POST", "/check-session", CheckSessionAsync);
        Map(context, "POST", "/logout", LogoutAsync);
        Map(context, "GET", "/user/sessions/web", ListSessionsAsync);
        Map(context, "DELETE", "/user/sessions/web/{target_token}", RevokeSessionAsync);
        Map(context, "GET", "/health", HealthAsync);

        if (context.Data is null)
        {
            _logger.Error(
                "tomyum.session needs trusted DB access — add \"tomyum.session\" to Plugins:Trusted and enable the "
                + "database. Endpoints will return 503 until then.");
            return;
        }

        _service = new SessionService(context.Data, options, context.Logger);
        await _service.EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);

        _cts = new CancellationTokenSource();
        _reaperTask = Task.Run(() => ReaperLoopAsync(_cts.Token), CancellationToken.None);

        _logger.Info(
            $"session store ready (ttl {options.TtlHours}h, bindClient={options.BindClient}); mounted on session.<domain>");
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        foreach (var route in _routes)
            route.Dispose();
        _routes.Clear();

        if (_cts is not null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
            if (_reaperTask is not null)
            {
                try { await _reaperTask.ConfigureAwait(false); }
                catch { /* reaper cancellation is expected during shutdown */ }
            }
            _cts.Dispose();
            _cts = null;
        }

        _service = null;
    }

    private void Map(
        IPluginContext context,
        string method,
        string pattern,
        Func<PluginHttpRequest, CancellationToken, Task<PluginHttpResponse>> handler)
        => _routes.Add(context.Http.Map(method, pattern, handler, requireAuth: false, subdomain: "session"));

    // --- endpoints -----------------------------------------------------------------------------

    private async Task<PluginHttpResponse> LoginAsync(PluginHttpRequest request, CancellationToken ct)
    {
        var service = _service;
        if (service is null)
            return Unavailable();

        using var body = await SessionHttp.ReadJsonAsync(request.Body, ct).ConfigureAwait(false);
        var username = SessionHttp.GetString(body, "username");
        var password = SessionHttp.GetString(body, "password");
        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
            return SessionHttp.Status(401, "Invalid credentials.");

        var clientIp = SessionHttp.ResolveClientIp(request.Headers, service.Options.FrontendProxyToken);
        var safeName = SessionHttp.MakeSafeName(username);
        if (service.IsRateLimited($"login:ip:{clientIp}") || service.IsRateLimited($"login:user:{safeName}"))
            return SessionHttp.Status(429, "Too many login attempts. Try again later.");

        var user = await service.FindUserAsync(username, ct).ConfigureAwait(false);
        if (user is null)
        {
            SessionService.BurnTiming(SessionHttp.Md5Hex(password)); // equalise timing on user-miss
            return SessionHttp.Status(401, "Invalid credentials.");
        }

        var u = user.Value;
        if (!service.VerifyPassword(password, u.PasswordBcrypt))
            return SessionHttp.Status(401, "Invalid credentials.");

        if (IsDisallowed(service, password))
            return SessionHttp.Status(403, "Password disallowed; please reset.");

        if (service.Options.RequireNormalPrivilege && (u.Privileges & PrivNormal) == 0)
            return SessionHttp.Status(403, "This account is not permitted to log in.");

        var token = SessionHttp.NewSessionToken();
        var now = DateTime.UtcNow;
        var expiresAt = now.AddHours(service.Options.TtlHours);
        var boundIp = SessionHttp.ClientIpPrefix(clientIp);
        var boundUa = SessionHttp.UserAgentHash(request.Headers);
        var rawUa = SessionHttp.UserAgent(request.Headers);
        var country = SessionHttp.CountryFromHeaders(request.Headers) ?? (u.Country == "xx" ? null : u.Country);

        await service.InsertSessionAsync(token, u.Id, now, expiresAt, boundIp, boundUa, rawUa, country, ct)
            .ConfigureAwait(false);

        var headers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Set-Cookie"] = BuildSetCookie(service.Options, token, service.Options.TtlSeconds),
        };
        return SessionHttp.Json(
            200,
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["status"] = "success", ["session"] = token },
            headers);
    }

    private async Task<PluginHttpResponse> CheckSessionAsync(PluginHttpRequest request, CancellationToken ct)
    {
        var service = _service;
        if (service is null)
            return Unavailable();

        using var body = await SessionHttp.ReadJsonAsync(request.Body, ct).ConfigureAwait(false);
        var token = ResolveToken(service, request, body);
        if (string.IsNullOrEmpty(token))
            return SessionHttp.Status(400, "Session token is missing.");

        var userId = await service.GetUserIdForTokenAsync(token, ct).ConfigureAwait(false);
        if (userId is null)
            return SessionHttp.Status(401, "Invalid or expired session token.");

        return SessionHttp.Json(200, ("status", "success"), ("userid", userId.Value));
    }

    private async Task<PluginHttpResponse> LogoutAsync(PluginHttpRequest request, CancellationToken ct)
    {
        var service = _service;
        if (service is null)
            return Unavailable();

        using var body = await SessionHttp.ReadJsonAsync(request.Body, ct).ConfigureAwait(false);
        var token = ResolveToken(service, request, body);
        if (string.IsNullOrEmpty(token))
            return SessionHttp.Status(400, "Session token is missing.");

        await service.DeleteTokenAsync(token, ct).ConfigureAwait(false);

        var headers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Set-Cookie"] = BuildSetCookie(service.Options, "", 0), // expire the cookie
        };
        return SessionHttp.Json(
            200,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["status"] = "success",
                ["message"] = "Logged out successfully.",
            },
            headers);
    }

    private async Task<PluginHttpResponse> ListSessionsAsync(PluginHttpRequest request, CancellationToken ct)
    {
        var service = _service;
        if (service is null)
            return Unavailable();

        var token = ResolveToken(service, request, body: null); // GET has no body
        var userId = await service.ValidateSessionAsync(token, headers: null, ct).ConfigureAwait(false);
        if (userId is null)
            return SessionHttp.Status(401, "Unauthorized");

        var sessions = await service.ListSessionsAsync(userId.Value, ct).ConfigureAwait(false);
        var list = new List<object?>(sessions.Count);
        foreach (var s in sessions)
        {
            var isCurrent = string.Equals(s.SessionToken, token, StringComparison.Ordinal);
            list.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["session_token"] = isCurrent
                    ? s.SessionToken
                    : (s.SessionToken.Length >= 8 ? s.SessionToken[^8..] : s.SessionToken),
                ["is_current"] = isCurrent,
                ["user_agent"] = s.UserAgent,
                ["browser"] = SessionHttp.PrettyUserAgent(s.UserAgent),
                ["client_ip"] = isCurrent ? s.ClientIp : null, // other sessions' IPs are hidden
                ["country"] = s.Country,
                ["last_active"] = SessionHttp.UtcIso(s.LastActive),
                ["expires_at"] = SessionHttp.UtcIso(s.ExpiresAt),
            });
        }

        return SessionHttp.Json(200, new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["status"] = "success",
            ["current_token"] = token,
            ["sessions"] = list,
        });
    }

    private async Task<PluginHttpResponse> RevokeSessionAsync(PluginHttpRequest request, CancellationToken ct)
    {
        var service = _service;
        if (service is null)
            return Unavailable();

        var token = ResolveToken(service, request, body: null);
        var userId = await service.ValidateSessionAsync(token, headers: null, ct).ConfigureAwait(false);
        if (userId is null)
            return SessionHttp.Status(401, "Unauthorized");

        if (!request.RouteValues.TryGetValue("target_token", out var target) || string.IsNullOrEmpty(target))
            return SessionHttp.Status(404, "Session not found.");

        if (string.Equals(target, token, StringComparison.Ordinal))
            return SessionHttp.Status(400, "Cannot end your current session. Use logout.");

        var resolved = await service.ResolveTargetTokenAsync(userId.Value, target, ct).ConfigureAwait(false);
        if (resolved is null || string.Equals(resolved, token, StringComparison.Ordinal))
            return SessionHttp.Status(404, "Session not found.");

        await service.DeleteTokenAsync(resolved, ct).ConfigureAwait(false);
        return SessionHttp.Json(200, ("status", "success"), ("message", "Session ended."));
    }

    private Task<PluginHttpResponse> HealthAsync(PluginHttpRequest request, CancellationToken ct)
        => Task.FromResult(SessionHttp.Json(
            200,
            ("status", "ok"),
            ("plugin", "tomyum.session"),
            ("trusted", _service is not null),
            ("time", SessionHttp.UtcIso(DateTime.UtcNow))));

    // --- helpers -------------------------------------------------------------------------------

    private static PluginHttpResponse Unavailable() => SessionHttp.Status(
        503,
        "Session store unavailable — add \"tomyum.session\" to Plugins:Trusted and enable the database.");

    private string? ResolveToken(SessionService service, PluginHttpRequest request, JsonDocument? body)
    {
        var cookie = SessionHttp.GetCookie(request.Headers, "session");
        if (!string.IsNullOrEmpty(cookie))
            return cookie;

        var bearer = SessionHttp.GetBearer(request.Headers);
        if (!string.IsNullOrEmpty(bearer))
            return bearer;

        if (service.Options.AllowBodyToken)
        {
            var bodyToken = SessionHttp.GetString(body, "session_token");
            if (!string.IsNullOrEmpty(bodyToken))
                return bodyToken;
        }

        return null;
    }

    private static bool IsDisallowed(SessionService service, string password)
    {
        foreach (var disallowed in service.Options.DisallowedPasswords)
            if (string.Equals(disallowed, password, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static string BuildSetCookie(SessionOptions options, string token, int maxAgeSeconds)
    {
        var sb = new StringBuilder();
        sb.Append("session=").Append(token);
        sb.Append("; Path=/");
        sb.Append("; Max-Age=").Append(maxAgeSeconds);
        sb.Append("; HttpOnly");
        sb.Append("; SameSite=").Append(options.CookieSameSite);
        if (options.CookieSecure)
            sb.Append("; Secure");
        if (!string.IsNullOrEmpty(options.CookieDomain))
            sb.Append("; Domain=").Append(options.CookieDomain);
        return sb.ToString();
    }

    private async Task ReaperLoopAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromMinutes(Math.Max(1, _reaperMinutes));
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                var service = _service;
                if (service is null)
                    continue;
                try
                {
                    var removed = await service.ReapExpiredAsync(ct).ConfigureAwait(false);
                    service.PruneMemory();
                    if (removed > 0)
                        _logger?.Info($"reaped {removed} expired session(s)");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { _logger?.Warn($"session reaper failed: {ex.Message}"); }
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }
}
