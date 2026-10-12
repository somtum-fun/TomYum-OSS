using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using StackExchange.Redis;

namespace TomYum.Server.Bancho;

/// <summary>
/// Slows password guessing on every credential path (the game's login, the stable <c>/web</c>
/// endpoints, IRC): wrong guesses are counted per account and per client address over
/// <see cref="Window"/>, and once an account has <see cref="AccountLimit"/> different wrong guesses (or
/// an address <see cref="AddressLimit"/>) its checks are refused, as a wrong password, until the window
/// runs out. Only <em>different</em> wrong guesses count, so a client still sending an old password
/// over and over can't lock anyone out; a guess already known to be wrong is answered without bcrypt.
/// A right password clears the account's count (never the address's). In Redis
/// (<c>tomyum:pw-guesses:*</c>) when there is one, else in memory.
/// </summary>
public sealed class PasswordGuesses
{
    /// <summary>Different wrong guesses an account takes in <see cref="Window"/> before its checks are refused.</summary>
    public const int AccountLimit = 10;

    /// <summary>Different wrong guesses one address makes in <see cref="Window"/> before its checks are refused.</summary>
    public const int AddressLimit = 30;

    /// <summary>How long wrong guesses are remembered (and so the longest a check stays refused).</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    private static readonly AsyncLocal<string?> Ambient = new();

    // KEYS[1] the account's guesses, KEYS[2] the address's count; ARGV[1] the guess, ARGV[2] the window in seconds.
    private const string FailScript = """
        local added = redis.call('SADD', KEYS[1], ARGV[1])
        if redis.call('TTL', KEYS[1]) < 0 then redis.call('EXPIRE', KEYS[1], ARGV[2]) end
        if added == 1 and KEYS[2] ~= KEYS[1] then
          redis.call('INCR', KEYS[2])
          if redis.call('TTL', KEYS[2]) < 0 then redis.call('EXPIRE', KEYS[2], ARGV[2]) end
        end
        return added
        """;

    private readonly IConnectionMultiplexer? _redis;
    private readonly TimeProvider _time;
    private readonly ILogger? _logger;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, (HashSet<string> Guesses, DateTimeOffset Until)> _accounts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (int Count, DateTimeOffset Until)> _addresses = new(StringComparer.Ordinal);

    /// <summary>Creates the limiter.</summary>
    /// <param name="time">The clock (windows).</param>
    /// <param name="redis">Redis, to share the counts across restarts; null keeps them in memory.</param>
    /// <param name="logger">For Redis faults (a check then goes ahead uncounted).</param>
    public PasswordGuesses(TimeProvider time, IConnectionMultiplexer? redis = null, ILogger<PasswordGuesses>? logger = null)
    {
        _time = time;
        _redis = redis;
        _logger = logger;
    }

    /// <summary>
    /// The client address for checks made inside the returned scope, where there's no HTTP request to
    /// read it from (the TCP bancho port, IRC). Dispose it when the check is done.
    /// </summary>
    /// <param name="address">The client's IP address, or null.</param>
    public static IDisposable From(string? address)
    {
        var previous = Ambient.Value;
        Ambient.Value = address;
        return new Restore(previous);
    }

    /// <summary>The address set by <see cref="From"/> around the current check, if any.</summary>
    public static string? CurrentAddress => Ambient.Value;

    /// <summary>The account key for a stored password hash (a new password starts a new count).</summary>
    /// <param name="storedHash">The account's stored credential.</param>
    public static string AccountKey(string storedHash) => Digest("account\n" + storedHash);

    /// <summary>
    /// Whether checks for <paramref name="account"/> from <paramref name="address"/> are refused for now.
    /// </summary>
    /// <param name="account">The account key (<see cref="AccountKey"/>, or another stable name for it).</param>
    /// <param name="address">The client's IP address, or null when unknown.</param>
    public bool IsRefused(string account, string? address)
    {
        var from = AddressKey(address);
        if (_redis is not null)
        {
            try
            {
                var database = _redis.GetDatabase();
                if (database.SetLength(RedisAccount(account)) >= AccountLimit)
                    return true;
                return from is not null && (long?)database.StringGet(RedisAddress(from)) >= AddressLimit;
            }
            catch (RedisException ex)
            {
                _logger?.LogWarning(ex, "Password guesses: Redis is unavailable; checking without the limit.");
                return false;
            }
        }

        var now = _time.GetUtcNow();
        lock (_gate)
        {
            if (_accounts.TryGetValue(account, out var guesses) && guesses.Until > now && guesses.Guesses.Count >= AccountLimit)
                return true;
            return from is not null && _addresses.TryGetValue(from, out var count) && count.Until > now && count.Count >= AddressLimit;
        }
    }

    /// <summary>Whether <paramref name="guess"/> was already found wrong for <paramref name="account"/> in this window.</summary>
    /// <param name="account">The account key.</param>
    /// <param name="guess">The credential tried.</param>
    public bool IsKnownWrong(string account, string guess)
    {
        var member = GuessKey(account, guess);
        if (_redis is not null)
        {
            try
            {
                return _redis.GetDatabase().SetContains(RedisAccount(account), member);
            }
            catch (RedisException ex)
            {
                _logger?.LogWarning(ex, "Password guesses: Redis is unavailable; checking without the limit.");
                return false;
            }
        }

        var now = _time.GetUtcNow();
        lock (_gate)
            return _accounts.TryGetValue(account, out var guesses) && guesses.Until > now && guesses.Guesses.Contains(member);
    }

    /// <summary>Counts a wrong <paramref name="guess"/> for <paramref name="account"/> and <paramref name="address"/>.</summary>
    /// <param name="account">The account key.</param>
    /// <param name="address">The client's IP address, or null when unknown.</param>
    /// <param name="guess">The credential tried (only a digest of it is kept).</param>
    public void Failed(string account, string? address, string guess)
    {
        var member = GuessKey(account, guess);
        var from = AddressKey(address);
        if (_redis is not null)
        {
            try
            {
                var accountKey = RedisAccount(account);
                RedisKey[] keys = [accountKey, from is null ? accountKey : RedisAddress(from)];
                _redis.GetDatabase().ScriptEvaluate(FailScript, keys, [member, (long)Window.TotalSeconds]);
            }
            catch (RedisException ex)
            {
                _logger?.LogWarning(ex, "Password guesses: Redis is unavailable; a wrong password wasn't counted.");
            }
            return;
        }

        var now = _time.GetUtcNow();
        lock (_gate)
        {
            if (_accounts.Count + _addresses.Count > 10_000)
                Prune(now);
            if (!_accounts.TryGetValue(account, out var guesses) || guesses.Until <= now)
                _accounts[account] = guesses = ([], now + Window);
            if (!guesses.Guesses.Add(member) || from is null)
                return;
            _addresses[from] = _addresses.TryGetValue(from, out var count) && count.Until > now
                ? (count.Count + 1, count.Until)
                : (1, now + Window);
        }
    }

    /// <summary>Clears <paramref name="account"/>'s wrong guesses (the right password was given).</summary>
    /// <param name="account">The account key.</param>
    public void Succeeded(string account)
    {
        if (_redis is not null)
        {
            try
            {
                _redis.GetDatabase().KeyDelete(RedisAccount(account), CommandFlags.FireAndForget);
            }
            catch (RedisException ex)
            {
                _logger?.LogWarning(ex, "Password guesses: Redis is unavailable; an account's count wasn't cleared.");
            }
            return;
        }

        lock (_gate)
            _accounts.Remove(account);
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (var (key, value) in _accounts.ToList())
        {
            if (value.Until <= now)
                _accounts.Remove(key);
        }
        foreach (var (key, value) in _addresses.ToList())
        {
            if (value.Until <= now)
                _addresses.Remove(key);
        }
    }

    /// <summary>The address counted: an IPv4 address, or an IPv6 address's /64 (one home or server); null when unknown.</summary>
    internal static string? AddressKey(string? address)
    {
        if (!IPAddress.TryParse(address?.Trim(), out var ip))
            return null;
        if (ip.IsIPv4MappedToIPv6)
            ip = ip.MapToIPv4();
        if (ip.AddressFamily != AddressFamily.InterNetworkV6)
            return ip.ToString();
        var bytes = ip.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new IPAddress(bytes).ToString() + "/64";
    }

    private static string GuessKey(string account, string guess) => Digest(account + "\n" + guess);

    private static string Digest(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..32];

    private static RedisKey RedisAccount(string account) => "tomyum:pw-guesses:account:" + account;

    private static RedisKey RedisAddress(string address) => "tomyum:pw-guesses:ip:" + address;

    private sealed class Restore(string? previous) : IDisposable
    {
        public void Dispose() => Ambient.Value = previous;
    }
}

/// <summary>
/// The password hasher every credential check goes through: <see cref="BcryptPasswordHasher"/> behind
/// <see cref="PasswordGuesses"/>. A refused check looks exactly like a wrong password. The client address
/// is the one set with <see cref="PasswordGuesses.From"/>, else the HTTP request's.
/// </summary>
public sealed class GuardedPasswordHasher : TomYum.Domain.Services.IPasswordHasher
{
    private readonly TomYum.Domain.Services.IPasswordHasher _inner;
    private readonly PasswordGuesses _guesses;
    private readonly IHttpContextAccessor? _http;

    /// <summary>Creates the hasher.</summary>
    /// <param name="inner">The real hasher.</param>
    /// <param name="guesses">The wrong-guess counts.</param>
    /// <param name="http">The current request, for the client's address (null outside a web host).</param>
    public GuardedPasswordHasher(TomYum.Domain.Services.IPasswordHasher inner, PasswordGuesses guesses, IHttpContextAccessor? http = null)
    {
        _inner = inner;
        _guesses = guesses;
        _http = http;
    }

    /// <inheritdoc />
    public string ComputeMd5Hex(string plaintext) => _inner.ComputeMd5Hex(plaintext);

    /// <inheritdoc />
    public string HashPassword(string passwordMd5Hex) => _inner.HashPassword(passwordMd5Hex);

    /// <inheritdoc />
    public bool Verify(string passwordMd5Hex, string storedBcrypt)
    {
        ArgumentException.ThrowIfNullOrEmpty(passwordMd5Hex);
        ArgumentException.ThrowIfNullOrEmpty(storedBcrypt);

        var account = PasswordGuesses.AccountKey(storedBcrypt);
        var address = PasswordGuesses.CurrentAddress
            ?? (_http?.HttpContext is { } context ? LoginIps.ClientAddress(context) : null);
        if (_guesses.IsRefused(account, address) || _guesses.IsKnownWrong(account, passwordMd5Hex))
            return false;

        if (_inner.Verify(passwordMd5Hex, storedBcrypt))
        {
            _guesses.Succeeded(account);
            return true;
        }
        _guesses.Failed(account, address, passwordMd5Hex);
        return false;
    }
}
