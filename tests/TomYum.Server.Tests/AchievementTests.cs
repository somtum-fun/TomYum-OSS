using TomYum.Bancho;
using TomYum.Server.Achievements;
using TomYum.Server.Bancho;

using Xunit;

namespace TomYum.Server.Tests;

/// <summary>
/// Tests the medal catalog (a clean-room port of bancho.py's vanilla achievements) and the award
/// service: catalog integrity, condition fidelity (banded combos, NoFail-off passes, FC/perfect,
/// exact-vs-bitwise mod intros), and unlock persistence + wire formatting.
/// </summary>
public class AchievementTests
{
    /// <summary>Ids of every catalog medal whose condition the given check satisfies.</summary>
    private static List<int> Matching(MedalCheck check) =>
        MedalCatalog.All.Where(m => m.Condition(check)).Select(m => m.Id).OrderBy(id => id).ToList();

    // ---- Catalog integrity ----

    [Fact]
    public void Catalog_has_the_83_vanilla_medals_with_unique_ids_files_and_names()
    {
        Assert.Equal(83, MedalCatalog.All.Count);
        Assert.Equal(Enumerable.Range(1, 83), MedalCatalog.All.Select(m => m.Id).OrderBy(id => id));
        Assert.Equal(83, MedalCatalog.All.Select(m => m.File).Distinct().Count());
        Assert.Equal(83, MedalCatalog.All.Select(m => m.Name).Distinct().Count());
        // Every medal carries non-empty text (it's shown to players).
        Assert.All(MedalCatalog.All, m =>
        {
            Assert.False(string.IsNullOrWhiteSpace(m.File));
            Assert.False(string.IsNullOrWhiteSpace(m.Name));
            Assert.False(string.IsNullOrWhiteSpace(m.Description));
        });
    }

    // ---- Combo pack (osu!standard only, banded) ----

    [Fact]
    public void Combo_medals_are_banded_so_only_the_reached_tier_unlocks()
    {
        // 2100 combo unlocks ONLY "2000 Combo" (id 24), not the lower tiers.
        Assert.Equal([24], Matching(new MedalCheck(0, Mods.None, MaxCombo: 2100, StarRating: 0.5, Perfect: false)));
        // 600 combo unlocks ONLY "500 Combo" (id 21).
        Assert.Equal([21], Matching(new MedalCheck(0, Mods.None, MaxCombo: 600, StarRating: 0.5, Perfect: false)));
    }

    [Fact]
    public void Combo_medals_are_osu_standard_only()
    {
        // A 600 combo in taiko earns no combo medal (combo pack is mode 0 only).
        Assert.Empty(Matching(new MedalCheck(1, Mods.None, MaxCombo: 600, StarRating: 0.5, Perfect: false)));
    }

    // ---- Skill pass / FC packs ----

    [Fact]
    public void Skill_pass_requires_nofail_off_and_matches_the_star_band()
    {
        // 3.5★ std pass, no mods -> "Building Confidence" (pass tier 3 = id 3).
        Assert.Equal([3], Matching(new MedalCheck(0, Mods.None, MaxCombo: 0, StarRating: 3.5, Perfect: false)));
        // Same play WITH NoFail earns the NoFail intro medal (80) but NOT the pass medal.
        var withNoFail = Matching(new MedalCheck(0, Mods.NoFail, MaxCombo: 0, StarRating: 3.5, Perfect: false));
        Assert.Contains(80, withNoFail);
        Assert.DoesNotContain(3, withNoFail);
    }

    [Fact]
    public void Full_combo_unlocks_both_the_pass_and_fc_tier()
    {
        // A perfect 3.5★ std play unlocks pass-3 (id 3) and fc-3 (id 13).
        Assert.Equal([3, 13], Matching(new MedalCheck(0, Mods.None, MaxCombo: 0, StarRating: 3.5, Perfect: true)));
    }

    [Fact]
    public void Skill_medals_are_per_mode()
    {
        // 5.5★ mania pass -> "Ever Onwards" (mania pass tier 5 = id 61), not an osu!std medal.
        Assert.Equal([61], Matching(new MedalCheck(3, Mods.None, MaxCombo: 0, StarRating: 5.5, Perfect: false)));
    }

    // ---- Mod-introduction pack ----

    [Fact]
    public void SuddenDeath_intro_fires_only_when_it_is_the_only_mod()
    {
        // SD alone -> "Finality" (id 73).
        Assert.Equal([73], Matching(new MedalCheck(0, Mods.SuddenDeath, MaxCombo: 0, StarRating: 0.5, Perfect: false)));
        // SD + Hidden -> the HD intro (74), NOT the SD intro (exact bitmask 32 required).
        var sdPlusHd = Matching(new MedalCheck(0, Mods.SuddenDeath | Mods.Hidden, MaxCombo: 0, StarRating: 0.5, Perfect: false));
        Assert.Contains(74, sdPlusHd);
        Assert.DoesNotContain(73, sdPlusHd);
    }

    [Fact]
    public void Mod_intro_medals_are_bitwise_and_mode_agnostic()
    {
        // Hidden + HardRock in taiko -> both intros (74 Blindsight, 76 Rock Around The Clock).
        Assert.Equal([74, 76], Matching(new MedalCheck(1, Mods.Hidden | Mods.HardRock, MaxCombo: 0, StarRating: 0.5, Perfect: false)));
    }

    [Fact]
    public void A_plain_low_star_pass_earns_nothing()
    {
        Assert.Empty(Matching(new MedalCheck(0, Mods.None, MaxCombo: 0, StarRating: 0.5, Perfect: false)));
    }

    // ---- Award service ----

    [Fact]
    public async Task AwardAsync_unlocks_new_medals_once_then_is_idempotent()
    {
        var repo = new InMemoryUserAchievementRepository();
        var check = new MedalCheck(0, Mods.None, MaxCombo: 600, StarRating: 0.5, Perfect: false); // -> id 21

        var first = await MedalService.AwardAsync(repo, userId: 1, check, DateTime.UtcNow);
        Assert.Equal([21], first.Select(m => m.Id));

        // Re-submitting the same play unlocks nothing new — the medal is already owned.
        var second = await MedalService.AwardAsync(repo, userId: 1, check, DateTime.UtcNow);
        Assert.Empty(second);

        // A different user still gets it.
        var other = await MedalService.AwardAsync(repo, userId: 2, check, DateTime.UtcNow);
        Assert.Equal([21], other.Select(m => m.Id));
    }

    [Fact]
    public void FormatAchievementsNew_uses_the_file_plus_name_plus_desc_slash_format()
    {
        var combo500 = MedalCatalog.All.Single(m => m.Id == 21);
        var combo750 = MedalCatalog.All.Single(m => m.Id == 22);

        Assert.Equal(
            "osu-combo-500+500 Combo+500 big ones! You're moving up in the world!",
            MedalService.FormatAchievementsNew([combo500]));
        Assert.Equal(
            "osu-combo-500+500 Combo+500 big ones! You're moving up in the world!/osu-combo-750+750 Combo+750 notes back to back? Woah.",
            MedalService.FormatAchievementsNew([combo500, combo750]));
        Assert.Equal(string.Empty, MedalService.FormatAchievementsNew([]));
    }
}
