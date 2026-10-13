using System.Globalization;
using System.Net;
using StackExchange.Redis;

namespace TomYum.Server.Bancho;

/// <summary>
/// The addresses each player logged in to the game from, for staff (Discord's <c>/user</c>): the
/// last <see cref="Kept"/> per player with when each was last used, kept for half a year. In Redis
/// (<c>tomyum:login-ips:{user}</c>, a sorted set scored by time).
/// </summary>
public sealed class LoginIps
{
    public const int Kept = 10;

    private static readonly TimeSpan KeptFor = TimeSpan.FromDays(180);

    private readonly IConnectionMultiplexer? _redis;
    private readonly TimeProvider _time;

    public LoginIps(TimeProvider time, IConnectionMultiplexer? redis = null)
    {
        _time = time;
        _redis = redis;
    }

    /// <summary>Remembers a login from <paramref name="address"/> (ignored when it isn't an IP address).</summary>
    public async Task RecordAsync(int userId, string? address)
    {
        if (_redis is null || !IPAddress.TryParse(address?.Trim(), out var ip))
            return;
        var key = Key(userId);
        var database = _redis.GetDatabase();
        await database.SortedSetAddAsync(key, ip.ToString(), _time.GetUtcNow().ToUnixTimeSeconds()).ConfigureAwait(false);
        await database.SortedSetRemoveRangeByRankAsync(key, 0, -(Kept + 1)).ConfigureAwait(false);
        await database.KeyExpireAsync(key, KeptFor).ConfigureAwait(false);
    }

    /// <summary>The player's addresses, most recent first.</summary>
    public async Task<IReadOnlyList<(string Address, DateTimeOffset LastSeen)>> ListAsync(int userId)
    {
        if (_redis is null)
            return [];
        var entries = await _redis.GetDatabase().SortedSetRangeByRankWithScoresAsync(Key(userId), 0, Kept - 1, Order.Descending).ConfigureAwait(false);
        return entries.Select(e => (e.Element.ToString(), DateTimeOffset.FromUnixTimeSeconds((long)e.Score))).ToList();
    }

    /// <summary>Set on a request the gateway vouched for (<c>UseGatewayLink</c>): the player's address.</summary>
    public const string GatewayClientItem = "tomyum:gateway-client";

    /// <summary>
    /// The client's address: the one the verified gateway gave, else as our own reverse proxy passes it
    /// on (<c>X-Real-IP</c>: Cloudflare's view of the client), else the connection's. The header is only
    /// believed from <see cref="FromProxy"/>: from anywhere else a client could name any address.
    /// </summary>
    /// <param name="context">The request.</param>
    public static string? ClientAddress(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Items.TryGetValue(GatewayClientItem, out var gateway) && gateway is string vouched)
            return vouched;
        var forwarded = ProxyHeader(context, "X-Real-IP");
        return forwarded.Length > 0 ? forwarded : context.Connection.RemoteIpAddress?.ToString();
    }

    /// <summary>
    /// A header only our reverse proxy sets (<c>X-Real-IP</c>, Cloudflare's <c>CF-IPCountry</c>), or empty
    /// when the request didn't come through it.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <param name="name">The header.</param>
    public static string ProxyHeader(HttpContext context, string name)
    {
        ArgumentNullException.ThrowIfNull(context);
        return FromProxy(context) ? context.Request.Headers[name].ToString() : string.Empty;
    }

    /// <summary>
    /// Whether the request reached us from our own reverse proxy: a loopback or private-network peer
    /// (the stack's Caddy, on the compose network), whose forwarding headers are its own.
    /// </summary>
    /// <param name="context">The request.</param>
    public static bool FromProxy(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Connection.RemoteIpAddress is { } peer && IsPrivate(peer);
    }

    /// <summary>Loopback, RFC 1918 / shared (100.64/10) IPv4, or unique-local / link-local IPv6.</summary>
    internal static bool IsPrivate(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address))
            return true;
        var b = address.GetAddressBytes();
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            return b[0] == 10 || (b[0] == 172 && (b[1] & 0xF0) == 16) || (b[0] == 192 && b[1] == 168) || (b[0] == 100 && (b[1] & 0xC0) == 64);
        return (b[0] & 0xFE) == 0xFC || (b[0] == 0xFE && (b[1] & 0xC0) == 0x80);
    }

    private static RedisKey Key(int userId) => "tomyum:login-ips:" + userId.ToString(CultureInfo.InvariantCulture);
}
