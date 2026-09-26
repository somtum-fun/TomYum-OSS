using System.Collections.Concurrent;

namespace TomYum.Server.Bancho;

/// <summary>
/// In-memory registry of the server's chat channels, keyed by name (case-insensitive). Seeds
/// the default channels (<c>#osu</c>, <c>#announce</c>) at construction. Mirrors
/// <see cref="PlayerRegistry"/>: single-node online state, with cross-node fan-out deferred to
/// a later slice (ADR-0003).
/// </summary>
public sealed class ChannelRegistry
{
    private readonly ConcurrentDictionary<string, ChatChannel> _channels =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Creates the registry with the default channels, mirroring bancho.py's standard set:
    /// <c>#osu</c> and <c>#announce</c> auto-join on login; <c>#lobby</c>, <c>#english</c>, and
    /// <c>#help</c> are visible and joinable but not auto-opened.
    /// </summary>
    public ChannelRegistry()
    {
        Add(new ChatChannel("#osu", "Main chat — welcome to TomYum!", autoJoin: true));
        Add(new ChatChannel("#announce", "Server announcements and ranked plays.", autoJoin: true));
        Add(new ChatChannel("#lobby", "Find and create multiplayer matches.", autoJoin: false));
        Add(new ChatChannel("#english", "English-speaking chat.", autoJoin: false));
        Add(new ChatChannel("#help", "Ask for help — staff and players answer here.", autoJoin: false));
    }

    /// <summary>A snapshot of every channel (safe to enumerate while others mutate).</summary>
    public IReadOnlyCollection<ChatChannel> All => _channels.Values.ToArray();

    /// <summary>Adds (or replaces) a channel.</summary>
    public void Add(ChatChannel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        _channels[channel.Name] = channel;
    }

    /// <summary>Looks up a channel by name (case-insensitive), or <c>null</c> when not found.</summary>
    public ChatChannel? Get(string name) =>
        _channels.TryGetValue(name, out var channel) ? channel : null;

    /// <summary>Removes the player with <paramref name="userId"/> from every channel (logout cleanup).</summary>
    public void RemoveFromAll(int userId)
    {
        foreach (var channel in _channels.Values)
        {
            channel.Leave(userId);
        }
    }
}
