using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using TomYum.Caching;
using Xunit;

namespace TomYum.Server.Tests;

/// <summary>One TomYum without Redis keeps its state in memory: nothing Redis is registered.</summary>
public class RedisOptionalTests
{
    private static IConfiguration Config(string? redis) => new ConfigurationBuilder()
        .AddInMemoryCollection(redis is null ? [] : [new("Redis:Configuration", redis)]).Build();

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData(" none ", false)]
    [InlineData("redis:6379", true)]
    public void IsRedisConfigured(string? value, bool expected) =>
        Assert.Equal(expected, CachingServiceCollectionExtensions.IsRedisConfigured(Config(value)));

    [Fact]
    public async Task WithoutRedis_NoConnection_AndScoresQueueNowhere()
    {
        var services = new ServiceCollection().AddTomYumCaching(Config(null)).BuildServiceProvider();
        Assert.Null(services.GetService<IConnectionMultiplexer>());
        await services.GetRequiredService<IScoreQueue>().EnqueueAsync(1);
    }
}
