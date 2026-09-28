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

    /// <summary>The client's address as Caddy passes it on (Cloudflare's view of the client), else the connection's.</summary>
    public static string? ClientAddress(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var forwarded = context.Request.Headers["X-Real-IP"].ToString();
        return forwarded.Length > 0 ? forwarded : context.Connection.RemoteIpAddress?.ToString();
    }

    private static RedisKey Key(int userId) => "tomyum:login-ips:" + userId.ToString(CultureInfo.InvariantCulture);
}
