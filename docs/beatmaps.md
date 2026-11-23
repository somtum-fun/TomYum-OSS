# Beatmaps: lookups, mirrors and downloads

How TomYum finds out about a map (a score, a leaderboard, lazer's import), where players' downloads go, and how to
keep both working when osu! or a mirror is slow or refuses.

## A lookup, step by step (`BeatmapService.GetByMd5Async`)

1. **Memory**: maps seen in the last day (misses for an hour).
2. **Redis** (`tomyum:beatmaps:md5:<md5>`, a day): a ranked, approved or loved map already looked up, kept across
   restarts. Stored as osu! gave it, before any staff override.
3. **TomYum's table** (`beatmaps`): a stored map with osu!'s status number and details (object counts, CS/AR/OD/HP,
   dates), when it's ranked, approved or loved. Maps stored before 1.0 have no details until they're looked up or
   imported again.
4. **Online**: the beatmap mirrors (below), then osu!'s own API (`Osu:RequestsPerMinute`, 50 by default). A settled
   map found there is stored whole and shared through Redis.
5. **Neither answers** (a timeout, every mirror refusing): a map this server has stored is used as stored, so a score
   on it isn't refused as one on an unknown map. Not cached: the next lookup asks again.

A pending or graveyarded map is always asked online: it may be ranked any time. Custom (uploaded) sets come from
the table (docs/custom-beatmaps.md).

## Keeping the table current

- **Sync** (`BeatmapMetadataSync`): every `Beatmaps:SyncHours` (6; 0 turns it off), the sets
  ranked or loved since the last sync are imported through osu!'s API (never the mirror: crawling gets a server banned
  there). Where it got to is in Redis (`tomyum:beatmaps:synced-until`); the first sync looks back two days.
- **Backfill, once**: the BN panel's bulk import by ranked date (2007 → today). It's paced by osu!'s API budget, so it
  takes hours; run it off-peak. Afterwards nearly every score is on a map the table answers for.

## Beatmap mirrors (Admin panel › Beatmap mirrors)

Staff list the mirrors here; TomYum reads the list every minute (`tomyum_site_settings.beatmap_mirrors_json`). Each
has an address (https, never this machine or a private network), what it's for (lookups, downloads or both), a
download path (`/d/{id}` by default), requests a minute, a priority (lower first) and an on/off switch.

- **Lookups** ask the enabled lookup mirrors by priority, each at its own pace. One that refuses (403, an hour), says
  too many (429, until its Retry-After) or fails is passed over for the next; a 404 is an answer ("unknown"), and
  osu! is asked instead. The mirror must answer like osu!'s API v2 at `api/v2/md5|b|s/…` (catboy.best does).
- **Downloads** (`/d/{id}`, osu!direct and the website's button) go to the first enabled download mirror.
- The page shows each mirror's health (paused until, last problem), which TomYum writes to Redis
  (`tomyum:beatmap-mirrors:health`).
- **No list**: TomYum uses its own settings, `Mirror:*` for lookups (catboy.best, 120 a minute) and `Direct:*` for
  downloads (osu.direct). An empty list (all removed) means lookups ask osu! only.

catboy.best (Mino) allows 1200 units a minute and bans for repeated 429s, for crawling metadata and for being used as
a mirror. Bulk jobs (imports, backfills) bypass the mirror for that reason.

## Keeping players' downloads

A download cache you run yourself (a service that keeps the sets players download and fetches a set from an upstream
only the first time) is added like any mirror: behind Caddy (`dl.<domain>`), on the Beatmap mirrors page with
**Downloads** on and **Lookups** off. Only use upstreams whose operators allow a server to keep their sets.

## Settings

| Setting | Default | |
|---|---|---|
| `Mirror:Url`, `Mirror:RequestsPerMinute`, `Mirror:Enabled` | catboy.best, 120, on | the lookup mirror while the panel sets none |
| `Direct:MirrorBaseUrl`, `Direct:DownloadPath` | osu.direct | the download mirror while the panel sets none; osu!direct search |
| `Osu:RequestsPerMinute` | 50 | osu!'s API budget, shared by everything |
| `Beatmaps:SyncHours` | 6 | the sync of newly ranked maps (0 off) |
