using TomYum.Bancho;

namespace TomYum.Server.Achievements;

/// <summary>
/// The facts about a passed score that a medal's unlock condition inspects. Mirrors the arguments
/// bancho.py's <c>cond(score, mode_vn)</c> lambdas read: the vanilla ruleset, the mod bitmask, the
/// score's max combo, the beatmap star rating, and whether the play was a full combo (<c>perfect</c>).
/// </summary>
/// <param name="ModeVn">Vanilla ruleset id: 0=osu!, 1=taiko, 2=catch, 3=mania.</param>
/// <param name="Mods">The gameplay mods on the score.</param>
/// <param name="MaxCombo">The highest combo reached in the play.</param>
/// <param name="StarRating">The beatmap's star rating (nominal / no-mod — see the medal catalog notes).</param>
/// <param name="Perfect">Whether the play was a full combo (the client's "perfect" flag).</param>
public readonly record struct MedalCheck(int ModeVn, Mods Mods, int MaxCombo, double StarRating, bool Perfect);

/// <summary>
/// A single achievement (medal) definition. Definitions live in code (see <see cref="MedalCatalog"/>);
/// only per-user unlocks are persisted. <see cref="File"/> is the osu! medal icon basename the client
/// resolves from <c>https://assets.ppy.sh/medals/client/{File}.png</c>.
/// </summary>
/// <param name="Id">Stable catalog id (matches bancho.py's vanilla ids 1–83).</param>
/// <param name="File">Icon basename, e.g. <c>osu-combo-500</c>.</param>
/// <param name="Name">Display name shown in the unlock popup, e.g. <c>500 Combo</c>.</param>
/// <param name="Description">Flavour text shown under the name.</param>
/// <param name="Condition">Returns <c>true</c> when a score qualifies to unlock this medal.</param>
public sealed record Medal(int Id, string File, string Name, string Description, Func<MedalCheck, bool> Condition);
