using System.Collections.Concurrent;

using StackExchange.Redis;

using TomYum.Plugins.Abstractions;

namespace TomYum.Server.Plugins;

/// <summary>
/// A plugin's <see cref="IPluginStateStore"/>: Redis keys <c>tomyum:plugin-state:&lt;plugin id&gt;:&lt;key&gt;</c> when
/// the server has Redis, otherwise a dictionary in memory (lost on restart).
/// </summary>
internal sealed class PluginStateStore : IPluginStateStore
{
    private readonly string _prefix;
    private readonly IConnectionMultiplexer? _redis;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, (string Value, DateTimeOffset? Expires)> _memory = new(StringComparer.Ordinal);

    public PluginStateStore(string pluginId, IConnectionMultiplexer? redis, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(pluginId);
        _prefix = "tomyum:plugin-state:" + pluginId.ToLowerInvariant() + ":";
        _redis = redis;
        _time = time ?? TimeProvider.System;
    }

    public async Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (_redis is not null)
        {
            var value = await _redis.GetDatabase().StringGetAsync(_prefix + key).ConfigureAwait(false);
            return value.HasValue ? value.ToString() : null;
        }

        if (!_memory.TryGetValue(key, out var entry))
            return null;
        if (entry.Expires is { } expires && expires <= _time.GetUtcNow())
        {
            _memory.TryRemove(new KeyValuePair<string, (string, DateTimeOffset?)>(key, entry));
            return null;
        }

        return entry.Value;
    }

    public async Task SetAsync(string key, string value, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        if (ttl is { } t && t <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(ttl), ttl, "The expiry has to be in the future.");

        if (_redis is not null)
        {
            await _redis.GetDatabase().StringSetAsync(_prefix + key, value, ttl).ConfigureAwait(false);
            return;
        }

        _memory[key] = (value, ttl is { } d ? _time.GetUtcNow() + d : null);
    }

    public async Task<bool> RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (_redis is not null)
            return await _redis.GetDatabase().KeyDeleteAsync(_prefix + key).ConfigureAwait(false);
        return _memory.TryRemove(key, out _);
    }
}
