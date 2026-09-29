using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using StackExchange.Redis;

namespace TomYum.Server.Irc;

/// <summary>
/// Players' IRC passwords (osu! gives IRC its own password, not the account's): made with
/// BanchoBot's <c>!irc</c>, a new one replacing the old. Only a hash is kept, in Redis
/// (<c>tomyum:irc:passwords</c>), or in memory without Redis.
/// </summary>
public sealed class IrcTokens
{
    private const string Key = "tomyum:irc:passwords";

    private readonly IConnectionMultiplexer? _redis;
    private readonly ConcurrentDictionary<int, string> _memory = new();

    public IrcTokens(IConnectionMultiplexer? redis = null) => _redis = redis;

    /// <summary>A new password for <paramref name="userId"/> (the old one stops working).</summary>
    public async Task<string> CreateAsync(int userId)
    {
        var password = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
        var hash = Hash(password);
        if (_redis is null)
            _memory[userId] = hash;
        else
            await _redis.GetDatabase().HashSetAsync(Key, userId.ToString(CultureInfo.InvariantCulture), hash).ConfigureAwait(false);
        return password;
    }

    public async Task<bool> VerifyAsync(int userId, string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        string? stored;
        if (_redis is null)
        {
            stored = _memory.TryGetValue(userId, out var hash) ? hash : null;
        }
        else
        {
            var value = await _redis.GetDatabase().HashGetAsync(Key, userId.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
            stored = value.IsNull ? null : value.ToString();
        }
        return stored is not null && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(stored), Encoding.ASCII.GetBytes(Hash(password.Trim())));
    }

    private static string Hash(string password) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(password)));
}
