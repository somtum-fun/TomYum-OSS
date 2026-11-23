# Changelog

TomYum follows [Semantic Versioning](https://semver.org) from 1.0.0 on. Versions before 0.9.0 are summarised in
[docs/ROADMAP.md](docs/ROADMAP.md#versions).

## 0.9.0 (2026-11-23)

The last series before 1.0.0: what's left for it is in [docs/ROADMAP.md](docs/ROADMAP.md).

### Added
- **Pla-ra**: stars and pp for custom lazer rulesets, the same on the server and in the game, with a pp calculator on
  the beatmap page; staff can rank a ruleset by score instead ([docs/plara.md](docs/plara.md)).
- **Beatmaps without osu! or a mirror in the way** ([docs/beatmaps.md](docs/beatmaps.md)): maps are kept whole
  (osu!'s status number and details) and newly ranked ones are synced every `Beatmaps:SyncHours` (6); lookups of
  ranked, approved and loved maps are kept in Redis for a day. Database migration `BeatmapLookupDetails` (two
  nullable columns on `beatmaps`).
- **Beatmap mirrors set in the website's admin panel**: lookup mirrors in order of priority, each at its own rate,
  passed over when one refuses or fails; a download mirror for `/d/`; each mirror's health shown. Without a list,
  `Mirror:*` and `Direct:*` as before.
- **The rankings' inactivity rule from the admin panel**: after how many days without a score a player is inactive,
  and whether inactive players are taken off the rankings or kept and greyed out.
- osu!lazer players watch osu!(stable) players (`tomyum-stable-spectate`, experimental), and the stable player sees
  who watches; someone not on stable can't be spectated from stable.
- Stable sign-ups checked by the website's email rules.
- The pp updater's `REDIS_CONTAINER`, for a server whose Redis isn't the stack's own.

### Changed
- Production: a password on Redis (`REDIS_PASSWORD`) and Elasticsearch logins (`ES_SECURITY`, `ES_PASSWORD`), both
  off until set; the database account has rights on the app's databases only (`mysql-narrow-grants.sh` for existing
  installs); MySQL's connection limit (`DB_MAX_CONNECTIONS`) and a cap on the score-statistics stream.
- Performance: connection pools capped (48 per database), spectators and online players found without scanning every
  session, the website's chat sent in batches, leaderboards that don't wait on osu! for stored maps.
- Redis is optional for a TomYum without the website.
- pp: a lazer rate mod is paid at its own speed (DT/NC below 1.5×).
- Builds no longer need `NuGetAudit=false`: AutoMapper stays on 13.0.1 (MIT) with its advisories suppressed, since
  nothing on the server maps with it.

### Fixed
- A score on a known map is no longer refused as one on an unknown map when osu! and the mirror don't answer.

### Docs
- [Beatmaps and mirrors](docs/beatmaps.md), [Pla-ra](docs/plara.md); a licence review of the packages the server
  ships ([docs/third-party-packages.md](docs/third-party-packages.md), [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)).
