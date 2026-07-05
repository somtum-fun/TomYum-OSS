using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace TomYum.Caching;

public static class CachingServiceCollectionExtensions
{
    public static IServiceCollection AddTomYumCaching(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration[$"{RedisOptions.SectionName}:{nameof(RedisOptions.Configuration)}"]
                               ?? "localhost:6379";

        services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(connectionString));
        services.AddSingleton<IScoreQueue, RedisScoreQueue>();
        return services;
    }
}
