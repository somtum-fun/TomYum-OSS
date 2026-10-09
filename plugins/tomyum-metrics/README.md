# tomyum.metrics: Prometheus numbers for the status page

A **loose-source** TomYum plugin that serves `GET /metrics` in the Prometheus text format, for the
status page's Prometheus (job `tomyum`). It only needs
`TomYum.Plugins.Abstractions`.

## What it serves

| Metric | Type | Labels | From |
|---|---|---|---|
| `tomyum_online_players` | gauge | `client` = `stable`, `lazer`, `irc` | `IHostOnlineCounts` (lazer comes from the spectator server's Redis feed; that sample is left out, not 0, while the feed can't be read) |
| `tomyum_scores_submitted_total` | counter | `client` = `stable`, `lazer` | stable: `ScoreSubmittingEvent`; lazer: new rows in the website's `scores` without a `legacy_score_id` (needs trusted DB access) |
| `tomyum_logins_total` | counter | `client` = `stable`, `result` = `ok`, `failed`, `held` | `PlayerLoginEvent` and `PlayerLoginRefusedEvent` (held: maintenance, or a device to verify) |
| `tomyum_registered_users` | gauge | | the website's `phpbb_users`, every 5 minutes, without inactive, restricted and bot accounts (needs trusted DB access; left out until counted) |
| `tomyum_multiplayer_matches` | gauge | | open osu!stable multiplayer rooms |
| `tomyum_build_info` | gauge | `version` | the server's version (always 1) |
| `process_*`, `dotnet_*` | | | CPU, memory, open files, GC and thread pool of the server process |

Counters start at 0 when the server starts; Prometheus' `rate()`/`increase()` handle the resets.
Every label has a fixed set of values, so the number of series doesn't grow.

## Who can read it

Plugin routes match every host name, so the plugin checks the `Host` header itself and answers 404 on
anything not in `hosts`. By default that's `127.0.0.1:8091` and `localhost:8091` (the localhost port
the status page scrapes) and `server:8080`, `localhost:8080` (inside the compose network). The public
names (`c.`, `osu.`, …) never get the numbers. The port itself must stay on 127.0.0.1.

## Requirements

- The folder must be in the server's `plugins/` directory (in Docker: `/app/plugins/tomyum-metrics`).
- `Plugins:AllowLooseSource` must be `true` (the default).
- For lazer scores and registered players: its id in `Plugins:Trusted`, and the database enabled. Without
  that the plugin still serves everything else, and logs a warning saying so.

## Config (`Plugins:tomyum.metrics`)

```yaml
plugins:
  trusted:
    - "tomyum.metrics"
  "tomyum.metrics":
    hosts: "127.0.0.1:8091,localhost:8091,server:8080,localhost:8080"
    osuWebDatabase: "osu"        # the website's database; "" turns lazer scores and registered players off
    intervalSeconds: 15          # how often the database is read (lazer scores)
    usersIntervalMinutes: 5      # how often registered players are counted
```

As environment variables: `TOMYUM_PLUGINS__TRUSTED__0=tomyum.metrics`, and for example
`TOMYUM_PLUGINS__tomyum.metrics__hosts=...`.

## Checking it

```sh
curl -s http://127.0.0.1:8091/metrics | grep '^tomyum_'
curl -s -o /dev/null -w '%{http_code}\n' -H 'Host: c.example.com' http://127.0.0.1:8091/metrics   # 404
```

The startup log shows `plugin loaded: tomyum.metrics …` and `tomyum.metrics: GET /metrics on …`.

Tests: `tests/TomYum.Server.Tests/MetricsPluginTests.cs` compiles this folder with Roslyn and checks the
route, the host check, the counters and the metric names.
