namespace TomYum.Domain.Services;

/// <summary>
/// Hashes and verifies osu!stable password credentials. The credential the client
/// transmits at login is the <em>md5 hex of the plaintext</em> (32 ASCII chars); the
/// stored value is <c>bcrypt(md5_hex(plaintext))</c>. This port hides the bcrypt details
/// and the in-memory verification cache from the domain. See
/// <c>memories/protocols/bancho-stable.md</c> §3.
/// </summary>
public interface IPasswordHasher
{
    /// <summary>
    /// Computes the lowercase md5 hex of a plaintext password. Used at registration, where
    /// the client posts the plaintext (over TLS): the result is then fed to
    /// <see cref="HashPassword"/>. The osu! client computes this same value itself at login.
    /// </summary>
    string ComputeMd5Hex(string plaintext);

    /// <summary>
    /// Produces the stored credential by running bcrypt over the 32-char md5 hex
    /// <paramref name="passwordMd5Hex"/>. Used at registration to fill
    /// <c>User.PasswordBcrypt</c>.
    /// </summary>
    /// <returns>A 60-char <c>$2*$</c> bcrypt hash to persist.</returns>
    string HashPassword(string passwordMd5Hex);

    /// <summary>
    /// Verifies the md5-hex credential sent by the osu! client at login against the stored
    /// bcrypt hash. <paramref name="passwordMd5Hex"/> is the 32-char lowercase md5 hex from
    /// the login body; <paramref name="storedBcrypt"/> is <c>User.PasswordBcrypt</c>.
    /// </summary>
    bool Verify(string passwordMd5Hex, string storedBcrypt);
}
