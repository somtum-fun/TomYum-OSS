using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using TomYum.Plugins.Abstractions;
using TomYum.Server.Bancho;

namespace TomYum.Server.Plugins;

/// <summary>
/// Raises <see cref="ChatRecordedEvent"/> for every recorded chat message (<see cref="ChatLog.RecordedFrom"/>), off the
/// chat path: a slow plugin never holds a message up.
/// </summary>
internal sealed class PluginChatFeed : IHostedService
{
    private readonly ChatLog? _chatLog;
    private readonly EventBus _events;

    public PluginChatFeed(EventBus events, ChatLog? chatLog = null)
    {
        _events = events;
        _chatLog = chatLog;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_chatLog is not null)
            _chatLog.RecordedFrom += OnRecorded;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (_chatLog is not null)
            _chatLog.RecordedFrom -= OnRecorded;
        return Task.CompletedTask;
    }

    private void OnRecorded(TomYum.Domain.Entities.ChatLogEntry entry, string origin)
    {
        if (!_events.HasSubscribers<ChatRecordedEvent>())
            return;
        var recorded = new ChatRecordedEvent(entry.Target, entry.SenderId, entry.SenderName, entry.Text, entry.RecipientId, origin);
        _ = Task.Run(() => _events.PublishAsync(recorded));
    }
}

/// <summary>
/// A logging provider that raises <see cref="ServerErrorEvent"/> for errors the server logs: the same category at most
/// once a minute (the rest are counted in the next one's <see cref="ServerErrorEvent.Suppressed"/>), and at most ten a
/// minute in all. A plugin handler that logs an error itself raises nothing more.
/// </summary>
public sealed class PluginErrorLoggerProvider : ILoggerProvider
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private const int MaxPerWindow = 10;

    private static readonly AsyncLocal<bool> InHandler = new();

    private readonly TimeProvider _time;
    private readonly object _lock = new();
    private readonly Dictionary<string, (DateTimeOffset Last, int Suppressed)> _byCategory = new(StringComparer.Ordinal);
    private DateTimeOffset _windowStart;
    private int _inWindow;
    private EventBus? _events;

    public PluginErrorLoggerProvider(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    /// <summary>Starts raising events on <paramref name="events"/> (once the app is built).</summary>
    public void Attach(EventBus events) => _events = events;

    public ILogger CreateLogger(string categoryName) => new ErrorLogger(this, categoryName);

    public void Dispose()
    {
    }

    private void Raise(string category, string message, Exception? exception)
    {
        if (InHandler.Value || _events is not { } events || !events.HasSubscribers<ServerErrorEvent>())
            return;

        var now = _time.GetUtcNow();
        int suppressed;
        lock (_lock)
        {
            if (now - _windowStart >= Window)
            {
                _windowStart = now;
                _inWindow = 0;
            }

            var seen = _byCategory.TryGetValue(category, out var last);
            if ((seen && now - last.Last < Window) || _inWindow >= MaxPerWindow)
            {
                _byCategory[category] = (seen ? last.Last : now, (seen ? last.Suppressed : 0) + 1);
                return;
            }

            suppressed = seen ? last.Suppressed : 0;
            _byCategory[category] = (now, 0);
            _inWindow++;
        }

        var error = new ServerErrorEvent(category, message, exception is null ? null : $"{exception.GetType().Name}: {exception.Message}", suppressed, now);
        _ = Task.Run(async () =>
        {
            InHandler.Value = true;
            await events.PublishAsync(error).ConfigureAwait(false);
        });
    }

    private sealed class ErrorLogger(PluginErrorLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;
            ArgumentNullException.ThrowIfNull(formatter);
            owner.Raise(category, formatter(state, exception), exception);
        }
    }
}
