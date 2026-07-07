using System.IO;
using System.Net.Http;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace TomYum.Performance;

/// <summary>DI wiring for the pluggable pp engine (ADR-0010).</summary>
public static class PerformanceServiceCollectionExtensions
{
    /// <summary>
    /// Registers the pp engine: <see cref="RoutingPerformanceCalculator"/> as the public
    /// <see cref="IPerformanceCalculator"/>, the <see cref="PerformanceBackendRegistry"/>, the
    /// in-process <see cref="OfficialPerformanceCalculator"/> backend, and supporting infra
    /// (memory cache + <c>IHttpClientFactory</c>). Both the server host and the RecalcPp tool call
    /// this so pp is computed identically online and during recalculation.
    /// </summary>
    public static IServiceCollection AddTomYumPerformance(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new PerformanceOptions();
        configuration.GetSection(PerformanceOptions.SectionName).Bind(options);

        // The official in-process backend is always available, even if the operator omitted it.
        if (!options.Backends.ContainsKey(PerformanceOptions.OfficialBackendName))
        {
            options.Backends[PerformanceOptions.OfficialBackendName] =
                new PerformanceBackendOptions { Type = PerformanceBackendOptions.InProcessLazer };
        }

        // Fail fast on a misconfigured backend name rather than silently recording 0 pp at runtime
        // (a typo would otherwise zero every affected score on the pp-ranked relax/autopilot boards).
        bool IsDefined(string? name) =>
            !string.IsNullOrWhiteSpace(name)
            && (options.Backends.ContainsKey(name!)
                || string.Equals(name, PerformanceOptions.OfficialBackendName, StringComparison.OrdinalIgnoreCase));

        if (!IsDefined(options.Default))
            throw new InvalidOperationException($"Performance:Default '{options.Default}' is not a defined backend (Performance:Backends).");

        foreach (var route in options.Routes)
        {
            if (!string.IsNullOrWhiteSpace(route.Backend) && !IsDefined(route.Backend))
                throw new InvalidOperationException(
                    $"Performance route backend '{route.Backend}' (modes: {string.Join(",", route.Modes)}) is not a defined backend (Performance:Backends).");
        }

        foreach (var (name, cfg) in options.Backends)
        {
            if (!string.IsNullOrWhiteSpace(cfg.Fallback) && !IsDefined(cfg.Fallback))
                throw new InvalidOperationException(
                    $"Performance backend '{name}' has fallback '{cfg.Fallback}' which is not a defined backend (Performance:Backends).");
        }

        services.AddSingleton(options);

        services.AddMemoryCache();                       // shared diff-attribute cache
        services.AddHttpClient();                        // IHttpClientFactory for sidecars
        services.AddHttpClient(OsuFileCache.HttpClientName); // named client for .osu downloads

        // Shared on-disk raw-.osu cache (survives restarts; feeds every mods/ruleset combo, and
        // later serves locally-hosted custom maps). Default dir is overridable via config.
        var osuCacheDirectory = string.IsNullOrWhiteSpace(options.OsuCacheDirectory)
            ? Path.Combine(AppContext.BaseDirectory, "data", "osu-cache")
            : options.OsuCacheDirectory!;
        services.AddSingleton(sp => new OsuFileCache(
            osuCacheDirectory,
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(OsuFileCache.HttpClientName),
            sp.GetRequiredService<ILogger<OsuFileCache>>()));

        services.AddTransient<OfficialPerformanceCalculator>(); // cheap; resolved per pp calc

        services.AddSingleton<IPerformanceBackendRegistry, PerformanceBackendRegistry>();
        services.AddSingleton<IPerformanceCalculator, RoutingPerformanceCalculator>();

        return services;
    }
}
