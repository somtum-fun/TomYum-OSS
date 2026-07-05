using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace TomYum.ScoreQueue;

/// <summary>
/// Consumes the Redis Stream <c>score-statistics</c> (consumer group <c>tomyum-score-stats</c>)
/// and publishes <c>osu-channel:score:processed</c> for spectator/MP fan-out.
/// Stats are already applied inline at submission; this worker handles post-processing
/// (notifications, spectator broadcasts, future achievements) without blocking the hot path.
/// See ADR-0004.
/// </summary>
internal sealed class ScoreStatisticsConsumer(
    IConnectionMultiplexer redis,
    ILogger<ScoreStatisticsConsumer> logger) : BackgroundService
{
    private const string StreamKey = "score-statistics";
    private const string GroupName = "tomyum-score-stats";
    private const string ProcessedChannel = "osu-channel:score:processed";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var db = redis.GetDatabase();
        var consumerName = Environment.MachineName;

        await EnsureConsumerGroupAsync(db).ConfigureAwait(false);

        logger.LogInformation(
            "ScoreStatisticsConsumer started (stream={Stream}, group={Group}, consumer={Consumer})",
            StreamKey, GroupName, consumerName);

        // Drain any messages that were delivered but not acked before the last shutdown.
        await DrainPendingAsync(db, consumerName, stoppingToken).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var entries = await db.StreamReadGroupAsync(
                    StreamKey, GroupName, consumerName,
                    position: ">", count: 10, noAck: false).ConfigureAwait(false);

                if (entries.Length == 0)
                {
                    // No new entries — wait a bit before polling again.
                    await Task.Delay(500, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                foreach (var entry in entries)
                    await ProcessEntryAsync(db, entry, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error reading score-statistics stream; retrying in 5 s");
                await Task.Delay(5_000, stoppingToken).ConfigureAwait(false);
            }
        }

        logger.LogInformation("ScoreStatisticsConsumer stopped");
    }

    private async Task ProcessEntryAsync(IDatabase db, StreamEntry entry, CancellationToken ct)
    {
        var scoreIdRaw = entry["score-id"];
        if (!long.TryParse((string?)scoreIdRaw, out var scoreId))
        {
            logger.LogWarning("Skipping malformed stream entry {EntryId}: score-id='{Raw}'", entry.Id, scoreIdRaw);
            await db.StreamAcknowledgeAsync(StreamKey, GroupName, entry.Id).ConfigureAwait(false);
            return;
        }

        logger.LogInformation("Processing score {ScoreId} from stream entry {EntryId}", scoreId, entry.Id);

        // Publish pub/sub for spectator/MP watchers (ADR-0003: ephemeral fan-out via pub/sub).
        await redis.GetSubscriber()
            .PublishAsync(RedisChannel.Literal(ProcessedChannel), scoreId.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .ConfigureAwait(false);

        await db.StreamAcknowledgeAsync(StreamKey, GroupName, entry.Id).ConfigureAwait(false);
        logger.LogDebug("Score {ScoreId} acknowledged", scoreId);
    }

    private async Task DrainPendingAsync(IDatabase db, string consumerName, CancellationToken ct)
    {
        var pending = await db.StreamPendingMessagesAsync(StreamKey, GroupName, 10, consumerName).ConfigureAwait(false);
        if (pending.Length == 0) return;

        logger.LogInformation("Draining {Count} pending messages for consumer {Consumer}", pending.Length, consumerName);
        var ids = pending.Select(p => p.MessageId).ToArray();
        var claimed = await db.StreamRangeAsync(StreamKey, ids[0], ids[^1], pending.Length).ConfigureAwait(false);
        foreach (var entry in claimed)
            await ProcessEntryAsync(db, entry, ct).ConfigureAwait(false);
    }

    private static async Task EnsureConsumerGroupAsync(IDatabase db)
    {
        try
        {
            // $ = only new messages; "0" would re-process all historical entries from start.
            await db.StreamCreateConsumerGroupAsync(StreamKey, GroupName, "$", createStream: true)
                .ConfigureAwait(false);
        }
        catch (RedisServerException ex) when (ex.Message.Contains("BUSYGROUP"))
        {
            // Group already exists — expected on restart; not an error.
        }
    }
}
