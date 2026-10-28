using System.Runtime.CompilerServices;

namespace TomYum.Server.Beatmaps;

/// <summary>
/// Tells a caller whether an osu! API lookup's <c>null</c> was osu! not answering (a timeout, a
/// network error) rather than osu! not knowing the map, without changing <see cref="IOsuApiClient"/>:
/// <see cref="BeatmapService"/> doesn't remember a failed lookup as an unsubmitted map.
/// </summary>
public static class OsuApiFailures
{
    private static readonly AsyncLocal<StrongBox<bool>?> Current = new();

    /// <summary>Until disposed, lookups on this async flow set <paramref name="failed"/> when they fail.</summary>
    public static IDisposable Watch(StrongBox<bool> failed)
    {
        var previous = Current.Value;
        Current.Value = failed;
        return new Restore(previous);
    }

    /// <summary>A lookup failed (called by <see cref="OsuApiClient"/>).</summary>
    internal static void Report()
    {
        if (Current.Value is { } failed)
            failed.Value = true;
    }

    private sealed class Restore(StrongBox<bool>? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}
