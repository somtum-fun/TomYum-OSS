using System.Collections.Concurrent;
using TomYum.Domain.Entities;
using TomYum.Domain.Repositories;

namespace TomYum.Server.Beatmaps;

/// <summary>In-game comments kept in memory, without a database (lost on restart).</summary>
public sealed class InMemoryBeatmapCommentRepository : IBeatmapCommentRepository
{
    private readonly ConcurrentQueue<BeatmapComment> _comments = new();

    public Task AddAsync(BeatmapComment comment, CancellationToken cancellationToken = default)
    {
        _comments.Enqueue(comment);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<BeatmapComment>> ForPlayAsync(long setId, long beatmapId, long scoreId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<BeatmapComment>>([.. _comments
            .Where(c => (c.TargetType == "song" && c.TargetId == setId)
                || (c.TargetType == "map" && c.TargetId == beatmapId)
                || (scoreId > 0 && c.TargetType == "replay" && c.TargetId == scoreId))
            .OrderBy(c => c.Time)]);
}
