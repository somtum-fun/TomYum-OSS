using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using TomYum.Domain.Services;

namespace TomYum.Server.Bancho;

/// <summary>
/// osu!stable-compatible password hasher: the stored credential is
/// <c>bcrypt(md5_hex(plaintext))</c>. The client never sends the plaintext at login — it
/// sends the lowercase md5 hex — so verification runs bcrypt over those 32 ASCII bytes.
/// Mirrors bancho.py (<c>$2*$</c> prefix, cost 12). See
/// <c>memories/protocols/bancho-stable.md</c> §3.
/// </summary>
/// <remarks>
/// bcrypt is deliberately slow (~200 ms at cost 12), so a successful (bcrypt → md5) pair is
/// cached in memory keyed by the stored hash. A later login with the same credential then
/// does a constant-time byte compare instead of re-running bcrypt — the same optimisation
/// bancho.py uses (<c>app.state.cache.bcrypt</c>). The cache only ever holds values that have
/// already been verified, so a hit is as trustworthy as the original bcrypt check.
/// </remarks>
public sealed class BcryptPasswordHasher : IPasswordHasher
{
    private const int WorkFactor = 12;

    // Stored bcrypt hash -> the md5-hex bytes proven to match it. Bounds: this is unbounded
    // in principle; a future slice should cap/evict it. Fine for the in-memory slice.
    private readonly ConcurrentDictionary<string, byte[]> _verified = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public string HashPassword(string passwordMd5Hex)
    {
        ArgumentException.ThrowIfNullOrEmpty(passwordMd5Hex);

        var md5Bytes = Encoding.ASCII.GetBytes(passwordMd5Hex);
        var hash = BCrypt.Net.BCrypt.HashPassword(passwordMd5Hex, WorkFactor);
        _verified[hash] = md5Bytes;
        return hash;
    }

    /// <inheritdoc />
    public bool Verify(string passwordMd5Hex, string storedBcrypt)
    {
        ArgumentException.ThrowIfNullOrEmpty(passwordMd5Hex);
        ArgumentException.ThrowIfNullOrEmpty(storedBcrypt);

        var candidate = Encoding.ASCII.GetBytes(passwordMd5Hex);

        // Fast path: we've already verified this credential against this hash.
        if (_verified.TryGetValue(storedBcrypt, out var known))
        {
            return CryptographicOperations.FixedTimeEquals(candidate, known);
        }

        // Slow path: real bcrypt check, then cache the proven pair.
        if (!BCrypt.Net.BCrypt.Verify(passwordMd5Hex, storedBcrypt))
        {
            return false;
        }

        _verified[storedBcrypt] = candidate;
        return true;
    }

    /// <inheritdoc />
    // CA5351: MD5 is mandated by the osu!stable wire protocol (the client sends md5 hex of
    // the password and bcrypt is layered over it); it is not used as a security primitive here.
#pragma warning disable CA5351
    public string ComputeMd5Hex(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        var digest = MD5.HashData(Encoding.UTF8.GetBytes(plaintext));
        return Convert.ToHexString(digest).ToLowerInvariant();
    }
#pragma warning restore CA5351
}
