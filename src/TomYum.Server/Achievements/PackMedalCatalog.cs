using System.Text.Json;
using System.Text.Json.Serialization;

namespace TomYum.Server.Achievements;

/// <summary>One osu! beatmap pack a pack medal accepts: every set in it must be passed.</summary>
/// <param name="Tag">The pack's tag on osu! (e.g. <c>T2</c>).</param>
/// <param name="Ruleset">The pack's ruleset, or null: then each map in its own ruleset (no converts).</param>
/// <param name="Sets">Every beatmap set in the pack, as on osu!.</param>
public sealed record PackMedalPack(string Tag, int? Ruleset, IReadOnlyList<int> Sets);

/// <summary>A pack medal: earned by passing every set of any one of its packs.</summary>
/// <param name="Id">osu!'s medal id.</param>
/// <param name="NoReductionMods">Challenge packs: plays with difficulty reduction (or unranked) mods don't count.</param>
/// <param name="Packs">The packs any one of which earns it (several for per-ruleset spotlight and Loved packs).</param>
public sealed record PackMedal(int Id, bool NoReductionMods, IReadOnlyList<PackMedalPack> Packs);

/// <summary>
/// osu!'s pack medals (the ones ppy's score processor knows) and the full contents of their packs,
/// from <c>PackMedals.json</c>: medal id → pack tags → beatmap set ids, taken from osu!'s pack
/// pages. TomYum awards these itself (<see cref="PackMedalAwarder"/>) for stable and lazer plays,
/// and only for complete packs: a set this server doesn't have can't be passed, so the medal waits
/// until the whole pack is here.
/// </summary>
public static class PackMedalCatalog
{
    private static IReadOnlyList<PackMedal>? _all;

    public static IReadOnlyList<PackMedal> All => _all ??= Load();

    /// <summary>The medals with a pack that contains <paramref name="beatmapsetId"/>.</summary>
    public static IEnumerable<PackMedal> Containing(int beatmapsetId) =>
        All.Where(m => m.Packs.Any(p => p.Sets.Contains(beatmapsetId)));

    private static List<PackMedal> Load()
    {
        using var stream = typeof(PackMedalCatalog).Assembly.GetManifestResourceStream("TomYum.Server.Achievements.PackMedals.json")
            ?? throw new InvalidOperationException("PackMedals.json isn't embedded in the server.");
        var raw = JsonSerializer.Deserialize<Dictionary<string, RawMedal>>(stream)
            ?? throw new InvalidOperationException("PackMedals.json is empty.");
        return raw
            .Select(m => new PackMedal(
                int.Parse(m.Key, System.Globalization.CultureInfo.InvariantCulture),
                m.Value.NoReductionMods,
                m.Value.Packs.Select(p => new PackMedalPack(p.Key, p.Value.Ruleset, p.Value.Sets)).ToList()))
            .OrderBy(m => m.Id)
            .ToList();
    }

    private sealed class RawMedal
    {
        [JsonPropertyName("noReductionMods")]
        public bool NoReductionMods { get; set; }

        [JsonPropertyName("packs")]
        public Dictionary<string, RawPack> Packs { get; set; } = [];
    }

    private sealed class RawPack
    {
        [JsonPropertyName("ruleset")]
        public int? Ruleset { get; set; }

        [JsonPropertyName("sets")]
        public List<int> Sets { get; set; } = [];
    }
}
