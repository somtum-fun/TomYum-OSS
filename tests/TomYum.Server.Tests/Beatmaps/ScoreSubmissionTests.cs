using TomYum.Bancho;
using TomYum.Domain.Entities;
using TomYum.Server.Beatmaps;
using Xunit;

namespace TomYum.Server.Tests.Beatmaps;

/// <summary>What stable score submission accepts (ScoreSubmissionEndpoint).</summary>
public class ScoreSubmissionTests
{
    [Theory]
    [InlineData("b20240123.2", false, true)] // today's client, today's URL
    [InlineData("b20240123.2", true, false)] // today's client posting as an old one (to skip today's checks)
    [InlineData("", true, false)] // a session without a build is today's client
    [InlineData("b20140624", true, true)]
    [InlineData("b20151228.3", true, true)]
    [InlineData("b20140624", false, false)]
    [InlineData("b1122", true, true)]
    [InlineData("b337", true, true)]
    public void The_session_build_decides_old_or_todays(string version, bool legacyPath, bool accepted) =>
        Assert.Equal(accepted, ScoreSubmissionEndpoint.SessionMatchesPath(ClientBuild.Parse(version), legacyPath));

    [Theory]
    [InlineData(0, 100, 0, 0, 0, 0, 0, 1_000_000, 150, "S", true)]
    [InlineData(3, 100, 0, 0, 50, 0, 0, 1_000_000, 150, "XH", true)]
    [InlineData(0, 0, 0, 0, 0, 0, 10, 0, 0, "F", true)]
    [InlineData(4, 100, 0, 0, 0, 0, 0, 1_000_000, 150, "S", false)] // no mode 4
    [InlineData(-1, 100, 0, 0, 0, 0, 0, 1_000_000, 150, "S", false)]
    [InlineData(0, -5, 0, 0, 0, 0, 0, 1_000_000, 150, "S", false)]
    [InlineData(0, 100, 0, 0, 0, -1, 0, 1_000_000, 150, "S", false)]
    [InlineData(0, 100, 0, 0, 0, 0, -1, 1_000_000, 150, "S", false)]
    [InlineData(0, 100, 0, 0, 0, 0, 0, -1, 150, "S", false)]
    [InlineData(0, 100, 0, 0, 0, 0, 0, 1_000_000, -1, "S", false)]
    [InlineData(0, 100, 0, 0, 0, 0, 0, 1_000_000, 150, "Z", false)]
    [InlineData(0, 100, 0, 0, 0, 0, 0, 1_000_000, 150, "", false)]
    [InlineData(0, 100, 0, 0, 0, 0, 0, 1_000_000, 150, "s", false)]
    public void Values_a_client_never_sends_are_refused(int mode, int n300, int n100, int n50, int geki, int katu, int miss, long score, int combo, string grade, bool accepted) =>
        Assert.Equal(accepted, ScoreChecks.ValuesProblem(mode, (n300, n100, n50, geki, katu, miss), score, combo, grade) is null);

    [Theory]
    [InlineData(Mods.None, true)]
    [InlineData(Mods.Hidden | Mods.HardRock | Mods.DoubleTime, true)]
    [InlineData(Mods.DoubleTime | Mods.Nightcore, true)]
    [InlineData(Mods.SuddenDeath | Mods.Perfect, true)]
    [InlineData(Mods.Relax | Mods.Hidden, true)]
    [InlineData(Mods.Autopilot | Mods.DoubleTime, true)]
    [InlineData(Mods.Key4 | Mods.KeyCoop, true)]
    [InlineData(Mods.Autoplay, false)]
    [InlineData(Mods.Cinema | Mods.Autoplay, false)]
    [InlineData(Mods.Cinema, false)]
    [InlineData(Mods.Easy | Mods.HardRock, false)]
    [InlineData(Mods.HalfTime | Mods.DoubleTime, false)]
    [InlineData(Mods.HalfTime | Mods.Nightcore, false)] // Nightcore is Double Time
    [InlineData(Mods.Relax | Mods.Autopilot, false)]
    [InlineData(Mods.NoFail | Mods.SuddenDeath, false)]
    [InlineData(Mods.NoFail | Mods.Perfect, false)] // Perfect is Sudden Death
    [InlineData(Mods.Key4 | Mods.Key7, false)]
    public void Mods_osu_wont_pick_together_are_refused(Mods mods, bool accepted) =>
        Assert.Equal(accepted, ScoreChecks.ModsProblem(ScoreChecks.NormalizeMods((int)mods)) is null);

    [Fact]
    public void Nightcore_and_Perfect_come_with_their_base_mods() =>
        Assert.Equal((int)(Mods.Nightcore | Mods.DoubleTime | Mods.Perfect | Mods.SuddenDeath), ScoreChecks.NormalizeMods((int)(Mods.Nightcore | Mods.Perfect)));

    [Theory]
    [InlineData(0, 300, 0, 0, 0, 0, 0, 500, true)] // every object judged, full combo
    [InlineData(0, 290, 5, 3, 0, 0, 2, 120, true)]
    [InlineData(0, 301, 0, 0, 0, 0, 0, 500, false)] // one judgement too many
    [InlineData(0, 300, 0, 0, 0, 0, 1, 500, false)]
    [InlineData(0, 300, 0, 0, 0, 0, 0, 510, true)] // a little over the map's max combo
    [InlineData(0, 300, 0, 0, 0, 0, 0, 511, false)]
    [InlineData(1, 250, 50, 0, 0, 0, 0, 500, true)]
    [InlineData(1, 250, 51, 0, 0, 0, 0, 500, false)]
    [InlineData(3, 100, 50, 0, 100, 50, 0, 100_000, true)] // mania: combo not checked, MAX and 200 are judgements
    [InlineData(3, 100, 50, 0, 100, 51, 0, 10, false)]
    public void A_play_must_fit_its_map(int mode, int n300, int n100, int n50, int geki, int katu, int miss, int combo, bool accepted)
    {
        var map = new Beatmap { Mode = mode, MaxCombo = 500, Details = new BeatmapDetails { CountCircles = 200, CountSliders = 90, CountSpinners = 10 } };
        Assert.Equal(accepted, ScoreChecks.MapProblem(mode, (n300, n100, n50, geki, katu, miss), combo, map) is null);
    }

    [Fact]
    public void What_isnt_known_of_a_map_isnt_checked()
    {
        var counts = (100_000, 0, 0, 0, 0, 0);
        // A convert (taiko on an osu! map), unknown object counts, or an unknown max combo.
        Assert.Null(ScoreChecks.MapProblem(1, counts, 100_000, new Beatmap { Mode = 0, MaxCombo = 500, Details = new BeatmapDetails { CountCircles = 1, CountSliders = 1, CountSpinners = 1 } }));
        Assert.Null(ScoreChecks.MapProblem(0, counts, 100_000, new Beatmap { Mode = 0, MaxCombo = 0 }));
        Assert.Null(ScoreChecks.MapProblem(0, counts, 100_000, new Beatmap { Mode = 0, MaxCombo = 0, Details = new BeatmapDetails { CountCircles = 5 } }));
        // Catch judges every droplet: only its combo is checked.
        Assert.Null(ScoreChecks.MapProblem(2, counts, 400, new Beatmap { Mode = 2, MaxCombo = 500, Details = new BeatmapDetails { CountCircles = 1, CountSliders = 1, CountSpinners = 1 } }));
        Assert.NotNull(ScoreChecks.MapProblem(2, counts, 900, new Beatmap { Mode = 2, MaxCombo = 500 }));
    }
}
