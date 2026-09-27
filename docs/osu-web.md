# osu-web as TomYum's website

TomYum serves the osu! **game** (bancho, `/web/*.php`, score submission). The **website** —
profiles, rankings, beatmap listings, forums, and later lazer's `/api/v2` + OAuth — is
[osu-web](https://github.com/ppy/osu-web), ppy's official Laravel site, run unmodified next to TomYum.

The two share one MySQL server and one Redis. The end state is that they share **one database**:
osu-web owns the schema (its migrations), and TomYum reads and writes osu-web's tables. That is
done in two phases:

- **Phase A — side by side (works today).** Both run on osu-web's MySQL/Redis behind one Caddy.
- **Phase B — one database (in progress, below).** TomYum's repositories are pointed at osu-web's
  tables. **Accounts are done:** an account made on the website logs into the game and an in-game
  registration shows up on the website. **Beatmaps, scores and stats are mirrored:** every map
  played, every vanilla score submitted and the player's per-mode stats are copied to osu-web, so
  profiles, rankings and beatmap leaderboards on the site fill up as people play. TomYum's own
  tables stay the source of truth the game reads.

---

## Phase A — running both

> **Trying it on one machine?** [docs/local-testing.md](local-testing.md) walks through osu-web,
> TomYum, HTTPS and the osu! client end to end.

> **Existing TomYum server with players?** Before switching it to osu-web accounts, run
> [`tools/MigrateToOsuWeb`](../tools/MigrateToOsuWeb/README.md): it turns your players into osu-web
> accounts (same passwords), renumbers TomYum's data to the osu-web ids and copies the history
> (maps, scores, stats) to the site. A fresh server can skip it.

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

Then, **once**, create osu-web's user groups. Its migrations don't, and both osu-web's own sign-up
and TomYum need the `default` and `bot` groups (TomYum refuses to start without them and prints
this command):

```bash
docker compose run --rm php artisan db:seed --class='Database\Seeders\ModelSeeders\GroupSeeder'
```

Only on a fresh database: the seeder truncates `phpbb_groups` first. Don't run the full
`db:seed` — it also creates fake users, beatmaps and scores.

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

It also sets `TOMYUM_DATABASE__OSUWEBNAME=osu`, which makes TomYum use osu-web's accounts
(`OSU_WEB_DB_NAME=` empty turns that off). On start TomYum creates BanchoBot as user 1 in osu-web's
`bot` group; nothing else is created or altered in osu-web's database. Outside Docker, set
`TOMYUM_DATABASE__OSUWEBNAME` yourself (see `config/.env.example`).

### Search on the website

osu-web's beatmap listing and user search read from **Elasticsearch**, not MySQL. Maps TomYum
copies and players who register in-game are reachable by URL straight away
(`/beatmapsets/<id>`, `/users/<id>`) but only appear in listings and search after re-indexing:

```bash
docker compose run --rm php artisan es:index-documents --types=beatmapsets,users --inplace
```

Run it on a schedule (e.g. every few minutes from cron) until TomYum queues index updates itself.

**Scores** are different: TomYum queues each new score for osu-web's own score indexer (the
`score-indexer` service in osu-web's compose), exactly as osu-web does, so leaderboards and profile
"best performance" lists update by themselves as long as that service runs. This needs TomYum on
osu-web's Redis (the compose overlay does that). If osu-web uses an `ES_INDEX_PREFIX`, set the same
value as `TOMYUM_DATABASE__OSUWEBINDEXPREFIX`.

### How plays look on the website

- **Only vanilla plays.** osu-web has no relax/autopilot rulesets, so those scores stay in TomYum.
- **Score numbers are stable's.** Scores are stored as osu-web "legacy" scores (with the `CL` mod,
  like imported stable scores on osu.ppy.sh), and the stable score is used both as the legacy score
  and as the score osu-web sorts and shows by default. So boards order exactly as in-game, and a
  play shows e.g. `12,345,678` rather than a lazer-style ~1,000,000 value. osu-web's optional
  "classic scoring" display assumes lazer numbers and will look odd.
- **pp** is TomYum's pp (ranked/approved maps only); **global rank** is recomputed for the mode
  after every play (osu-web reads it, but doesn't compute it), leaving out restricted/banned
  accounts. Country rank is computed live by osu-web.
- **Profile stats** (pp, accuracy, play count, ranked/total score, level, SS/S/A counts, max combo,
  hit counts, play time) are rebuilt from TomYum's data on every play, so they never drift.
- **Replays** aren't served by the site (TomYum keeps them); scores are marked as having none.
- **History** from before the switch is copied by `tools/MigrateToOsuWeb` (run before switching).
  The server itself only mirrors new plays: it never guesses whose an old TomYum user id was.
- **Session plugin** (`plugins/tomyum-session`) reads TomYum's own `users` table, so it doesn't see
  osu-web accounts. The osu-web site has its own login; leave the plugin off in osu-web mode.

### Medals and osu-web's InterOp API

osu-web's migrations leave its medal catalog (`osu_achievements`) empty, and a profile only shows
catalogued medals, so TomYum adds its 83 medals on start (only ids that are missing; ids, names and
slugs match osu!'s, so the icons load from osu-web's default icon URL). Earlier unlocks TomYum
recorded are copied over at the same time.

New unlocks go through osu-web's **InterOp API** (`POST /_lio/user-achievement/{user}/{medal}`), the
way osu!'s own game server reports them: osu-web records the unlock, adds "unlocked the … medal" to
the player's recent activity, updates the medal's unlock count and notifies the player. InterOp is
also used to put players who register in-game into the website's search. To enable it, set in
osu-web's `.env` and TomYum's `config/.env`:

| osu-web | TomYum | |
|---|---|---|
| `SHARED_INTEROP_SECRET=<long random string>` | `TOMYUM_OSUWEB__INTEROPSECRET=<same>` | shared signing key |
| | `TOMYUM_OSUWEB__INTEROPURL=http://nginx` | how TomYum reaches osu-web (the overlay sets this) |

Requests are signed like osu-web's `LegacyInterOpAuth` expects (HMAC-SHA1 of the full URL). Without
InterOp — or if a call fails — TomYum writes the unlock straight into `osu_user_achievements`: the
medal still shows on the profile, just without the activity entry.

### Making yourself staff

With shared accounts TomYum does **not** auto-grant Owner to "user id 3" as it does standalone:
ids are shared with website sign-ups, and staff in TomYum means osu-web's `admin` group, which is
full website admin. Register (on the site or in-game), then grant it in osu-web:

```bash
docker compose run --rm php artisan tinker
>>> App\Models\User::where('username', 'YourName')->first()->addToGroup(app('groups')->byIdentifier('admin'));
```

After that, `!setpriv` in-game works for everyone else. TomYum maps its privileges to osu-web like
this (other osu-web groups such as `nat` or `bng` are left alone):

| TomYum | osu-web |
|---|---|
| Administrator | `admin` group |
| Developer | `dev` group |
| Moderator | `gmt` group |
| Supporter | `phpbb_users.osu_subscriber = 1` |
| Owner | all of the above |

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

## Phase B — one database

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

1. ~~**Custom beatmap id range.**~~ **Decided:** `osu_beatmaps.beatmap_id` and
   `osu_beatmapsets.beatmapset_id` are `MEDIUMINT UNSIGNED` (max **16,777,215**), so
   `Beatmap.CustomIdFloor` moved from 100,000,000 to **10,000,000**. Custom ids are 10,000,000 –
   16,777,215; official ids stay below that. osu-web is left unmodified.
2. ~~**User ids.**~~ **Done:** `phpbb_users` auto-increments from 2. TomYum inserts BanchoBot as
   `user_id = 1` in osu-web's `bot` group at startup (osu-web needs `BANCHO_BOT_USER_ID=1`), and in
   osu-web mode the Development seed user takes the next free id instead of a fixed 2.
3. **Search and leaderboards on the site.** osu-web's score listings go through Elasticsearch,
   filled by `osu-elastic-indexer` (`score-indexer` in osu-web's compose) from a Redis queue.
   TomYum's score submission must push new score ids onto that queue, or the site won't show
   new plays. The exact queue key must be taken from `osu-elastic-indexer`.

### Steps

1. ~~Users~~ **Done:** `OsuWebUserRepository` (`src/TomYum.Persistence/OsuWeb/`) implements
   `IUserRepository` over `phpbb_users` + groups; enabled by `Database:OsuWebName`. Usernames:
   osu-web's `username_clean` keeps spaces, TomYum's safe name uses underscores — lookups accept
   both. Countries are upper-case in osu-web and converted both ways. Tested against the real
   table definitions (`tests/TomYum.Server.Tests/OsuWeb/`, run in CI with MySQL) and end-to-end
   with the stable login/registration flow. TomYum's own `users` table is simply unused in this mode.
2. ~~Beatmaps~~ **Done (mirror):** `OsuWebMirroringBeatmapRepository` writes every map to TomYum's
   table *and* osu-web's `osu_beatmapsets`/`osu_beatmaps`; TomYum's table stays the one the server
   and `RecalcPp` read, because scores (which join to it) still live there. On start, maps osu-web
   doesn't have yet are copied over. Status: osu-web gets the raw osu! API value, so graveyard and
   WIP aren't folded into pending; a staff-frozen status wins. Names are cut to osu-web's 80
   characters. Only played difficulties are known, so a set lists those; bpm, AR/OD/CS/HP and
   object counts keep osu-web's defaults, and drain time is filled with total length.
3. ~~Scores + stats~~ **Done (mirror):** `OsuWebScoreMirror` + the server's `OsuWebSync` write each
   vanilla score to osu-web's `scores` (legacy format, see "How plays look on the website"),
   rebuild the player's `osu_user_stats*` row, recompute `rank_score_index` for the ruleset, and
   queue the score for `osu-elastic-indexer` (`LPUSH osu-queue:{schema}`, like osu-web's
   `ScoreSearch::queueForIndex`). TomYum's `osu_web_score_links` table records which TomYum score
   became which osu-web score. Best-effort: a failure is logged and never fails the submission.
   Beatmaps can become osu-web-only (and `RecalcPp` follow) once TomYum *reads* scores from osu-web.
4. ~~Achievements~~ **Done:** catalog added to `osu_achievements` on start; unlocks via InterOp
   (`OsuWebInterop`) with a direct-insert fallback (`OsuWebMedals`); earlier unlocks copied.
5. ~~Data migration~~ **Done:** `tools/MigrateToOsuWeb` (`OsuWebAccountMigration` +
   `OsuWebScoreMirror.CopyHistoryAsync`): creates or links osu-web accounts, renumbers TomYum's
   scores/stats/medals to the osu-web ids (two-pass, one transaction), drops the Development seed
   user, then copies maps, vanilla scores, stats and ranks and queues the scores for indexing.
   Refuses to run on a server that already recorded plays with osu-web accounts; re-runnable.
6. Lazer: osu-web already serves `/api/v2` and `/oauth/token`, which covers most of roadmap
   Phase 7; TomYum then only needs the realtime hubs.
