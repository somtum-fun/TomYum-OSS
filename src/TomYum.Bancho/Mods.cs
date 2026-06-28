namespace TomYum.Bancho;

/// <summary>
/// osu!stable gameplay mods as the i32 bitmask used across the bancho protocol
/// (CHANGE_ACTION, USER_STATS, score submission). Values are the canonical osu! mod
/// bits, stable across the ecosystem. See <c>memories/protocols/bancho-stable.md</c>.
/// </summary>
[Flags]
public enum Mods
{
    /// <summary>No mods.</summary>
    None = 0,

    /// <summary>No-fail.</summary>
    NoFail = 1 << 0,

    /// <summary>Easy.</summary>
    Easy = 1 << 1,

    /// <summary>Touch device (formerly No-video).</summary>
    TouchDevice = 1 << 2,

    /// <summary>Hidden.</summary>
    Hidden = 1 << 3,

    /// <summary>Hard-rock.</summary>
    HardRock = 1 << 4,

    /// <summary>Sudden-death.</summary>
    SuddenDeath = 1 << 5,

    /// <summary>Double-time.</summary>
    DoubleTime = 1 << 6,

    /// <summary>Relax.</summary>
    Relax = 1 << 7,

    /// <summary>Half-time.</summary>
    HalfTime = 1 << 8,

    /// <summary>Nightcore (always set together with <see cref="DoubleTime"/>).</summary>
    Nightcore = 1 << 9,

    /// <summary>Flashlight.</summary>
    Flashlight = 1 << 10,

    /// <summary>Auto-play.</summary>
    Autoplay = 1 << 11,

    /// <summary>Spun-out.</summary>
    SpunOut = 1 << 12,

    /// <summary>Relax2 / Autopilot.</summary>
    Autopilot = 1 << 13,

    /// <summary>Perfect (always set together with <see cref="SuddenDeath"/>).</summary>
    Perfect = 1 << 14,

    /// <summary>Key4 (mania).</summary>
    Key4 = 1 << 15,

    /// <summary>Key5 (mania).</summary>
    Key5 = 1 << 16,

    /// <summary>Key6 (mania).</summary>
    Key6 = 1 << 17,

    /// <summary>Key7 (mania).</summary>
    Key7 = 1 << 18,

    /// <summary>Key8 (mania).</summary>
    Key8 = 1 << 19,

    /// <summary>Fade-in (mania).</summary>
    FadeIn = 1 << 20,

    /// <summary>Random (mania).</summary>
    Random = 1 << 21,

    /// <summary>Cinema (formerly Last-mod / Auto-cinema).</summary>
    Cinema = 1 << 22,

    /// <summary>Target-practice.</summary>
    TargetPractice = 1 << 23,

    /// <summary>Key9 (mania).</summary>
    Key9 = 1 << 24,

    /// <summary>Key-coop (mania).</summary>
    KeyCoop = 1 << 25,

    /// <summary>Key1 (mania).</summary>
    Key1 = 1 << 26,

    /// <summary>Key3 (mania).</summary>
    Key3 = 1 << 27,

    /// <summary>Key2 (mania).</summary>
    Key2 = 1 << 28,

    /// <summary>Score-V2.</summary>
    ScoreV2 = 1 << 29,

    /// <summary>Mirror (mania).</summary>
    Mirror = 1 << 30,
}
