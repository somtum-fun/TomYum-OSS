using System.Collections.Concurrent;

namespace TomYum.Server.Bancho;

/// <summary>
/// A server-side chat channel: a named room with a topic, an auto-join flag, and the set of
/// online players currently in it. Public chat is fanned out to every member except the
/// sender. Membership is keyed by user id so a player appears at most once.
/// </summary>
/// <remarks>
/// In-memory single-node state, mirroring <see cref="PlayerRegistry"/>. Cross-node channel
/// fan-out over Redis pub/sub is a later slice (ADR-0003). Read/write privilege gating and
/// the special per-instance <c>#spectator</c>/<c>#multiplayer</c> channels are also later;
/// every channel is world-readable, and world-writable unless <see cref="StaffOnly"/>.
/// </remarks>
public sealed class ChatChannel
{
    private readonly ConcurrentDictionary<int, OnlinePlayer> _members = new();

    /// <summary>Creates a channel with the given <paramref name="name"/> (including the leading <c>#</c>).</summary>
    public ChatChannel(string name, string topic, bool autoJoin)
    {
        Name = name;
        Topic = topic;
        AutoJoin = autoJoin;
    }

    /// <summary>Channel name including the leading <c>#</c> (e.g. <c>#osu</c>).</summary>
    public string Name { get; }

    /// <summary>Human-readable topic shown in the client's channel list.</summary>
    public string Topic { get; }

    /// <summary>Whether the client should auto-open this channel on login (e.g. <c>#osu</c>).</summary>
    public bool AutoJoin { get; }

    /// <summary>Only staff (and the server itself) may post in it, from the game or IRC (e.g. <c>#announce</c>).</summary>
    public bool StaffOnly { get; init; }

    /// <summary>Current member count.</summary>
    public int MemberCount => _members.Count;

    /// <summary>A snapshot of current members (safe to enumerate while others mutate).</summary>
    public IReadOnlyCollection<OnlinePlayer> Members => _members.Values.ToArray();

    /// <summary>Adds <paramref name="player"/> to the channel. Idempotent.</summary>
    public void Join(OnlinePlayer player)
    {
        ArgumentNullException.ThrowIfNull(player);
        _members[player.UserId] = player;
    }

    /// <summary>Removes the player with <paramref name="userId"/> from the channel. Idempotent.</summary>
    public void Leave(int userId) => _members.TryRemove(userId, out _);

    /// <summary>Whether the player with <paramref name="userId"/> is a member.</summary>
    public bool Contains(int userId) => _members.ContainsKey(userId);
}
