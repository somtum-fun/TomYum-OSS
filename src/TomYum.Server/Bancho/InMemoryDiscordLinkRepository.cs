using TomYum.Domain.Entities;
using TomYum.Domain.Repositories;

namespace TomYum.Server.Bancho;

/// <summary>Throwaway in-memory Discord links used when no database is configured (local dev, tests).</summary>
public sealed class InMemoryDiscordLinkRepository : IDiscordLinkRepository
{
    private readonly Lock _lock = new();
    private readonly List<DiscordLink> _links = [];

    public Task<DiscordLink?> GetByDiscordIdAsync(ulong discordId, CancellationToken cancellationToken = default)
    {
        lock (_lock)
            return Task.FromResult(_links.FirstOrDefault(l => l.DiscordId == discordId));
    }

    public Task<DiscordLink?> GetByUserIdAsync(int userId, CancellationToken cancellationToken = default)
    {
        lock (_lock)
            return Task.FromResult(_links.FirstOrDefault(l => l.UserId == userId));
    }

    public Task LinkAsync(int userId, ulong discordId, DateTime at, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            _links.RemoveAll(l => l.UserId == userId || l.DiscordId == discordId);
            _links.Add(new DiscordLink { UserId = userId, DiscordId = discordId, LinkedAt = at });
        }
        return Task.CompletedTask;
    }

    public Task<bool> UnlinkAsync(ulong discordId, CancellationToken cancellationToken = default)
    {
        lock (_lock)
            return Task.FromResult(_links.RemoveAll(l => l.DiscordId == discordId) > 0);
    }

    public Task<IReadOnlyList<DiscordLink>> ListAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
            return Task.FromResult<IReadOnlyList<DiscordLink>>(_links.OrderBy(l => l.UserId).ToList());
    }
}
