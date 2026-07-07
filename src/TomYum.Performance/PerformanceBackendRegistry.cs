using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// This is a polymorphic factory: Resolve returns different IPerformanceCalculator implementations
// depending on config, so the interface return type is intentional (CA1859 over-fires here).
#pragma warning disable CA1859

namespace TomYum.Performance;

/// <summary>Resolves a named pp backend to a live <see cref="IPerformanceCalculator"/>.</summary>
public interface IPerformanceBackendRegistry
{
    /// <summary>Resolve the backend registered under <paramref name="name"/>.</summary>
    IPerformanceCalculator Resolve(string name);
}

/// <summary>
/// Resolves a named pp backend to a live <see cref="IPerformanceCalculator"/>. Backends are cheap
/// to construct, so they are built per resolution (no long-lived HttpClient pinning); the official
/// backend's diff-attribute cache lives in the shared singleton <c>IMemoryCache</c>. Fallback chains
/// are wired here and depth-guarded against config loops. See ADR-0010.
/// </summary>
public sealed class PerformanceBackendRegistry : IPerformanceBackendRegistry
{
    private const int MaxFallbackDepth = 4;

    private readonly IServiceProvider _sp;
    private readonly PerformanceOptions _options;
    private readonly ILoggerFactory _loggerFactory;

    public PerformanceBackendRegistry(IServiceProvider sp, PerformanceOptions options, ILoggerFactory loggerFactory)
    {
        _sp = sp;
        _options = options;
        _loggerFactory = loggerFactory;
    }

    /// <summary>Resolve the backend registered under <paramref name="name"/>.</summary>
    public IPerformanceCalculator Resolve(string name) => Resolve(name, depth: 0);

    private IPerformanceCalculator Resolve(string name, int depth)
    {
        if (!_options.Backends.TryGetValue(name, out var cfg))
        {
            // The official backend is always available even if omitted from config.
            if (string.Equals(name, PerformanceOptions.OfficialBackendName, StringComparison.OrdinalIgnoreCase))
                return _sp.GetRequiredService<OfficialPerformanceCalculator>();
            throw new InvalidOperationException($"pp backend '{name}' is not defined in Performance:Backends.");
        }

        switch (cfg.Type.Trim().ToLowerInvariant())
        {
            case PerformanceBackendOptions.InProcessLazer:
            case "official":
                return _sp.GetRequiredService<OfficialPerformanceCalculator>();

            case PerformanceBackendOptions.Http:
                if (string.IsNullOrWhiteSpace(cfg.Url))
                    throw new InvalidOperationException($"pp backend '{name}' is type 'http' but has no Url.");

                Func<PerformanceRequest, CancellationToken, Task<double>>? fallback = null;
                if (!string.IsNullOrWhiteSpace(cfg.Fallback)
                    && !string.Equals(cfg.Fallback, name, StringComparison.OrdinalIgnoreCase)
                    && depth < MaxFallbackDepth)
                {
                    var next = cfg.Fallback!;
                    fallback = (req, ct) => Resolve(next, depth + 1).CalculateAsync(req, ct);
                }

                return new HttpPerformanceCalculator(
                    _sp.GetRequiredService<IHttpClientFactory>().CreateClient("pp-sidecar"),
                    cfg.Url!,
                    TimeSpan.FromMilliseconds(cfg.TimeoutMs <= 0 ? 3000 : cfg.TimeoutMs),
                    name,
                    fallback,
                    _loggerFactory.CreateLogger<HttpPerformanceCalculator>());

            default:
                throw new InvalidOperationException($"pp backend '{name}' has unknown type '{cfg.Type}'.");
        }
    }
}
