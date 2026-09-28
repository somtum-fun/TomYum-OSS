# Achievements (medals)

> Audience: **people** operating or contributing to TomYum.

TomYum awards osu!'s **medals** for stable plays, under **osu!'s own medal ids and rules**, so they're
the same medals the website shows and ppy's score processor awards for lazer plays. When a stable play
qualifies, the client pops the medal up; it's recorded against the account (and on the website).

## Which medals, and who awards them

| Medals | Stable plays | Lazer plays |
|---|---|---|
| Skill (pass / full combo, per ruleset and star tier), mod introduction | TomYum | ppy's score processor |
| Combo (500 / 750 / 1000 / 2000) | TomYum | (stable only, as on osu!) |
| Beatmap packs, challenge packs, spotlights, Project Loved | TomYum | TomYum |
| Statistics, rank milestones, daily challenge | the processor | the processor |

The rules (osu!'s, as ppy's processor applies them):

- **Skill medals** judge the map's star rating **in the ruleset played, with the play's mods** (so a
  catch convert of a 6★ osu! map counts at its catch rating, and DT raises it). Only ranked or approved
  maps (not qualified or loved); no difficulty reduction (EZ, NF, HT, SO), automation (RX, AP) or unranked
  mods; in mania no key or co-op mods. osu!standard has 9★ and 10★ tiers too.
- **Combo medals**: every milestone the play reached (a 2100 combo gives all four).
- **Mod introduction**: the play used exactly that mod (Nightcore and Perfect include the mod they
  build on; touch doesn't count).
- **Pack medals**: every set of the pack passed, in the pack's ruleset (or each map's own). Challenge
  packs also exclude difficulty reduction mods. A pack only counts **once the server has all of it**:
  the medal needs osu!'s whole pack (`src/TomYum.Server/Achievements/PackMedals.json`, taken from osu!'s
  pack pages), and a set the server doesn't have can't be passed.

No medals for relax/autopilot plays, failed plays, maps without a leaderboard, or restricted players.

## The website's catalog

At startup TomYum writes osu!'s full medal list into the website (`osu_achievements`), read from
osu.ppy.sh (every profile page carries it). Offline, it writes its own medals instead and tries again
next start. Pack medals are TomYum's job because ppy's processor finds packs by osu!'s pack numbers,
which a private server's packs don't have; local packs are numbered from 20000 so the processor never
mistakes one for an osu! pack.

## Medal icons (the `assets.` subdomain)

`GET /medals/client/{file}.png` on `assets.<domain>` serves the icons: fetched from ppy's CDN on first
use and cached under `data/assets/medals/client/` (`Assets:CachePath`). The stable client asks there
(the devserver client rewrites `*.ppy.sh`), and the website does too
(`USER_ACHIEVEMENT_ICON_PREFIX=https://assets.<domain>/medals/client/`, set in the production compose file).

## History

Before 2026-09-28 TomYum used bancho.py's medal numbers (1–83) and rules, so the website showed wrong
medals, and ppy's processor gave lazer plays medals of other rulesets. Two one-time rebuilds fixed that on
the next start (recorded in the website's `osu_counts` as `tomyum_medals_official` and
`tomyum_medals_official_lazer`): every stored play was judged again, wrong medals were removed, missing
ones added, pack medals recalculated from complete packs, and the old medal notifications removed.

## Storage

TomYum's own unlocks are in its `user_achievements` table (osu!'s ids since the `OfficialMedalIds`
migration); the website's in `osu_user_achievements`.
