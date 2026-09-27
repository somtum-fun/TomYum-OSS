# Testing everything locally

This runs the whole stack on one machine: **osu-web** (the website), **TomYum** (the game server) on
osu-web's MySQL and Redis, **Caddy** in front with HTTPS, and your **osu!stable** client pointed at
it. Written for Windows (Docker Desktop + WSL); on Linux/macOS skip the WSL parts.

Everything uses the domain `localtest.me`: it and every subdomain of it resolve to `127.0.0.1` in
public DNS, so there's nothing to add to your hosts file.

```
osu! client / browser ──https──> Caddy (Windows) ──> localhost:8080  TomYum.Server  ─┐
                                                 └─> localhost:8180  osu-web nginx   ├─ osu-web's MySQL, Redis,
                                                                                     ┘  Elasticsearch (Docker)
```

## 0. Prerequisites

- **Docker Desktop** with the WSL 2 backend, and "WSL integration" enabled for your Ubuntu distro
  (Settings → Resources → WSL integration). Give Docker at least **6 GB RAM** (Elasticsearch alone
  wants 1 GB).
- **WSL Ubuntu** (`wsl --install -d Ubuntu` in an admin PowerShell). Run the `bash` commands below
  inside it, and keep both repositories inside WSL (e.g. `~/src`), not under `C:\` — it's much faster.
- **Caddy** for Windows: download `caddy.exe` from <https://caddyserver.com/download>.
- **Git** (inside WSL: `sudo apt install git`).
- Optional, only for TomYum's automated tests: **.NET 10 SDK**.

## 1. osu-web

```bash
mkdir -p ~/src && cd ~/src
git clone https://github.com/sundeiii/osu-web.git
cd osu-web
NGINX_PORT=8180 bin/docker_dev.sh -d     # first run builds images and installs deps: takes a while
```

The first run creates `.env`. Open it (`nano .env`) and set, adding lines that aren't there:

```ini
NGINX_PORT=8180                        # keep osu-web off 8080 (TomYum uses it) for every compose command
APP_URL=https://osu.localtest.me
BANCHO_BOT_USER_ID=1                   # TomYum's BanchoBot
SHARED_INTEROP_SECRET=pick-a-long-random-string   # TomYum uses the same value
TRUSTED_PROXIES=PRIVATE_SUBNETS        # trust Caddy/nginx so the site knows it's on https
REGISTRATION_MODE_WEB=true             # allow sign-up on the website (off by default)
USER_BYPASS_VERIFICATION=true          # local only: no emailed login code
```

Then:

```bash
docker compose up -d && docker compose restart      # pick up the new .env
docker compose logs -f migrator                     # wait until it exits (database migrated)
docker compose run --rm php artisan db:seed --class='Database\Seeders\ModelSeeders\GroupSeeder'
```

The last command creates the user groups (`default`, `bot`, `admin`, …); TomYum won't start without
them. Check <http://localhost:8180> shows the osu-web front page.

## 2. TomYum

```bash
cd ~/src
git clone https://github.com/sundeiii/TomYum.git
cd TomYum
git checkout eager-ritchie       # until this work is merged
cp config/.env.example config/.env
```

In `config/.env` set:

```ini
TOMYUM_SERVER__DOMAIN=localtest.me
TOMYUM_OSUWEB__INTEROPSECRET=pick-a-long-random-string   # same as osu-web's SHARED_INTEROP_SECRET
TOMYUM_OSU__APIKEY=                    # optional; empty uses the osu.direct mirror
```

(The database, Redis and osu-web settings in that file are overridden by the compose overlay.)

Start the server on osu-web's network — **not** this repo's own `mysql`/`redis`:

```bash
docker compose -f deploy/docker/docker-compose.yml -f deploy/docker/docker-compose.osu-web.yml \
  up -d --build server
docker compose -f deploy/docker/docker-compose.yml -f deploy/docker/docker-compose.osu-web.yml logs -f server
```

The log should show `Database schema ensured`, `Accounts stored in osu-web database osu`,
`osu-web medals: 83 added to the catalog` (first start only) and `Now listening on`. `curl
localhost:8080/health` should answer `{"status":"ok",...}`.

Optional: add `pp-sidecar` to the `up` command for relax/autopilot pp (it's a separate build; the
server falls back to the built-in calculator without it).

Because the compose file runs TomYum in `Development`, it creates a test account
**`TomYum` / `faker123`** (in osu-web's user table). Handy for a first login.

## 3. Caddy (HTTPS)

Copy `deploy/caddy/Caddyfile.local` somewhere on Windows (from WSL it's at
`\\wsl$\Ubuntu\home\<you>\src\TomYum\deploy\caddy\Caddyfile.local`), then in PowerShell:

```powershell
.\caddy.exe trust                                 # once, as administrator: trust Caddy's local CA
.\caddy.exe run --config .\Caddyfile.local
```

Docker Desktop publishes the containers' ports on Windows' `localhost`, which is where Caddy sends
traffic. Open <https://osu.localtest.me>: it should be the osu-web site with a valid certificate.

## 4. osu! client

Make a shortcut to your `osu!.exe` with the target:

```
"C:\...\osu!\osu!.exe" -devserver localtest.me
```

The client then talks to `c.localtest.me`, `osu.localtest.me`, `assets.localtest.me`, … over HTTPS,
which is why Caddy and `caddy trust` are needed.

## 5. What to check

| Test | How | Where to look |
|---|---|---|
| Website account → game | Register on <https://osu.localtest.me>, log in with it in osu! | You're online; BanchoBot is in the user list |
| Game account → website | Register in the osu! client | `https://osu.localtest.me/users/<id>` shows the profile |
| Staff | Grant yourself admin (below), log in to osu! again, then `!help` | Staff commands listed |
| Score | Play a **ranked** map to the end | Profile → Ranks, the map's leaderboard on the site, pp and global rank |
| Stats | Same | Profile header: pp, accuracy, play count, level |
| Medals | Pass a ranked map with HD (or get a 500 combo) | In-game popup; Profile → Medals; "unlocked the … medal" under Recent activity |
| Relax | Play with RX | Stays off the website (by design) |
| Logs | `docker compose ... logs -f server` | Any `osu-web sync failed` / `medal sync failed` warning |

Grant yourself admin (from the osu-web folder):

```bash
docker compose run --rm php artisan tinker
>>> App\Models\User::where('username', 'YourName')->first()->addToGroup(app('groups')->byIdentifier('admin'));
```

**Search and listings** (beatmap listing, user search) read Elasticsearch. New maps and players are
reachable by URL straight away; to see them in listings run, from the osu-web folder:

```bash
docker compose run --rm php artisan es:index-documents --types=beatmapsets,users --inplace
```

Scores on leaderboards and profiles are indexed automatically by osu-web's `score-indexer` service.

## 6. TomYum's automated tests (optional)

These need the .NET 10 SDK and a throwaway MySQL + Redis. osu-web's stack already uses ports 3306
and 6379, so stop it first (`docker compose stop` in the osu-web folder), then from TomYum:

```bash
docker compose -f deploy/docker/docker-compose.yml up -d mysql redis
export TOMYUM_TEST_MYSQL="Server=127.0.0.1;User=root;Password=dev-root;"
export TOMYUM_TEST_REDIS="127.0.0.1:6379"
dotnet test TomYum.sln
docker compose -f deploy/docker/docker-compose.yml stop mysql redis
```

Without the two variables the database tests are skipped, not failed.

## Troubleshooting

- **TomYum exits with "osu-web group 'default' does not exist"** — step 1's `GroupSeeder` command.
- **"address already in use" on 8080 / 3306 / 6379** — osu-web isn't on `NGINX_PORT=8180`, or this
  repo's own `mysql`/`redis` are running alongside osu-web's. Stop them: `docker compose -f
  deploy/docker/docker-compose.yml stop mysql redis`.
- **`network osu-web_default not found`** — osu-web isn't running, or its folder isn't called
  `osu-web`; set `OSU_WEB_NETWORK=<folder>_default`.
- **osu! says it can't connect** — check <https://c.localtest.me> in a browser loads without a
  certificate warning (if not: `caddy trust` as administrator, restart osu!).
- **Medals show but no "unlocked" activity entry** — InterOp isn't set up: the secrets differ, or the
  log says `InterOp not configured`. A `403 invalid_signature` in the log means the secrets differ.
- **Site links are `http://`** — `TRUSTED_PROXIES` in osu-web's `.env`.

## Starting over

```bash
# osu-web folder: removes its database, Redis and Elasticsearch data
docker compose down -v
# TomYum folder
docker compose -f deploy/docker/docker-compose.yml -f deploy/docker/docker-compose.osu-web.yml down
```

Then repeat from step 1's `bin/docker_dev.sh`.
