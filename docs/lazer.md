# osu!(lazer) support

## Status

Done (2026-09-27):
- **Login**: `php artisan tomyum:setup` adds lazer's built-in OAuth client (id 5). This needs
  Passport keys (`php artisan passport:keys`) and `CLIENT_CHECK_VERSION=false` in osu-web's `.env`.
- **Merged pp (option A below)**: osu-web's compose runs ppy's processor (`score-processor`), and
  TomYum runs with `OsuWeb:ScoreProcessor=true` (the osu-web compose overlay sets it). Stable and
  lazer scores share one pool.
  - For an existing server, turn it on once: zero the grade counts in `osu_user_stats*`
    (TomYum wrote them before; the processor recounts them), then run
    `docker compose run --rm score-processor queue pump-all` in osu-web.
  - TomYum's pp library matches the processor's (ppy rulesets 2026.730.0). Scores from before the
    upgrade keep their old in-game pp until recalculated (`tools/RecalcPp`); the website already
    has the new values.

- **Multiplayer, spectating, online presence, lazer replays**: osu-web's compose builds ppy's
  spectator server (`spectator`, from `docker/spectator/Dockerfile`) with one change: replays go
  to `public/uploads/replay` (where the website reads them) instead of Amazon S3. Caddy serves it
  as `https://spectator.localtest.me` (`deploy/caddy/Caddyfile.local`).

- **Maps the website doesn't have yet**: lazer asks about every map the player has. The fork's
  `TomYumImportMissingBeatmaps` middleware imports a missing beatmap or set from bancho through
  TomYum (`POST /_admin/beatmaps/import`, `/_admin/beatmapsets/{id}/import`) before the request is
  handled. Maps osu! doesn't know are remembered for an hour.

Not yet: custom-map pp (the processor downloads `.osu` files from osu.ppy.sh).

## Connecting a lazer client

Use **LazerAuthlibInjection** (below). Before installing it, **sign out of your osu.ppy.sh account
in lazer**: using it against the official servers gets accounts banned. Remove the DLL to go back.

1. In lazer: Settings → "Open osu! folder" (Windows: `%AppData%\osu!`).
2. Copy `osu.Game.Rulesets.EnhancedAuth.dll` from the
   [latest release](https://github.com/MingxuanGame/LazerAuthlibInjection/releases) into `rulesets`.
3. Restart lazer. Settings → Rulesets → set **API Url** and **Website Url** to
   `https://osu.localtest.me`. Leave the Spectator / Multiplayer / Metadata Urls empty (the client
   then uses `https://osu.localtest.me/signalr/<hub>`, which Caddy sends to the spectator server) or
   set them to `https://spectator.localtest.me/spectator`, `.../multiplayer`, `.../metadata`. Keep
   the default client ID/secret. Turn off the g0v0-server features (the `--non-g0v0-server` option).
4. Sign in with your normal account from the website.

## Research

Checked 2026-09-27 against ppy's current sources:
`ppy/osu` (lazer 2026.921.0), `ppy/osu-server-spectator`, `ppy/osu-queue-score-statistics`, the
osu-web fork, and the osu! wiki/news.

## How osu.ppy.sh does it

- **One score pool.** Stable and lazer scores both live in osu-web's `scores` table. Stable ones are
  "legacy" scores (`legacy_score_id` set). TomYum already writes its stable scores this way.
- **Total pp combines both clients.** `UserTotalPerformanceProcessor` reads
  `scores WHERE user_id, ruleset_id, pp IS NOT NULL, preserve = 1, ranked = 1 ORDER BY pp DESC LIMIT 1000`,
  keeps the **best pp per beatmap** (whichever client), weights by `0.95^n`, adds bonus pp
  `(417 - 1/3) * (1 - 0.995^count)`, and computes accuracy the same weighted way. Global rank follows.
- **Stable scores get lazer's pp formula too.** `ScorePerformanceProcessor` runs on legacy scores
  (`RunOnLegacyScores => true`) and rewrites their `pp`.
- **Split of work for stable scores.** The processor skips legacy scores for play count, total
  score, ranked score, hit counts, max combo and play time (`RunOnLegacyScores => false`): the
  stable server handles those. It does do pp, total pp, rank, rank counts (SS/S/A) and medals.
- **Where scores show** (wiki "Upgrading to lazer"): stable scores show in lazer; lazer scores do
  **not** show in the stable client. On the website, "lazer mode" changes beatmap leaderboards only.
- **Scoring.** Lazer uses standardised scoring (ScoreV2-like). Stable scores are converted and
  carry the Classic mod (0.96x). Leaderboards and ranked score use the classic display.
- **pp mods.** Only mods marked `Ranked` give pp (`AllModsValidForPerformance`); Classic is allowed
  on legacy scores only. **Relax/Autopilot give no pp in lazer** (`Mod.Ranked` defaults to false and
  ModRelax doesn't override it). Only ranked/approved maps give pp (`IsBeatmapValidForPerformance`).
- **Client builds.** With client checking on, a lazer score only gets pp if its build
  (`version_hash` → `osu_builds.allow_performance`) is allowed.

## What lazer needs from a server

| Piece | Role | Here today |
|---|---|---|
| osu-web API v2 | Login, beatmaps, leaderboards, score tokens/submission (`POST beatmaps/{id}/solo/scores`, `PUT .../{token}`), chat, rankings | Running (fork already has the routes) |
| OAuth (Laravel Passport) | Lazer logs in with the password grant and a client ID/secret built into the client | Needs `passport:keys` and a client row matching lazer's built-in ID/secret |
| `pppy/osu-server-spectator` | SignalR hubs `/spectator`, `/multiplayer`, `/metadata` (+ `/referee`). Spectating, multiplayer, online presence; **saves lazer replays** (local `REPLAYS_PATH` or S3) | Not running. Needs osu-web's MySQL + Redis, `SHARED_INTEROP_*`, `BANCHO_BOT_USER_ID`, and osu-web's `oauth-public.key` to check logins |
| `osu-queue-score-statistics` | Reads `osu-queue:score-statistics` (the fork already pushes lazer scores there in `Solo\Score::queueForProcessing`) and computes pp/stats/rank/medals | Not running, so lazer scores would save with no pp |
| Difficulty | The processor computes star ratings itself (`REALTIME_DIFFICULTY`, default on), downloading `.osu` files from `BEATMAP_DOWNLOAD_PATH` (default osu.ppy.sh) | Custom maps would need that path pointed at TomYum |
| `CLIENT_CHECK_VERSION` | osu-web and spectator reject/unrank unknown builds | Turn it off, or register build hashes in `osu_builds` |

## Getting the client to connect

Lazer has **no server setting**. `OsuGameBase.CreateEndpoints()` picks the production endpoints
(osu.ppy.sh, spectator.osu.ppy.sh/{spectator,multiplayer,metadata}, bss.ppy.sh), or dev.ppy.sh in debug
builds. No environment variable or argument overrides it. Options:

1. **LazerAuthlibInjection** (MingxuanGame, actively maintained: v2026.923.0 two days after lazer
   2026.921.0). It's a ruleset you drop in that sets API/website/spectator/multiplayer/metadata URLs
   and the client ID/secret, from the settings panel or command line. Its README warns that using it
   on official servers will get the account banned. Use a separate lazer install for your server.
2. **Custom build.** Fork `ppy/osu` and change `ProductionEndpointConfiguration`. This is fully in
   your control, but you rebuild on every lazer release.

## Fitting it into TomYum (stable and lazer merged, like osu.ppy.sh)

TomYum already mirrors stable scores as legacy rows, which matches ppy's model. The main change is
**who owns the website's stats**. Today `OsuWebScoreMirror.WriteStatsAsync` overwrites
`osu_user_stats*` from TomYum's stable data, which would erase lazer's share. Options:

- **A: ppy's split (recommended).** Run the score processor for lazer *and* stable scores (TomYum
  queues its mirrored scores too). The processor owns pp, total pp, rank and rank counts. TomYum keeps
  the stable-only counters, like ppy's stable server. The in-game panel reads total pp back from the
  website, so stable shows the combined number like on osu.ppy.sh.
  - Stable scores' pp would come from lazer's formula on the website. TomYum's own in-game pp should
    use the same calculator, or the two numbers will differ.
  - Relax/autopilot stay TomYum-only. They're not mirrored today and lazer gives them no pp anyway.
- **B: TomYum computes everything.** TomYum reads the website's lazer scores and does the pp and
  stats itself. It needs lazer-mod pp and standardised→classic conversion. That's more work and
  duplicates ppy's processor.

**Quirks to handle either way:**
- Lazer scores never show in the stable client (osu.ppy.sh behaviour). TomYum's in-game leaderboards
  stay stable-only.
- Ranked score for lazer scores uses classic scoring.
- Mods with no stable equivalent (rate changes, Difficulty Adjust, …) give no pp unless ranked.
- Restricted users: the processor checks `IsUserRestricted` on osu-web's tables, which TomYum already
  uses.
- Medals from lazer scores are awarded by the processor. TomYum awards stable medals, so the two
  must not double-award.

## Suggested order

1. OAuth keys + login client, `CLIENT_CHECK_VERSION` off, injection ruleset: lazer can log in,
   browse, and see stable leaderboards.
2. Score processor (option A) and TomYum's stats handover: lazer scores give pp, and total pp combines
   both clients.
3. Spectator server: spectating, multiplayer, online presence, lazer replays.
4. Custom-map support for the processor (`BEATMAP_DOWNLOAD_PATH` → TomYum).

## Sources

- ppy/osu: `osu.Game/OsuGameBase.cs`, `osu.Game/Online/*EndpointConfiguration.cs`, `osu.Game/Rulesets/Mods/Mod.cs`
- ppy/osu-queue-score-statistics: `Processors/UserTotalPerformanceProcessor.cs`,
  `Helpers/UserTotalPerformanceAggregateHelper.cs`, `Processors/ScorePerformanceProcessor.cs`,
  `Processors/RankedScoreProcessor.cs`, `Stores/BeatmapStore.cs`
- ppy/osu-server-spectator: `AppSettings.cs`, `Startup.cs`, `Authentication/ConfigureJwtBearerOptions.cs`, `Hubs/ScoreUploader.cs`
- osu-web fork: `app/Http/Controllers/Solo/ScoresController.php`, `app/Models/Solo/Score.php`, `config/osu.php`
- https://osu.ppy.sh/wiki/en/Help_centre/Upgrading_to_lazer
- https://osu.ppy.sh/home/news/2024-01-30-osulazer-updates-performance-points-now-live
- https://osu.ppy.sh/wiki/en/Performance_points
- https://github.com/MingxuanGame/LazerAuthlibInjection
