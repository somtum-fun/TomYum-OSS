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
  spectator server (`spectator`, from [sundeiii/tomyum-spectator](https://github.com/sundeiii/tomyum-spectator), a checkout next to TomYum) with one change: replays go
  to `public/uploads/replay` (where the website reads them) instead of Amazon S3. Caddy serves it
  as `https://spectator.localtest.me` (`deploy/caddy/Caddyfile.local`).

- **Maps the website doesn't have yet**: lazer asks about every map the player has. The fork's
  `TomYumImportMissingBeatmaps` middleware imports a missing beatmap or set from bancho through
  TomYum (`POST /_admin/beatmaps/import`, `/_admin/beatmapsets/{id}/import`) before the request is
  handled. Maps osu! doesn't know are remembered for an hour.

- **Presence across clients** (2026-09-28): the spectator build also publishes who's online on lazer,
  and what they're doing, to Redis every 5 seconds (`tomyum:lazer-presence`, from
  `TomYumPresencePublisher.cs` in tomyum-spectator). TomYum merges it with stable players and website
  visitors (`OnlinePlayers`): the staff panel's online list, Discord's `/online`, `!alert`, and the
  website's online statistics (`osu_banchostats`, written every minute: the front page's counts and graph).

- **Medals**: ppy's processor gives lazer plays skill, mod introduction and statistics medals under
  osu!'s ids; TomYum gives pack medals for both clients (docs/achievements.md).

- **Beatmap submission** (2026-09-28): the editor's Submit goes to TomYum
  (`{API URL}/beatmap-submission`, lazer's default with LazerAuthlibInjection). Custom sets are
  downloaded and played like any other (docs/custom-beatmaps.md).

Custom-map pp works (the processor downloads `.osu` files from TomYum, `BEATMAP_DOWNLOAD_PATH`).

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

## Daily challenge

Lazer's daily challenge (main menu → Daily Challenge) is osu-web's own feature: at 00:05 UTC its
scheduler (`daily-challenge:create-next`) opens a playlist room from the first unused entry in
`daily_challenge_queue`, and at 00:10 it works out the players' streaks and percentiles
(`daily-challenge:user-stats-calculate`; they show on profiles and in the daily challenge ranking).
The spectator server tells lazer clients the room is open.

TomYum keeps that queue filled (`DailyChallengeQueueJob`, hourly): a week ahead, each a random
ranked map made for the day's ruleset, 1–5 minutes long, 4–6.5★, from a set that was never a daily
challenge. Players may add EZ, NF, HD, HR (FI and MR in mania), SD, PF and FL, but no rate changes.
The Discord bot announces each day's room in the map of the day channel and posts the top five when
it closes. Settings (`tomyum.env`):

```
TOMYUM_DAILYCHALLENGE__ENABLED=true         # false: fill daily_challenge_queue yourself
TOMYUM_DAILYCHALLENGE__DAYSAHEAD=7
TOMYUM_DAILYCHALLENGE__MINSTARS=4
TOMYUM_DAILYCHALLENGE__MAXSTARS=6.5
TOMYUM_DAILYCHALLENGE__RULESETS=osu         # rotates by day, e.g. osu,osu,taiko,osu,osu,catch,mania
```

To open a day's room before 00:05 (e.g. the first time), fill the queue and run
`docker compose exec osu-web php artisan daily-challenge:create-next`.

## Ranked mods

lazer plays give pp (and count for medals) only when every mod on them is ranked. osu! decides that
in its game code (`Mod.Ranked`: the usual mods with their normal settings). TomYum builds ppy's score
processor from source (`deploy/score-processor`) with one change: mods staff switch on in the admin
panel (**Admin panel → Ranked mods**) count as ranked too, whatever their settings.

- The processor makes `tomyum_ranked_mods` (osu-web's database) when it starts and writes which mods
  osu! ranks (`osu_ranked`, shown in the panel); the panel sets `ranked`.
- Every 30 seconds it looks for mods whose `ranked` changed (`applied` is the value old scores were
  last worked out for): lazer scores with that mod lose their pp and get it worked out again, go
  back into the search index, and their players' totals are recalculated.
- Relax/Autopilot plays (RX in osu!, taiko, catch; AP in osu!) wait in the processor until TomYum's
  import (`LazerRelaxImport`, every second) has taken them into the relax/autopilot statistics; at most
  15 seconds (`TomYumRelaxWait.cs`). The game asks for the player's statistics once the processor says
  a score is processed, so its results screen then shows the play counted (the client asks for the
  `osurx`/`osuap`/... statistics after an RX/AP play).
- Relax and Autopilot aren't offered (they have their own leaderboards here, like stable's); Classic
  marks stable plays. Stable plays aren't affected.
- The lazer client still shows such a mod as unranked while playing: only the server's answer changes.

## Last.fm

Players connect Last.fm in the website's account settings (**Last.fm → Connect Last.fm**; the section
shows when the server has a Last.fm key). Their plays are then scrobbled, stable's and lazer's alike
(`LastFmScrobbler` follows the website's scores): every passed play, as the map's song, in its
original script when the map has it, with its length at the play's speed (DT, HT, …). Songs under 30
seconds aren't scrobbled (Last.fm's rule). While they play, Last.fm shows the song as "now playing"
(from the online list: stable's presence, lazer's spectator server). Someone who takes the server's
access away on Last.fm is disconnected here too. Their settings list their last ten Last.fm scrobbles
(Spotify and the rest too), the ones from osu! here saying whether they were played on stable or lazer
(the server keeps its last 50 per player to tell), and their profile shows what Last.fm says they're
listening to right now; both show the cover art (the map's cover for osu! plays) and are asked for
again every 10 seconds while the page is open (Last.fm itself at most every 10 seconds per player).

The website's API documentation (generated at build, like osu.ppy.sh/docs) is at `/docs/`. The key and secret stay in the game server
(`TOMYUM_LASTFM__APIKEY`, `TOMYUM_LASTFM__APISECRET`); the website asks it for the player's status.

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
