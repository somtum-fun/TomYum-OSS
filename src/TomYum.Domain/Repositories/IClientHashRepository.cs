using TomYum.Domain.Entities;

namespace TomYum.Domain.Repositories;

/// <summary>What to look for when matching a login's hashes against other accounts (see <see cref="Services.ClientHashMatch"/>).</summary>
/// <param name="Adapters">Adapter hash to match, or null to skip it.</param>
/// <param name="UninstallId">Uninstall-id hash to match, or null to skip it.</param>
/// <param name="DiskSerial">Disk-serial hash to match, or null to skip it.</param>
public sealed record ClientHashQuery(string? Adapters, string? UninstallId, string? DiskSerial)
{
    public bool IsEmpty => Adapters is null && UninstallId is null && DiskSerial is null;
}

/// <summary>Login hardware hashes (<see cref="ClientHash"/>) and verified accounts (<see cref="VerifiedUser"/>).</summary>
public interface IClientHashRepository
{
    /// <summary>Records a login with these hashes: adds the combination or bumps its count and time.</summary>
    Task RecordAsync(ClientHash hashes, CancellationToken cancellationToken = default);

    /// <summary>Other accounts that logged in with any of the query's hashes, most recent first.</summary>
    Task<IReadOnlyList<int>> FindOtherUsersAsync(int userId, ClientHashQuery query, int limit = 50, CancellationToken cancellationToken = default);

    /// <summary>Every hash combination the account has used, most recent first.</summary>
    Task<IReadOnlyList<ClientHash>> ListAsync(int userId, CancellationToken cancellationToken = default);

    Task<bool> IsVerifiedAsync(int userId, CancellationToken cancellationToken = default);

    /// <summary>Marks the account verified; false when it already was.</summary>
    Task<bool> VerifyAsync(int userId, int verifiedBy, DateTime at, CancellationToken cancellationToken = default);
}
