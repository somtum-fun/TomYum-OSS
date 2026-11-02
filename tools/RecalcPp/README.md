# PP Recalculation Tool

Standalone CLI tool that recomputes PP (performance points) for all Best scores in the database and updates weighted stats (PP + accuracy) for all affected users.

## When to use

- After upgrading the PP calculation algorithm (new `ppy.osu.Game.Rulesets.*` package version)
- After fixing a PP calculation bug (like the accuracy=0 issue from 2026-07-03)
- When migrating from another server with incorrect/missing PP values
- To backfill PP for scores that were submitted before the PP calculator was working

## Usage

From the repository root:

```bash
# Dry run (calculate but don't save changes)
dotnet run --project tools/RecalcPp -- --dry-run

# Live run (recalculates and saves to database)
dotnet run --project tools/RecalcPp

# Recalc only osu! standard (mode 0)
dotnet run --project tools/RecalcPp -- --mode 0

# Custom batch size (default 100)
dotnet run --project tools/RecalcPp -- --batch-size 50
```

## Options

- `--dry-run` — Calculate PP but do not persist changes (useful for testing)
- `--mode MODE` — Only process scores for the given mode (0=osu, 1=taiko, 2=catch, 3=mania)
- `--batch-size N` — Number of scores to process per transaction (default 100)
- `--force` — Write pp for every score, changed or not
- `--all-passed` — Also recompute passed scores that aren't the player's best
- `--create-stats` — Give players who have scores but no stats row in a mode a new row (pp and accuracy only; it puts them on that mode's leaderboard). Off by default: only existing rows are updated.

With the website's score pool on (`OsuWeb:ScoreProcessor`), the vanilla modes' totals are the website's (stable and lazer together, ranked maps only, ppy's formula) and TomYum copies them in-game, so this tool leaves them alone. After it, run ppy's processor for the website side:

```bash
docker compose run --rm --no-deps score-processor performance scores all -t 4
for r in 0 1 2 3; do docker compose run --rm --no-deps score-processor performance user-totals all --ruleset-id $r -t 2; done
```

To run the tool against a deployed stack, publish it (`dotnet publish tools/RecalcPp -c Release -o out -p:UseAppHost=false`) and run it in `mcr.microsoft.com/dotnet/aspnet:10.0` as the server's user (`--user 1654:1654`), on the stack's network, with the server container's environment and `--volumes-from` the server (so it uses its `.osu` cache: set `TOMYUM_PERFORMANCE__OSUCACHEDIRECTORY=/app/data/osu-cache`).

## Configuration

The tool reads database connection settings from `appsettings.json` in the same directory, or from environment variables prefixed with `TOMYUM_`:

```bash
export TOMYUM_DATABASE__HOST=localhost
export TOMYUM_DATABASE__PASSWORD=yourpassword
dotnet run --project tools/RecalcPp
```

## What it does

1. **Fetches all Best scores** from the database (filtered by mode if `--mode` is specified)
2. **Recalculates PP** for each score using `IPerformanceCalculator` (same engine as live submissions)
3. **Updates the score** in the database with the new PP value
4. **Recomputes weighted PP + accuracy** for each affected user using the standard osu! formula:
   - Weighted PP: `Σ(pp_i × 0.95^i) + 416.6667 × (1 − 0.9994^n)`
   - Weighted accuracy: `Σ(acc_i × 0.95^i) × 100 / (20 × (1 − 0.95^n))`
5. **Saves updated stats** to the `UserStats` table

## Performance

- Processes scores in batches (default 100) to avoid holding long transactions
- Shows progress percentage during execution
- PP calculation is the bottleneck: ~100ms per uncached beatmap, <1ms when cached
- Typical throughput: 5-10 scores/second for cold cache, 50-100 scores/second when warm

## Safety

- **Always run `--dry-run` first** to verify results before committing changes
- The tool does not modify `RankedScore`, `TotalScore`, `PlayCount`, or grade counts — only `Pp` and `Accuracy`
- Best-score status is not recalculated (the tool assumes existing Best/Submitted flags are correct)
- Failed/incomplete PP calculations log a warning and skip the score without failing the whole batch

## Example output

```
TomYum PP Recalculation Tool
Mode: LIVE
Filter: all modes
Batch size: 100

Found 1523 Best scores to recalculate
  Scores: 1523/1523 (100%)   
Updating stats for 47 user+mode pairs

=== Summary ===
Total scores processed: 1523
PP recalculated: 1489
Users updated: 47
Elapsed: 142.3s
```
