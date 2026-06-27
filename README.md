# TomYum

An extensible, horizontally scalable osu! server backend (a "bancho" server) written in
C# / .NET 8, supporting **both** the osu!stable and osu!lazer clients.

> Status: **early scaffolding.** The solution structure, contracts, and documentation are in
> place; feature implementation has not started. See `memories/progress.md`.

## What it is

- Serves **osu!stable** (legacy bancho binary protocol + `/web/*.php` + IRC) and **osu!lazer**
  (osu-web-style REST API + SignalR hubs) from a shared domain core.
- **MySQL** for persistence, **Redis** for online state, pub/sub fan-out, and leaderboard caches.
- Designed for **k8s/k3s**, targeting **100–1000+ concurrent players** across stateless web pods
  coordinated through Redis.
- **Pluggable**: a `plugins/` drop-in directory loads compiled `.dll` plugins or runtime-compiled
  loose `.cs` source. DAN Mode and Session ship as plugins, not core code.
- **Storage-agnostic**: avatars, replays, screenshots, beatmaps, and sets sit behind a provider
  interface with Cloudflare R2 / AWS S3 / OVH S3 / Azure Blob / local-filesystem implementations,
  configurable per asset type.
- Hosts its own **custom beatmaps** in the reserved ID range `100000000+`.

## Repository layout

| Path | What lives here |
|------|-----------------|
| `src/` | Core libraries (Domain, Bancho, Lazer, Persistence, Caching, Storage, Irc, Performance, Plugins.*) and the `TomYum.Server` host |
| `services/` | Independently deployable services (Spectator, ScoreQueue, BeatmapSubmission, ReplayStore) |
| `plugins/` | Runtime plugin drop-in directory + examples |
| `tests/` | Unit tests |
| `config/` | Sample configuration (`appsettings`, `tomyum.yaml`, `.env.example`) |
| `deploy/` | Docker, k8s, k3s, and Caddy deployment scaffolding |
| `docs/` | Human and AI-oriented documentation |
| `memories/` | The knowledge base — protocols, architecture/ADRs, library usage, study notes |
| `reference/` | Read-only third-party reference implementations (do not modify) |

## Getting started

```bash
# Verify the SDK (pinned in global.json)
dotnet --info

# Build everything (warnings are errors)
dotnet build TomYum.sln

# Run the tests
dotnet test TomYum.sln

# Start local dependencies (MySQL + Redis)
docker compose -f deploy/docker/docker-compose.yml up -d mysql redis

# Run the server host (health probe at /health)
dotnet run --project src/TomYum.Server
```

## Documentation

- **`AGENTS.md`** — canonical onboarding for any contributor (human or AI): facts, layering
  rules, conventions, Do/Don't.
- **`memories/`** — start at `memories/README.md`. Protocols live in `memories/protocols/`,
  architecture decisions in `memories/architecture/decisions/`, per-library usage guides in
  `memories/libraries/`.

## License

See individual reference repositories under `reference/` for their respective licenses
(mostly MIT; `osu-bancho-main` is reference-only with no usable license). A project license
for TomYum itself is TODO.
