using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace TomYum.Caching;

public static class CachingServiceCollectionExtensions
{
    /// <summary>
    /// Redis when <c>Redis:Configuration</c> is set; otherwise none, and everything that would use it keeps its state in
    /// memory (one TomYum on its own). The cluster and the website (osu-web mode) need it: see <see cref="IsRedisConfigured"/>.
    /// </summary>
    public static IServiceCollection AddTomYumCaching(this IServiceCollection services, IConfiguration configuration)
    {
        if (!IsRedisConfigured(configuration))
        {
            services.AddSingleton<IScoreQueue, NoScoreQueue>();
            return services;
        }

        var connectionString = configuration[$"{RedisOptions.SectionName}:{nameof(RedisOptions.Configuration)}"]!;
        services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(connectionString));
        services.AddSingleton<IScoreQueue, RedisScoreQueue>();
        return services;
    }

    /// <summary>Whether <c>Redis:Configuration</c> names a Redis (empty or <c>none</c>: no Redis).</summary>
    public static bool IsRedisConfigured(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var value = configuration[$"{RedisOptions.SectionName}:{nameof(RedisOptions.Configuration)}"];
        return !string.IsNullOrWhiteSpace(value) && !value.Trim().Equals("none", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Without Redis there's no score stream (its reader, services/TomYum.ScoreQueue, needs Redis too).</summary>
internal sealed class NoScoreQueue : IScoreQueue
{
    public Task EnqueueAsync(long scoreId, CancellationToken ct = default) => Task.CompletedTask;
}
