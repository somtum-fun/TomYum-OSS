using Microsoft.EntityFrameworkCore;
using TomYum.Domain.Entities;
using TomYum.Domain.Repositories;

namespace TomYum.Persistence.Repositories;

public sealed class ChatLogRepository : IChatLogRepository
{
    private const int MaxLimit = 500;

    private readonly TomYumDbContext _db;

    public ChatLogRepository(TomYumDbContext db) => _db = db;

    public async Task AddRangeAsync(IReadOnlyCollection<ChatLogEntry> entries, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0)
            return;

        _db.ChatLog.AddRange(entries);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        _db.ChangeTracker.Clear();
    }

    public async Task<IReadOnlyList<ChatLogEntry>> ListAsync(ChatLogQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var messages = _db.ChatLog.AsNoTracking();
        if (query.UserId is { } userId)
            messages = messages.Where(m => m.SenderId == userId || m.RecipientId == userId);
        if (!string.IsNullOrEmpty(query.Target))
        {
            var target = query.Target;
            // A channel, or a private conversation with that user (in either direction).
            messages = target.StartsWith('#')
                ? messages.Where(m => m.Target == target)
                : messages.Where(m => m.RecipientId != null && (m.Target == target || m.SenderName == target));
        }
        if (query.BeforeId is { } beforeId)
            messages = messages.Where(m => m.Id < beforeId);

        return await messages
            .OrderByDescending(m => m.Id)
            .Take(Math.Clamp(query.Limit, 1, MaxLimit))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<int> DeleteOlderThanAsync(DateTime cutoff, CancellationToken cancellationToken = default) =>
        _db.ChatLog.Where(m => m.SentAt < cutoff).ExecuteDeleteAsync(cancellationToken);
}
