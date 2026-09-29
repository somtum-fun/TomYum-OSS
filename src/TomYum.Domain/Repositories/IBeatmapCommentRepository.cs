using TomYum.Domain.Entities;

namespace TomYum.Domain.Repositories;

/// <summary>osu!stable's in-game comments (<see cref="BeatmapComment"/>).</summary>
public interface IBeatmapCommentRepository
{
    Task AddAsync(BeatmapComment comment, CancellationToken cancellationToken = default);

    /// <summary>
    /// The comments shown while playing: the set's, the difficulty's, and the replay's when one is
    /// being watched (<paramref name="scoreId"/> above 0), in timeline order.
    /// </summary>
    Task<IReadOnlyList<BeatmapComment>> ForPlayAsync(long setId, long beatmapId, long scoreId, CancellationToken cancellationToken = default);
}
