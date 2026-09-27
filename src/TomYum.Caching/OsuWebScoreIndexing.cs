using System.Text.Json;
using StackExchange.Redis;

namespace TomYum.Caching;

/// <summary>
/// Queues scores for osu-web's score indexer (<c>osu-elastic-indexer</c>, the <c>score-indexer</c>
/// service in osu-web's compose) exactly as osu-web's <c>ScoreSearch::queueForIndex</c> does:
/// <c>LPUSH osu-queue:{schema} {"ScoreId":id}</c> for every schema listed in
/// <c>osu-queue:score-index:{prefix}active-schemas</c>, where prefix is osu-web's
/// <c>ES_INDEX_PREFIX</c>. Needs osu-web's Redis (the compose overlay points TomYum at it). With no indexer running there are no active schemas and nothing is queued,
/// same as osu-web.
/// </summary>
public sealed class OsuWebScoreIndexing
{
    private readonly IConnectionMultiplexer _redis;
    private readonly string _activeSchemasKey;

    public OsuWebScoreIndexing(IConnectionMultiplexer redis, string indexPrefix)
    {
        _redis = redis;
        _activeSchemasKey = $"osu-queue:score-index:{indexPrefix}active-schemas";
    }

    public async Task QueueAsync(IReadOnlyCollection<ulong> scoreIds)
    {
        ArgumentNullException.ThrowIfNull(scoreIds);
        if (scoreIds.Count == 0)
            return;

        var db = _redis.GetDatabase();
        var values = scoreIds
            .Select(id => (RedisValue)JsonSerializer.Serialize(new Dictionary<string, ulong> { ["ScoreId"] = id }))
            .ToArray();

        foreach (var schema in await db.SetMembersAsync(_activeSchemasKey).ConfigureAwait(false))
            await db.ListLeftPushAsync($"osu-queue:{schema}", values).ConfigureAwait(false);
    }
}
