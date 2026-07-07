namespace TomYum.Performance;

/// <summary>
/// Operator configuration for the pluggable pp engine (config section <c>Performance</c>).
/// Each gamemode variant (vanilla / relax / autopilot × ruleset) can be routed to a different
/// backend — the in-process official calculator, an HTTP sidecar (rosu-pp / akatsuki-pp-rs or any
/// fork), etc. See ADR-0010.
/// </summary>
/// <example>
/// <code>
/// performance:
///   default: official
///   routes:
///     - modes: [vanilla]              # vn!std/taiko/catch/mania -> official (in-process ppy)
///       backend: official
///     - modes: [relax, autopilot]     # rx!* + ap!* -> a rust sidecar
///       backend: akatsuki
///   backends:
///     official: { type: inprocess-lazer }
///     akatsuki: { type: http, url: "http://localhost:9000/pp", timeoutMs: 2000, fallback: official }
/// </code>
/// </example>
public sealed class PerformanceOptions
{
    public const string SectionName = "Performance";

    /// <summary>Backend used when no route matches. Must be a key in <see cref="Backends"/>.</summary>
    public string Default { get; set; } = OfficialBackendName;

    /// <summary>Ordered routing rules; the first route whose <c>Modes</c> matches wins.</summary>
    public List<PerformanceRoute> Routes { get; set; } = [];

    /// <summary>Named backend definitions referenced by <see cref="Default"/> and routes.</summary>
    public Dictionary<string, PerformanceBackendOptions> Backends { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The always-available in-process backend name (auto-registered if absent).</summary>
    public const string OfficialBackendName = "official";
}

/// <summary>A single routing rule: send the listed gamemodes to <see cref="Backend"/>.</summary>
public sealed class PerformanceRoute
{
    /// <summary>
    /// Gamemode tokens this route matches: numeric ("6"), short name ("rx!catch"), a variant group
    /// ("vanilla"/"vn", "relax"/"rx", "autopilot"/"ap"), or "all"/"*". See
    /// <c>TomYum.Domain.Enums.GameMode.MatchesToken</c>.
    /// </summary>
    public List<string> Modes { get; set; } = [];

    /// <summary>Name of the backend (a key in <see cref="PerformanceOptions.Backends"/>).</summary>
    public string Backend { get; set; } = string.Empty;
}

/// <summary>Definition of one pp backend.</summary>
public sealed class PerformanceBackendOptions
{
    /// <summary>
    /// Backend kind. <c>inprocess-lazer</c> (a.k.a. <c>official</c>) runs the ppy packages in this
    /// process; <c>http</c> POSTs the score to a sidecar (rosu-pp / akatsuki-pp-rs / any fork).
    /// </summary>
    public string Type { get; set; } = InProcessLazer;

    /// <summary>Sidecar endpoint for <c>http</c> backends, e.g. <c>http://localhost:9000/pp</c>.</summary>
    public string? Url { get; set; }

    /// <summary>Per-request timeout for <c>http</c> backends (milliseconds).</summary>
    public int TimeoutMs { get; set; } = 3000;

    /// <summary>
    /// Backend to fall back to when this one errors (down sidecar, timeout, bad response). Null =
    /// no fallback (returns 0 pp, best-effort). Self-references are ignored to avoid loops.
    /// </summary>
    public string? Fallback { get; set; }

    public const string InProcessLazer = "inprocess-lazer";
    public const string Http = "http";
}
