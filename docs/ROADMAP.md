# TomYum Roadmap & TODO (human-facing)

> **Docs convention.** `docs/` holds the documentation (contributors, operators): one page per
> feature, plus this checklist.

_Last updated: 2026-09-29. .NET 10; production at sundei.eu (stable, lazer and the website)._

This is the living checklist for the big feature wave requested on 2026-07-07: `.osu` cache →
plugin host → moderation/HWID → spectating → DAN/Session ports → custom beatmaps → **osu!lazer
support**. Work top-to-bottom; each box is committed + tested on its own so any mistake reverts
cleanly. Deferred by explicit decision: **IRC** (do lazer+stable first). circleguard was deferred
too and is now done (Anticheat, below).

Legend: `[ ]` todo · `[~]` in progress · `[x]` done · 🔒 blocked-by · ⚖️ decision needed.

---

## Phase 0 — Foundations (small, unblock everything)

- [x] **`.osu` disk cache** — `OsuFileCache` (C#, L2 on disk) + sidecar L2 (`OSU_CACHE_DIR`). Vanilla
      and rx/ap now reuse one download; survives restarts; seam for serving custom `.osu` locally. `bd1d2e1`
- [x] **PP sidecar smoke test + go-live** — `smoke-test.sh`, `pp-sidecar` compose service (persistent
      volume + healthcheck), startup readiness log. Verified 11.70pp / 2.42pp, cache write. `bd1d2e1`
- [x] ⚖️ **Version bump** → **`0.1.0-dev`** (minor; opens the feature wave; no MAJOR until `1.0.0`). `bd1d2e1`
- [x] **EF Core migrations** — `src/TomYum.Persistence/Migrations` (InitialCreate 2026-09-27, then one
      per change: client hashes, Discord links, official medal ids, custom beatmap sets).

## Phase 1 — Plugin host 🔒 Phase 0 migrations for `IPluginDataAccess`
_ABI already exists (`TomYum.Plugins.Abstractions`); ADR-0007 already picks `.dll` (AssemblyLoadContext)
+ loose `.cs` (Roslyn). This phase is the missing **host implementation**._

- [x] Concrete event types in Abstractions: `ActionChanged`, `ScoreSubmitting` (vetoable),
      `ChatMessage`, `PlayerLogin`, `SpectateFrame` + `VetoableEvent` base. `563b9e5`
- [x] Host services: `PluginContext`, `EventBus`, `CommandRegistry`, `HttpRouteRegistry` (+ middleware),
      `PluginConfig`, `PluginLogger`, `PluginDataAccess` (trusted-only), `HostServiceRegistry`,
      `HostPlayerView`. `563b9e5`
- [x] `IPluginLoader` impl — `.dll` via collectible `AssemblyLoadContext`; loose `.cs` via Roslyn
      (`plugin.json`, gated by `AllowLooseSource`). `563b9e5`
- [x] `PluginHostService` (`IHostedService`) — load → Initialize → Shutdown + ALC unload; wired in `Program.cs`. `563b9e5`
- [x] Per-player state bag on `OnlinePlayer` (transient). `563b9e5`
- [~] Hot-path publish sites: `ActionChanged` live now (`563b9e5`); `ScoreSubmitting` veto, `SpectateFrame`,
      `PlayerLogin`, `ChatMessage` land with their consumers in Phases 2–4.
- [x] Command bridge — unmatched `!cmd` → plugin `ICommandRegistry`. `563b9e5`
- [x] **`!plugins`** — lists loaded plugins (name / version / author). `563b9e5`
- [x] Example plugins: compiled (`.dll`) + loose (`.cs`), both verified loading into the live server. `563b9e5`
- [x] ⚖️ **Plugin template** — lives in its **own repo**,
      [somtum-fun/tomyum-plugin-template](https://github.com/somtum-fun/tomyum-plugin-template) (compiled +
      loose shapes + `docs/writing-plugins.md`). It is example code, **not required** by the server, so it
      is **not tracked** in TomYum (a local copy under `templates/` is gitignored). Human docs
      `docs/plugins.md` remain here.

## Phase 2 — Moderation + HWID anti-cheat 🔒 Phase 0 migrations
_All packet IDs exist (`AccountRestricted=104`, `UserSilenced=94`, `SilenceEnd=92`, `TargetIsSilenced=101`).
Model matches bancho.py: **restricted = shadow-ban** (still logs in, hidden from others, own scores
only visible to self); **banned ≡ restricted** (no hard login reject); **silence = orthogonal timed mute**._

- [x] State model: `User.SilenceEnd` (unix), restriction via a privilege bit + `Verified` bit;
      migration + a moderation **audit-log** table. _(Done as: the website's restriction/silence rows,
      `verified_users`, `staff_log`.)_
- [x] Login enforcement: restricted → `ACCOUNT_RESTRICTED(104)` + bot DM + one-way presence +
      resume real silence countdown; grant `Verified` on first in-game login.
- [x] Enforcement points: presence hiding (stats/presence/logout broadcasts skip restricted),
      chat silence gate (`TargetIsSilenced`/`UserDmBlocked`), multiplayer block, **shadow leaderboard**
      (restricted scores hidden from others, visible to self — the subtle carve-out), force-logout on restrict.
- [x] Commands: `!restrict` / `!unrestrict` (= un-ban) / `!ban` (alias) / `!silence` / `!unsilence` /
      `!alert` — staff-gated (reload domain privileges, don't trust the wire bitmask).
- [x] **HWID capture** — `client_hashes` table (userid + 4 md5s, occurrences); upsert at login.
      The 4 hashes are **already parsed** today, just dropped.
- [x] **Multi-account / ban-evasion gate** — at first login (unverified), reject if any HWID match
      is a restricted account: `USER_ID(-1)` + `"Please contact staff directly to create an account."`
      Guards: `runningunderwine` → uninstall-only; exclude `md5("0")` disk id.
- [x] **Shared-machine alert** — a verified account that logs in from a restricted account's machine
      (restricted after the alt got in, or verified by hand) gets in, but staff get one Discord alert
      per pair in the reports channel with Restrict / Silence buttons (`user.shared_machine`). 2026-09-29
- [x] Presence geolocation — IP→country fallback: when `CF-IPCountry` is missing, `XX` or `T1`, the
      client address is looked up in DB-IP's free country database (CC BY 4.0, downloaded to
      `data/geoip` and refreshed monthly; `GeoIp:Download=false` turns the download off). Used at
      login (an `xx` account gets its country then) and in-game registration. 2026-09-29

## Phase 3 — Stable spectating 🔒 (none) — prereq for DAN
_All 10 packet IDs exist; `WriteRaw` fastpath exists (see `MatchScoreUpdateHandler`). Just handlers + state._
_Core watch-and-relay done 2026-07-08 (`docs/spectating.md`). 143 tests. Chat channel deferred._

- [x] `Spectators` / `SpectatingUserId` on `OnlinePlayer` (id-keyed set). `48304f5`-line
- [x] Handlers: `StartSpectating(16)`, `StopSpectating(17)`, `SpectateFrames(18)` (raw relay),
      `CantSpectate(21)` + the six server encoders (`SpectatorManager`). Verified live (no packet-id clash).
- [x] Logout teardown of spectate state (`SpectatorManager.TeardownForLeaver`; drains own spectators,
      bancho.py fork behaviour). Shared with a future reaper.
- [x] Ephemeral `#spectator` chat channel (`SpectatorChannel` on `OnlinePlayer`, mirroring
      `Match.Channel`; hidden from the login channel-list; host joins on first spectator, all leave when
      empty) + `#spectator` routing in `SendPublicMessageHandler`. 2026-07-09; 6 tests.
- [ ] Publish `SpectateFrameEvent` on the plugin bus (lands with the DAN plugin, Phase 4).

## Phase 4 — DAN mode + Session plugins (ports) 🔒 Phase 1 + Phase 3
_Clean-room reimplementations of the old server's DAN mode and session.py (that fork is GPL — behaviour only)._

- [ ] **DAN mode** plugin — mania clear validator: bot spectates on `CHANGE_ACTION`, taints on pause
      frame, vetoes score submit; `is_dan` per-diff flag + set-granularity tag endpoint; transient per-login state.
- [~] **Session** plugin — `session.<domain>` vhost, opaque web-session table (IP/UA binding).
  - [x] **Auth core** (loose source `plugins/tomyum-session/`): `/login` (bcrypt, mint token, cookie),
        `/check-session`, `/logout`, `GET`/`DELETE /user/sessions/web` (list + masked-suffix revoke),
        `/health`, expiry reaper. Trusted plugin owns its `sessions` table. Auth resolved in-plugin
        (`requireAuth:false`). Verified live vs MySQL. Docs: `docs/session-plugin.md`. (2026-07-08)
  - [ ] Feature endpoints (profile, beatmap mode-status, clans, donations, admin, uploads,
        change-password) — the other ~11k lines of `session.py`. Scope out Thai-deployment specifics
        (TrueMoney/PromptPay/OAuth/email) as optional plugin config.
  - [ ] Flip to `requireAuth:true` once the host grows an auth-resolver hook (Session provides it).
  - [ ] Ship compiled `.dll` variant (net10.0 lib ref Abstractions + BCrypt.Net-Next).

## Phase 5 — Custom beatmaps 🔒 Phase 0 (storage/migrations); `!map` needs none
_Custom sets are uploaded as `.osz` from the BN panel, or submitted from either game's editor, and
served everywhere (docs/custom-beatmaps.md)._

- [x] **`!map` rank/love/unrank/qualified** + `/np` last-map tracking — status-only, no osz2. `/np` to BanchoBot answers with the map, status and pp at 95–100%.
- [x] Custom-set model: `custom_beatmapsets` (owner, WIP/pending) + `custom_beatmap_slots` (ids handed
      out); custom maps are never re-synced from the osu! API. 2026-09-28
- [x] **osz2 reader** (C#, ported from MIT osz2-go) + the stable editor's getid/upload/post endpoints;
      always full uploads, so no bsdiff. 2026-09-28
- [x] **Lazer beatmap submission** (`/beatmap-submission`, ppy's protocol and rules; lazer's JWT checked
      against the website's Passport key). 2026-09-28
- [x] In-place map updates in stable: an unknown hash whose file name is a known difficulty (here or
      in osu!'s set) answers "update available", and `/web/maps/{file}` gives the new `.osu` (custom
      maps from here, osu!'s from osu!). File names follow osu!'s rule (`OsuFileName`). 2026-09-29
- [x] Graveyard: custom sets still WIP/pending and untouched for four weeks go to the graveyard
      (daily); an update brings them back. 2026-09-29
- [x] Local serving — `/d/{setId}`, osu!direct search/set info, `b.*` thumbnails and previews, the
      website's covers and audio preview come from TomYum for sets from 10,000,000. `.osu` files are kept
      for pp (TomYum's and ppy's processor). 2026-09-28
- [x] **`.osz` upload in the BN panel** — new custom set or a new version of one (difficulties keep
      their ids by name), ids written into the files, mapper owns it, pending status, covers made from
      the background. 2026-09-28

## Phase 6 — Command framework refactor (polish, optional)
- [x] Sunrise-style `[ChatCommand(name, Usage, Help, Access, Hidden)]` on BanchoBot's command methods,
      found by reflection (`ChatCommandTable`); `!help` is generated from them (plugin commands listed
      too), and plugins can't take a built-in name. 2026-09-29

## Phase 7 — osu!lazer support ✅ (done differently: ppy's own services + osu-web)
_The plan below was to build lazer's server inside TomYum. Instead lazer runs on ppy's open-source
services next to osu-web (docs/lazer.md): osu-web is the API, OAuth and chat; ppy's spectator server
does multiplayer/spectating/presence; ppy's score processor does lazer pp and stats. TomYum shares the
accounts, chat and leaderboards with them._

- [x] Identity scheme — one account table (osu-web's); BanchoBot is user 1 on both.
- [x] Shared presence/chat — stable ↔ website chat bridge (lazer chats through the website), BanchoBot
      answers on both, `/np` from both.
- [x] OAuth2, `/api/v2`, score submission, SignalR hubs, notification websocket — ppy's services
      (osu-web, osu-server-spectator, osu-queue-score-statistics, notification-server).
- [x] Minimum bar: one account logs into **both** clients · shared BanchoBot · shared `#osu` cross-client
      chat · osu! beatmap status works.
- [x] Minimum bar, last item: **custom beatmap status in lazer** — custom sets served locally, submitted
      from lazer's editor (Phase 5). 2026-09-28
- [x] Shared leaderboards (stable scores standardised for lazer; lazer scores for stable's website view),
      lazer score submission, lazer beatmap submission.
- [x] Relax on lazer — lazer RX plays go on TomYum's relax leaderboards with relax pp and stats. 2026-09-29
- [x] Deployment — players use the stock lazer client with the injection ruleset (docs/lazer.md).
- [x] Stable friends (the website's `phpbb_zebra`, shared with lazer and the site): login list, add/remove,
      `osu-getfriends.php`; the selected mods / friends / country leaderboard tabs; `osu-getfavourites.php`;
      away messages and friends-only PMs (also IRC's `AWAY`). 2026-09-29
- [x] Stable in-game comments (`osu-comment.php`: song / difficulty / replay, supporter colours, staff as "bat")
      and the user panel's update filter (`ReceiveUpdates`: all / friends / none). 2026-09-29
- [x] Request log for the website/API and lazer hosts (Caddy, rotated in its data volume);
      `deploy/production/misses.sh` lists what clients asked for and didn't get. 2026-09-29
- [x] Stable map ratings (`osu-rate.php`, kept with the website's ratings; the leaderboard shows the average),
      the client's anti-cheat flags (`lastfm.php`: hq!osu restricts, relife traces warn and alert staff),
      osu!'s beatmap tags synced daily for lazer's tag voting. 2026-09-29
- [x] Admin panel: seasonal backgrounds (lazer + stable's `osu-getseasonal.php`), Quick Play / Ranked Play
      pools (the spectator server fills them with featured-artist maps; extra maps added there), news posts
      (osu-wiki markdown, not synced from GitHub). The changelog from git history (`deploy/production/changelog.sh`,
      run by `deploy.sh`); popular beatmaps from this week's plays. 2026-09-29
- [x] One osu! API budget for the whole server (50 requests a minute; osu! asks for at most 60) and map
      lookups from catboy.best first (osu!'s v2 format; paused on a 429 until Retry-After, an hour on a
      403; bulk imports and backfills skip it, since Mino counts sweeping many sets as crawling). 2026-09-29
- [ ] Stable tournament client (`TournamentMatchInfoRequest`, match channel join/leave); lazer Quick Play
      (matchmaking pools: none set up yet).

## osu-web website 🔒 Phase A: none · Phase B: decisions below
_osu-web (ppy's Laravel site) runs unmodified as the website; long-term it owns the schema and TomYum
uses its tables. Human doc: `docs/osu-web.md`._

- [x] **Phase A — side by side:** compose overlay (`deploy/docker/docker-compose.osu-web.yml`) puts
      TomYum on osu-web's MySQL/Redis; Caddy splits `osu.<domain>` (stable `/web/*`, `/d/*`,
      in-game `POST /users` → TomYum; rest → osu-web). Accounts/scores not shared yet.
- [x] Custom beatmap id floor → **10,000,000** (`Beatmap.CustomIdFloor`), so custom ids fit osu-web's
      `MEDIUMINT UNSIGNED` `beatmap_id`/`beatmapset_id` (max 16,777,215). Was 100,000,000.
- [x] BanchoBot as a real `phpbb_users` row (id 1, `bot` group) + osu-web `BANCHO_BOT_USER_ID=1`.
- [x] **Phase B, accounts:** `OsuWebUserRepository` over `phpbb_users` + groups (`Database:OsuWebName`);
      website and in-game accounts are the same. No auto-Owner in this mode (staff via osu-web `admin`).
- [x] **Phase B, beatmaps (mirror):** every map written to TomYum's table and osu-web's
      `osu_beatmapsets`/`osu_beatmaps`; startup copies missing ones. Raw osu! API status kept.
- [x] **Phase B, scores + stats (mirror):** vanilla scores → osu-web `scores` (legacy format, `CL`),
      `osu_user_stats*` rebuilt per play, `rank_score_index` recomputed, scores queued for
      `osu-elastic-indexer`. Relax/autopilot stay TomYum-only.
- [x] **Phase B, migration:** `tools/MigrateToOsuWeb` moves an existing server's players to osu-web
      accounts, renumbers TomYum's data and copies the history (maps, scores, stats, ranks).
- [x] **Phase B, medals:** TomYum's catalog added to osu-web's `osu_achievements`; unlocks sent through
      osu-web's InterOp API (activity entry + notification), direct insert as fallback; in-game sign-ups
      pushed to the website search via InterOp. Local end-to-end guide: `docs/local-testing.md`.
- [ ] **Phase B, rest:** TomYum reads scores (and medals) from osu-web → beatmaps become osu-web-only
      (RecalcPp follows). Replaces the EF-migrations item in Phase 0 for these tables.
- [x] Phase 7 rescope: osu-web serves `/api/v2` + `/oauth/token`, ppy's spectator server the realtime hubs.
- [x] **Daily challenge** — osu-web opens the room each day; TomYum keeps its queue filled and the
      Discord bot announces and settles it (docs/lazer.md). 2026-09-29
- [x] **Stable matches the website** — the stable panel shows the website's combined numbers (so level,
      ranked score and pp match lazer and the profile); stable and lazer plays both get "achieved rank
      #N" activity entries (top 1000) and #1s in `#announce`. 2026-09-29

## Achievements (medals) 🔒 (none) — done 2026-07-09
_Clean-room port of bancho.py's vanilla medal catalog; custom/website medals are a later Session slice._
- [x] Catalog in code (`TomYum.Server/Achievements/`): 83 vanilla medals (std combo 500–2000, 11
      mod-intro medals, per-mode skill pass/FC packs) with conditions ported verbatim.
- [x] `user_achievements` table (unlocks only; definitions live in code) + EF/in-memory repos.
      `SchemaGuard` idempotent `CREATE TABLE IF NOT EXISTS` bridges the EnsureCreated gap on existing DBs.
- [x] Awarded in `ScoreSubmissionEndpoint` (gate: passed + ranked) → `achievements-new:` response field
      (drives the popup). 11 tests; verified live vs MySQL.
- [x] Medal icon proxy (`AssetsEndpoints`, `assets.<domain>`): devserver rewrites `assets.ppy.sh` →
      `assets.<domain>`, so icons are cache-and-served from ppy's CDN → `data/assets/medals/client`.
      Traversal-guarded; 13 tests; verified live (cold/cache-hit/@2x=200, unknown/traversal=404).
- [x] Restricted gate (Phase 2), mod-adjusted star rating (medals, score cards, `/np`), medal listing
      (website profiles, Discord `/medals`).
- [ ] Custom medals.

## Cross-cutting infra (interleave as needed)
- [x] **Discord bot** (docs/discord.md): slash commands, two-way `#osu` relay, announcements
      (#1s, big plays, medals, new players, ranked/loved), account linking via BanchoBot with
      group roles, staff user/server log channels. 2026-09-28.
- [ ] Redis cross-node online state (multi-instance) — needed before scaling lazer realtime; ADR-0003.
- [ ] Storage providers multi-target write / single read — needed for Phase 5 upload; ADR-0006.
- [x] **Anticheat** — circleguard (sundeiii/lazer-circleguard, C#) checks every ranked pass, stable and
      lazer, incl. against osu!'s top replays (docs/anticheat.md). 2026-09-29
- [x] **IRC server** (docs/irc.md): port 6667, passwords from BanchoBot's `!irc`, the game's chat
      handlers behind it. 2026-09-29

---

### Working rules (agreed 2026-07-07)
- Commit every slice (revertable). Test every slice. Warnings are errors.
- Every feature ships a page in `docs/` (or updates one) and updates this checklist.
- Think/question/plan **then** build. Keep it contributor-friendly — a human must be able to pick it up.
