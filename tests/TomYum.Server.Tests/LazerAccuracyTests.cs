using TomYum.Domain.Entities;
using TomYum.Performance;
using TomYum.Server.Admin;
using Xunit;

namespace TomYum.Server.Tests;

/// <summary>A lazer Relax play's accuracy as lazer shows it (slider ticks and ends count), not from its 300/100/50s alone.</summary>
public class LazerAccuracyTests
{
    // Lilily's Koko Soko [INCREDIBILIS] +DT(1.12x) RX: lazer (and the website) gave 0.906784; its
    // 300/100/50/misses alone give 0.89761, which the Relax leaderboard showed.
    private const string Play = """
        {"statistics":{"ok":83,"meh":16,"miss":67,"great":1159,"ignore_hit":289,"ignore_miss":5,"large_tick_hit":10,"slider_tail_hit":288},
         "maximum_statistics":{"great":1325,"ignore_hit":291,"large_tick_hit":10,"slider_tail_hit":291},
         "mods":[{"acronym":"DT","settings":{"speed_change":1.12}},{"acronym":"RX"}]}
        """;

    [Fact]
    public void LazerPlay_AsLazerShowsIt()
    {
        Assert.Equal(0.906784, OfficialPerformanceCalculator.LazerAccuracy(0, Play)!.Value, 6);
    }

    [Fact]
    public void RelaxPlay_FromLazer_UsesLazersAccuracy_StableUsesItsCounts()
    {
        var lazer = new Score { Mode = 4, FromLazer = true, LazerScore = Play, Count300 = 1159, Count100 = 83, Count50 = 16, CountMiss = 67 };
        var stable = new Score { Mode = 4, FromLazer = false, Count300 = 1159, Count100 = 83, Count50 = 16, CountMiss = 67 };

        Assert.Equal(0.906784, AdminModeScoresEndpoints.Accuracy(lazer), 6);
        Assert.Equal(0.897610, AdminModeScoresEndpoints.Accuracy(stable), 6);
    }

    [Fact]
    public void NoJudgements_Null()
    {
        Assert.Null(OfficialPerformanceCalculator.LazerAccuracy(0, null));
        Assert.Null(OfficialPerformanceCalculator.LazerAccuracy(0, """{"statistics":{"great":1}}"""));
    }
}
