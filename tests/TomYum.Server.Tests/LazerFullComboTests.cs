using System.Text.Json.Nodes;
using TomYum.Performance;
using Xunit;

namespace TomYum.Server.Tests;

/// <summary><see cref="LazerScoreJson.AsFullCombo"/>: a play as a full combo, for "pp if FC".</summary>
public class LazerFullComboTests
{
    [Fact]
    public void Misses_AreGreats_AndEveryTickIsHit()
    {
        const string play = """
            {"mods":[{"acronym":"DT","settings":{"speed_change":1.36}},{"acronym":"RX"}],
             "statistics":{"great":409,"ok":86,"meh":3,"miss":15,"large_tick_hit":50,"large_tick_miss":2,"slider_tail_hit":90},
             "maximum_statistics":{"great":513,"large_tick_hit":52,"slider_tail_hit":95}}
            """;

        var full = JsonNode.Parse(LazerScoreJson.AsFullCombo(play)!)!;
        var statistics = full["statistics"]!.AsObject();

        Assert.Equal(424, (int)statistics["great"]!);
        Assert.Equal(86, (int)statistics["ok"]!);
        Assert.Equal(3, (int)statistics["meh"]!);
        Assert.False(statistics.ContainsKey("miss"));
        Assert.Equal(52, (int)statistics["large_tick_hit"]!);
        Assert.False(statistics.ContainsKey("large_tick_miss"));
        Assert.Equal(95, (int)statistics["slider_tail_hit"]!);
        // The mods keep their settings.
        Assert.Equal(1.36, (double)full["mods"]![0]!["settings"]!["speed_change"]!);
    }

    [Fact]
    public void NoData_StaysNone() => Assert.Null(LazerScoreJson.AsFullCombo(null));
}
