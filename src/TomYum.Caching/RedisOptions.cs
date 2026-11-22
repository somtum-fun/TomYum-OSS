namespace TomYum.Caching;

public sealed class RedisOptions
{
    public const string SectionName = "Redis";

    /// <summary>A StackExchange.Redis configuration (e.g. <c>redis:6379</c>); empty or <c>none</c>: no Redis, state in memory.</summary>
    public string Configuration { get; set; } = string.Empty;
}
