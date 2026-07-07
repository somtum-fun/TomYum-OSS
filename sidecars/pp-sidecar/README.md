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

`GET /health` → `ok`. The sidecar downloads the `.osu` from `osu.ppy.sh/osu/{beatmapId}` and caches
it in memory. `mode` is the vanilla ruleset; Relax/Autopilot are read from `mods`, so an osu! map
played as a catch convert with relax comes through as `mode: 2, mods: 128`.

## Run

```bash
cargo run --release          # listens on :9000 (override with PORT)
PORT=9100 cargo run --release
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
