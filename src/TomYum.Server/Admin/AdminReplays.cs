using StackExchange.Redis;

namespace TomYum.Server.Admin;

/// <summary>
/// Signed admin API requests already used, so a captured one can't be sent again while its timestamp
/// is still fresh (<see cref="AdminSignature.MaxClockSkew"/>). Kept for <see cref="KeptFor"/> (longer
/// than any timestamp stays fresh), in Redis (<c>tomyum:admin-signatures:*</c>) when there is one, else
/// in memory.
/// </summary>
/// <remarks>
/// Only requests that change something (anything but GET and HEAD), or that carry a <c>nonce</c>, are
/// single-use: osu-web's timestamps are whole seconds, so two visitors reading the same thing (a guest's
/// rankings page, a replay download) in the same second send the very same signed GET.
/// </remarks>
public sealed class AdminReplays
{
    /// <summary>How long a used signature is remembered.</summary>
    public static readonly TimeSpan KeptFor = TimeSpan.FromMinutes(10);

    private readonly IConnectionMultiplexer? _redis;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _used = new(StringComparer.Ordinal);

    /// <summary>Creates the store.</summary>
    /// <param name="time">The clock (in memory).</param>
    /// <param name="redis">Redis, to remember across restarts; null keeps them in memory.</param>
    public AdminReplays(TimeProvider time, IConnectionMultiplexer? redis = null)
    {
        _time = time;
        _redis = redis;
    }

    /// <summary>Whether a request with this method and query is single-use (see the remarks).</summary>
    /// <param name="method">The request's method.</param>
    /// <param name="query">The request's query string.</param>
    public static bool IsSingleUse(string method, IQueryCollection query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return !(HttpMethods.IsGet(method) || HttpMethods.IsHead(method)) || query.ContainsKey("nonce");
    }

    /// <summary>
    /// Marks <paramref name="signature"/> as used. False when it was used already (the request is a replay).
    /// </summary>
    /// <param name="signature">The request's (valid) signature.</param>
    public async Task<bool> TryUseAsync(string signature)
    {
        ArgumentException.ThrowIfNullOrEmpty(signature);
        var key = signature.ToLowerInvariant();
        if (_redis is not null)
            return await _redis.GetDatabase().StringSetAsync("tomyum:admin-signatures:" + key, 1, KeptFor, When.NotExists).ConfigureAwait(false);

        var now = _time.GetUtcNow();
        lock (_gate)
        {
            if (_used.Count > 1000)
            {
                foreach (var (old, until) in _used.ToList())
                {
                    if (until <= now)
                        _used.Remove(old);
                }
            }
            if (_used.TryGetValue(key, out var kept) && kept > now)
                return false;
            _used[key] = now + KeptFor;
            return true;
        }
    }
}
