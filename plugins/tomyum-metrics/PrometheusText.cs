// Metrics plugin — the counters and the Prometheus text exposition.
//
// Metric names and labels are what the status page's Prometheus and web app query
// (status-page/README.md): keep them stable.

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;

using TomYum.Plugins.Abstractions;

namespace TomYum.Plugins.Metrics;

/// <summary>Which client a score was submitted from.</summary>
public enum ScoreClient
{
    /// <summary>osu!stable (counted from the host's score event).</summary>
    Stable,

    /// <summary>osu!lazer (counted from the website's scores table).</summary>
    Lazer,
}

/// <summary>How an osu!stable login ended.</summary>
public enum LoginResult
{
    /// <summary>Logged in.</summary>
    Ok,

    /// <summary>Refused (unknown user, bad password, locked, deleted, bot, ban evasion…).</summary>
    Failed,

    /// <summary>Held: maintenance, or a device the player hasn't verified yet.</summary>
    Held,
}

/// <summary>The plugin's counters and gauges. Every member is safe to call from any thread.</summary>
public sealed class MetricsCounters
{
    private long _scoresStable;
    private long _scoresLazer;
    private long _loginsOk;
    private long _loginsFailed;
    private long _loginsHeld;
    private long _registeredUsers = -1;

    /// <summary>Counts submitted scores.</summary>
    /// <param name="client">Where they were played.</param>
    /// <param name="count">How many (lazer scores are counted in batches).</param>
    public void ScoreSubmitted(ScoreClient client, long count = 1)
    {
        if (count <= 0)
            return;
        if (client == ScoreClient.Lazer)
            Interlocked.Add(ref _scoresLazer, count);
        else
            Interlocked.Add(ref _scoresStable, count);
    }

    /// <summary>Counts one osu!stable login attempt.</summary>
    /// <param name="result">How it ended.</param>
    public void Login(LoginResult result)
    {
        switch (result)
        {
            case LoginResult.Ok:
                Interlocked.Increment(ref _loginsOk);
                break;
            case LoginResult.Held:
                Interlocked.Increment(ref _loginsHeld);
                break;
            default:
                Interlocked.Increment(ref _loginsFailed);
                break;
        }
    }

    /// <summary>Registered players, or null until counted once.</summary>
    public long? RegisteredUsers
    {
        get => Interlocked.Read(ref _registeredUsers) is var n and >= 0 ? n : null;
        set => Interlocked.Exchange(ref _registeredUsers, value ?? -1);
    }

    internal long ScoresStable => Interlocked.Read(ref _scoresStable);

    internal long ScoresLazer => Interlocked.Read(ref _scoresLazer);

    internal long LoginsOk => Interlocked.Read(ref _loginsOk);

    internal long LoginsFailed => Interlocked.Read(ref _loginsFailed);

    internal long LoginsHeld => Interlocked.Read(ref _loginsHeld);
}

/// <summary>Writes the Prometheus text format (version 0.0.4).</summary>
public static class PrometheusText
{
    private static readonly double StartTimeSeconds = ReadStartTime();

    // The host's display version, as TomYum shows it ("0.1.0-dev", without the "+commit" part, so a
    // deploy of the same version doesn't start a new series).
    private static readonly string? HostVersion =
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?.Split('+')[0] is { Length: > 0 } version ? version : null;

    /// <summary>Every metric, as Prometheus text.</summary>
    /// <param name="counters">The plugin's counters.</param>
    /// <param name="online">Players online by client, or null when the host couldn't say.</param>
    public static string Render(MetricsCounters counters, HostOnlineCounts? online)
    {
        ArgumentNullException.ThrowIfNull(counters);
        var sb = new StringBuilder(4096);

        if (online is not null)
        {
            Header(sb, "tomyum_online_players", "gauge", "Players online right now, by client (BanchoBot left out).");
            Sample(sb, "tomyum_online_players", "client", "stable", online.Stable);
            if (online.Lazer is { } lazer) // left out (not 0) while the lazer feed can't be read
                Sample(sb, "tomyum_online_players", "client", "lazer", lazer);
            Sample(sb, "tomyum_online_players", "client", "irc", online.Irc);

            Header(sb, "tomyum_multiplayer_matches", "gauge", "osu!stable multiplayer rooms open right now.");
            Sample(sb, "tomyum_multiplayer_matches", null, online.MultiplayerMatches);
        }

        Header(sb, "tomyum_scores_submitted_total", "counter", "Scores submitted since TomYum started, by client.");
        Sample(sb, "tomyum_scores_submitted_total", "client", "stable", counters.ScoresStable);
        Sample(sb, "tomyum_scores_submitted_total", "client", "lazer", counters.ScoresLazer);

        Header(sb, "tomyum_logins_total", "counter", "osu!stable login attempts since TomYum started, by result.");
        Sample(sb, "tomyum_logins_total", "client=\"stable\",result=\"ok\"", counters.LoginsOk);
        Sample(sb, "tomyum_logins_total", "client=\"stable\",result=\"failed\"", counters.LoginsFailed);
        Sample(sb, "tomyum_logins_total", "client=\"stable\",result=\"held\"", counters.LoginsHeld);

        if (counters.RegisteredUsers is { } registered)
        {
            Header(sb, "tomyum_registered_users", "gauge", "Registered players (not restricted, inactive or bots).");
            Sample(sb, "tomyum_registered_users", null, registered);
        }

        if (HostVersion is not null)
        {
            Header(sb, "tomyum_build_info", "gauge", "TomYum's version (always 1).");
            Sample(sb, "tomyum_build_info", "version=\"" + Escape(HostVersion) + "\"", 1);
        }

        WriteProcess(sb);
        return sb.ToString();
    }

    private static void WriteProcess(StringBuilder sb)
    {
        using (var process = Process.GetCurrentProcess())
        {
            Header(sb, "process_cpu_seconds_total", "counter", "Total user and system CPU time spent in seconds.");
            Sample(sb, "process_cpu_seconds_total", null, process.TotalProcessorTime.TotalSeconds);
            Header(sb, "process_resident_memory_bytes", "gauge", "Resident memory size in bytes.");
            Sample(sb, "process_resident_memory_bytes", null, process.WorkingSet64);
            Header(sb, "process_virtual_memory_bytes", "gauge", "Virtual memory size in bytes.");
            Sample(sb, "process_virtual_memory_bytes", null, process.VirtualMemorySize64);
        }

        Header(sb, "process_start_time_seconds", "gauge", "Start time of the process since unix epoch in seconds.");
        Sample(sb, "process_start_time_seconds", null, Math.Round(StartTimeSeconds, 3));

        if (OperatingSystem.IsLinux())
        {
            try
            {
                var fds = Directory.EnumerateFileSystemEntries("/proc/self/fd").Count();
                Header(sb, "process_open_fds", "gauge", "Number of open file descriptors.");
                Sample(sb, "process_open_fds", null, fds);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        Header(sb, "dotnet_gc_heap_bytes", "gauge", "Bytes the GC thinks are allocated.");
        Sample(sb, "dotnet_gc_heap_bytes", null, GC.GetTotalMemory(false));
        Header(sb, "dotnet_gc_allocated_bytes_total", "counter", "Bytes allocated since the process started.");
        Sample(sb, "dotnet_gc_allocated_bytes_total", null, GC.GetTotalAllocatedBytes());
        Header(sb, "dotnet_gc_collections_total", "counter", "Garbage collections since the process started, by generation.");
        for (var gen = 0; gen <= GC.MaxGeneration; gen++)
            Sample(sb, "dotnet_gc_collections_total", "generation", gen.ToString(CultureInfo.InvariantCulture), GC.CollectionCount(gen));
        Header(sb, "dotnet_threadpool_threads", "gauge", "Thread pool threads.");
        Sample(sb, "dotnet_threadpool_threads", null, ThreadPool.ThreadCount);
        Header(sb, "dotnet_threadpool_queue_length", "gauge", "Work items waiting for the thread pool.");
        Sample(sb, "dotnet_threadpool_queue_length", null, ThreadPool.PendingWorkItemCount);
    }

    private static double ReadStartTime()
    {
        using var process = Process.GetCurrentProcess();
        return process.StartTime.ToUniversalTime().Subtract(DateTime.UnixEpoch).TotalSeconds;
    }

    private static void Header(StringBuilder sb, string name, string type, string help) =>
        sb.Append("# HELP ").Append(name).Append(' ').Append(help).Append('\n')
          .Append("# TYPE ").Append(name).Append(' ').Append(type).Append('\n');

    private static void Sample(StringBuilder sb, string name, string label, string value, double sample) =>
        Sample(sb, name, label + "=\"" + Escape(value) + "\"", sample);

    private static void Sample(StringBuilder sb, string name, string? labels, double sample)
    {
        sb.Append(name);
        if (!string.IsNullOrEmpty(labels))
            sb.Append('{').Append(labels).Append('}');
        sb.Append(' ').Append(sample.ToString("R", CultureInfo.InvariantCulture)).Append('\n');
    }

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
             .Replace("\"", "\\\"", StringComparison.Ordinal)
             .Replace("\n", "\\n", StringComparison.Ordinal);
}
