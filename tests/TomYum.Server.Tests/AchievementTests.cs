using TomYum.Bancho;
using TomYum.Domain.Entities;
using TomYum.Domain.Enums;
using TomYum.Server.Achievements;
using TomYum.Server.Bancho;

using Xunit;

namespace TomYum.Server.Tests;

/// <summary>
/// The stable medal catalog (osu!'s ids and rules) and the award service: skill medals on the
/// mod-adjusted star rating of the ruleset played, no difficulty reduction, ranked/approved maps
/// only; combo milestones; single-mod introductions.
/// </summary>
public class AchievementTests
{
    private static List<int> Matching(MedalCheck check) =>
        MedalCatalog.All.Where(m => m.Condition(check)).Select(m => m.Id).OrderBy(id => id).ToList();

    private static MedalCheck Play(int mode = 0, Mods mods = Mods.None, int combo = 0, double? stars = 0.5, bool perfect = false,
        RankedStatus status = RankedStatus.Ranked, int beatmapId = 1) =>
        new(mode, mods, combo, stars, perfect, status, beatmapId);

    [Fact]
    public void Catalog_UsesOsusIds_OncePerMedal()
    {
        Assert.Equal(83, MedalCatalog.All.Count);
        Assert.Equal(83, MedalCatalog.All.Select(m => m.Id).Distinct().Count());
        Assert.Equal(83, MedalCatalog.All.Select(m => m.File).Distinct().Count());
        Assert.All(MedalCatalog.All, m => Assert.False(string.IsNullOrWhiteSpace(m.Name) || string.IsNullOrWhiteSpace(m.Description)));
        // A few fixed points of osu!'s numbering.
        Assert.Equal("osu-skill-pass-1", MedalCatalog.All.Single(m => m.Id == 55).File);
        Assert.Equal("osu-skill-pass-9", MedalCatalog.All.Single(m => m.Id == 242).File);
        Assert.Equal("mania-skill-fc-8", MedalCatalog.All.Single(m => m.Id == 118).File);
        Assert.Equal("osu-combo-750", MedalCatalog.All.Single(m => m.Id == 3).File);
        Assert.Equal("all-intro-spunout", MedalCatalog.All.Single(m => m.Id == 131).File);
        // Every medal's earlier (bancho.py) id is known, one to one.
        Assert.Equal(MedalCatalog.All.Select(m => m.Id).Order(), MedalCatalog.LegacyIds.Keys.Order());
        Assert.Equal(Enumerable.Range(1, 83), MedalCatalog.LegacyIds.Values.Order());
    }

    [Fact]
    public void Combo_medals_are_every_milestone_reached_in_osu_standard()
    {
        Assert.Equal([1, 3, 4, 5], Matching(Play(combo: 2100)));
        Assert.Equal([1], Matching(Play(combo: 600)));
        Assert.Empty(Matching(Play(mode: 1, combo: 2100)));
    }

    [Fact]
    public void Skill_medals_follow_the_star_rating_of_the_ruleset_played()
    {
        Assert.Equal([57], Matching(Play(stars: 3.5)));                  // osu! pass 3★
        Assert.Equal([57, 65], Matching(Play(stars: 3.5, perfect: true))); // and the 3★ FC
        Assert.Equal([91], Matching(Play(mode: 3, stars: 5.5)));          // mania pass 5★
        Assert.Equal([242], Matching(Play(stars: 9.2)));                  // osu! 9★
        Assert.Empty(Matching(Play(stars: 11.3)));                        // no tier above 10★
        Assert.Empty(Matching(Play(stars: null)));                        // unknown: nothing
    }

    [Theory]
    [InlineData(Mods.NoFail)]
    [InlineData(Mods.Easy)]
    [InlineData(Mods.HalfTime)]
    [InlineData(Mods.SpunOut)]
    [InlineData(Mods.Relax)]
    [InlineData(Mods.ScoreV2)]
    public void Skill_medals_dont_count_with_easier_or_unranked_mods(Mods mods) =>
        Assert.DoesNotContain(57, Matching(Play(mods: mods, stars: 3.5)));

    [Theory]
    [InlineData(RankedStatus.Loved)]
    [InlineData(RankedStatus.Qualified)]
    public void Skill_medals_need_a_ranked_or_approved_map(RankedStatus status) =>
        Assert.Empty(Matching(Play(stars: 3.5, status: status)));

    [Fact]
    public void Mania_skill_medals_dont_count_with_key_mods_and_taiko_has_its_exception()
    {
        Assert.Empty(Matching(Play(mode: 3, mods: Mods.Key7, stars: 5.5)));
        Assert.Empty(Matching(Play(mode: 1, stars: 3.5, beatmapId: 19990)));
        Assert.Equal([73], Matching(Play(mode: 1, stars: 3.5, beatmapId: 1)));
    }

    [Fact]
    public void Mod_intros_need_that_mod_alone()
    {
        Assert.Equal([119], Matching(Play(mods: Mods.SuddenDeath)));
        Assert.Equal([120], Matching(Play(mods: Mods.Perfect | Mods.SuddenDeath)));      // Perfect includes SD
        Assert.Equal([123], Matching(Play(mods: Mods.Nightcore | Mods.DoubleTime)));     // Nightcore includes DT
        Assert.Equal([124], Matching(Play(mods: Mods.Hidden | Mods.TouchDevice)));       // touch doesn't count
        Assert.Empty(Matching(Play(mode: 1, mods: Mods.Hidden | Mods.HardRock)));        // two mods: neither
        Assert.Equal([131], Matching(Play(mods: Mods.SpunOut)));
        Assert.Empty(Matching(Play(mode: 2, mods: Mods.SpunOut)));                       // spun out is osu! only
    }

    [Theory]
    [InlineData(true, RankedStatus.Loved, Mods.None, false, true)]
    [InlineData(false, RankedStatus.Ranked, Mods.None, false, false)]
    [InlineData(true, RankedStatus.Pending, Mods.None, false, false)]
    [InlineData(true, RankedStatus.Ranked, Mods.Relax, false, false)]
    [InlineData(true, RankedStatus.Ranked, Mods.None, true, false)]
    public void Only_passed_unrestricted_plays_on_leaderboard_maps_earn_medals(bool passed, RankedStatus status, Mods mods, bool restricted, bool eligible) =>
        Assert.Equal(eligible, MedalService.Eligible(passed, status, mods, restricted));

    [Theory]
    [InlineData(0, Mods.None, 4.2)]      // the map's own ruleset, nothing changing difficulty: stored rating
    [InlineData(2, Mods.None, null)]     // a convert: needs the calculator
    [InlineData(0, Mods.DoubleTime, null)]
    [InlineData(0, Mods.Hidden, 4.2)]
    public async Task Without_a_calculator_the_stored_rating_is_used_only_when_it_applies(int ruleset, Mods mods, double? expected) =>
        Assert.Equal(expected, await MedalService.StarRatingAsync(null, new Beatmap { Id = 1, Mode = 0, StarRating = 4.2 }, ruleset, (int)mods));

    [Fact]
    public async Task AwardAsync_unlocks_new_medals_once_then_is_idempotent()
    {
        var repo = new InMemoryUserAchievementRepository();
        var check = Play(combo: 600);

        Assert.Equal([1], (await MedalService.AwardAsync(repo, userId: 1, check, DateTime.UtcNow)).Select(m => m.Id));
        Assert.Empty(await MedalService.AwardAsync(repo, userId: 1, check, DateTime.UtcNow));
        Assert.Equal([1], (await MedalService.AwardAsync(repo, userId: 2, check, DateTime.UtcNow)).Select(m => m.Id));
    }

    [Fact]
    public void FormatAchievementsNew_uses_the_file_plus_name_plus_desc_slash_format()
    {
        var combo500 = MedalCatalog.All.Single(m => m.Id == 1);
        var combo750 = MedalCatalog.All.Single(m => m.Id == 3);

        Assert.Equal(
            "osu-combo-500+500 Combo+500 big ones! You're moving up in the world!/osu-combo-750+750 Combo+750 notes back to back? Woah.",
            MedalService.FormatAchievementsNew([combo500, combo750]));
        Assert.Equal(string.Empty, MedalService.FormatAchievementsNew([]));
    }
}
