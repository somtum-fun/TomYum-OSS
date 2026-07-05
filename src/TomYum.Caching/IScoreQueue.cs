namespace TomYum.Caching;

/// <summary>Enqueues score IDs onto the Redis Stream consumed by TomYum.ScoreQueue.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711", Justification = "Queue suffix is intentional — this is a queue abstraction.")]
public interface IScoreQueue
{
    Task EnqueueAsync(long scoreId, CancellationToken ct = default);
}
