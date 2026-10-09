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

> Status: **used in production** (osu!stable, osu!lazer and the website on one account system).
> Version `0.3.0-dev`. The detailed checklist, with what's left, is
> [docs/ROADMAP.md](docs/ROADMAP.md).

## Features

**osu!stable (bancho)**
- Login, in-game registration, presence, stats, chat channels and PMs, friends (shared with lazer
  and the website), away messages, friends-only PMs, spectating
- Score submission with pp for all four modes, plus Relax and Autopilot leaderboards and stats
- Leaderboards (global, selected mods, friends, country), replays, map updates, song select statuses
  and grades, favourites, screenshots, star ratings with mods, osu!direct, in-game comments
- Multiplayer: lobby, rooms, the full match lifecycle, BanchoBot's `!mp` commands, the tournament client
- BanchoBot `!`-commands, staff tiers and supporter mapped to client badges
- Medals, rank history, country from the client's IP

**osu!lazer**
- Runs on [osu-web](https://github.com/ppy/osu-web) (with TomYum's changes:
  [osu-web-tomyum](https://github.com/somtum-fun/osu-web-tomyum)) and
  [osu-server-spectator](https://github.com/ppy/osu-server-spectator) (spectating, multiplayer,
  playlists, the daily challenge), sharing accounts, scores and rankings with stable
- Lazer plays with Relax get relax pp and leaderboards like stable's
- Relax and Autopilot on the website: profiles, rank graphs, rankings and score pages
- Ranked Play (matchmaking pools set up from the admin panel)

**Everything around it**
- Moderation: restrictions, silences, a chat log, score removal, staff panels on the website
  (BN imports, GMT tools, admin, the daily challenge map picker, seasonal backgrounds, news,
  Ranked Play pools, badges, user titles)
- A changelog made from the repositories' history on every deploy (`deploy/production/deploy.sh`)
- Anti-cheat: hardware/client hash checks and alt detection; replay checks can be added as a plugin
- Custom beatmaps (uploaded sets, graveyard, guest difficulties) next to the official ones
- An IRC server (port 6667) for IRC clients and bots, logging in with the website's IRC key
- Plugins: compiled `.dll` or loose `.cs`, with events, commands and HTTP routes
- EF Core migrations, Docker Compose / k8s deployment, Caddy and a Cloudflare tunnel in front
- Email (sign-up verification), downtime alerts by Discord DM, nightly backups

**Not done yet** (see the roadmap): custom medals, multi-instance (Redis-backed) online state, and
multi-target storage. Put off for now: TomYum reading scores straight from
the website's tables (they're mirrored today).

## Documentation by topic

[osu-web](docs/osu-web.md) · [lazer](docs/lazer.md) · [spectating](docs/spectating.md) ·
[custom beatmaps](docs/custom-beatmaps.md) · [achievements](docs/achievements.md) · [IRC](docs/irc.md) ·
[plugins](docs/plugins.md) · [local testing](docs/local-testing.md)

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

### Website (osu-web)

The website is [osu-web](https://github.com/ppy/osu-web), run alongside TomYum on the same MySQL and
Redis, with Caddy splitting `osu.<domain>` between the two. See [docs/osu-web.md](docs/osu-web.md)
for setup and the plan for sharing one database. Moving an existing server with players:
[tools/MigrateToOsuWeb](tools/MigrateToOsuWeb/README.md). Running the whole stack on one machine to
try it: [docs/local-testing.md](docs/local-testing.md).

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

Our team believes in using AI as an assistant, always under human review. Low-effort AI-generated ("slop") pull requests or comments may be closed without notice.

## License

TomYum is licensed under the MIT License. Please see the [LICENSE](LICENSE) file for more
information. Copyright (c) 2026 blueskychan-dev, somtum.fun & sundeiii.

## Acknowledgments

- Heavily inspired by [bancho.py](https://github.com/osuAkatsuki/bancho.py)
- [BanchoNET](https://github.com/NovemoG/BanchoNET) — osu!stable server in C#
- [Sunrise](https://github.com/SunriseCommunity/Sunrise) — osu!stable server in C#
- [osu!](https://github.com/ppy/osu) and [osu-server-spectator](https://github.com/ppy/osu-server-spectator) — official lazer client & spectator server
- [g0v0-server](https://github.com/GooGuTeam/g0v0-server) — osu!lazer server (Python), referenced for lazer API/flow understanding
- [rosu-pp](https://github.com/MaxOhn/rosu-pp) — fast PP/difficulty calculation
