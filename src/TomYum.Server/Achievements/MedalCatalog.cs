using TomYum.Bancho;
using TomYum.Domain.Enums;

namespace TomYum.Server.Achievements;

/// <summary>
/// The medals TomYum awards for osu!stable scores, under osu!'s official medal ids, so they're the
/// same medals the website's catalog and ppy's score processor (which awards lazer's) use.
/// </summary>
/// <remarks>
/// The rules follow osu!'s (as ppy's score processor applies them to lazer scores):
/// <list type="bullet">
/// <item>Skill medals: the map's star rating <em>in the ruleset played, with the score's mods</em>
/// falls in [N, N+1); only ranked or approved maps (not qualified or loved); no difficulty
/// reduction, automation or unranked mods; in mania, no key or co-op mods. FC medals also need a
/// full combo. osu!standard has 9★ and 10★ medals too.</item>
/// <item>Combo medals (osu!standard): every combo milestone the play reached.</item>
/// <item>Mod introduction medals: the play used exactly that one mod (Nightcore and Perfect
/// include the mod they build on; Touch Device doesn't count).</item>
/// </list>
/// </remarks>
public static class MedalCatalog
{
    private const int ModeStd = 0;
    private const int ModeTaiko = 1;
    private const int ModeCatch = 2;
    private const int ModeMania = 3;

    /// <summary>Mods a skill medal can't be earned with (difficulty reduction, automation, unranked).</summary>
    private const Mods NotForSkillMedals = Mods.Easy | Mods.NoFail | Mods.HalfTime | Mods.SpunOut | Mods.Relax | Mods.Autopilot
        | Mods.Autoplay | Mods.Cinema | Mods.TargetPractice | Mods.Random | Mods.ScoreV2;

    /// <summary>Mania key count and co-op mods, which skill medals don't allow either.</summary>
    private const Mods ManiaLayoutMods = Mods.Key1 | Mods.Key2 | Mods.Key3 | Mods.Key4 | Mods.Key5 | Mods.Key6 | Mods.Key7
        | Mods.Key8 | Mods.Key9 | Mods.KeyCoop;

    /// <summary>osu!'s taiko medal exception (a map whose star rating misleads).</summary>
    private const int TaikoExceptionBeatmap = 19990;

    private static Medal[]? _all;

    /// <summary>Every medal, ordered by id.</summary>
    // Lazily built so it doesn't depend on this type's static-field initialization order (the medal-text
    // arrays below are initialized after this member in textual order).
    public static IReadOnlyList<Medal> All => _all ??= Build();

    private static Medal[] Build()
    {
        var medals = new List<Medal>(83);

        // Skill: osu!'s ids run 1-8★ in sequence per ruleset; osu!standard's 9★ and 10★ came later.
        medals.AddRange(SkillPack(ModeStd, "osu", passIds: [55, 56, 57, 58, 59, 60, 61, 62, 242, 244], fcIds: [63, 64, 65, 66, 67, 68, 69, 70, 243, 245], StdPass, StdFc));
        medals.AddRange(SkillPack(ModeTaiko, "taiko", passIds: Sequence(71), fcIds: Sequence(95), TaikoPass, TaikoFc));
        medals.AddRange(SkillPack(ModeCatch, "fruits", passIds: Sequence(79), fcIds: Sequence(103), CatchPass, CatchFc));
        medals.AddRange(SkillPack(ModeMania, "mania", passIds: Sequence(87), fcIds: Sequence(111), ManiaPass, ManiaFc));
        medals.AddRange(ComboPack());
        medals.AddRange(ModIntroPack());

        return medals.OrderBy(m => m.Id).ToArray();
    }

    private static int[] Sequence(int first) => Enumerable.Range(first, 8).ToArray();

    /// <summary>Combo medals (osu!standard): each milestone the play's combo reached.</summary>
    private static IEnumerable<Medal> ComboPack()
    {
        yield return new Medal(1, "osu-combo-500", "500 Combo", "500 big ones! You're moving up in the world!",
            c => c.ModeVn == ModeStd && c.MaxCombo >= 500);
        yield return new Medal(3, "osu-combo-750", "750 Combo", "750 notes back to back? Woah.",
            c => c.ModeVn == ModeStd && c.MaxCombo >= 750);
        yield return new Medal(4, "osu-combo-1000", "1000 Combo", "A thousand reasons why you rock at this game.",
            c => c.ModeVn == ModeStd && c.MaxCombo >= 1000);
        yield return new Medal(5, "osu-combo-2000", "2000 Combo", "Nothing can stop you now.",
            c => c.ModeVn == ModeStd && c.MaxCombo >= 2000);
    }

    /// <summary>A ruleset's skill pass and full-combo medals, tier N at index N-1.</summary>
    private static IEnumerable<Medal> SkillPack(
        int mode, string filePrefix, int[] passIds, int[] fcIds,
        (string Name, string Desc)[] pass, (string Name, string Desc)[] fc)
    {
        for (var i = 0; i < pass.Length; i++)
        {
            var tier = i + 1;
            yield return new Medal(passIds[i], $"{filePrefix}-skill-pass-{tier}", pass[i].Name, pass[i].Desc,
                c => QualifiesForSkill(c, mode, tier));
        }

        for (var i = 0; i < fc.Length; i++)
        {
            var tier = i + 1;
            yield return new Medal(fcIds[i], $"{filePrefix}-skill-fc-{tier}", fc[i].Name, fc[i].Desc,
                c => c.Perfect && QualifiesForSkill(c, mode, tier));
        }
    }

    private static bool QualifiesForSkill(MedalCheck check, int mode, int tier) =>
        check.ModeVn == mode
        && check.StarRating is { } stars && stars >= tier && stars < tier + 1
        && check.Status is RankedStatus.Ranked or RankedStatus.Approved
        && (check.Mods & NotForSkillMedals) == 0
        && (mode != ModeMania || (check.Mods & ManiaLayoutMods) == 0)
        && !(mode == ModeTaiko && check.BeatmapId == TaikoExceptionBeatmap);

    /// <summary>Mod introduction medals: the play used that mod and no other.</summary>
    private static IEnumerable<Medal> ModIntroPack()
    {
        yield return new Medal(119, "all-intro-suddendeath", "Finality", "High stakes, no regrets.", c => OnlyMod(c, Mods.SuddenDeath));
        yield return new Medal(120, "all-intro-perfect", "Perfectionist", "Accept nothing but the best.", c => OnlyMod(c, Mods.Perfect | Mods.SuddenDeath));
        yield return new Medal(121, "all-intro-hardrock", "Rock Around The Clock", "You can't stop the rock.", c => OnlyMod(c, Mods.HardRock));
        yield return new Medal(122, "all-intro-doubletime", "Time And A Half", "Having a right ol' time. One and a half of them, almost.", c => OnlyMod(c, Mods.DoubleTime));
        yield return new Medal(123, "all-intro-nightcore", "Sweet Rave Party", "Founded in the fine tradition of changing things that were just fine as they were.", c => OnlyMod(c, Mods.Nightcore | Mods.DoubleTime));
        yield return new Medal(124, "all-intro-hidden", "Blindsight", "I can see just perfectly", c => OnlyMod(c, Mods.Hidden));
        yield return new Medal(125, "all-intro-flashlight", "Are You Afraid Of The Dark?", "Harder than it looks, probably because it's hard to look.", c => OnlyMod(c, Mods.Flashlight));
        yield return new Medal(126, "all-intro-easy", "Dial It Right Back", "Sometimes you just want to take it easy.", c => OnlyMod(c, Mods.Easy));
        yield return new Medal(127, "all-intro-nofail", "Risk Averse", "Safety nets are fun!", c => OnlyMod(c, Mods.NoFail));
        yield return new Medal(128, "all-intro-halftime", "Slowboat", "You got there. Eventually.", c => OnlyMod(c, Mods.HalfTime));
        yield return new Medal(131, "all-intro-spunout", "Burned Out", "One cannot always spin to win.", c => c.ModeVn == ModeStd && OnlyMod(c, Mods.SpunOut));
    }

    private static bool OnlyMod(MedalCheck check, Mods mod) => (check.Mods & ~Mods.TouchDevice) == mod;

    /// <summary>
    /// TomYum's medal ids before they became osu!'s (bancho.py's numbering), by official id; for
    /// cleaning up unlocks copied to the website under the old ids.
    /// </summary>
    public static readonly IReadOnlyDictionary<int, int> LegacyIds = new Dictionary<int, int>
    {
        [55] = 1, [56] = 2, [57] = 3, [58] = 4, [59] = 5, [60] = 6, [61] = 7, [62] = 8, [242] = 9, [244] = 10,
        [63] = 11, [64] = 12, [65] = 13, [66] = 14, [67] = 15, [68] = 16, [69] = 17, [70] = 18, [243] = 19, [245] = 20,
        [1] = 21, [3] = 22, [4] = 23, [5] = 24,
        [71] = 25, [72] = 26, [73] = 27, [74] = 28, [75] = 29, [76] = 30, [77] = 31, [78] = 32,
        [95] = 33, [96] = 34, [97] = 35, [98] = 36, [99] = 37, [100] = 38, [101] = 39, [102] = 40,
        [79] = 41, [80] = 42, [81] = 43, [82] = 44, [83] = 45, [84] = 46, [85] = 47, [86] = 48,
        [103] = 49, [104] = 50, [105] = 51, [106] = 52, [107] = 53, [108] = 54, [109] = 55, [110] = 56,
        [87] = 57, [88] = 58, [89] = 59, [90] = 60, [91] = 61, [92] = 62, [93] = 63, [94] = 64,
        [111] = 65, [112] = 66, [113] = 67, [114] = 68, [115] = 69, [116] = 70, [117] = 71, [118] = 72,
        [119] = 73, [124] = 74, [120] = 75, [121] = 76, [122] = 77, [125] = 78, [126] = 79, [127] = 80,
        [123] = 81, [128] = 82, [131] = 83,
    };

    // ---- Verbatim medal text (osu! official), indexed tier-1 first ----

    private static readonly (string Name, string Desc)[] StdPass =
    [
        ("Rising Star", "Can't go forward without the first steps."),
        ("Constellation Prize", "Definitely not a consolation prize. Now things start getting hard!"),
        ("Building Confidence", "Oh, you've SO got this."),
        ("Insanity Approaches", "You're not twitching, you're just ready."),
        ("These Clarion Skies", "Everything seems so clear now."),
        ("Above and Beyond", "A cut above the rest."),
        ("Supremacy", "All marvel before your prowess."),
        ("Absolution", "My god, you're full of stars!"),
        ("Event Horizon", "No force dares to pull you under."),
        ("Phantasm", "Fevered is your passion, extraordinary is your skill."),
    ];

    private static readonly (string Name, string Desc)[] StdFc =
    [
        ("Totality", "All the notes. Every single one."),
        ("Business As Usual", "Two to go, please."),
        ("Building Steam", "Hey, this isn't so bad."),
        ("Moving Forward", "Bet you feel good about that."),
        ("Paradigm Shift", "Surprisingly difficult."),
        ("Anguish Quelled", "Don't choke."),
        ("Never Give Up", "Excellence is its own reward."),
        ("Aberration", "They said it couldn't be done. They were wrong."),
        ("Chosen", "Reign among the Prometheans, where you belong."),
        ("Unfathomable", "You have no equal."),
    ];

    private static readonly (string Name, string Desc)[] TaikoPass =
    [
        ("My First Don", "Marching to the beat of your own drum. Literally."),
        ("Katsu Katsu Katsu", "Hora! Izuko!"),
        ("Not Even Trying", "Muzukashii? Not even."),
        ("Face Your Demons", "The first trials are now behind you, but are you a match for the Oni?"),
        ("The Demon Within", "No rest for the wicked."),
        ("Drumbreaker", "Too strong."),
        ("The Godfather", "You are the Don of Dons."),
        ("Rhythm Incarnate", "Feel the beat. Become the beat."),
    ];

    private static readonly (string Name, string Desc)[] TaikoFc =
    [
        ("Keeping Time", "Don, then katsu. Don, then katsu.."),
        ("To Your Own Beat", "Straight and steady."),
        ("Big Drums", "Bigger scores to match."),
        ("Adversity Overcome", "Difficult? Not for you."),
        ("Demonslayer", "An Oni felled forevermore."),
        ("Rhythm's Call", "Heralding true skill."),
        ("Time Everlasting", "Not a single beat escapes you."),
        ("The Drummer's Throne", "Percussive brilliance befitting royalty alone."),
    ];

    private static readonly (string Name, string Desc)[] CatchPass =
    [
        ("A Slice Of Life", "Hey, this fruit catching business isn't bad."),
        ("Dashing Ever Forward", "Fast is how you do it."),
        ("Zesty Disposition", "No scurvy for you, not with that much fruit."),
        ("Hyperdash ON!", "Time and distance is no obstacle to you."),
        ("It's Raining Fruit", "And you can catch them all."),
        ("Fruit Ninja", "Legendary techniques."),
        ("Dreamcatcher", "No fruit, only dreams now."),
        ("Lord of the Catch", "Your kingdom kneels before you."),
    ];

    private static readonly (string Name, string Desc)[] CatchFc =
    [
        ("Sweet And Sour", "Apples and oranges, literally."),
        ("Reaching The Core", "The seeds of future success."),
        ("Clean Platter", "Clean only of failure. It is completely full, otherwise."),
        ("Between The Rain", "No umbrella needed."),
        ("Addicted", "That was an overdose?"),
        ("Quickening", "A dash above normal limits."),
        ("Supersonic", "Faster than is reasonably necessary."),
        ("Dashing Scarlet", "Speed beyond mortal reckoning."),
    ];

    private static readonly (string Name, string Desc)[] ManiaPass =
    [
        ("First Steps", "It isn't 9-to-5, but 1-to-9. Keys, that is."),
        ("No Normal Player", "Not anymore, at least."),
        ("Impulse Drive", "Not quite hyperspeed, but getting close."),
        ("Hyperspeed", "Woah."),
        ("Ever Onwards", "Another challenge is just around the corner."),
        ("Another Surpassed", "Is there no limit to your skills?"),
        ("Extra Credit", "See me after class."),
        ("Maniac", "There's just no stopping you."),
    ];

    private static readonly (string Name, string Desc)[] ManiaFc =
    [
        ("Keystruck", "The beginning of a new story"),
        ("Keying In", "Finding your groove."),
        ("Hyperflow", "You can *feel* the rhythm."),
        ("Breakthrough", "Many skills mastered, rolled into one."),
        ("Everything Extra", "Giving your all is giving everything you have."),
        ("Level Breaker", "Finesse beyond reason"),
        ("Step Up", "A precipice rarely seen."),
        ("Behind The Veil", "Supernatural!"),
    ];
}
