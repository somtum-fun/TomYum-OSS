using System.Collections.Concurrent;
using TomYum.Bancho;

namespace TomYum.Server.Bancho;

/// <summary>
/// In-memory registry of all live multiplayer match rooms, analogous to
/// <see cref="PlayerRegistry"/>. Assigns monotonically increasing ushort match ids.
/// Thread-safe for concurrent add/remove; per-match mutation uses the match's own
/// <see cref="Match.SyncRoot"/>.
/// </summary>
public sealed class MatchRegistry
{
    private readonly ConcurrentDictionary<ushort, Match> _matches = new();
    private int _nextId = 1;

    /// <summary>Where new rooms' history is written; none in tests.</summary>
    public MatchHistory? History { get; init; }

    /// <summary>Number of active matches.</summary>
    public int Count => _matches.Count;

    /// <summary>Snapshot of all active matches.</summary>
    public IReadOnlyCollection<Match> All => _matches.Values.ToArray();

    /// <summary>Creates a new match, registers it, and returns it.</summary>
    public Match Create(int hostUserId, string name, string password,
        string mapName, int mapId, string mapMd5,
        byte mode, MatchWinCondition winCondition, MatchTeamType teamType,
        bool freeMods, uint mods, int seed)
    {
        var id = (ushort)Interlocked.Increment(ref _nextId);
        var match = new Match(id, hostUserId, name, password, mapName, mapId, mapMd5,
            mode, winCondition, teamType, freeMods, mods, seed) { History = History };
        _matches[id] = match;
        return match;
    }

    /// <summary>Looks up a match by id, or <c>null</c>.</summary>
    public Match? Get(ushort id) =>
        _matches.TryGetValue(id, out var m) ? m : null;

    /// <summary>Returns the match a player is in, or <c>null</c>.</summary>
    public Match? GetByUserId(int userId)
    {
        foreach (var m in _matches.Values)
        {
            lock (m.SyncRoot)
            {
                if (m.IndexOf(userId) >= 0) return m;
            }
        }
        return null;
    }

    /// <summary>Removes the match. Idempotent.</summary>
    public void Remove(ushort id) => _matches.TryRemove(id, out _);
}
