namespace TomYum.Domain.Entities;

/// <summary>
/// One set of hardware/install hashes an account has logged in with (osu!stable sends them with
/// every login). Kept per distinct combination with a use count, so staff can see which accounts
/// share a machine and new accounts can be checked against restricted ones.
/// </summary>
public sealed class ClientHash
{
    public int UserId { get; init; }

    /// <summary>MD5 of the osu! install path.</summary>
    public string OsuPath { get; init; } = string.Empty;

    /// <summary>MD5 of the network adapter list (meaningless under Wine).</summary>
    public string Adapters { get; init; } = string.Empty;

    /// <summary>MD5 of the osu! uninstall id (a per-install GUID in the registry).</summary>
    public string UninstallId { get; init; } = string.Empty;

    /// <summary>MD5 of the system disk's serial.</summary>
    public string DiskSerial { get; init; } = string.Empty;

    /// <summary>The last login with these hashes (UTC).</summary>
    public DateTime LatestTime { get; init; }

    /// <summary>How many logins used these hashes.</summary>
    public int Occurrences { get; init; }
}
