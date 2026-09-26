# TomYum

An extensible, horizontally scalable [osu!](https://osu.ppy.sh) bancho server written in
C# / .NET 10, supporting **both** the osu!stable and osu!lazer clients.

## Technologies

- ASP.NET Core (.NET 10)
- MySQL 8.4 (EF Core + Pomelo 9)
- Redis
- Docker / k8s / k3s
- Caddy (edge + storage fronting)

## Progress

> Status: **early development.** A player can register in-client, log in, chat, play maps,
> submit scores, and compete in multiplayer rooms. Accounts, scores, and beatmap metadata are
> persisted to MySQL. Many features are still to come — see the TODO list below.

## Features

- osu!stable login handshake (binary packet protocol, `cho-token` session)
- In-game registration (`POST /users`) + real password auth (`bcrypt(md5_hex(plaintext))`)
- Post-login packet dispatch: presence, stats, status changes, logout
- Chat: server-side channels (`#osu`, `#announce`, `#lobby`, …), public + private messages
- BanchoBot: resident bot answering `!`-commands (`!help`, `!stats`, `!setpriv`, `!roll`, `!mp …`)
- Role/privilege system: staff tiers (owner/admin/dev/mod) + supporter, mapped to client badges
- MySQL persistence (EF Core + Pomelo): user, score, and beatmap repositories
- Score submission (`POST /web/osu-submit-modular-selector.php`): Rijndael-256 CBC decryption, status tracking (Best/Submitted/Failed), leaderboard update
- Real PP calculation via `ppy.osu.Game.Rulesets.*` (all four rulesets); weighted PP + accuracy using the standard osu! formula
- Replay save (`data/replays/{id}.osr`) and serve (`GET /web/osu-getreplay.php`)
- Leaderboards (`GET /web/osu-osz2-getscores.php`) with personal-best row
- Beatmap ranked-status lookup via the osu! v1 API (keyed `old.ppy.sh` / keyless `osu.direct`)
- Host-aware subdomain routing (`c`/`c4`/`ce`/`cho` for bancho, `osu` for `/web/*`)
- **Multiplayer**: 16-slot lobby + match rooms with full lifecycle (create/join/part, ready, start, score updates, skip, fail, complete), per-match `#multiplayer` chat, host transfer, invites, password, freemods
- **BanchoBot `!mp` commands**: `!mp start [s]` (immediate or countdown-then-start), `!mp countdown <s>` (count down only), `!mp abort`, `!mp host <player>`, `!mp kick <player>`
- **Redis score queue**: `TomYum.ScoreQueue` background worker reads from a Redis Stream consumer group, publishes `osu-channel:score:processed` pub/sub, and ACKs

## In development / TODO

- [x] Score submission with Rijndael-256 CBC decryption, status tracking, and chart response
- [x] Real PP calculation (all four rulesets) + weighted PP/accuracy formula
  - ✅ **"Catch convert = 180pp" bug fixed (2026-07-07) — it was a beatmap MD5→ID resolution
    bug, not a PP bug.** `OsuApiClient.GetByMd5Async` took `FirstOrDefault()` of the
    `get_beatmaps?h={md5}` response. With no API key set (the default) the request hits the
    keyless osu.direct mirror, which **returns every difficulty in the set** — so plays on any
    non-first difficulty were filed under the set's *first* difficulty's id, and pp was computed
    against the wrong (often much harder) map. Example: an SS on beatmap 1801936 (1★→~12pp
    catch) was stored as 1797542 (4.48★) → ~188pp. Fix: match on `file_md5` instead of taking
    the first entry (regression tests in `OsuApiClientResolutionTests`). The PP calculator
    itself was verified correct. Existing misfiled scores need a re-resolve + `RecalcPp --force`
    migration. See CHANGELOG 2026-07-07.
- [x] Replay save and serve (`/web/osu-getreplay.php`)
- [x] Redis score queue (`TomYum.ScoreQueue` background worker, Redis Stream consumer group)
- [x] Multiplayer: lobby, match rooms, full lifecycle, per-match chat, BanchoBot `!mp` commands
- [ ] Redis-backed online state for cross-node fan-out (100–1000+ players)
- [ ] EF Core migrations (currently `EnsureCreated`; schema cannot evolve without data loss)
- [ ] Spectating
- [ ] osu!lazer support (osu-web REST API + SignalR hubs)
- [ ] IRC server for external bots (auth via osu! API key)
- [ ] Plugin system (`.dll` + runtime-compiled loose `.cs`); DAN Mode & Session as plugins
- [ ] Configurable multi-target storage (R2 / AWS S3 / OVH S3 / Azure Blob / local)
- [ ] Custom beatmap submission (reserved ID range `100000000+`)
- [ ] Geolocation for presence (lon/lat/country from client IP)
- [ ] Docker / k8s deployment

## Building

### Prerequisites

- .NET SDK 10.0 (pinned in `global.json`)
- MySQL / MariaDB Server
- Redis Server

### Building

1. Clone the repository to your local machine.
```bash
git clone https://github.com/sundeiii/TomYum.git
```
2. Navigate to the project directory.
```bash
cd TomYum
```
3. Restore the project dependencies.
```bash
dotnet restore TomYum.sln
```
4. Build the solution (warnings are errors).
```bash
dotnet build TomYum.sln
```
5. Run the tests.
```bash
dotnet test TomYum.sln
```

## Usage

### Running

1. Start local dependencies (MySQL + Redis).
```bash
docker compose -f deploy/docker/docker-compose.yml up -d mysql redis
```
2. Run the server host (health probe at `/health`).
```bash
scripts/dev-server.sh
```
This wraps `dotnet run` with the right environment (`ASPNETCORE_ENVIRONMENT=Development` so the
config loads, and the DB password injected to match the compose defaults). On a successful start
the log prints `Database schema ensured (...)`, confirming it is using MySQL rather than the
in-memory fallback. Pass `DB=off scripts/dev-server.sh` to run without a database.

TomYum routes by subdomain (`c`, `c4`, `cho` for bancho, `osu` for `/web/*.php`). Front it
with Caddy and point the subdomain records at your host — see `deploy/caddy/Caddyfile`.

### Deployment

Docker, k8s, k3s, and Caddy scaffolding lives under `deploy/`.

## Documentation

Configuration samples live under `config/`, and deployment scaffolding (Docker, k8s, k3s,
Caddy) under `deploy/`. The project also keeps an internal knowledge base — protocols,
architecture decisions, and contributor onboarding notes — that is maintained privately and
not published with the source.

## Contributing

- **Fork** the Project
- Create your **Feature Branch** `git checkout -b feature/AmazingFeature`
- Commit your **Changes** `git commit -m 'Add some AmazingFeature'`
- Push **to the Branch** `git push origin feature/AmazingFeature`
- Open a **Pull Request**

## License

TomYum is licensed under the MIT License. Please see the [LICENSE](LICENSE) file for more
information. Copyright (c) 2026 blueskychan-dev & production.

## Acknowledgments

- Heavily inspired by [bancho.py](https://github.com/osuAkatsuki/bancho.py)
- [BanchoNET](https://github.com/NovemoG/BanchoNET) — osu!stable server in C#
- [Sunrise](https://github.com/SunriseCommunity/Sunrise) — osu!stable server in C#
- [osu!](https://github.com/ppy/osu) and [osu-server-spectator](https://github.com/ppy/osu-server-spectator) — official lazer client & spectator server
- [g0v0-server](https://github.com/GooGuTeam/g0v0-server) — osu!lazer server (Python), referenced for lazer API/flow understanding
- [rosu-pp](https://github.com/MaxOhn/rosu-pp) — fast PP/difficulty calculation
