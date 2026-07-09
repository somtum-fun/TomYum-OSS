# Achievements (medals)

> Audience: **people** operating or contributing to TomYum. Agent-facing internals live in
> `memories/features/achievements.md`.

TomYum awards osu! **medals** (achievements) on score submission, the same way osu!official and
bancho.py do. When you finish a play that qualifies, the client pops up the medal(s) you just
unlocked, and they're recorded against your account so you never earn the same one twice.

## What's included

The **default osu! medal set** — a faithful reimplementation of bancho.py's vanilla catalog (83
medals, ids 1–83):

- **Combo pack** (osu!standard): *500 / 750 / 1000 / 2000 Combo*. Banded — a 2100 combo unlocks only
  *2000 Combo*, not the lower tiers.
- **Mod-introduction pack** (any mode): the first time you play with a given mod — *Finality* (SD),
  *Blindsight* (HD), *Perfectionist* (PF), *Rock Around The Clock* (HR), *Time And A Half* (DT),
  *Are You Afraid Of The Dark?* (FL), *Dial It Right Back* (EZ), *Risk Averse* (NF), *Sweet Rave
  Party* (NC), *Slowboat* (HT), *Burned Out* (SO).
- **Skill packs** — per mode, per star tier, in two flavours:
  - **Pass** (*"…-skill-pass-N"*): pass a map in the *N*–*N+1*★ band **without No-Fail**. osu! has
    tiers 1–10; taiko/catch/mania have 1–8.
  - **FC** (*"…-skill-fc-N"*): full-combo a map in the same band.

Custom, server-specific medals (the prod website set) are **not** part of this — they're a later
slice that will live in the session plugin.

## How it works

On a submitted score the server checks every medal you don't already have; any whose condition your
play satisfies is unlocked and returned in the submission response's `achievements-new:` field, which
is what drives the in-game popup. Medals are only awarded for a **passed** score on a **ranked/approved**
map (matching bancho.py's gate).

Medal **icons** are the osu! official ones — the client loads them from
`https://assets.ppy.sh/medals/client/{file}.png`, so no server-side art hosting is needed.

## A few faithful quirks (kept to match bancho.py / osu!)

- **Nightcore and Double-Time are separate intro medals.** Because NC sets the DT bit too, a nightcore
  play earns both.
- **Sudden-Death's intro medal only fires when SD is your *only* mod** (it's an exact-bitmask check).
- **Skill medals use the beatmap's nominal (no-mod) star rating.** Mod-adjusted star rating (so DT
  bumps you into a higher tier) is a later refinement.
- **Restricted players still earn medals for now** — the restriction gate lands with Phase 2
  moderation.

## Storage

Unlocks are persisted in the `user_achievements` table (`UserId`, `AchievementId`, `UnlockedAt`).
The table is created automatically at startup — including on an already-existing database — so no
manual migration step is needed (see `SchemaGuard`).

See also: `memories/features/achievements.md` (internals), `docs/ROADMAP.md`.
