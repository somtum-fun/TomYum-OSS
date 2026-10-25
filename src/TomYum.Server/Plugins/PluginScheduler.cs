using Microsoft.Extensions.Logging;

using TomYum.Plugins.Abstractions;

namespace TomYum.Server.Plugins;

/// <summary>
/// A plugin's <see cref="IPluginScheduler"/>: each <see cref="Every"/> runs on its own loop, never overlapping itself;
/// a handler that throws is logged and runs again next time. <see cref="Dispose"/> (the plugin unloading) stops them all.
/// </summary>
internal sealed class PluginScheduler : IPluginScheduler, IDisposable
{
    private static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(1);

    private readonly string _pluginId;
    private readonly ILogger _log;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _stopping = new();
    private readonly List<Job> _jobs = [];
    private readonly object _lock = new();

    public PluginScheduler(string pluginId, ILogger log, TimeProvider? time = null)
    {
        _pluginId = pluginId;
        _log = log;
        _time = time ?? TimeProvider.System;
    }

    public IDisposable Every(TimeSpan interval, Func<CancellationToken, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (interval < MinInterval)
            throw new ArgumentOutOfRangeException(nameof(interval), interval, "The interval is at least one second.");

        var job = new Job(this, CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token));
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_stopping.IsCancellationRequested, this);
            _jobs.Add(job);
        }

        _ = Task.Run(() => RunAsync(job, interval, handler));
        return job;
    }

    private async Task RunAsync(Job job, TimeSpan interval, Func<CancellationToken, Task> handler)
    {
        var token = job.Cancellation.Token;
        using var timer = new PeriodicTimer(interval, _time);
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                try
                {
                    await handler(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    return;
                }
#pragma warning disable CA1031 // a plugin's failing job must not stop its next run or the host
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    _log.LogWarning(ex, "plugin {Id}: a scheduled job threw; it runs again in {Interval}", _pluginId, interval);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // stopped
        }
    }

    private void Remove(Job job)
    {
        lock (_lock)
            _jobs.Remove(job);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_stopping.IsCancellationRequested)
                return;
            _stopping.Cancel();
            foreach (var job in _jobs)
                job.Cancellation.Dispose();
            _jobs.Clear();
        }

        _stopping.Dispose();
    }

    private sealed class Job(PluginScheduler owner, CancellationTokenSource cancellation) : IDisposable
    {
        public CancellationTokenSource Cancellation { get; } = cancellation;

        public void Dispose()
        {
            try
            {
                Cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // the plugin is already unloaded
            }

            owner.Remove(this);
        }
    }
}
