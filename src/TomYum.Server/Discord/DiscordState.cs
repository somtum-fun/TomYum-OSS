using System.Collections.Concurrent;
using StackExchange.Redis;

namespace TomYum.Server.Discord;

/// <summary>
/// Small things the bot remembers across restarts (the status message, the map of the day,
/// milestones reached): Redis keys under <c>tomyum:discord:</c>, or memory when there's no Redis.
/// </summary>
public sealed class DiscordState
{
    private const string Prefix = "tomyum:discord:";

    private readonly IConnectionMultiplexer? _redis;
    private readonly ConcurrentDictionary<string, string> _memory = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, string>> _memoryHashes = new(StringComparer.Ordinal);

    public DiscordState(IConnectionMultiplexer? redis = null) => _redis = redis;

    public async Task<string?> GetAsync(string key)
    {
        if (_redis is null)
            return _memory.TryGetValue(key, out var value) ? value : null;
        var stored = await _redis.GetDatabase().StringGetAsync(Prefix + key).ConfigureAwait(false);
        return stored.IsNull ? null : stored.ToString();
    }

    public async Task SetAsync(string key, string value)
    {
        if (_redis is null)
        {
            _memory[key] = value;
            return;
        }
        await _redis.GetDatabase().StringSetAsync(Prefix + key, value).ConfigureAwait(false);
    }

    public async Task<string?> GetFieldAsync(string key, string field)
    {
        if (_redis is null)
            return _memoryHashes.TryGetValue(key, out var hash) && hash.TryGetValue(field, out var value) ? value : null;
        var stored = await _redis.GetDatabase().HashGetAsync(Prefix + key, field).ConfigureAwait(false);
        return stored.IsNull ? null : stored.ToString();
    }

    public async Task SetFieldAsync(string key, string field, string value)
    {
        if (_redis is null)
        {
            _memoryHashes.GetOrAdd(key, _ => new ConcurrentDictionary<string, string>(StringComparer.Ordinal))[field] = value;
            return;
        }
        await _redis.GetDatabase().HashSetAsync(Prefix + key, field, value).ConfigureAwait(false);
    }

    public async Task RemoveFieldAsync(string key, string field)
    {
        if (_redis is null)
        {
            if (_memoryHashes.TryGetValue(key, out var hash))
                hash.TryRemove(field, out _);
            return;
        }
        await _redis.GetDatabase().HashDeleteAsync(Prefix + key, field).ConfigureAwait(false);
    }

    public async Task<IReadOnlyDictionary<string, string>> GetFieldsAsync(string key)
    {
        if (_redis is null)
        {
            return _memoryHashes.TryGetValue(key, out var hash)
                ? new Dictionary<string, string>(hash, StringComparer.Ordinal)
                : new Dictionary<string, string>(StringComparer.Ordinal);
        }
        var entries = await _redis.GetDatabase().HashGetAllAsync(Prefix + key).ConfigureAwait(false);
        return entries.ToDictionary(e => e.Name.ToString(), e => e.Value.ToString(), StringComparer.Ordinal);
    }
}
