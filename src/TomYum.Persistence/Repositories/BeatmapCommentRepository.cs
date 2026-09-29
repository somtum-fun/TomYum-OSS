using Microsoft.EntityFrameworkCore;
using TomYum.Domain.Entities;
using TomYum.Domain.Repositories;

namespace TomYum.Persistence.Repositories;

public sealed class BeatmapCommentRepository : IBeatmapCommentRepository
{
    private readonly TomYumDbContext _db;

    public BeatmapCommentRepository(TomYumDbContext db) => _db = db;

    public async Task AddAsync(BeatmapComment comment, CancellationToken cancellationToken = default)
    {
        _db.BeatmapComments.Add(comment);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        _db.ChangeTracker.Clear();
    }

    public async Task<IReadOnlyList<BeatmapComment>> ForPlayAsync(long setId, long beatmapId, long scoreId, CancellationToken cancellationToken = default) =>
        await _db.BeatmapComments.AsNoTracking()
            .Where(c => (c.TargetType == "song" && c.TargetId == setId)
                || (c.TargetType == "map" && c.TargetId == beatmapId)
                || (scoreId > 0 && c.TargetType == "replay" && c.TargetId == scoreId))
            .OrderBy(c => c.Time)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
}
