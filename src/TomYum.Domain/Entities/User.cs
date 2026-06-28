using TomYum.Domain.Enums;

namespace TomYum.Domain.Entities;

/// <summary>
/// A registered player. The aggregate root for identity, profile, and authentication
/// state shared across both the stable and lazer protocol edges.
/// </summary>
/// <remarks>
/// Domain entities must stay free of infrastructure concerns (no EF attributes, no
/// protocol DTOs). See ADR-0001 and ADR-0002. Auth fields mirror the osu!stable model
/// studied from bancho.py: the stored credential is <c>bcrypt(md5_hex(plaintext))</c>
/// (see <see cref="PasswordBcrypt"/>), and <see cref="SafeName"/> is the canonical
/// lookup key.
/// </remarks>
public sealed class User
{
    /// <summary>Primary key / osu! user id.</summary>
    public int Id { get; init; }

    /// <summary>Login name as shown in-client (may contain spaces, 2–15 chars).</summary>
    public string Username { get; init; } = string.Empty;

    /// <summary>
    /// Canonical lookup key: the username lowercased with spaces replaced by underscores.
    /// All by-name lookups match on this so "Blue Sky" and "blue_sky" collide as intended.
    /// Build it with <see cref="MakeSafeName"/>.
    /// </summary>
    public string SafeName { get; init; } = string.Empty;

    /// <summary>Account email (unique). Used for website auth and account recovery.</summary>
    public string Email { get; init; } = string.Empty;

    /// <summary>
    /// The stored password credential: <c>bcrypt(md5_hex(plaintext))</c>, a 60-char
    /// <c>$2*$</c> hash. The osu! client sends the md5 hex of the plaintext at login;
    /// verification runs bcrypt over those 32 ASCII bytes. Never store the plaintext or
    /// the bare md5. See <c>memories/protocols/bancho-stable.md</c> §3.
    /// </summary>
    public string PasswordBcrypt { get; init; } = string.Empty;

    /// <summary>ISO-3166 alpha-2 country acronym (lowercase, e.g. <c>th</c>); <c>xx</c> when unknown.</summary>
    public string Country { get; init; } = "xx";

    /// <summary>Unix seconds when the account was created.</summary>
    public long CreationTime { get; init; }

    /// <summary>Unix seconds of the player's most recent activity.</summary>
    public long LatestActivity { get; set; }

    /// <summary>
    /// The player's osu! API key. Doubles as the IRC password (PASS): external IRC
    /// clients/bots authenticate with the underscored username + this key. Minted via
    /// the Session frontend or an admin bot command; <c>null</c> until issued, and
    /// rotating it invalidates IRC auth. See <c>memories/protocols/irc.md</c>.
    /// </summary>
    public string? ApiKey { get; init; }

    /// <summary>
    /// The server's authorization flags for this account (what it may do), independent of any
    /// protocol. A fresh registration is <see cref="UserPrivileges.Normal"/>; the server owner
    /// (first real registration, id 3) is granted <see cref="UserPrivileges.Owner"/>. The stable
    /// edge maps this onto the client-facing privilege bitmask at login.
    /// </summary>
    public UserPrivileges Privileges { get; init; } = UserPrivileges.Normal;

    // TODO: silence/donor end, clan, preferred mode, play style.

    /// <summary>
    /// Produces the canonical <see cref="SafeName"/> for a username: lowercase, spaces
    /// replaced with underscores. Mirrors bancho.py's <c>make_safe_name</c>.
    /// </summary>
    public static string MakeSafeName(string username)
    {
        ArgumentNullException.ThrowIfNull(username);
        return username.ToLowerInvariant().Replace(' ', '_');
    }
}
