using StackExchange.Redis;

namespace TomYum.Caching;

internal sealed class RedisScoreQueue(IConnectionMultiplexer redis) : IScoreQueue
{
    private const string StreamKey = "score-statistics";

    /// <summary>About how many scores the stream keeps (trimmed by Redis as it goes).</summary>
    public const int MaxLength = 10_000;

    public async Task EnqueueAsync(long scoreId, CancellationToken ct = default)
    {
        var db = redis.GetDatabase();
        // Nothing in TomYum reads this stream; keep only the latest scores so it can't grow forever.
        await db.StreamAddAsync(StreamKey, [new NameValueEntry("score-id", scoreId)], maxLength: MaxLength, useApproximateMaxLength: true).ConfigureAwait(false);
    }
}
