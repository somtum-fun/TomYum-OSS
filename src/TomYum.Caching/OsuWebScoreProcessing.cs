using StackExchange.Redis;

namespace TomYum.Caching;

/// <summary>
/// Queues website scores for ppy's score processor (<c>osu-queue-score-statistics</c>, the
/// <c>score-processor</c> service in osu-web's compose) the way osu-web's
/// <c>Solo\Score::queueForProcessing</c> does for lazer scores: <c>LPUSH osu-queue:score-statistics
/// {"Score": row}</c>. The processor computes each score's pp, the player's total pp, accuracy,
/// global rank and SS/S/A counts from stable and lazer scores together (docs/lazer.md).
/// </summary>
public sealed class OsuWebScoreProcessing
{
    /// <summary>osu-web's default <c>SCORES_PROCESSING_QUEUE</c>.</summary>
    public const string DefaultQueue = "osu-queue:score-statistics";

    private readonly IConnectionMultiplexer _redis;
    private readonly string _queue;

    public OsuWebScoreProcessing(IConnectionMultiplexer redis, string queue = DefaultQueue)
    {
        _redis = redis;
        _queue = queue;
    }

    /// <summary>How many scores the processor still has queued.</summary>
    public async Task<long> LengthAsync() =>
        await _redis.GetDatabase().ListLengthAsync(_queue).ConfigureAwait(false);

    /// <summary>Queues <paramref name="items"/>: each a JSON queue item (<c>{"Score": …}</c>).</summary>
    public async Task QueueAsync(IReadOnlyCollection<string> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0)
            return;

        await _redis.GetDatabase().ListLeftPushAsync(_queue, items.Select(i => (RedisValue)i).ToArray()).ConfigureAwait(false);
    }
}
