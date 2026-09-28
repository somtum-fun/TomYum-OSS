using TomYum.Domain.Entities;

namespace TomYum.Domain.Repositories;

/// <summary>Game accounts linked to Discord accounts (<see cref="DiscordLink"/>).</summary>
public interface IDiscordLinkRepository
{
    Task<DiscordLink?> GetByDiscordIdAsync(ulong discordId, CancellationToken cancellationToken = default);

    Task<DiscordLink?> GetByUserIdAsync(int userId, CancellationToken cancellationToken = default);

    /// <summary>Links the two, replacing any earlier link either of them had.</summary>
    Task LinkAsync(int userId, ulong discordId, DateTime at, CancellationToken cancellationToken = default);

    /// <summary>Removes the Discord account's link; false when it had none.</summary>
    Task<bool> UnlinkAsync(ulong discordId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DiscordLink>> ListAsync(CancellationToken cancellationToken = default);
}
