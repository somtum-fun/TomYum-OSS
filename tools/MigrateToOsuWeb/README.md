# Move an existing TomYum server to osu-web

One-time tool for a TomYum server that already has players, run **before** switching the server to
osu-web accounts (`TOMYUM_DATABASE__OSUWEBNAME`). A fresh server doesn't need it. Background:
[docs/osu-web.md](../../docs/osu-web.md).

It does three things:

1. **Accounts.** Every TomYum player becomes an osu-web account with the same name, email, password
   (hashes are compatible, nobody has to reset), country, join date and staff rights. A player who
   already has an osu-web account with the **same name and email** is linked to it instead.
2. **Renumbering.** TomYum's scores, stats and medals are moved from the old TomYum user ids to the
   osu-web ids, in one transaction. Web sessions (session plugin) are cleared; people log in again.
   TomYum's own `users` table is left untouched as a record of the old ids.
3. **History.** Beatmaps, vanilla scores, per-mode stats and global ranks are copied to osu-web, and
   the scores are queued for osu-web's score indexer (needs its Redis; if unreachable the tool says
   so and the scores just aren't searchable until re-indexed).

## Usage

Stop the TomYum server and **back up both databases** first. From the repository root:

```bash
export TOMYUM_DATABASE__OSUWEBNAME=osu      # plus the usual TOMYUM_DATABASE__* settings

dotnet run --project tools/MigrateToOsuWeb                    # dry run: shows the plan, changes nothing
dotnet run --project tools/MigrateToOsuWeb -- --apply         # do it
```

Then set `TOMYUM_DATABASE__OSUWEBNAME` for the server too and start it.

It reads the same configuration as the server and `tools/RecalcPp` (`config/.env`,
`config/tomyum.yaml`, `TOMYUM_*` variables); `appsettings.json` here holds local defaults.

## The plan

| Action | Meaning |
|---|---|
| `Create` | New osu-web account for this player. |
| `Link` | osu-web already has this player (same name and email); their data joins that account. |
| `Drop` | Not moved; their scores/stats/medals are deleted. The Development seed user (`tomyum@tomyum.local`, known password) is always dropped. `(no account)` rows are ids that have data but no TomYum account. |
| `Conflict` | The name, or the email, belongs to a *different* osu-web account. Rename one side, then run again. |

The tool refuses to apply while there are conflicts, and while there are `(no account)` ids unless
you add `--drop-unmapped` (which deletes that data).

## Safety

- **Order matters.** If the server has already recorded plays with osu-web accounts, TomYum's
  tables mix old and new ids and can't be sorted out; the tool detects this and refuses. Restore
  the backup from before the switch and run the tool on that.
- **Re-runnable.** The account step is recorded (`osu_web_migration` table in TomYum's database)
  and never runs twice; the history copy only copies what's missing. If it stops halfway, fix the
  cause and run it again.
- **Relax/autopilot** scores stay in TomYum (osu-web has no such rulesets); their user ids are
  still renumbered.
