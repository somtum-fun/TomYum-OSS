namespace TomYum.Persistence;

/// <summary>
/// The modes a pp rework is running in (TomYum's ids: 0-3 vanilla, 4-6 Relax, 8 Autopilot). Their
/// rankings stay as they were until it's done: global ranks aren't recomputed and new scores' pp
/// waits. Kept up to date by the server's rework service.
/// </summary>
public static class RankingPause
{
    private static volatile int[] _modes = [];

    public static bool IsPaused(int mode) => Array.IndexOf(_modes, mode) >= 0;

    public static IReadOnlyList<int> Modes => _modes;

    public static void Set(IEnumerable<int> modes) => _modes = [.. modes.Distinct().Order()];
}
