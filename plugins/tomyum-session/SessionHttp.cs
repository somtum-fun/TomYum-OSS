// Session plugin — framework-agnostic HTTP / crypto helpers.
//
// The host hands plugins a PluginHttpRequest (headers/query/route-values/body) and expects a
// PluginHttpResponse back. It does NOT parse cookies, expose the socket peer IP, or give us
// redis — so everything the original `session.py` got from FastAPI/redis we reproduce here from
// headers + the BCL. See docs/session-plugin.md for the behavioural spec this mirrors.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using TomYum.Plugins.Abstractions;

namespace TomYum.Plugins.Session;

/// <summary>Static helpers shared by the Session endpoints: JSON responses, cookie/IP/UA parsing, hashing.</summary>
internal static class SessionHttp
{
    private const string JsonContentType = "application/json; charset=utf-8";

    /// <summary>Builds a JSON <see cref="PluginHttpResponse"/> from a field map (keys emitted verbatim, snake_case preserved).</summary>
    public static PluginHttpResponse Json(
        int status,
        IReadOnlyDictionary<string, object?> body,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(body);
        return new PluginHttpResponse(status, JsonContentType, bytes, headers);
    }

    /// <summary>Convenience overload: JSON response from inline (key, value) fields.</summary>
    public static PluginHttpResponse Json(int status, params (string Key, object? Value)[] fields)
    {
        var map = new Dictionary<string, object?>(fields.Length, StringComparer.Ordinal);
        foreach (var (key, value) in fields)
            map[key] = value;
        return Json(status, map);
    }

    /// <summary>A single <c>{"status": "..."}</c> body — the original's convention for every response.</summary>
    public static PluginHttpResponse Status(int status, string message) => Json(status, ("status", message));

    // --- token sources (cookie -> bearer -> body), first non-empty wins ------------------------

    /// <summary>Reads a cookie value out of the raw <c>Cookie</c> request header (the host does not pre-parse cookies).</summary>
    public static string? GetCookie(IReadOnlyDictionary<string, string> headers, string name)
    {
        if (!headers.TryGetValue("Cookie", out var raw) || string.IsNullOrEmpty(raw))
            return null;

        foreach (var part in raw.Split(';'))
        {
            var trimmed = part.Trim();
            var eq = trimmed.IndexOf('=');
            if (eq <= 0)
                continue;
            if (string.Equals(trimmed[..eq], name, StringComparison.Ordinal))
            {
                var value = trimmed[(eq + 1)..].Trim();
                return value.Length == 0 ? null : value;
            }
        }

        return null;
    }

    /// <summary>Extracts a <c>Bearer &lt;token&gt;</c> value from the Authorization header.</summary>
    public static string? GetBearer(IReadOnlyDictionary<string, string> headers)
    {
        if (!headers.TryGetValue("Authorization", out var raw) || string.IsNullOrWhiteSpace(raw))
            return null;

        var sp = raw.IndexOf(' ');
        if (sp <= 0)
            return null;
        if (!string.Equals(raw[..sp], "Bearer", StringComparison.OrdinalIgnoreCase))
            return null;

        var token = raw[(sp + 1)..].Trim();
        return token.Length == 0 ? null : token;
    }

    // --- client IP + binding primitives --------------------------------------------------------

    /// <summary>
    /// Resolves the trusted client IP the same spoof-resistant way the original does, but limited to what
    /// the host exposes (there is no socket-peer IP in a PluginHttpRequest): the proxy header when the
    /// shared token matches, else Cloudflare's <c>CF-Connecting-IP</c>. X-Forwarded-For is NOT trusted.
    /// Returns <c>"unknown"</c> when nothing usable is present.
    /// </summary>
    public static string ResolveClientIp(IReadOnlyDictionary<string, string> headers, string frontendProxyToken)
    {
        if (!string.IsNullOrEmpty(frontendProxyToken)
            && headers.TryGetValue("X-TomYum-Proxy-Token", out var proxyToken)
            && string.Equals(proxyToken, frontendProxyToken, StringComparison.Ordinal)
            && headers.TryGetValue("X-TomYum-Client-IP", out var forwarded)
            && IPAddress.TryParse(forwarded.Trim(), out var forwardedIp))
        {
            return forwardedIp.ToString();
        }

        if (headers.TryGetValue("CF-Connecting-IP", out var cf) && IPAddress.TryParse(cf.Trim(), out var cfIp))
            return cfIp.ToString();

        return "unknown";
    }

    /// <summary>
    /// Reduces an IP to the network prefix stored/compared for binding: /24 for IPv4, /64 for IPv6.
    /// Roaming inside a subnet keeps the same prefix, so it doesn't log the user out. <c>null</c> for
    /// <c>"unknown"</c> or an unparseable address.
    /// </summary>
    public static string? ClientIpPrefix(string? ip)
    {
        if (string.IsNullOrEmpty(ip) || ip == "unknown" || !IPAddress.TryParse(ip, out var addr))
            return null;

        var bytes = addr.GetAddressBytes();
        int keepBytes = addr.AddressFamily == AddressFamily.InterNetworkV6 ? 8 : 3; // /64 or /24
        for (var i = keepBytes; i < bytes.Length; i++)
            bytes[i] = 0;

        return new IPAddress(bytes).ToString();
    }

    /// <summary>SHA-256 (lowercase hex) of the User-Agent header, or <c>null</c> when absent — used for binding.</summary>
    public static string? UserAgentHash(IReadOnlyDictionary<string, string> headers)
    {
        var ua = UserAgent(headers, cap: int.MaxValue);
        if (ua is null)
            return null;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ua))).ToLowerInvariant();
    }

    /// <summary>The raw User-Agent header, truncated to <paramref name="cap"/> chars (default 512 for storage).</summary>
    public static string? UserAgent(IReadOnlyDictionary<string, string> headers, int cap = 512)
    {
        if (!headers.TryGetValue("User-Agent", out var ua) || string.IsNullOrEmpty(ua))
            return null;
        return ua.Length > cap ? ua[..cap] : ua;
    }

    /// <summary>Two-letter country from Cloudflare's <c>CF-IPCountry</c> header (lowercased), else <c>null</c>.</summary>
    public static string? CountryFromHeaders(IReadOnlyDictionary<string, string> headers)
    {
        if (!headers.TryGetValue("CF-IPCountry", out var c) || string.IsNullOrWhiteSpace(c))
            return null;
        c = c.Trim().ToLowerInvariant();
        // CF sends "XX"/"T1" for unknown/Tor — treat those as no-country.
        return c.Length == 2 && c != "xx" && c != "t1" ? c : null;
    }

    /// <summary>A human-readable "Browser on OS" label for the Security page (mirrors the original's _pretty_user_agent).</summary>
    public static string? PrettyUserAgent(string? ua)
    {
        if (string.IsNullOrEmpty(ua))
            return null;

        string browser =
            ua.Contains("Edg", StringComparison.OrdinalIgnoreCase) ? "Edge"
            : ua.Contains("Chrome", StringComparison.OrdinalIgnoreCase) ? "Chrome"
            : ua.Contains("Firefox", StringComparison.OrdinalIgnoreCase) ? "Firefox"
            : ua.Contains("Safari", StringComparison.OrdinalIgnoreCase) ? "Safari"
            : "Browser";

        string os =
            ua.Contains("Windows", StringComparison.OrdinalIgnoreCase) ? "Windows"
            : ua.Contains("Mac OS", StringComparison.OrdinalIgnoreCase) || ua.Contains("Macintosh", StringComparison.OrdinalIgnoreCase) ? "macOS"
            : ua.Contains("Android", StringComparison.OrdinalIgnoreCase) ? "Android"
            : ua.Contains("iPhone", StringComparison.OrdinalIgnoreCase) || ua.Contains("iPad", StringComparison.OrdinalIgnoreCase) ? "iOS"
            : ua.Contains("Linux", StringComparison.OrdinalIgnoreCase) ? "Linux"
            : "Unknown";

        return $"{browser} on {os}";
    }

    // --- misc ----------------------------------------------------------------------------------

    /// <summary>Lowercase MD5 hex of a UTF-8 string — the inner hash of TomYum's <c>bcrypt(md5(password))</c> scheme.</summary>
    public static string Md5Hex(string plaintext)
        => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(plaintext))).ToLowerInvariant();

    /// <summary>Canonical lookup key for a username: lowercase, spaces to underscores (matches User.MakeSafeName).</summary>
    public static string MakeSafeName(string username)
        => username.ToLowerInvariant().Replace(' ', '_');

    /// <summary>ISO-8601 (UTC, trailing Z) for display; <c>null</c> passes through.</summary>
    public static string? UtcIso(DateTime? dt)
    {
        if (dt is not { } value)
            return null;
        var utc = DateTime.SpecifyKind(value, DateTimeKind.Utc);
        return utc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
    }

    /// <summary>An opaque, URL-safe session token — 32 random bytes, base64url, no padding (~43 chars).</summary>
    public static string NewSessionToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    /// <summary>Reads and parses a JSON request body once (the host stream is forward-only, read-once). <c>null</c> when empty/invalid.</summary>
    public static async Task<JsonDocument?> ReadJsonAsync(Stream? body, CancellationToken ct)
    {
        if (body is null)
            return null;
        try
        {
            using var reader = new StreamReader(body, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            var text = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(text))
                return null;
            return JsonDocument.Parse(text);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Reads a string property from a parsed JSON body, or <c>null</c>.</summary>
    public static string? GetString(JsonDocument? doc, string property)
    {
        if (doc is null)
            return null;
        return doc.RootElement.ValueKind == JsonValueKind.Object
            && doc.RootElement.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }
}
