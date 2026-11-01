using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using StackExchange.Redis;

namespace TomYum.Server.Bancho;

/// <summary>
/// Slows password guessing on every credential path (the game's login, the stable <c>/web</c>
/// endpoints, IRC): wrong guesses are counted per account, per account and address, and per address
/// over <see cref="Window"/>. Checks are refused, as a wrong password, until the window runs out for an
/// address that made <see cref="AccountLimit"/> different wrong guesses for the account; once the account
/// has <see cref="AccountLimit"/> from anywhere, also for an address that made <see cref="SuspectGuesses"/>;
/// and for an address that made <see cref="AddressLimit"/> across accounts. An address that hasn't been
/// guessing (the player's own game, a session submitting scores) is never refused, so nobody can lock a
/// player out by guessing their name from elsewhere, and spreading guesses over addresses costs one
/// address for every few. Only <em>different</em> wrong guesses count, so a client still sending an old
/// password over and over can't lock anyone out; a guess already known to be wrong is answered without
/// bcrypt. A right password clears the account's count and that address's (never the address's own).
/// In Redis (<c>tomyum:pw-guesses:*</c>) when there is one, else in memory.
/// </summary>
public sealed class PasswordGuesses
{
    /// <summary>
    /// Different wrong guesses for an account from one address in <see cref="Window"/> before that
    /// address's checks of it are refused; also how many from anywhere make the account guarded.
    /// </summary>
    public const int AccountLimit = 10;

    /// <summary>Wrong guesses after which an address is refused for an account that's guarded (see <see cref="AccountLimit"/>).</summary>
    public const int SuspectGuesses = 3;

    /// <summary>Different wrong guesses one address makes in <see cref="Window"/> before its checks are refused.</summary>
    public const int AddressLimit = 30;

    /// <summary>How long wrong guesses are remembered (and so the longest a check stays refused).</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    private static readonly AsyncLocal<string?> Ambient = new();

    // KEYS[1] the account's guesses, KEYS[2] the address's count, KEYS[3] the account's guesses from the
    // address; ARGV[1] the guess, ARGV[2] the window in seconds.
    private const string FailScript = """
        local added = redis.call('SADD', KEYS[1], ARGV[1])
        if redis.call('TTL', KEYS[1]) < 0 then redis.call('EXPIRE', KEYS[1], ARGV[2]) end
        redis.call('SADD', KEYS[3], ARGV[1])
        if redis.call('TTL', KEYS[3]) < 0 then redis.call('EXPIRE', KEYS[3], ARGV[2]) end
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
    private readonly Dictionary<string, (HashSet<string> Guesses, DateTimeOffset Until)> _pairs = new(StringComparer.Ordinal);

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
    /// Whether this check should be answered as wrong without trying it: checks for
    /// <paramref name="account"/> from <paramref name="address"/> are refused for now, or
    /// <paramref name="guess"/> was already found wrong in this window. Runs on every credential check
    /// (each score submission and most of stable's <c>/web</c> calls), so in Redis it's one batch: it mustn't
    /// hold a thread-pool thread while it waits, and a burst of blocking calls here starved the pool.
    /// </summary>
    /// <param name="account">The account key (<see cref="AccountKey"/>, or another stable name for it).</param>
    /// <param name="address">The client's IP address, or null when unknown.</param>
    /// <param name="guess">The credential tried.</param>
    public async ValueTask<bool> ShouldRefuseAsync(string account, string? address, string guess)
    {
        var from = AddressKey(address);
        var pair = PairKey(account, from);
        var member = GuessKey(account, guess);
        if (_redis is not null)
        {
            try
            {
                var accountKey = RedisAccount(account);
                var batch = _redis.GetDatabase().CreateBatch();
                var fromHere = batch.SetLengthAsync(RedisPair(pair));
                var total = batch.SetLengthAsync(accountKey);
                var addressCount = from is null ? Task.FromResult(RedisValue.Null) : batch.StringGetAsync(RedisAddress(from));
                var known = batch.SetContainsAsync(accountKey, member);
                batch.Execute();
                await Task.WhenAll(fromHere, total, addressCount, known).ConfigureAwait(false);
                return Refused(await fromHere.ConfigureAwait(false), await total.ConfigureAwait(false), (long?)await addressCount.ConfigureAwait(false) ?? 0)
                    || await known.ConfigureAwait(false);
            }
            catch (Exception ex) when (IsRedisFault(ex))
            {
                _logger?.LogWarning(ex, "Password guesses: Redis is unavailable; checking without the limit.");
                return false;
            }
        }

        var now = _time.GetUtcNow();
        lock (_gate)
        {
            var mine = _pairs.TryGetValue(pair, out var fromAddress) && fromAddress.Until > now ? fromAddress.Guesses.Count : 0;
            var accountGuesses = _accounts.TryGetValue(account, out var guesses) && guesses.Until > now ? guesses.Guesses : null;
            var addressCount = from is not null && _addresses.TryGetValue(from, out var count) && count.Until > now ? count.Count : 0;
            return Refused(mine, accountGuesses?.Count ?? 0, addressCount) || accountGuesses?.Contains(member) == true;
        }
    }

    /// <summary>Counts a wrong <paramref name="guess"/> for <paramref name="account"/> and <paramref name="address"/>.</summary>
    /// <param name="account">The account key.</param>
    /// <param name="address">The client's IP address, or null when unknown.</param>
    /// <param name="guess">The credential tried (only a digest of it is kept).</param>
    public async ValueTask FailedAsync(string account, string? address, string guess)
    {
        var member = GuessKey(account, guess);
        var from = AddressKey(address);
        if (_redis is not null)
        {
            try
            {
                var accountKey = RedisAccount(account);
                RedisKey[] keys = [accountKey, from is null ? accountKey : RedisAddress(from), RedisPair(PairKey(account, from))];
                await _redis.GetDatabase().ScriptEvaluateAsync(FailScript, keys, [member, (long)Window.TotalSeconds]).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsRedisFault(ex))
            {
                _logger?.LogWarning(ex, "Password guesses: Redis is unavailable; a wrong password wasn't counted.");
            }
            return;
        }

        var now = _time.GetUtcNow();
        lock (_gate)
        {
            if (_accounts.Count + _addresses.Count + _pairs.Count > 10_000)
                Prune(now);
            if (!_accounts.TryGetValue(account, out var guesses) || guesses.Until <= now)
                _accounts[account] = guesses = ([], now + Window);
            var pair = PairKey(account, from);
            if (!_pairs.TryGetValue(pair, out var mine) || mine.Until <= now)
                _pairs[pair] = mine = ([], now + Window);
            mine.Guesses.Add(member);
            if (!guesses.Guesses.Add(member) || from is null)
                return;
            _addresses[from] = _addresses.TryGetValue(from, out var count) && count.Until > now
                ? (count.Count + 1, count.Until)
                : (1, now + Window);
        }
    }

    /// <summary>Clears <paramref name="account"/>'s wrong guesses, and those from <paramref name="address"/> (the right password was given there).</summary>
    /// <param name="account">The account key.</param>
    /// <param name="address">The client's IP address, or null when unknown.</param>
    public void Succeeded(string account, string? address = null)
    {
        var pair = PairKey(account, AddressKey(address));
        if (_redis is not null)
        {
            try
            {
                _redis.GetDatabase().KeyDelete([RedisAccount(account), RedisPair(pair)], CommandFlags.FireAndForget);
            }
            catch (Exception ex) when (IsRedisFault(ex))
            {
                _logger?.LogWarning(ex, "Password guesses: Redis is unavailable; an account's count wasn't cleared.");
            }
            return;
        }

        lock (_gate)
        {
            _accounts.Remove(account);
            _pairs.Remove(pair);
        }
    }

    private static bool Refused(long fromHere, long total, long addressCount) =>
        fromHere >= AccountLimit || (fromHere >= SuspectGuesses && total >= AccountLimit) || addressCount >= AddressLimit;

    /// <summary>A Redis fault this limiter lets a check through on. <c>RedisTimeoutException</c> is a <see cref="TimeoutException"/>, not a <see cref="RedisException"/>.</summary>
    private static bool IsRedisFault(Exception ex) => ex is RedisException or TimeoutException;

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
        foreach (var (key, value) in _pairs.ToList())
        {
            if (value.Until <= now)
                _pairs.Remove(key);
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

    /// <summary>An account's guesses from one address (an unknown address counts as one).</summary>
    private static string PairKey(string account, string? from) => Digest(account + "\n@" + (from ?? "unknown"));

    private static string Digest(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..32];

    private static RedisKey RedisAccount(string account) => "tomyum:pw-guesses:account:" + account;

    private static RedisKey RedisAddress(string address) => "tomyum:pw-guesses:ip:" + address;

    private static RedisKey RedisPair(string pair) => "tomyum:pw-guesses:pair:" + pair;

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
    public async ValueTask<bool> VerifyAsync(string passwordMd5Hex, string storedBcrypt, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(passwordMd5Hex);
        ArgumentException.ThrowIfNullOrEmpty(storedBcrypt);

        var account = PasswordGuesses.AccountKey(storedBcrypt);
        var address = PasswordGuesses.CurrentAddress
            ?? (_http?.HttpContext is { } context ? LoginIps.ClientAddress(context) : null);
        if (await _guesses.ShouldRefuseAsync(account, address, passwordMd5Hex).ConfigureAwait(false))
            return false;

        if (await _inner.VerifyAsync(passwordMd5Hex, storedBcrypt, cancellationToken).ConfigureAwait(false))
        {
            _guesses.Succeeded(account, address);
            return true;
        }
        await _guesses.FailedAsync(account, address, passwordMd5Hex).ConfigureAwait(false);
        return false;
    }
}
