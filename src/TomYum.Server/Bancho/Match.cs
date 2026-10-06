using TomYum.Bancho;

namespace TomYum.Server.Bancho;

/// <summary>
/// In-memory server-side state for one multiplayer match room. Thread-safe via a lock
/// (one lock per match; concurrency is low since multiple clients rarely hit the same
/// match simultaneously). The lock is exposed as <see cref="SyncRoot"/> for handlers
/// that need an atomic read-then-modify.
/// </summary>
public sealed class Match
{
    private const int SlotCount = 16;

    /// <summary>Creates a match with the given id and initial settings supplied by the host.</summary>
    public Match(ushort id, int hostUserId, string name, string password,
        string mapName, int mapId, string mapMd5,
        byte mode, MatchWinCondition winCondition, MatchTeamType teamType,
        bool freeMods, uint mods, int seed)
    {
        Id           = id;
        HostUserId   = hostUserId;
        Name         = name;
        Password     = password;
        MapName      = mapName;
        MapId        = mapId;
        MapMd5       = mapMd5;
        Mode         = mode;
        WinCondition = winCondition;
        TeamType     = teamType;
        FreeMods     = freeMods;
        Mods         = mods;
        Seed         = seed;
        for (var i = 0; i < SlotCount; i++)
            Slots[i] = new MatchSlot();
    }

    /// <summary>Where the room's history is written (osu-web's match pages); none in tests.</summary>
    public MatchHistory? History { get; set; }

    /// <summary>Lock object for atomic read-then-modify operations.</summary>
    public object SyncRoot { get; } = new();

    public ushort Id            { get; }
    public int    HostUserId    { get; set; }
    public string Name          { get; set; }
    public string Password      { get; set; }
    public string MapName       { get; set; }
    public int    MapId         { get; set; }
    public string MapMd5        { get; set; }
    public uint   Mods          { get; set; }
    public byte   Mode          { get; set; }
    public MatchWinCondition WinCondition { get; set; }
    public MatchTeamType     TeamType     { get; set; }
    public bool   FreeMods      { get; set; }
    public int    Seed          { get; set; }
    public bool   InProgress    { get; set; }

    /// <summary>Tournament client windows watching the match: they get its packets like its players.</summary>
    public HashSet<OnlinePlayer> TournamentWatchers { get; } = [];

    /// <summary>The per-match chat channel (<c>#multiplayer</c> from the client's view).</summary>
    public ChatChannel Channel { get; } = new("#multiplayer", "Match chat", autoJoin: false);

    /// <summary>Non-null while a !mp countdown/start timer is running; cancel to abort.</summary>
    public CancellationTokenSource? CountdownCts { get; set; }

    /// <summary>User ids of players who have finished loading the map (cleared after ALL_PLAYERS_LOADED).</summary>
    public HashSet<int> LoadedPlayers { get; } = [];

    /// <summary>User ids of players who have sent a skip request (cleared after MATCH_SKIP).</summary>
    public HashSet<int> SkipRequests  { get; } = [];

    /// <summary>The 16 match slots (always non-null after construction).</summary>
    public MatchSlot[] Slots { get; } = new MatchSlot[SlotCount];

    // ---- slot helpers ----

    /// <summary>Returns the slot index occupied by <paramref name="userId"/>, or -1.</summary>
    public int IndexOf(int userId)
    {
        for (var i = 0; i < Slots.Length; i++)
            if (Slots[i].UserId == userId) return i;
        return -1;
    }

    /// <summary>Returns the first open slot index, or -1 when all slots are occupied/locked.</summary>
    public int FirstOpenSlot(int slotCount = SlotCount)
    {
        // An old build sees only its own number of slots (docs/old-clients.md): it's placed among those.
        for (var i = 0; i < Math.Min(slotCount, Slots.Length); i++)
            if (Slots[i].Status == SlotStatus.Open) return i;
        return -1;
    }

    /// <summary>Returns ids of all players currently in this match.</summary>
    public IReadOnlyList<int> PlayerIds()
    {
        var ids = new List<int>(Slots.Length);
        foreach (var s in Slots)
            if (s.HasPlayer) ids.Add(s.UserId);
        return ids;
    }

    // ---- state helpers ----

    /// <summary>True when every player with a map is ready (or playing/complete).</summary>
    public bool AllReady()
    {
        foreach (var s in Slots)
        {
            if (s.Status == SlotStatus.NotReady) return false;
        }
        return true;
    }

    /// <summary>True when all playing slots are in Complete state.</summary>
    public bool AllComplete()
    {
        foreach (var s in Slots)
        {
            if (s.Status == SlotStatus.Playing) return false;
        }
        return true;
    }

    /// <summary>Counts how many slots are still in Playing state.</summary>
    public int PlayingCount()
    {
        var n = 0;
        foreach (var s in Slots)
            if (s.Status == SlotStatus.Playing) n++;
        return n;
    }
}
