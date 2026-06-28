# TomYum

An extensible, horizontally scalable [osu!](https://osu.ppy.sh) bancho server written in
C# / .NET 8, supporting **both** the osu!stable and osu!lazer clients.

## Technologies

- ASP.NET Core (.NET 8)
- MySQL (MariaDB)
- Redis
- Docker / k8s / k3s
- Caddy (edge + storage fronting)

## Progress

> Status: **early development.** A player can register in-client, log in, chat, and appear
> online to others, with accounts and beatmap metadata persisted to MySQL. Many features are
> still to come — see the TODO list below.

## Features

- osu!stable login handshake (binary packet protocol, `cho-token` session)
- In-game registration (`POST /users`) + real password auth (`bcrypt(md5_hex(plaintext))`)
- Post-login packet dispatch: presence, stats, status changes, logout
- Chat: server-side channels (`#osu`, `#announce`, `#lobby`, …), public + private messages
- BanchoBot: resident bot answering `!`-commands (`!help`, `!stats`, `!setpriv`, …)
- Role/privilege system: staff tiers (owner/admin/dev/mod) + supporter, mapped to client badges
- MySQL persistence (EF Core + Pomelo): user + beatmap repositories
- Beatmap ranked-status lookup via the osu! v1 API (keyed `old.ppy.sh` / keyless `osu.direct`)
- Host-aware subdomain routing (`c`/`c4`/`ce`/`cho` for bancho, `osu` for `/web/*`)

## In development / TODO

- [ ] Redis-backed online state for cross-node fan-out (100–1000+ players)
- [ ] osu!lazer support (osu-web-style REST API + SignalR hubs)
- [ ] IRC server for external bots (auth via osu! API key)
- [ ] Plugin system (`.dll` + runtime-compiled loose `.cs`); DAN Mode & Session as plugins
- [ ] Score submission pipeline + async statistics/PP queue
- [ ] Configurable multi-target storage (R2 / AWS S3 / OVH S3 / Azure Blob / local)
- [ ] Custom beatmap submission (reserved ID range `100000000+`)
- [ ] osu!Direct, leaderboards, multiplayer, spectating
- [ ] EF Core migrations (currently `EnsureCreated`; schema cannot evolve yet)
- [ ] Docker / k8s deployment

## Building

### Prerequisites

- .NET SDK 8.0 (pinned in `global.json`)
- MySQL / MariaDB Server
- Redis Server

### Building

1. Clone the repository to your local machine.
```bash
git clone https://github.com/somtum-fun/TomYum-OSS.git
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
