# Pla-ra: stars and pp for custom rulesets

Custom lazer rulesets (sentakki, tau, rush, …, ids 10 and up on the website) are ranked by **pp** by default, with
ranked score as the other sort. Their map boards stay sorted by score, as osu!'s do, with each play's pp shown.

Staff can rank a ruleset by **ranked score** instead (admin panel → Custom rulesets → "ranked by"). Its plays then
show no pp anywhere, and it has no Performance or Top Plays tab. Pla-ra keeps working out the pp, so switching back is
instant.

Pla-ra is open source: **[somtum-fun/plara](https://github.com/somtum-fun/plara)** (MIT), with the library, the server
and the full docs. `src/Somtum.Plara/` here is the same three files; change them there and here together.

Pla-ra works out that pp, for every ruleset, the same way on the server and in TumThai.

## Where it lives

| Piece | Path | What it does |
|---|---|---|
| Library | `src/Somtum.Plara/` | Profiles, the difficulty model, the pp formula. Only uses osu.Game. |
| TumThai copy | `osu-tumthai/osu.Game/Rulesets/Plara/`, `osu-tumthai-next/osu.Game/Rulesets/Plara/` | **The same three files**: in-game stars, the pp counter, results pp. Copy them over after any change. |
| Sidecar | `services/TomYum.Plara/` (compose service `plara`, port 9100) | `POST /pp`, `POST /stars`, `POST /pp-calc` (the beatmap page's pp calculator: a map, mods, misses and some accuracies; it answers each one's pp and the mods that give pp), `GET /version`, `GET /health`. Runs the pinned ruleset DLLs. |
| Website | osu-web `TomYumPlara`, job `TomYumCustomRulesetPp`, `TomYumCustomRulesetScores` | Asks the sidecar for each ranked pass's pp, stores `scores.pp`, ranks by it (or by score, per ruleset). |
| Admin panel | Admin → pp libraries (Pla-ra row), Custom rulesets ("ranked by") | What the sidecar runs, plays with and without pp, Recalculate (missing / all plays), pp or score per ruleset. |
| Tests | `tests/TomYum.Plara.Tests/` | Formula, mods, profiles. |

The sidecar is its own process because it runs third-party code. It loads DLLs into the default load context:
osu! finds rulesets with `Type.GetType`, which a separate `AssemblyLoadContext` breaks ("Type lookup failure").

## Stars

Each ruleset has a profile (`PlaraProfiles`, keyed by the DLL's assembly name), with one of two star sources:

- **Own**: the ruleset's own difficulty calculator, which is a real one. This is tau and soyokaze; osu!fps and FPSosu
  use osu!'s.
- **Pla-ra**: a timing model, for rulesets whose own calculator is a placeholder. These are sentakki, rush,
  hishigata, swing, bosu, touhosu and sandbox. It rates the map *after the ruleset converts it* with the play's mods,
  so mods like sentakki's EX are counted automatically. It reads only judged notes' times, chords (15 ms) and holds.
  - Three strain skills, built like osu!'s: Density (decay 0.3), Stamina (0.85) and Rhythm (sin² of log₂ of the gap
    ratio).
  - The usual 400 ms peaks, weighted 0.9ⁱ.
  - `★ = Scale · (Σ √D^1.1)^(1/1.1)`.

`Scale` is set so that across 150 random ranked osu! maps, the median of ★ / osu!★ is 1 (2026-11-18 calibration). It
doesn't measure aim or dodging, so bosu and touhosu are the roughest.

## pp

```
pp = 1.2 · ★³ · acc⁵ · (1 − misses/N)⁴ · (0.95 + 0.4·min(1, N/2000) + [N>2000] 0.5·log₁₀(N/2000)) · mods
```

- **N** is the map's judged notes (basic judgements).
- **Below 50 notes**, pp is scaled by N/50.
- **NF** also costs `max(0.9, 1 − 0.02·misses)`.
- Only **ranked passes** get pp.
- **Totals** are each player's best pp per map, weighted 0.95ⁱ, with no bonus for play count.
- **The miss penalty** is the share of notes missed. osu!'s per-miss curve would take nearly every play in these
  rulesets to 0.

**Mods.** Each profile lists the mods that give pp and their multipliers:

| Mods | Multiplier |
|---|---|
| Rate mods, and mods that change the notes | ×1 |
| TD | ×1 |
| EZ | ×0.5 |
| Lenient mods (tau LN/LT, hishigata TW) | ×0.8 |
| Hidden-like | ×1.05 |
| FL, BL | ×1.10 |
| SO | ×0.95 |

**A mod that isn't listed gives no pp.** That includes DA, WU, WD, AS and fun mods. The play still counts on score
boards, so a new mod in a re-pinned ruleset can't be farmed before someone has reviewed it. RX and AP have their own
boards. NT, MG and DL are unranked altogether (osu-web `TomYumCustomRulesets::RULESET_UNRANKED_MODS`).

**Checks.** The sidecar refuses pp when a play's judgements don't add up to the converted map's notes, which stops
made-up statistics from a scripted submit.

## Running it

- **New play:** stored → job → sidecar → `scores.pp` → the rankings refresh.
- **Sidecar down:** the job retries 5 times (30 s … 30 min). After that, Admin → pp libraries → Pla-ra → **Missing**
  (or `php artisan tomyum:plara-recalculate --missing`).
- **Changed a number in the library:**
  1. Bump `PlaraProfiles.VERSION`.
  2. Rebuild and restart `plara`.
  3. Admin → pp libraries → Pla-ra → **All plays** (or `php artisan tomyum:plara-recalculate`).
  4. Copy the three files into both TumThai checkouts and release TumThai. Until players update, their in-game
     numbers are the old ones.
- **Ruleset pinned again:** the sidecar restarts itself to load the new DLL, because the default load context holds
  one release per assembly. Then run `php artisan tomyum:plara-recalculate --ruleset=<slug>`.
- **New ruleset enabled:** add its profile (assembly name, star source, scale, mods), then follow the steps above. A
  ruleset without a profile gets no pp.
