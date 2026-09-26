using TomYum.Bancho;

namespace TomYum.Server.Achievements;

/// <summary>
/// The default osu! medal catalog — a clean-room reimplementation of bancho.py's vanilla achievement
/// seed (ids 1–83; see <c>reference/bancho.py/migrations/base.sql</c>). Definitions (icon/name/desc/
/// condition) live here in code; only per-user unlocks are persisted. Custom, server-specific medals
/// (the prod website/session set, ids ≥ 84) are intentionally out of scope for this basic port.
/// </summary>
/// <remarks>
/// Condition fidelity notes (matching the vanilla conds verbatim):
/// <list type="bullet">
/// <item>Combo medals are <em>banded</em> — a 2100 combo unlocks only "2000 Combo", not the lower tiers.</item>
/// <item>Skill <em>pass</em> medals require No-Fail to be OFF; <em>FC</em> medals require a full combo.</item>
/// <item>Skill medals band the star rating as <c>[N, N+1)</c>, so an 11★+ pass matches no std tier.</item>
/// <item>The Sudden-Death intro medal fires only when SD is the <em>only</em> mod (exact bitmask 32).</item>
/// <item>Star rating is the beatmap's nominal (no-mod) value — mod-adjusted SR is a later refinement.</item>
/// </list>
/// </remarks>
public static class MedalCatalog
{
    private const int ModeStd = 0;
    private const int ModeTaiko = 1;
    private const int ModeCatch = 2;
    private const int ModeMania = 3;

    private static Medal[]? _all;

    /// <summary>Every default medal, ordered by id.</summary>
    // Lazily built so it doesn't depend on this type's static-field initialization order (the medal-text
    // arrays below are initialized after this member in textual order).
    public static IReadOnlyList<Medal> All => _all ??= Build();

    private static Medal[] Build()
    {
        var medals = new List<Medal>(83);

        // ---- Skill pass/fc packs ----
        medals.AddRange(SkillPack(ModeStd, "osu", passStartId: 1, fcStartId: 11, StdPass, StdFc));
        // ids 21–24 (combo) sit between the std and taiko skill packs in the vanilla numbering.
        medals.AddRange(ComboPack());
        medals.AddRange(SkillPack(ModeTaiko, "taiko", passStartId: 25, fcStartId: 33, TaikoPass, TaikoFc));
        medals.AddRange(SkillPack(ModeCatch, "fruits", passStartId: 41, fcStartId: 49, CatchPass, CatchFc));
        medals.AddRange(SkillPack(ModeMania, "mania", passStartId: 57, fcStartId: 65, ManiaPass, ManiaFc));

        // ---- Mod-introduction pack (73–83), mode-agnostic ----
        medals.AddRange(ModIntroPack());

        return medals.OrderBy(m => m.Id).ToArray();
    }

    /// <summary>Combo medals — osu!standard only, banded so only the highest reached tier unlocks.</summary>
    private static IEnumerable<Medal> ComboPack()
    {
        yield return new Medal(21, "osu-combo-500", "500 Combo", "500 big ones! You're moving up in the world!",
            c => c.ModeVn == ModeStd && c.MaxCombo is >= 500 and < 750);
        yield return new Medal(22, "osu-combo-750", "750 Combo", "750 notes back to back? Woah.",
            c => c.ModeVn == ModeStd && c.MaxCombo is >= 750 and < 1000);
        yield return new Medal(23, "osu-combo-1000", "1000 Combo", "A thousand reasons why you rock at this game.",
            c => c.ModeVn == ModeStd && c.MaxCombo is >= 1000 and < 2000);
        yield return new Medal(24, "osu-combo-2000", "2000 Combo", "Nothing can stop you now.",
            c => c.ModeVn == ModeStd && c.MaxCombo >= 2000);
    }

    /// <summary>Builds a mode's skill "pass" (ids from <paramref name="passStartId"/>) and "fc" (from <paramref name="fcStartId"/>) medals.</summary>
    private static IEnumerable<Medal> SkillPack(
        int mode, string filePrefix, int passStartId, int fcStartId,
        (string Name, string Desc)[] pass, (string Name, string Desc)[] fc)
    {
        for (var i = 0; i < pass.Length; i++)
        {
            var tier = i + 1;
            yield return new Medal(passStartId + i, $"{filePrefix}-skill-pass-{tier}", pass[i].Name, pass[i].Desc,
                c => (c.Mods & Mods.NoFail) == 0 && c.ModeVn == mode && c.StarRating >= tier && c.StarRating < tier + 1);
        }

        for (var i = 0; i < fc.Length; i++)
        {
            var tier = i + 1;
            yield return new Medal(fcStartId + i, $"{filePrefix}-skill-fc-{tier}", fc[i].Name, fc[i].Desc,
                c => c.Perfect && c.ModeVn == mode && c.StarRating >= tier && c.StarRating < tier + 1);
        }
    }

    /// <summary>Mod-introduction medals — unlock the first time a mod is used, in any mode.</summary>
    private static IEnumerable<Medal> ModIntroPack()
    {
        // Sudden-Death is the exact-bitmask case: it fires only when SD is the sole mod (vanilla `mods == 32`).
        yield return new Medal(73, "all-intro-suddendeath", "Finality", "High stakes, no regrets.",
            c => c.Mods == Mods.SuddenDeath);
        yield return new Medal(74, "all-intro-hidden", "Blindsight", "I can see just perfectly",
            c => (c.Mods & Mods.Hidden) != 0);
        yield return new Medal(75, "all-intro-perfect", "Perfectionist", "Accept nothing but the best.",
            c => (c.Mods & Mods.Perfect) != 0);
        yield return new Medal(76, "all-intro-hardrock", "Rock Around The Clock", "You can't stop the rock.",
            c => (c.Mods & Mods.HardRock) != 0);
        yield return new Medal(77, "all-intro-doubletime", "Time And A Half", "Having a right ol' time. One and a half of them, almost.",
            c => (c.Mods & Mods.DoubleTime) != 0);
        yield return new Medal(78, "all-intro-flashlight", "Are You Afraid Of The Dark?", "Harder than it looks, probably because it's hard to look.",
            c => (c.Mods & Mods.Flashlight) != 0);
        yield return new Medal(79, "all-intro-easy", "Dial It Right Back", "Sometimes you just want to take it easy.",
            c => (c.Mods & Mods.Easy) != 0);
        yield return new Medal(80, "all-intro-nofail", "Risk Averse", "Safety nets are fun!",
            c => (c.Mods & Mods.NoFail) != 0);
        yield return new Medal(81, "all-intro-nightcore", "Sweet Rave Party", "Founded in the fine tradition of changing things that were just fine as they were.",
            c => (c.Mods & Mods.Nightcore) != 0);
        yield return new Medal(82, "all-intro-halftime", "Slowboat", "You got there. Eventually.",
            c => (c.Mods & Mods.HalfTime) != 0);
        yield return new Medal(83, "all-intro-spunout", "Burned Out", "One cannot always spin to win.",
            c => (c.Mods & Mods.SpunOut) != 0);
    }

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
