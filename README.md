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

See [`memories/progress.md`](memories/progress.md) for the living status of the project.

> Status: **early development.** The solution structure, contracts, and documentation are
> in place, and the osu!stable login handshake works end to end. Most features are still
> to come.

## Features

- osu!stable login handshake (binary packet protocol, `cho-token` session)
- In-memory online-player registry + per-player outbound packet queue

## In development / TODO

- [ ] Post-login packet dispatch (chat, presence, status, spectate)
- [ ] Persistence (MySQL via EF Core) + password verification
- [ ] Redis-backed online state for cross-node fan-out (100–1000+ players)
- [ ] osu!lazer support (osu-web-style REST API + SignalR hubs)
- [ ] IRC server for external bots (auth via osu! API key)
- [ ] Plugin system (`.dll` + runtime-compiled loose `.cs`); DAN Mode & Session as plugins
- [ ] Score submission pipeline + async statistics/PP queue
- [ ] Configurable multi-target storage (R2 / AWS S3 / OVH S3 / Azure Blob / local)
- [ ] Custom beatmap submission (reserved ID range `100000000+`)
- [ ] osu!Direct, leaderboards, multiplayer, spectating
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
dotnet run --project src/TomYum.Server
```

TomYum routes by subdomain (`c`, `c4`, `cho` for bancho, `osu` for `/web/*.php`). Front it
with Caddy and point the subdomain records at your host — see `deploy/caddy/Caddyfile`.

### Deployment

Docker, k8s, k3s, and Caddy scaffolding lives under `deploy/`.

## Documentation

- **`AGENTS.md`** — canonical onboarding for any contributor (human or AI).
- **`memories/`** — start at `memories/README.md`: protocols, architecture/ADRs, and
  per-library usage guides.

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
- [rosu-pp](https://github.com/MaxOhn/rosu-pp) — fast PP/difficulty calculation
