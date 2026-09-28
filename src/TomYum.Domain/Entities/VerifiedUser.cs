namespace TomYum.Domain.Entities;

/// <summary>
/// An account that has been let into the game at least once (or was let in by staff). Only
/// unverified accounts go through the ban-evasion check at login.
/// </summary>
public sealed class VerifiedUser
{
    public int UserId { get; init; }

    public DateTime VerifiedAt { get; init; }

    /// <summary>The staff member who verified the account by hand; 0 = its first in-game login.</summary>
    public int VerifiedBy { get; init; }
}
