using TomYum.Bancho;

namespace TomYum.Server.Bancho;

/// <summary>One of the 16 slots in a multiplayer match.</summary>
public sealed class MatchSlot
{
    /// <summary>Occupying player's user id, or 0 when the slot has no player.</summary>
    public int UserId { get; set; }

    /// <summary>Current slot state.</summary>
    public SlotStatus Status { get; set; } = SlotStatus.Open;

    /// <summary>Team assignment (0 = neutral, 1 = blue, 2 = red).</summary>
    public byte Team { get; set; }

    /// <summary>Per-slot mods (only relevant when the match is in free-mods mode).</summary>
    public uint Mods { get; set; }

    /// <summary>Whether this slot is occupied by a player.</summary>
    public bool HasPlayer => (Status & SlotStatus.HasPlayer) != 0;

    /// <summary>Puts a player into this slot, marking them as not-ready.</summary>
    public void Join(int userId)
    {
        UserId = userId;
        Status = SlotStatus.NotReady;
    }

    /// <summary>Removes the player from this slot, resetting it to open.</summary>
    public void Reset()
    {
        UserId = 0;
        Status = SlotStatus.Open;
        Team   = 0;
        Mods   = 0;
    }
}
