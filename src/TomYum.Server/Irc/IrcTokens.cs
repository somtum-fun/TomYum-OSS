using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using StackExchange.Redis;
using TomYum.Persistence.OsuWeb;

namespace TomYum.Server.Irc;

/// <summary>
/// Players' IRC passwords (osu! gives IRC its own password, not the account's). With website
/// accounts it's the IRC key from the website's account settings (<see cref="OsuWebIrcKeys"/>).
/// Otherwise BanchoBot's <c>!irc</c> makes one, a new one replacing the old; only a hash is kept,
/// in Redis (<c>tomyum:irc:passwords</c>), or in memory without Redis.
/// </summary>
public sealed class IrcTokens
{
    private const string Key = "tomyum:irc:passwords";

    private readonly IConnectionMultiplexer? _redis;
    private readonly IServiceScopeFactory? _website;
    private readonly ConcurrentDictionary<int, string> _memory = new();

    /// <param name="redis">Where hashes of !irc passwords are kept (memory without it).</param>
    /// <param name="website">Given with website accounts: keys come from the website instead.</param>
    public IrcTokens(IConnectionMultiplexer? redis = null, IServiceScopeFactory? website = null)
    {
        _redis = redis;
        _website = website;
    }

    /// <summary>Whether keys are made on the website (then <see cref="CreateAsync"/> isn't used).</summary>
    public bool FromWebsite => _website is not null;

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
        if (_website is not null)
        {
            using var scope = _website.CreateScope();
            var key = await scope.ServiceProvider.GetRequiredService<OsuWebIrcKeys>().TokenAsync(userId).ConfigureAwait(false);
            return key is not null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(password.Trim()));
        }

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
