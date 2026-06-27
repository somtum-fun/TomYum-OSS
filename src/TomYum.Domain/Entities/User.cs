namespace TomYum.Domain.Entities;

/// <summary>
/// A registered player. The aggregate root for identity, profile, and authentication
/// state shared across both the stable and lazer protocol edges.
/// </summary>
/// <remarks>
/// This is a scaffolding stub. Fields will be filled in as persistence and protocol
/// work begins. Domain entities must stay free of infrastructure concerns
/// (no EF attributes, no protocol DTOs). See ADR-0001 and ADR-0002.
/// </remarks>
public sealed class User
{
    /// <summary>Primary key / osu! user id.</summary>
    public int Id { get; init; }

    /// <summary>Login name as shown in-client.</summary>
    public string Username { get; init; } = string.Empty;

    /// <summary>
    /// The player's osu! API key. Doubles as the IRC password (PASS): external IRC
    /// clients/bots authenticate with the underscored username + this key. Minted via
    /// the Session frontend or an admin bot command; <c>null</c> until issued, and
    /// rotating it invalidates IRC auth. See <c>memories/protocols/irc.md</c>.
    /// </summary>
    public string? ApiKey { get; init; }

    // TODO: country, privileges, password hash reference, timestamps, etc.
}
