# TomYum Roadmap & TODO (human-facing)

> **Docs convention.** `docs/` holds the documentation (contributors, operators): one page per
> feature, plus this checklist.

_Last updated: 2026-10-01 (version 0.2.0-dev)._ .NET 10; production at sundei.eu (stable, lazer and the website)._

This is the living checklist for the big feature wave requested on 2026-07-07: `.osu` cache →
plugin host → moderation/HWID → spectating → DAN/Session ports → custom beatmaps → **osu!lazer
support**. Work top-to-bottom; each box is committed + tested on its own so any mistake reverts
cleanly. Deferred by explicit decision: **IRC** (do lazer+stable first). circleguard was deferred
too and is now done (Anticheat, below).

Legend: `[ ]` todo · `[~]` in progress · `[x]` done · ⏸ deferred by decision · 🔒 blocked-by · ⚖️ decision needed.

---

## Versions
- `0.1.0-dev` (2026-07-07): the feature wave opens (Phase 0).
- `0.2.0-dev` (2026-10-01): lazer and the website (osu-web) as accounts and scores, moderation and the
  staff panels, anti-cheat, Discord bot, IRC, bot accounts, BN/NAT tools (QAT), maintenance mode,
  support tickets, the store. Still `-dev`: not ready for production elsewhere or open source.

## Path to 1.0 (production-ready, open source) ⚖️
_Before `1.0.0`: TomYum should run anyone's server without this one's choices baked in._
- [ ] Hard-coded features into plugins (the plugin host, Phase 1): server-specific behaviour (welcome
      texts, Discord channels and wording, store/payments, maintenance texts, ticket categories and
      wording, Relax/Autopilot pp source, custom medals) behind plugins or settings, the core generic.
- [ ] No server-specific names or addresses in code, docs or defaults (domains, emails, branding);
      everything from configuration, with examples.
- [ ] Clean-up: dead code and leftovers (unused `Storage` stubs until implemented), consistent naming.
      Done 2026-10-02: `TomYum.Caching/PlaceHolder.cs` removed, the test projects build without warnings.
      Kept for now by decision (2026-10-02): the empty `TomYum.Lazer` project and the `services/` stubs.
- [ ] Docs for operators (install, upgrade, backup, every setting) and contributors (architecture,
      plugin API), a changelog per version, a licence review of what's bundled.
- [ ] Security review (admin API signing, InterOp, uploads, OAuth), and the open items above that
      affect every server (object storage, scaling).
- [ ] Then `1.0.0` without `-dev`; afterwards SemVer: breaking changes bump the major version.

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

### Plugins in production (2026-10-01)
_The host works (Phase 1), but the production server runs **0 plugins**: nothing is mounted at
`/app/plugins`._

- [ ] Mount `deploy/production/plugins/` into the server container and document deploying one
      (compiled `.dll` or loose `.cs` with `Plugins:AllowLooseSource`).
- [ ] Decide which to run: the Session plugin (`plugins/tomyum-session`), DAN mode (⏸ above), custom ones.
- [ ] Publish the remaining hot-path events (`ScoreSubmitting` veto, `SpectateFrame`, `ChatMessage`) as
      their first consumers land.

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

- [ ] ⏸ **DAN mode** plugin (not wanted for now, 2026-09-29) — mania clear validator: bot spectates on `CHANGE_ACTION`, taints on pause
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

- [x] Identity scheme — one account table (osu-web's); BanchoBot is user 3 on both, like osu!'s
      (moved from 1 on 2026-09-30: lazer treats id 1 as its guest user and shows no avatar for it).
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
- [x] Admin panel: seasonal backgrounds (lazer + stable's `osu-getseasonal.php`), Ranked Play
      pools (the spectator server fills them with featured-artist maps; extra maps added there), news posts
      (osu-wiki markdown, not synced from GitHub). The changelog from git history (`deploy/production/changelog.sh`,
      run by `deploy.sh`); popular beatmaps from this week's plays. 2026-09-29
- [x] One osu! API budget for the whole server (50 requests a minute; osu! asks for at most 60) and map
      lookups from catboy.best first (osu!'s v2 format; paused on a 429 until Retry-After, an hour on a
      403; bulk imports and backfills skip it, since Mino counts sweeping many sets as crawling). 2026-09-29
- [x] Admin panel, more: Ranked Play pools with their map lists, badges, user titles. 2026-09-29
- [x] Relax/Autopilot on the website: profiles (`/users/{id}/osurx` and the like, with a Vanilla/RX/AP
      switch), rankings (`?variant=rx|ap`), score pages with pp and the replay; plays from stable get the
      Classic mod, lazer's say so (`FromLazer`). Rank history: everyone's rank once a day, 90 days, for
      the profile graph and highest rank (`ModeRankHistoryJob`). 2026-09-30
- [x] BanchoBot counts as online on the website and in lazer, with its avatar and flag (Saint Helena);
      stable and IRC players count as online there too. 2026-09-29
- [x] Admin panel: tournament banners on profiles (osu-web's own banners: a tournament, one image,
      given to players; uploaded or fetched from an address, stored at normal and double size). 2026-09-30
- [x] Last.fm scrobbling: players connect it in account settings; passed plays (stable and lazer)
      are scrobbled as the map's song, and shown as "now playing" while they play. 2026-09-30
- [x] Community → Activity: the whole server's recent activity (#1s, top 50 ranks, medals, beatmaps,
      supporters, name changes), by type and mode, new entries appearing by themselves. 2026-10-01
- [x] Admin panel account page: standing and notes, lock (no logins anywhere), anti-cheat whitelist,
      devices (wipe HWID), login history, connected profiles, content resets, score rollback, name
      rollback; stable device verification on the website (off by default). Panels restyled. 2026-10-01
- [x] Ranked mods: the admin panel picks lazer mods that count as ranked (pp, medals) besides osu!'s;
      TomYum's build of ppy's score processor reads them and works out old scores again. 2026-09-30
- [x] Stable tournament client — its windows are extra sessions on the same account (they don't kick
      the player's game or show as players), follow a match's state and chat
      (`TournamentMatchInfoRequest`, match channel join/leave) and spectate players. 2026-09-30

## osu-web website 🔒 Phase A: none · Phase B: decisions below
_osu-web (ppy's Laravel site) runs unmodified as the website; long-term it owns the schema and TomYum
uses its tables. Human doc: `docs/osu-web.md`._

- [x] **Phase A — side by side:** compose overlay (`deploy/docker/docker-compose.osu-web.yml`) puts
      TomYum on osu-web's MySQL/Redis; Caddy splits `osu.<domain>` (stable `/web/*`, `/d/*`,
      in-game `POST /users` → TomYum; rest → osu-web). Accounts/scores not shared yet.
- [x] Custom beatmap id floor → **10,000,000** (`Beatmap.CustomIdFloor`), so custom ids fit osu-web's
      `MEDIUMINT UNSIGNED` `beatmap_id`/`beatmapset_id` (max 16,777,215). Was 100,000,000.
- [x] BanchoBot as a real `phpbb_users` row (id 3, `bot` group) + osu-web `BANCHO_BOT_USER_ID=3`.
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
- [ ] ⏸ **Phase B, rest** (not now, 2026-09-30: the mirror keeps them in step): TomYum reads scores (and medals) from osu-web → beatmaps become osu-web-only
      (RecalcPp follows). Replaces the EF-migrations item in Phase 0 for these tables.
- [x] Phase 7 rescope: osu-web serves `/api/v2` + `/oauth/token`, ppy's spectator server the realtime hubs.
- [x] **Daily challenge** — osu-web opens the room each day; TomYum keeps its queue filled and the
      Discord bot announces and settles it (docs/lazer.md). 2026-09-29
- [x] **Stable matches the website** — the stable panel shows the website's combined numbers (so level,
      ranked score and pp match lazer and the profile); stable and lazer plays both get "achieved rank
      #N" activity entries (top 1000) and #1s in `#announce`. 2026-09-29

## Beatmap Nominators & NAT 🔒 (none)
_Ranking on this server: nominations, qualify, rank and love already work from the discussion page and
the BN panel (`/panel/bn`). These close the gaps. Requested 2026-10-01._

- [x] **Uploads and updates in activity** — after a stable/lazer submission or a BN panel upload,
      osu-web's InterOp `broadcast-new` (new set; also notifies the mapper's followers) / `broadcast-update`
      (new version, by the uploader), so "… has submitted a new beatmap" / "… has updated the beatmap"
      reach the mapper's Recent activity and Community → Activity. (Rank/qualify/love entries already do;
      disqualify, nomination reset, unlove and kudosu don't on osu! either.) 2026-10-02
  - [x] Reviving a graveyarded set: osu!'s "… has been revived from eternal slumber by …" entry
        (`broadcast-revive`, followers notified) instead of "updated". 2026-10-02
- [x] **Nomination workflow verified live** (2026-10-01) with throwaway BN, probationary BN, NAT and Loved
      accounts on an imported set: hype gate, nominate ×2 → qualified, NAT problem → disqualify (and the
      game follows each step), unresolved problems block nominating, qualified → ranked by `modding:rank`,
      love by Project Loved; ranked/qualified/loved entries in Community → Activity. osu-web runs osu!'s
      own rules (modes per nominator, a probationary BN can't give the final nomination, 10 a day, genre
      and language required; remove from Loved is admin-only, as osu-web has it).
  - [x] Fixed: the BN panel's "Pending"/"bancho" left the old nominations current, so BNs couldn't
        nominate again; they're reset now, as on a disqualification.
  - [x] Fixed: NAT had no modes (`nat.has_playmodes` was 0 here; osu! gives NAT modes) and a BN could be
        saved with none (then they can't nominate); the admin panel now asks for at least one mode.
- [x] **QAT / BN Management** ([pishifat/qat](https://github.com/pishifat/qat)) — fork
      `example-org/private-repo` (branch `prod`): server address configurable, our group ids mapped (7 is
      everyone's default here), an event feed from `/api/v2/beatmapsets/events` instead of the Aiess bot,
      deployed as `qat` + `qat-mongo` behind `bn.<domain>` (docs/qat.md). 2026-10-01
  - [x] DNS `bn.<domain>`; signed in from a browser (2026-10-01). Fixed: default settings on a new
        database; BN/NAT without an evaluation round.
  - [x] BN applications: no kudosu; Discord + osu! account linked (account settings → Connections) and no
        osu! restriction seen in 12 months (docs/qat.md). Kudosu by hand for admins/developers
        (`/panel/kudosu`). 2026-10-01
  - [ ] Discord application for linking Discord (`DISCORD_OAUTH_CLIENT_ID`/`SECRET`); the osu! app's
        callback set on osu.ppy.sh.
  - [ ] Discord webhooks (`qat-webhooks.json`) and `enableAutomation`; a bot account + OAuth client for
        `enableMessages` (docs/bots.md).
- [x] **Project Loved** on the website (Community → Project Loved; `loved.<domain>` redirects there),
      docs/loved.md: players ask for maps (mode + why) and review requests (for/against); the Project
      Loved group loves (Loved at once), rejects, removes or blacklists, with reasons. Built on sundei's
      in-website Loved pages without rounds or polls; the separate Loved sites are retired. 2026-10-03

## Old clients (2013–2015 first) — requested 2026-10-01, planned 2026-10-02
_Decided 2026-10-02: builds from **late 2013 to 2015** first; their plays go on the **same leaderboards
as today's stable**; players connect with **Titanic! Hook**. Research: [osuTitanic](https://github.com/osuTitanic)
(anchor = bancho, deck = web), Lekuruu's per-build protocol notes ([chio](https://github.com/Lekuruu/chio.py),
`chio/clients/b2013….py`) and [bancho-documentation](https://github.com/Lekuruu/bancho-documentation/wiki).
osuTitanic's code is GPL and TomYum is MIT: behaviour only, written fresh (as with the Session plugin)._

What the research found:
- **Connecting:** [Titanic! Hook](https://github.com/osuTitanic/hook) (GPLv3, a loader dropped next to an
  untouched `osu!.exe`) patches the client in memory: every `*.ppy.sh` host becomes `*.<ServerName>`.
  Builds come from [Osekai Snapshots](https://osekai.net/snapshots/); we host no osu! files. Our side: its
  `Titanic!.cfg` with `ServerName=<domain>` (or a rebranded fork later). Found testing b20140624
  (2026-10-02): builds on .NET 2.0 (before late 2015) need **`Titanic.fallback.exe`** (the .NET 2.0
  loader); under `Titanic.exe` (.NET 4) the game runs but its importer fails every `.osz` (all go to
  `Songs\Failed`). Downloaded files need Windows' "from the internet" mark removed (`Unblock-File`),
  or .NET refuses to load `osu!.exe`.
- **Transport:** builds up to **b20130815** speak bancho over raw TCP (13380–13384) to hardcoded IPs; HTTP
  bancho (`osu-token`, as today) from about **b20130915**. sundei.eu is reachable only through the
  Cloudflare Tunnel, which can't carry raw TCP for players, so TCP builds wait (⏸ until a TCP port can be
  exposed). Builds before late 2015 (.NET 2.0) most likely use **plain HTTP**, not HTTPS.
- **Protocol:** packet ids and the frame header are the same as today. Mid-2013 to mid-2015 is protocol
  **18**, b20150915 on is **19**. Differences that matter: 8 multiplayer slots before b20150915; spectator
  and match score frames without the ScoreV2 fields before b20151106 and without the sequence number
  before b20160404; stats pp as a signed 16-bit number (cap 32767); packets the build doesn't know yet
  (e.g. 104 AccountRestricted b20140716, 106 MatchAbort b20141104) must not be sent; no Qualified status
  before b20150826 and no Loved before b20161101 (sent as approved).
- **Web:** score submission is `osu-submit-modular.php` (same score string; key `h89f2-890h2h89b34g-h80g134n90133`
  without `osuver`, else the `osu!-scoreburgr` key; reply without the chart, accuracy 0–1);
  `osu-osz2-getscores.php` with older `vv` formats (date string at vv=1, no Loved before vv=4, replay flag
  from vv=4); also `osu-stat.php`/`osu-statoth.php`, `osu-checktweets.php`, `check-updates.php` (`[]`),
  and `osu-search.php` taking the password as `c`.

Plan (each step committed and tested on its own; the local stack first, a 2014 build as the test client).
Progress 2026-10-02 with b20140624 locally: see docs/old-clients.md for what works.
- [~] **0. Reachable on plain HTTP:** done locally (`Caddyfile.local`: the game's hosts on `http://`
      without redirecting, `c1.`–`c6.`, `s.`) and in the production Caddyfile. On sundei.eu still:
      Cloudflare's "Always Use HTTPS" off (or a rule for the game's hosts), DNS + tunnel hostnames for
      `c1.`–`c3.`, `c5.`, `c6.`, `s.`.
- [~] **1. Version detection:** the build is kept on the session (`ClientBuild`). b20140624 logs in with
      today's client-info line and works with protocol 19 as sent today; picking 18 for older builds
      waits until one is tested.
- [~] **2. Outgoing translation:** not needed for b20140624 (it reads today's packets, 16-slot matches
      included). Dropping unknown packets, capping pp and 8-slot matches wait for an older build that needs them.
- [~] **3. Incoming translation:** frames in the older layout get today's fields (`LegacyFrames`);
      b20140624 already sends today's layout. Multiplayer and spectating today's client: untested (needs
      a second PC).
- [x] **4. Old web endpoints:** `osu-submit-modular.php` (same leaderboards, pp, medals), getscores by
      `vv`, osu!direct downloads and thumbnails passed through (no TLS 1.2), medal images on `s.`, update
      checks. Replays (`osu-getreplay`) and `osu-stat` not seen from b20140624 yet. 2026-10-02
- [x] **5. Rules check for shared leaderboards:** score v1 and mod multipliers unchanged since 2013 in
      every mode (osuTitanic shares boards without converting); safeguards: NoVideo isn't TouchDevice
      before 2014, Co-op refused, taiko/catch plays whose judgements don't cover the map kept off the
      board (`LegacyScoreRules`). 2026-10-02
- [ ] **6. Anti-cheat and verification:** old builds send the same client hashes (exe md5, adapters,
      uninstall id, disk) — keep ban-evasion; decide on device verification and circleguard for them.
- [~] **7. Production:** `docs/old-clients.md` (player setup with `Titanic.fallback.exe`, supported
      builds) and the production Caddyfile done; deploy and the Cloudflare settings next, then test
      multiplayer with a second player and go build by build towards 2013.
- [ ] Later: TCP builds (2013 and older) if a TCP port can be exposed; older eras.

### ⏸ Status 2026-10-03: paused, needs more research (example.com)
_Decided 2026-10-03: old-client work is **paused**. Getting old builds stable needs more research and
far more work than expected, whether through a hook or by patching the client. Priority goes to the
upstream side: the **latest lazer client**, **stable 2023+ (latest)** and **anti-cheat**. Comments and
findings on old clients are welcome; add them here._

Tested on example.com with the production hook ([example-org/private-repo](https://github.com/example-org/private-repo),
MIT, a clean-room loader instead of Titanic! Hook):
- **Works:** no more startup crashes on b1814–b20131009 (the hook never patches osu!'s obfuscated
  `Main`: Harmony 2/MonoMod crashed there; Titanic! Hook avoids it with a Harmony 1 fork), no update
  prompt (`/release/update.php` answers `0`), TCP-era builds reach bancho through the hook's local
  **bancho bridge** (raw TCP in, HTTPS bancho to `c.<domain>` out, so no TCP port or server IP is
  exposed and Cloudflare stays in front), login bodies without `pm_private`/hashes are accepted
  (`LoginRequest`), held logins carry a verify-your-device notice, and the hook fills blank client
  hashes with its own machine id. b20151128.2 logs in, chats, searches and downloads maps.
- **Open problems:**
  - b20151128.2 freezes on a black screen after **Play Solo**: the client posts `osu-error.php` (now
    logged by TomYum as a warning) and a finalizer NRE follows; the cause isn't known yet. It may be a
    server reply the build can't read (then it's a TomYum fix) or an osu! bug.
  - osu!'s own races on .NET 4 (`MapPackage.Close`, `Dispose` finalizers): keeping the process alive
    (legacy exception policy, a `Dispose` guard) only turned crashes into frozen black screens, so a
    crash to desktop stays the default.
  - The hook runs 2.0-era builds on the .NET 4 CLR; native .NET 2 behaviour differs (exception policy,
    timing), which is a likely source of more of these.
  - Old builds have no osu!auth: anything the hook sends (device id, hashes) can be faked, so anti-cheat
    for them is weak until the hook has its own integrity checks.
- **Options to research (none chosen):**
  - Keep the hook (players bring their own `osu!.exe`, no ppy code shipped) and add small, targeted
    in-memory fixes for specific osu! bugs, found by the method's IL shape rather than its obfuscated
    name. Most work per bug, least legal risk, stays MIT.
  - Patch the client like osuTitanic (their download page ships pre-patched builds, "unofficial,
    unlicensed", with a DMCA contact). Easier to fix osu!'s own bugs, but obfuscation means editing IL
    per build (not recompiling decompiled source), it redistributes ppy's code (DMCA risk, can't be
    open source) and every build needs its own patches. If ever done, ship a patcher for the player's
    own copy, never patched binaries.
  - Use Titanic! Hook as docs/old-clients.md describes, and only keep TomYum's server side compatible.

## Bot accounts 🔒 (none) — done 2026-10-01
- [x] Admins/developers make bots in the panel (`/panel/bots`): `bot` group, an owner, an IRC key.
      Owners (user menu → My bots), admins and developers change the avatar, banner, me! page, name,
      website password and IRC password. Bots chat over IRC and use the website (verification codes go
      to the owner); stable and lazer logins and score submission are refused. docs/bots.md

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
- [x] **Operations** (sundei.eu): mail through Proton (`osu@sundei.eu`, email verification on), downtime
      alerts by Discord DM to the admin (`watchdog.sh`, paused while `deploy.sh` runs), nightly backups
      (`backup.sh`, kept on the VM; ⏸ no off-site copy, by decision 2026-10-02), the store says "not available".
      2026-09-29
- [ ] **Scaling up and out** — today one TomYum instance holds every online player, channel, match and
      spectator in memory (`PlayerRegistry`, `ChannelRegistry`, `MatchRegistry`), so bancho can't run as
      several copies behind a load balancer yet (a second copy loops stable's logins: its `osu-token` is
      unknown there). Not needed at today's load (2026-10-01: 4 vCPU/8 GB VM, TomYum ~280 MB, under 3%
      CPU); a few hundred daily players fit one VM. Stages, in order — stop at the first that's enough:
  - [x] **Always:** monitoring to know *what* to scale (usually MySQL/Elasticsearch, not bancho):
        `watchdog.sh` also alerts on disk, memory, CPU, stopped/unhealthy/crashed containers and slow
        requests (docs/monitoring.md); `deploy.sh` caps Docker's build cache at 20 GB (it had reached
        128 GB). 2026-10-02. (Off-site backups: ⏸ skipped, 2026-10-02.)
  - [ ] **1. Scale up:** bigger VM (8/16, then 16/32), NVMe; more osu-web PHP workers, MySQL buffer pool,
        Elasticsearch heap. No code changes.
  - [ ] **2. Split the stack (config only):** MySQL + Redis on their own VM or managed (DigitalOcean
        Managed MySQL/Valkey, AWS RDS/ElastiCache); Elasticsearch, score indexer, circleguard and
        pp-sidecar on a workers VM; uploads/replays to object storage (below). Caveats: DigitalOcean
        Managed MySQL requires a primary key on every table (`sql_require_primary_key`, can be turned off
        via its API) and offers OpenSearch, not Elasticsearch (osu-web expects Elasticsearch: use Elastic
        Cloud or a VM).
  - [ ] **3. Replicas of what's stateless:** osu-web (sessions/cache/queues already in Redis), circleguard,
        pp-sidecar, score indexer, job workers behind Caddy (`lb_policy`) or the provider's load balancer;
        TomYum's `/web/*`, score submission, `/d/*`, assets and admin API once they no longer touch
        in-memory state.
  - [ ] **4. Bancho on many nodes:** Redis cross-node online state — presence, tokens, channels, matches,
        spectating and per-player packet queues in Redis (pub/sub between nodes); sticky sessions by
        `osu-token` until then. tomyum-spectator likewise for lazer realtime; IRC (raw TCP 6667) behind a
        TCP load balancer. ADR-0003.
  - [ ] Background jobs (Discord bot, daily challenge, imports, backfills) on one node only (a Redis lock).
  - [ ] Shared storage for `.osz`/replays/screenshots across nodes (see object storage below).
  - [ ] ⏸ **Kubernetes** (not now, 2026-10-01: one VM + Compose gives the same restarts and deploys; k8s
        only adds work while bancho is single-instance). Worth it after stage 4, with the databases
        managed (stage 2) so k8s runs only stateless deployments: TomYum, osu-web, spectator,
        circleguard, pp-sidecar, workers; Helm chart or manifests from the Compose services, autoscaling,
        an ingress for HTTP and a TCP load balancer for IRC.
- [ ] **Object storage (S3 / R2 / Spaces / B2)** — ADR-0006. Only the design exists: `TomYum.Storage` has
      `IStorageProvider`, `StorageOptions` (R2, AWS S3, OVH S3, Azure Blob, local; multi-target write /
      single read) and nothing implements or reads them (no AWSSDK package; the csproj says "Stubs only").
      Today TomYum writes to the `server-data` volume (`/app/data`: replays, custom `.osz`/`.osu`,
      screenshots, `.osu` cache, assets, geoip — ~26 MB on 2026-10-01). Custom uploads already work on
      local disk, so this is for scaling out and off-VM durability, not a blocker:
  - [ ] One S3-compatible provider (AWS S3, Cloudflare R2, DigitalOcean Spaces, Backblaze B2, MinIO) plus
        the local one behind `IStorageProvider`; bind `StorageOptions`; local stays the default.
  - [ ] Route the file users through it: stable replays, custom map upload/download (`/d/*`, `/web/maps`),
        screenshots (`/ss/*`), the `.osu` cache (shared with pp-sidecar).
  - [ ] A one-off copy of `/app/data` into the bucket; a doc page (`docs/storage.md`).
  - [ ] Same bucket for the rest of the stack, which already supports S3: osu-web uploads (`S3_*`) and
        tomyum-spectator's lazer replays.
- [x] **Config audit** (2026-10-01): every setting in TomYum's option classes is read somewhere, except
      `Storage:*` (above). (`src/TomYum.Caching/PlaceHolder.cs`, an empty leftover, removed 2026-10-02.)
- [x] **Anticheat** — circleguard (sundeiii/lazer-circleguard, C#) checks every ranked pass, stable and
      lazer, incl. against osu!'s top replays (docs/anticheat.md). 2026-09-29
- [x] **IRC server** (docs/irc.md): port 6667, passwords from BanchoBot's `!irc`, the game's chat
      handlers behind it. 2026-09-29

---

### Working rules (agreed 2026-07-07)
- Commit every slice (revertable). Test every slice. Warnings are errors.
- Every feature ships a page in `docs/` (or updates one) and updates this checklist.
- Think/question/plan **then** build. Keep it contributor-friendly — a human must be able to pick it up.
