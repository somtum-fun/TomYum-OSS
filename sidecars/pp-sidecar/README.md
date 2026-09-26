# tomyum-pp-sidecar

Reference **pp sidecar** for TomYum. Computes pp (including **relax / autopilot**) with
[akatsuki-pp-rs](https://github.com/osuAkatsuki/akatsuki-pp-rs) — a fork of `rosu-pp` with
relax/autopilot-tuned algorithms — behind TomYum's `HttpPerformanceCalculator` contract
(see `ADR-0010`).

Use it when you want proper relax/autopilot pp that the in-process official (ppy/lazer) calculator
can't provide, while keeping vanilla on the official calc.

## Contract

`POST /pp`

```jsonc
// request
{ "beatmapId": 1801936, "mode": 0,   // mode = vanilla ruleset 0-3
  "mods": 128,                        // legacy bitmask; Relax=128, Autopilot=8192
  "n300": 100, "n100": 0, "n50": 0, "nGeki": 0, "nKatu": 0, "nMiss": 0,
  "maxCombo": 100, "passed": true }
// response
{ "pp": 2.41, "starRating": 1.50 }
```

`GET /health` → `ok`. The sidecar downloads the `.osu` from `osu.ppy.sh/osu/{beatmapId}`. `mode` is
the vanilla ruleset; Relax/Autopilot are read from `mods`, so an osu! map played as a catch convert
with relax comes through as `mode: 2, mods: 128`.

## `.osu` cache (two levels)

`.osu` bytes are cached **L1 in-memory** (process lifetime) and **L2 on disk** so they survive
restarts and are not re-downloaded on every request. The disk dir is set by `OSU_CACHE_DIR`
(default `data/osu-cache`); a `{beatmapId}.osu` dropped there is served without ever hitting
osu.ppy.sh — the seam for locally-hosted custom maps. Point it at the **same** dir the C# official
backend uses (`Performance:OsuCacheDirectory`) to dedupe downloads across both.

## Run

```bash
cargo run --release                         # listens on :9000 (override with PORT)
PORT=9100 OSU_CACHE_DIR=/srv/osu cargo run --release
```

## Smoke test

```bash
./smoke-test.sh            # builds, boots on :9101, checks /health + vanilla&relax pp + L2 cache write
./smoke-test.sh 9200       # or on another port
```

Then point TomYum at it (`config/tomyum.yaml`):

```yaml
performance:
  routes:
    - { modes: [vanilla],            backend: official }
    - { modes: [relax, autopilot],   backend: akatsuki }
  backends:
    official: { type: inprocess-lazer }
    akatsuki: { type: http, url: "http://localhost:9000/pp", timeoutMs: 2000, fallback: official }
```

**After changing which backend a mode uses, run** `dotnet run --project tools/RecalcPp -- --force --all-passed`
so existing scores in that mode are re-valued with the new algorithm.

## Docker

```bash
docker build -t tomyum-pp-sidecar sidecars/pp-sidecar
docker run -p 9000:9000 tomyum-pp-sidecar
```

## Swapping the pp crate

`Cargo.toml` pins `akatsuki-pp` (the akatsuki-pp-rs crate). To use a **fork**, point the `git`/`tag`
at it. To use **plain rosu-pp** (no relax/autopilot tuning, same API):

```toml
akatsuki-pp = { package = "rosu-pp", version = "1" }
```

The `main.rs` API (`Beatmap::from_bytes`, `Difficulty`, `Performance`) is identical across both.
