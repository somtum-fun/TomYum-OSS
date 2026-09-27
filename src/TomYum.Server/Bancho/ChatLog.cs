using System.Threading.Channels;
using TomYum.Domain.Entities;
using TomYum.Domain.Repositories;

namespace TomYum.Server.Bancho;

/// <summary>
/// Records in-game chat for staff (GMT panel). Chat handlers call <see cref="Record"/>, which only
/// queues the message; the background writer saves them in batches, so chat never waits on the
/// database. Messages older than <see cref="Retention"/> are deleted daily. Without a database
/// (no <see cref="IChatLogRepository"/>) nothing is kept.
/// </summary>
public sealed class ChatLog : BackgroundService
{
    /// <summary>How long messages are kept.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    private const int BatchSize = 200;

    private readonly Channel<ChatLogEntry> _pending = Channel.CreateBounded<ChatLogEntry>(
        new BoundedChannelOptions(10_000) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _time;
    private readonly ILogger<ChatLog> _logger;

    public ChatLog(IServiceScopeFactory scopes, TimeProvider time, ILogger<ChatLog> logger)
    {
        _scopes = scopes;
        _time = time;
        _logger = logger;
    }

    /// <summary>Queues a delivered message. <paramref name="recipientId"/> is set for private messages.</summary>
    public void Record(int senderId, string senderName, string target, string text, int? recipientId = null)
    {
        _pending.Writer.TryWrite(new ChatLogEntry
        {
            SentAt = _time.GetUtcNow().UtcDateTime,
            SenderId = senderId,
            SenderName = Cut(senderName, 32),
            Target = Cut(target, 64),
            RecipientId = recipientId,
            Text = Cut(text, 2048),
        });
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nextPrune = _time.GetUtcNow();
        var batch = new List<ChatLogEntry>(BatchSize);

        while (await _pending.Reader.WaitToReadAsync(stoppingToken).ConfigureAwait(false))
        {
            while (batch.Count < BatchSize && _pending.Reader.TryRead(out var entry))
                batch.Add(entry);

            try
            {
                using var scope = _scopes.CreateScope();
                if (scope.ServiceProvider.GetService<IChatLogRepository>() is { } repository)
                {
                    await repository.AddRangeAsync(batch, stoppingToken).ConfigureAwait(false);

                    if (_time.GetUtcNow() >= nextPrune)
                    {
                        var pruned = await repository.DeleteOlderThanAsync((_time.GetUtcNow() - Retention).UtcDateTime, stoppingToken).ConfigureAwait(false);
                        if (pruned > 0)
                            _logger.LogInformation("Chat log: deleted {Count} message(s) older than {Days} days.", pruned, Retention.TotalDays);
                        nextPrune = _time.GetUtcNow().AddDays(1);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Losing some log lines beats taking chat down.
                _logger.LogWarning(ex, "Couldn't save {Count} chat message(s) to the chat log.", batch.Count);
            }

            batch.Clear();
        }
    }

    private static string Cut(string value, int length) => value.Length <= length ? value : value[..length];
}
