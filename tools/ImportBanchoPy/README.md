# ImportBanchoPy

Moves a **bancho.py** server onto TomYum + osu-web: [osuAkatsuki/bancho.py](https://github.com/osuAkatsuki/bancho.py),
[osu-NoLimits/bancho.py-ex](https://github.com/osu-NoLimits/bancho.py-ex), [kawatapw/kawata.py](https://github.com/kawatapw/kawata.py)
and forks of them. It looks at the source's tables to tell which one it is (`--flavor` overrides) and
imports what each has.

Players keep their **ids, passwords and names**; scores keep their ids, so replays and links keep
working. Nobody has to reset a password: bancho.py and osu-web both store `bcrypt(md5(password))`.

| From bancho.py | To | Notes |
|---|---|---|
| `users` | osu-web `phpbb_users` + default group | same id; country upper-cased; supporter time kept while it runs; restricted players restricted (`osu_user_banhistory`, reason from the old log); previous names (`old_name`, somtum) searchable |
| `users.priv` bits | osu-web groups | NOMINATOR → `bng`, NAT → `nat`, MODERATOR → `gmt`, ADMINISTRATOR → `admin`, DEVELOPER → `dev`, ALUMNI → `alumni`, TOURNEY_MANAGER → `tc`, HELPER → `spt`; VERIFIED → TomYum `verified_users`; WHITELISTED → `tomyum_account_flags`. kawata.py's bits are folded: can manage beatmaps → `bng`, can ban → `gmt`, can change privileges → `admin` |
| `relationships` | `phpbb_zebra` | friends and blocks |
| `maps` (osu!'s, played or staff-frozen) | TomYum `beatmaps` → osu-web `osu_beatmaps`/`osu_beatmapsets` | frozen statuses stay frozen (ranked, loved, pending); nothing is ever cleared |
| `scores` | TomYum `scores` (same ids) → osu-web `scores` for modes 0-3 | relax `+4` and autopilot `+8` stay in TomYum; pp is kept only on ranked/approved passes and recalculated later |
| `.data/osr/<id>.osr` | TomYum's replay folder | same format and name |
| `.data/osu/<id>.osu` | TomYum's `.osu` cache | so the pp rework doesn't download every map again |
| `stats` | TomYum `user_stats` | play count, ranked and total score kept; pp, accuracy and ranks recomputed |
| `user_achievements` | TomYum + osu-web medals | matched by slug (`achievements.file`); a server's own medals: `php artisan tomyum:custom-medals-import` |
| `client_hashes` | TomYum `client_hashes` + osu-web `osu_user_security` | known machines are trusted: nobody verifies a PC they played on |
| `logs` | TomYum `staff_log` | actions prefixed `bpy.`, `Via = bancho.py` |
| `favourites`, `ratings` | osu-web favourites and ratings | for sets the website has |
| `user_connections` (somtum) | TomYum `discord_links`, osu-web connections | verified OAuth links only; the legacy `users.discord_id` is not a link |
| `ranking_history` (somtum) | osu-web rank graph (modes 0-3), TomYum `mode_rank_history` (relax/autopilot) | the last 89 days |
| `beatmap_nominations` (somtum) | osu-web `beatmapset_events` | nominate, qualify, rank, love, disqualify, with their dates |
| private sets (`server = 'private'`, ids from 100,000,000) | TomYum custom sets | uploaded again through the server (`--custom-sets`): they get ids in TomYum's range; their scores follow |
| `clans`, avatars, banners, userpages | osu-web teams, avatars, covers, userpages | `php artisan tomyum:import-bpy-media` on the website (it needs osu-web's image code) |
| `maps.is_dan` (somtum) | `tomyum.dan` plugin courses and clears | `--dan`, after the private sets: one course per dan difficulty (category = the set's title, 96% accuracy, 95% for intro dans, the dan mods), each player's best qualifying pass as their clear |

Not imported: offline mail, in-game login IPs, donation records, screenshots, API keys, open map
requests (osu-web has no request queue; players hype a set again on the website).

## Before you start

1. The new server is **installed and empty**: osu-web migrated and seeded (`php artisan tomyum:setup`),
   TomYum started **once** (it creates BanchoBot and its tables) and then **stopped**. Stopped, because
   its Discord announcer would post every imported score and player, and plays could race the import.
2. **BanchoBot's id.** bancho.py's bot is user 1 and gets no account here (lazer shows no avatar for
   id 1). TomYum's BanchoBot is **3** unless `TOMYUM_BANCHO__BOTUSERID` says otherwise. If one of your
   players is id 3 (on most bancho.py servers that is the owner), either run TomYum with
   `TOMYUM_BANCHO__BOTUSERID=2` **and** osu-web with `BANCHO_BOT_USER_ID=2` (and the spectator server)
   before its first start, so the bot takes 2 and the owner keeps 3, or move that player with
   `--remap 3=<free id>`.
3. Load the **dump** into the new server's MySQL as its own database, say `bpy_import`, and copy
   bancho.py's `.data/` (at least `osr/` and `osz/`) and its avatar and banner folders somewhere the
   containers can read.
4. Settings: the tool reads TomYum's (`TOMYUM_DATABASE__*`, `TOMYUM_REDIS__CONFIGURATION`,
   `TOMYUM_REPLAYS__PATH`, `TOMYUM_BANCHO__BOTUSERID`, `TOMYUM_OSUWEB__SCOREPROCESSOR`,
   `TOMYUM_OSUWEB__INTEROPSECRET`, `TOMYUM_SERVER__DOMAIN`). The simplest is to run it inside the
   server's container (below), where they are already set.

## Running it

With the production compose stack (`deploy/production`), publish the tool once and run it in a
one-off `server` container, which has the runtime, the settings, the network and the replay volume:

```sh
cd TomYum
dotnet publish tools/ImportBanchoPy -c Release -o /srv/bpy-import/tool

cd deploy/production
zcat /srv/bpy-import/somtum_db.sql.gz | docker compose exec -T db sh -c 'MYSQL_PWD="$MYSQL_ROOT_PASSWORD" mysql -uroot -e "CREATE DATABASE IF NOT EXISTS bpy_import" && MYSQL_PWD="$MYSQL_ROOT_PASSWORD" mysql -uroot bpy_import'
docker compose stop server

run() { docker compose run --rm --no-deps --entrypoint dotnet \
    -v /srv/bpy-import/tool:/tool:ro -v /srv/bpy-import/data:/import/data:ro -v /srv/bpy-import/out:/app/import-out \
    server /tool/ImportBanchoPy.dll --source bpy_import --data /import/data "$@"; }

run              # the plan: counts, and anything that blocks (names, ids, emails)
run --apply      # the import; ~1 minute per 50,000 scores
docker compose start server
run --custom-sets --server-url http://server:8080     # only if the plan listed private sets; then restart server
run --rework --server-url http://server:8080          # always: a pp rework of every mode (see below)
run --dan --server-url http://server:8080             # somtum fork: osu!dan maps as courses of the tomyum.dan plugin
```

Then, on the website:

```sh
docker compose run --rm -v /srv/bpy-import:/import:ro artisan tomyum:import-bpy-media --database=bpy_import \
    --avatars=/import/www/avatars --banners=/import/www/assets/banners --clans=/import/www/assets/clans \
    --userpages=/import/data/user/about_me --teams
docker compose run --rm -v /srv/bpy-import:/import:ro artisan tomyum:custom-medals-import --database=bpy_import \
    --icons=/import/www/assets/medals --by=<staff user id>      # the server's own medals, if it had any
```

**pp: always a rework.** The old server's pp came from another calculator, so `--rework` starts a
TomYum pp rework of every mode (Admin → pp rework shows it): every passed score is worked out again,
then bests, totals, the website's copies (through ppy's processor) and ranks follow, and the rankings
are paused until it is done. Then the search index:

```sh
cd deploy/production
docker compose run --rm artisan es:index-scores:set-schema --schema scores_1   # a fresh website only
docker compose exec score-indexer score-indexer index alias scores_1          # idem
docker compose run --rm artisan es:index-documents                            # players, beatmaps, teams
docker compose run --rm artisan es:index-scores:queue --all -n                # the scores
```

The plan prints counts only. Names that osu-web's rules would refuse (3-15 bytes, `A-Z a-z 0-9 _ - [ ]`
and spaces) are listed in `import-out/refused-names.csv` and imported as they are; the import stops if
two names differ only in case or an email is on two accounts. Everything else that it writes goes to
`import-out/import-<time>.log` and `custom-set-ids.csv` (old → new ids of the private sets). Keep the
folder private.

Each step is recorded in `tomyum.bpy_import_steps`, so a run that stopped halfway continues where it
was; `--step <name>` runs one step again. The id map is `tomyum.bpy_import_user_map`. Both tables can
be dropped once you are happy.

## What to check afterwards

- Log in on the website, in stable and in lazer with an old account (same password); the owner's and
  BanchoBot's profiles; a ranked map's leaderboard and a replay download; a relax leaderboard;
  staff-frozen statuses on the website and in osu!direct; medals; friends; a restricted player is off
  the rankings; a known PC logs in without verifying; Discord links and roles.
- pp moves a little: bancho.py's calculator isn't TomYum's. Say so when you announce the move.
- The rework and the score processor (`TOMYUM_OSUWEB__SCOREPROCESSOR=true`) take a while on a big
  import; wait for the rework to finish and `redis-cli LLEN osu-queue:score-statistics` to reach 0
  before judging pp and ranks on the website.

## Rollback

The import only adds rows to an empty server. To start over, wipe the target (`docker compose down -v`
on a test stack; on production, restore the pre-import dump) and run it again. Never patch a bad import
by hand.
