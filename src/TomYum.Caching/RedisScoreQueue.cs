using StackExchange.Redis;

namespace TomYum.Caching;

internal sealed class RedisScoreQueue(IConnectionMultiplexer redis) : IScoreQueue
{
    private const string StreamKey = "score-statistics";

    /// <summary>About how many scores the stream keeps (trimmed by Redis as it goes; ~100 bytes each).</summary>
    public const int MaxLength = 100_000;

    public async Task EnqueueAsync(long scoreId, CancellationToken ct = default)
    {
        var db = redis.GetDatabase();
        // Read by services/TomYum.ScoreQueue (consumer group tomyum-score-stats) when it runs; when it doesn't, nothing
        // trims the stream. Capped well above any backlog that service would have.
        await db.StreamAddAsync(StreamKey, [new NameValueEntry("score-id", scoreId)], maxLength: MaxLength, useApproximateMaxLength: true).ConfigureAwait(false);
    }
}
