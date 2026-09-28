using System.Threading.Channels;

namespace TomYum.Server.Discord;

/// <summary>
/// Collects the server's errors (log level Error and up) for the staff server-log channel. The
/// announcer posts them; this only queues, keeps the newest 100 and never logs itself, so a Discord
/// outage can't feed back into more errors. The bot's own messages are left out for the same reason.
/// </summary>
public sealed class DiscordLogSink : ILoggerProvider
{
    private readonly Channel<string> _errors = Channel.CreateBounded<string>(
        new BoundedChannelOptions(100) { FullMode = BoundedChannelFullMode.DropOldest });

    public ILogger CreateLogger(string categoryName) => new SinkLogger(this, categoryName);

    /// <summary>Takes up to <paramref name="max"/> queued errors.</summary>
    public IReadOnlyList<string> Drain(int max)
    {
        var drained = new List<string>();
        while (drained.Count < max && _errors.Reader.TryRead(out var error))
            drained.Add(error);
        return drained;
    }

    public void Dispose()
    {
    }

    internal void Add(LogLevel level, string category, string message, Exception? exception)
    {
        var text = $"**{level}** `{category}`: {Cut(message, 1_000)}";
        if (exception is not null)
        {
            var detail = Cut($"{exception.GetType().Name}: {exception.Message}", 400).Replace("`", "'", StringComparison.Ordinal);
            text += $"\n```\n{detail}\n```";
        }
        _errors.Writer.TryWrite(text);
    }

    private static string Cut(string text, int max) => text.Length > max ? text[..max] + "…" : text;

    private sealed class SinkLogger(DiscordLogSink sink, string category) : ILogger
    {
        private readonly bool _ignored = category.StartsWith("TomYum.Server.Discord", StringComparison.Ordinal)
            || category.StartsWith("Discord", StringComparison.Ordinal);

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => !_ignored && logLevel >= LogLevel.Error && logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            if (IsEnabled(logLevel))
                sink.Add(logLevel, category, formatter(state, exception), exception);
        }
    }
}
