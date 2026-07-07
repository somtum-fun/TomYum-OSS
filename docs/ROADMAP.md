# TomYum Roadmap & TODO (human-facing)

> **Docs convention.** `docs/` is for **humans** (contributors, operators). `memories/` is for
> **AI agents** (dense protocol facts, ADRs, library notes). Same feature → two notes: a
> human page in `docs/`, an agent note in `memories/`. Don't mix them.

_Last updated: 2026-07-07. Baseline: `0.0.1-dev`, 122 tests green, .NET 10, 20 projects._

This is the living checklist for the big feature wave requested on 2026-07-07: `.osu` cache →
plugin host → moderation/HWID → spectating → DAN/Session ports → custom beatmaps → **osu!lazer
support**. Work top-to-bottom; each box is committed + tested on its own so any mistake reverts
cleanly. Deferred by explicit decision: **IRC** (do lazer+stable first) and **circleguard**
(no ML anti-cheat yet).

Legend: `[ ]` todo · `[~]` in progress · `[x]` done · 🔒 blocked-by · ⚖️ decision needed.

---

## Phase 0 — Foundations (small, unblock everything)

- [ ] **`.osu` disk cache** — cache raw `.osu` bytes on disk so vanilla (C# `OfficialPerformanceCalculator`)
      and rx/ap (Rust sidecar) stop re-downloading from `osu.ppy.sh/osu/{id}` every time. L1 mem + L2 disk.
      Also the seam that later serves **custom** maps' `.osu` locally.
- [ ] **PP sidecar smoke test + go-live** — documented build/run, a smoke-test script, a
      `docker-compose` service, and a startup readiness log. (Sidecar already builds and returns
      correct pp; this makes it operable + verifiable.)
- [ ] ⚖️ **Version bump decision** — we're `0.0.1-dev` (pre-1.0). This wave is large; recommend a
      **minor** bump to `0.1.0-dev` to mark it. No MAJOR bump until a stable `1.0.0` API baseline.
- [ ] **EF Core migrations bootstrap** — repo currently has **no migrations** (schema hand-managed).
      Every table below (client_hashes, restrictions, sessions, mapsets…) needs this first.

## Phase 1 — Plugin host 🔒 Phase 0 migrations for `IPluginDataAccess`
_ABI already exists (`TomYum.Plugins.Abstractions`); ADR-0007 already picks `.dll` (AssemblyLoadContext)
+ loose `.cs` (Roslyn). This phase is the missing **host implementation**._

- [ ] Concrete event types in Abstractions: `ActionChanged`, `ScoreSubmitting` (vetoable),
      `ChatMessage`, `PlayerLogin`, `SpectateFrame` (ABI addition — design fields in one pass).
- [ ] Host services: `PluginContext`, `EventBus`, `CommandRegistry`, `HttpRouteRegistry`,
      `PluginConfig`, `PluginLogger`, `PluginDataAccess` (trusted-only DB connection).
- [ ] `IPluginLoader` impl — `.dll` via collectible `AssemblyLoadContext`; loose `.cs` via Roslyn
      (`plugin.json` manifest, gated by `plugins.allowLooseSource`).
- [ ] `PluginHostService` (`IHostedService`) — load on start, `InitializeAsync`, graceful
      `ShutdownAsync` + ALC unload. Wire in `Program.cs`.
- [ ] Per-player state bag on `OnlinePlayer` (transient, cleared on logout — DAN needs this).
- [ ] Hot-path publish sites: `ChangeActionHandler`, spectate-frames handler, `ScoreSubmissionEndpoint`
      (pre-persist veto seam).
- [ ] Command bridge — route unmatched `!cmd` from `BanchoBotCommands` into plugin `ICommandRegistry`.
- [ ] **`!plugins`** — list loaded plugins with name / version / author.
- [ ] Example plugins: one compiled (`.dll`) + one loose (`.cs`), both loading at runtime.
- [ ] ⚖️ **Plugin template repo** — new git submodule (`TomYum-plugin-template`) + **human docs**
      (`docs/plugins.md`: how to write/build/drop-in a plugin) and **agent docs** (`memories/features/plugins.md`).

## Phase 2 — Moderation + HWID anti-cheat 🔒 Phase 0 migrations
_All packet IDs exist (`AccountRestricted=104`, `UserSilenced=94`, `SilenceEnd=92`, `TargetIsSilenced=101`).
Model matches bancho.py: **restricted = shadow-ban** (still logs in, hidden from others, own scores
only visible to self); **banned ≡ restricted** (no hard login reject); **silence = orthogonal timed mute**._

- [ ] State model: `User.SilenceEnd` (unix), restriction via a privilege bit + `Verified` bit;
      migration + a moderation **audit-log** table.
- [ ] Login enforcement: restricted → `ACCOUNT_RESTRICTED(104)` + bot DM + one-way presence +
      resume real silence countdown; grant `Verified` on first in-game login.
- [ ] Enforcement points: presence hiding (stats/presence/logout broadcasts skip restricted),
      chat silence gate (`TargetIsSilenced`/`UserDmBlocked`), multiplayer block, **shadow leaderboard**
      (restricted scores hidden from others, visible to self — the subtle carve-out), force-logout on restrict.
- [ ] Commands: `!restrict` / `!unrestrict` (= un-ban) / `!ban` (alias) / `!silence` / `!unsilence` /
      `!alert` — staff-gated (reload domain privileges, don't trust the wire bitmask).
- [ ] **HWID capture** — `client_hashes` table (userid + 4 md5s, occurrences); upsert at login.
      The 4 hashes are **already parsed** today, just dropped.
- [ ] **Multi-account / ban-evasion gate** — at first login (unverified), reject if any HWID match
      is a restricted account: `USER_ID(-1)` + `"Please contact staff directly to create an account."`
      Guards: `runningunderwine` → uninstall-only; exclude `md5("0")` disk id.
- [ ] Presence geolocation — IP→country enrichment (have `CF-IPCountry`; add a fallback lookup).

## Phase 3 — Stable spectating 🔒 (none) — prereq for DAN
_All 10 packet IDs exist; `WriteRaw` fastpath exists (see `MatchScoreUpdateHandler`). Just handlers + state._

- [ ] `Spectators` / `Spectating` on `OnlinePlayer`; ephemeral `#spec_<hostId>` channel;
      `ChannelRegistry.Remove` + an "ephemeral" flag so it isn't listed like `#osu`.
- [ ] Handlers: `StartSpectating(16)`, `StopSpectating(17)`, `SpectateFrames(18)` (raw relay),
      `CantSpectate(21)` + spectator encoders (`SpectatorJoined/Left/Frames/CantSpectate/Fellow*`).
- [ ] Logout/reaper teardown of spectate state.

## Phase 4 — DAN mode + Session plugins (ports) 🔒 Phase 1 + Phase 3
_Clean-room reimplementation from `memories/features/dan-mode.md` + `session.md` (fork is GPL — behavior only)._

- [ ] **DAN mode** plugin — mania clear validator: bot spectates on `CHANGE_ACTION`, taints on pause
      frame, vetoes score submit; `is_dan` per-diff flag + set-granularity tag endpoint; transient per-login state.
- [ ] **Session** plugin — `session.<domain>` vhost, opaque web-session table (IP/UA binding),
      the essential frontend endpoints (auth, profile, beatmap status). Scope out Thai-deployment
      specifics (TrueMoney/PromptPay/OAuth/email) as optional plugin config.

## Phase 5 — Custom beatmaps 🔒 Phase 0 (storage/migrations); `!map` needs none
_TomYum already **serves** status; it can't **accept uploads** yet. Split status-work (easy) from upload (hard)._

- [ ] **`!map` rank/love/unrank/qualified** + `/np` last-map tracking — status-only, no osz2. Do early.
- [ ] Custom-status model: `mapsets` table (ownership, `frozen`, `prod_only`), per-mode override,
      set-vs-diff granularity; exclude custom maps from osu!api re-sync.
- [ ] ⚖️ **osz2 codec** — the single biggest build item. Decide: clean-room C# vs a sidecar (like the
      pp sidecar) wrapping an existing lib. Then the 3 stable submit endpoints + download-side osz2 endpoints.
- [ ] Local serving — route `/d/{setId}` + search + `b.*` assets to local storage when `setId >= 100_000_000`
      (currently always 302s to an external mirror, so custom sets are undownloadable).

## Phase 6 — Command framework refactor (polish, optional)
- [ ] Sunrise-style `[ChatCommand(name, prefix, priv, isGlobal, isHidden)]` + reflection discovery;
      unify core + plugin commands into one registry; `(bool toPlayer, string response)` reply shape.

## Phase 7 — osu!lazer support 🔒 Phase 1 (shared presence/chat), Phase 0 (migrations)
_Lazer edge is a stub. No reference serves **both** stable + lazer in one process → the shared-account /
shared-`#osu` / shared-BanchoBot model is **net-new design**. Client `client_id=5` / secret is fixed & known._

- [ ] ⚖️ **Reconcile identity scheme** — BanchoBot id (TomYum=1 vs osu-web=2) and `AUTO_INCREMENT` policy;
      dev seed currently puts a real user at id=2. Must fix before lazer or identities collide.
- [ ] Shared presence/chat refactor — lift `PlayerRegistry`/`ChannelRegistry`/`OnlinePlayer`/`BanchoBot`
      off the stable byte-queue into a transport-neutral model with stable + lazer projections.
- [ ] **OAuth2 `/oauth/token`** (password + refresh grants) — HS256 JWT + opaque refresh, DB-backed
      revocation; `JwtBearer` scheme + SignalR user-id provider. Anti-spoof: validate client version hash.
- [ ] **`/api/v2`** read surface — me/users/friends/beatmaps/beatmapsets/rankings/leaderboards/chat.
- [ ] **Two-phase solo score submit** (`POST` token → `PUT` SoloScoreInfo) → funnel into the **same**
      Domain score path (pp, Best/Submitted, weighted stats) + Redis score-statistics stream.
- [ ] **SignalR hubs** in-process — `/spectator` + `/multiplayer` (MessagePack), `/metadata` (Newtonsoft
      JSON); reuse `ppy.osu.Game` DTOs; port `EntityStore`/`StatefulUserHub`/`ConcurrentConnectionLimiter`
      from the MIT `osu-server-spectator`. (Skip g0v0's `/_lio` bridge — we're single-stack.)
- [ ] **Notification websocket** (raw WS, not SignalR) for chat + notifications; `GET /api/v2/notifications`
      returns its URL.
- [ ] Minimum bar: one account logs into **both** clients · shared BanchoBot · shared `#osu` cross-client
      chat · osu! beatmap status works · custom beatmap status works.
- [ ] Stretch: shared leaderboard (stable scores in lazer), lazer score submission, lazer beatmap submission.
- [ ] For stable scores shown in lazer: simulate score numbers as faithfully as possible; relax uses
      akatsuki-pp (as stable does) — may need to port akatsuki-pp to the client.
- [ ] **Deployment prereq (not server code):** lazer only ships Production/Development endpoints → reaching
      TomYum needs a patched client build **or** DNS + trusted-TLS spoofing of `osu.ppy.sh` +
      `spectator.osu.ppy.sh`. Document in `docs/lazer-client.md`.

## Cross-cutting infra (interleave as needed)
- [ ] Redis cross-node online state (multi-instance) — needed before scaling lazer realtime; ADR-0003.
- [ ] Storage providers multi-target write / single read — needed for Phase 5 upload; ADR-0006.
- [ ] Fix stale `AGENTS.md` (.NET 8 → .NET 10) and stale `memories/libraries/TomYum.Performance.md`.

---

### Working rules (agreed 2026-07-07)
- Commit every slice (revertable). Test every slice. Warnings are errors.
- Every feature ships **two docs** (human in `docs/`, agent in `memories/`) and updates this checklist.
- Think/question/plan **then** build. Keep it contributor-friendly — a human must be able to pick it up.
