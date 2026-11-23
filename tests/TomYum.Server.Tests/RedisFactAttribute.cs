using Xunit;

namespace TomYum.Server.Tests;

/// <summary>Needs Redis in <c>TOMYUM_TEST_REDIS</c> (e.g. <c>127.0.0.1:6379</c>); skipped otherwise.</summary>
public sealed class RedisFactAttribute : FactAttribute
{
    public const string EnvVar = "TOMYUM_TEST_REDIS";

    /// <summary>The test collection of everything that uses these Redis keys.</summary>
    public const string Collection = "redis";

    public RedisFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnvVar)))
            Skip = $"Set {EnvVar} to a throwaway Redis (host:port) to run the Redis tests.";
    }
}
