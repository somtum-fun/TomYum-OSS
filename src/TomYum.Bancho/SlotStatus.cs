namespace TomYum.Bancho;

/// <summary>
/// Per-slot state in a multiplayer match. Values match the osu!stable wire protocol
/// (confirmed against bancho.py <c>SlotStatus</c> and BanchoNET).
/// </summary>
[Flags]
public enum SlotStatus : byte
{
    Open     = 1,
    Locked   = 2,
    NotReady = 4,
    Ready    = 8,
    NoMap    = 16,
    Playing  = 32,
    Complete = 64,
    Quit     = 128,

    /// <summary>Mask for statuses that indicate a player occupies the slot.</summary>
    HasPlayer = NotReady | Ready | NoMap | Playing | Complete,
}
