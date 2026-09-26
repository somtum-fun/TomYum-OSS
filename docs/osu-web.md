# osu-web as TomYum's website

TomYum serves the osu! **game** (bancho, `/web/*.php`, score submission). The **website** —
profiles, rankings, beatmap listings, forums, and later lazer's `/api/v2` + OAuth — is
[osu-web](https://github.com/ppy/osu-web), ppy's official Laravel site, run unmodified next to TomYum.

The two share one MySQL server and one Redis. The end state is that they share **one database**:
osu-web owns the schema (its migrations), and TomYum reads and writes osu-web's tables. That is
done in two phases:

- **Phase A — side by side (this page, works today).** Both run on osu-web's MySQL/Redis behind one
  Caddy. TomYum still uses its own `tomyum` database, so accounts and scores are **not yet shared**.
- **Phase B — one database (planned, below).** TomYum's repositories are pointed at osu-web's
  tables, so an account made on the site logs into the game and plays show up on the site.

---

## Phase A — running both

### 1. Start osu-web

In an osu-web checkout (ours is the private `sundeiii/osu-web`, an unmodified import of upstream):

```bash
# osu-web's nginx defaults to 8080, which TomYum.Server uses — move it.
NGINX_PORT=8180 bin/docker_dev.sh -d
```

`bin/docker_dev.sh` runs `docker/development/prepare.sh` (copies `.env`, generates `APP_KEY` and
the Passport OAuth keys, installs PHP/JS deps) and then `docker compose up`. The `migrator` service
runs osu-web's migrations into the `osu` database. Elasticsearch needs ~1 GB RAM.

Set these in osu-web's `.env` before first start:

| Setting | Value | Why |
|---|---|---|
| `APP_URL` | `https://osu.<your domain>` | Links and redirects on the site. |
| `BANCHO_BOT_USER_ID` | `1` | osu-web defaults to user **3**; TomYum's BanchoBot is **1**. |
| `CLIENT_CHECK_VERSION` | `false` | Already the default; keeps stable-client checks off. |

### 2. Start TomYum on osu-web's network

```bash
cp config/.env.example config/.env   # the server service reads it; fill in the osu! API key etc.
docker compose -f deploy/docker/docker-compose.yml -f deploy/docker/docker-compose.osu-web.yml \
  up -d server pp-sidecar
```

The overlay (`deploy/docker/docker-compose.osu-web.yml`) attaches `server` to osu-web's compose
network (`osu-web_default`; override with `OSU_WEB_NETWORK` if your checkout directory has another
name) and points it at osu-web's `db` and `redis`. **Don't start this stack's own `mysql`/`redis`**
with the overlay — they would clash on ports 3306/6379 and on the `redis` hostname.

### 3. Route one domain to both

`deploy/caddy/Caddyfile` splits `osu.<domain>`:

| Request | Goes to |
|---|---|
| `/web/*`, `/d/*` | TomYum (stable client endpoints) |
| `POST /users` with `User-Agent: osu!` | TomYum (in-game registration) |
| everything else | osu-web (the website) |

`c`/`c4`/`ce`/`cho` stay on TomYum. Upstreams default to `localhost:8080` (TomYum) and
`localhost:8180` (osu-web) and can be changed with `TOMYUM_UPSTREAM` / `OSU_WEB_UPSTREAM`.

### Not for production as-is

osu-web's dev stack is a **development** setup: its MySQL user `osuweb` has an empty password and
`ALL PRIVILEGES` (`docker/development/db_user.sql`), and PHP runs with `--watch`. For a public
server, give TomYum its own MySQL user with a password and grants only on the tables it needs,
and follow osu-web's `DEPLOYMENT.md` for the site.

### Licence and branding

osu-web is **AGPL-3.0**. Running it privately is fine. If you run a **modified** osu-web that other
people use over the network, you must offer them its source. TomYum is a separate program that
only shares a database, so it stays MIT. osu-web ships ppy's name and artwork — rebrand it before
running it publicly.

---

## Phase B — one database (plan)

Verified against the osu-web checkout (`database/migrations/2015_01_01_133337_base_tables.php`,
`app/Models/User.php`, `app/Libraries/User/PasswordHelper.php`, `config/osu.php`).

### What already lines up

- **Passwords are the same format.** osu-web stores `bcrypt(md5_hex(password))` (cost 10) in
  `phpbb_users.user_password` — exactly what TomYum's `BcryptPasswordHasher` computes. PHP's
  `password_verify` and BCrypt.Net both accept `$2a$`/`$2y$`, so hashes written by either side
  verify on the other. No password migration is needed.
- **Restriction model.** osu-web: banned = `user_type = 1`; restricted = banned or
  `user_warnings > 0`. Roadmap Phase 2 (moderation) should use these columns instead of a new
  privilege bit, so the site and the game agree on who is restricted.

### Table mapping

| TomYum entity | osu-web table | Notes |
|---|---|---|
| `User` | `phpbb_users` | `user_id`, `username`, `username_clean` (≈ `SafeName`), `user_email`, `user_password`, `country_acronym` (two letters, must match `osu_countries`), `user_regdate`/`user_lastvisit` (unix). `user_sig`, `user_occ`, `user_interests` are NOT NULL text with no default — insert `''`. |
| `User.Privileges` | `phpbb_user_group` + `phpbb_groups` | Staff tiers become group memberships (`group_id` identifiers such as `admin`, `gmt`, `dev`); supporter is `osu_subscriber` + `osu_subscriptionexpiry`. |
| `UserStats` (per mode) | `osu_user_stats`, `_taiko`, `_fruits`, `_mania` | `rank_score` = pp, `accuracy_new`, `playcount`, `ranked_score`, `total_score`, `max_combo`, `x/s/a_rank_count`, `level`, `rank_score_index`. Several columns are NOT NULL without defaults — write them all. |
| `Beatmap` | `osu_beatmaps` + `osu_beatmapsets` | `checksum` = md5, `version`, `playmode`, `approved` (ranked status), `difficultyrating`, `total_length`; artist/title/creator live on the set. |
| `Score` | `scores` (view over `solo_scores`) | What the site's profiles and leaderboards read; JSON `data` column, `ruleset_id`, `ranked`, `preserve`, `legacy_score_id`, `legacy_total_score`, `has_replay`. The legacy `osu_scores_high` tables still exist but aren't what the site shows. |
| `UserAchievement` | `osu_user_achievements` | `user_id`, `achievement_id`, `date`, `beatmap_id` — matching column-for-column. Achievement ids must match osu-web's `osu_achievements`. |

### Decisions needed before Phase B

1. **Custom beatmap id range.** `osu_beatmaps.beatmap_id` and `osu_beatmapsets.beatmapset_id` are
   `MEDIUMINT UNSIGNED` (max **16,777,215**). TomYum's `Beatmap.CustomIdFloor` is **100,000,000**,
   which does not fit. Either pick a floor inside the range above current ppy ids (e.g.
   10,000,000), or widen the columns with an extra migration (which means modifying osu-web).
2. **User ids.** `phpbb_users` auto-increments from 2. TomYum reserves 1 for BanchoBot and its dev
   seed takes 2. Insert a real BanchoBot row with `user_id = 1` in osu-web's `bot` group
   (`phpbb_groups.identifier = 'bot'`), set osu-web's `BANCHO_BOT_USER_ID=1`, and drop the
   fixed-id dev seed.
3. **Search and leaderboards on the site.** osu-web's score listings go through Elasticsearch,
   filled by `osu-elastic-indexer` (`score-indexer` in osu-web's compose) from a Redis queue.
   TomYum's score submission must push new score ids onto that queue, or the site won't show
   new plays. The exact queue key must be taken from `osu-elastic-indexer`.

### Steps

1. Users: new `OsuWebUserRepository` over `phpbb_users` behind `IUserRepository`; login and
   registration work against it. Remove `EnsureCreated`/`SchemaGuard` for these tables — osu-web's
   migrations own them.
2. Beatmaps: cache resolved maps into `osu_beatmaps`/`osu_beatmapsets`.
3. Scores + stats: write `solo_scores` and `osu_user_stats*`; queue scores for the indexer.
4. Achievements: `osu_user_achievements`, with TomYum's catalog mapped to osu-web achievement ids.
5. Data migration: copy existing `tomyum` rows into osu-web's tables (then `RecalcPp --force`).
6. Lazer: osu-web already serves `/api/v2` and `/oauth/token`, which covers most of roadmap
   Phase 7; TomYum then only needs the realtime hubs.
