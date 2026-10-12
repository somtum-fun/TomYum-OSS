# TomYum Roadmap

_Last updated: 2026-10-12 (version 0.5.0)._ .NET 10; used in production for stable, lazer and the website.

`0.5.0` is the first **stable** release. `0.6.0` to `1.0.0` are small fixes and growing up:
scaling, object storage, Kubernetes, and a core generic enough for anyone's server. How each feature
works is in its own page under `docs/` and on [docs.somtum.fun](https://docs.somtum.fun); the history of
how it was built is in git.

Legend: `[ ]` todo · `[~]` in progress · `[x]` done · ⏸ deferred by decision.

## Versions
- `0.1.0-dev` (2026-07-07): `.osu` cache, pp sidecar, database migrations, the plugin host.
- `0.2.0-dev` (2026-10-01): lazer and the website (osu-web) as accounts and scores, moderation and the
  staff panels, anti-cheat hooks, IRC, bot accounts, BN/NAT tools, maintenance mode, support tickets.
- `0.3.0-dev` (2026-10-03): old clients, Relax/Autopilot with Akatsuki pp, pp reworks from the admin
  panel, monitoring, metrics as a plugin, tickets from any IMAP mailbox.
- `0.5.0` (2026-10-12): the first stable release. Old clients finished (every build from 2008 to 2015, with TCP
  bancho for the oldest); stable sessions fixed (one client per account, no "already logged in", idle
  sessions ended); ranking rules (days in qualified, an inactivity rule); no server's names in core
  defaults; a security review of the whole server and its fixes (2026-10-11/12).

## 0.6.0 – 1.0.0: small fixes, scaling, a generic core
- [ ] Small fixes from production.
- [ ] Scaling, stage by stage (stop at the first that's enough; one 4 vCPU/8 GB VM holds a few hundred
      daily players):
  - [ ] Scale up: a bigger VM, more osu-web workers, MySQL buffer pool, Elasticsearch heap.
  - [ ] Split the stack: MySQL and Redis on their own or managed; Elasticsearch, the score indexer and
        the pp sidecar on a workers VM.
  - [ ] Copies of what's stateless (osu-web, pp sidecar, workers) behind a load balancer.
  - [ ] Bancho on many nodes: online state, channels, matches, spectating and packet queues in Redis
        (ADR-0003); background jobs on one node (a Redis lock).
  - [ ] ⏸ Kubernetes, after the above (`deploy/k8s` is a start).
- [ ] Object storage (S3 / R2 / Spaces / B2, ADR-0006) for replays, maps, screenshots and the `.osu`
      cache; `TomYum.Storage` has the interfaces only.
- [ ] The remaining plugin events (`ScoreSubmitting` veto, `ChatMessage`).
- [ ] Server-specific behaviour behind plugins or settings (welcome texts, store, maintenance texts,
      ticket categories, the Relax/Autopilot pp source, custom medals); no server names or addresses in
      code, docs or defaults.
- [ ] Docs for operators and contributors, a changelog per version, a licence review of what's bundled.
- [ ] Then `1.0.0`; afterwards SemVer: breaking changes bump the major version.

## Working rules
- Commit every slice (revertable). Test every slice. Warnings are errors.
- Every feature ships a page in `docs/` (or updates one) and updates this roadmap.
