using StackExchange.Redis;

namespace TomYum.Caching;

internal sealed class RedisScoreQueue(IConnectionMultiplexer redis) : IScoreQueue
{
    private const string StreamKey = "score-statistics";

    public async Task EnqueueAsync(long scoreId, CancellationToken ct = default)
    {
        var db = redis.GetDatabase();
        await db.StreamAddAsync(StreamKey, [new NameValueEntry("score-id", scoreId)]).ConfigureAwait(false);
    }
}
