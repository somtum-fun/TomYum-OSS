# Monitoring

`deploy/production/watchdog.sh` checks the server every minute (cron) and sends the admin a Discord
DM from the bot when something goes wrong, then another when it's fine again. It runs outside the
containers, so it still works when the game server is down. When the whole VM is down it can't
report anything, so that needs an outside monitor.

```
* * * * * ~/TomYum/deploy/production/watchdog.sh
```

`./watchdog.sh --test` sends a DM to check the bot can reach the admin (who must share a server with
the bot and allow DMs). `./watchdog.sh --status` prints every check's reading without sending
anything. While `deploy.sh` runs, the checks are paused.

## What it checks

| Check | Alerts when | After |
|---|---|---|
| Website, bancho, stable's `/web`, lazer's servers | the public address doesn't answer as expected (through Cloudflare and the tunnel) | 2 checks |
| Disk | `/` is 85% full or more; the alert gives Docker's build cache size, the usual culprit | 3 checks |
| Memory | less than 10% of memory is available; names the three containers using the most | 3 checks |
| CPU | the 5-minute load average is over 2 per CPU; names the three busiest containers | 5 checks |
| Containers | a service isn't running, or its health check fails (one-off services left out) | 2 checks |
| Crashes | Docker restarted a container on its own; the DM has its last log lines | at once |
| Slow requests | 10 or more requests over 3 seconds in the last five minutes, by Caddy's request log; the DM names the slowest addresses | 2 checks |

For slow requests, connections that stay open on purpose are left out: websockets (website
notifications, lazer's hubs), downloads (`/d/`, previews, replays, `/web/maps`) and uploads (beatmap
submission, screenshots).

## Limits

Set them in the crontab line to change them:

```
* * * * * WATCHDOG_DISK_PERCENT=90 ~/TomYum/deploy/production/watchdog.sh
```

| Variable | Default | Meaning |
|---|---|---|
| `WATCHDOG_DISK_PERCENT` | `85` | disk use (%) that alerts |
| `WATCHDOG_MEMORY_PERCENT` | `10` | available memory (%) below which it alerts |
| `WATCHDOG_LOAD_FACTOR` | `2` | 5-minute load average per CPU above which it alerts |
| `WATCHDOG_SLOW_SECONDS` | `3` | a request this long counts as slow |
| `WATCHDOG_SLOW_COUNT` | `10` | slow requests in five minutes that alert |
| `WATCHDOG_DISCORD_USER` | the admin | Discord user id that gets the DMs |
| `WATCHDOG_DIR` | `~/.tomyum-watchdog` | where it keeps what it saw last |

## Disk space

Docker's build cache is what fills the disk: every deploy builds images again. `deploy.sh` keeps the
most recently used 20 GB of it (`docker builder prune -af --max-used-space 20GB`). Run the same by
hand when the disk alert comes between deploys. On 2026-10-02 the cache had reached 128 GB after a
day of many deploys, when deploys still kept a day's worth instead.
