# pp libraries and pp reworks

## The pp libraries

| | Library | Used by | Pinned in |
|---|---|---|---|
| Vanilla | ppy's `ppy.osu.Game.Rulesets.*` | TomYum (stable scores) and ppy's score processor (lazer, the website) | `src/TomYum.Performance/TomYum.Performance.csproj`, `deploy/score-processor/Dockerfile` (`PROCESSOR_REF`) |
| Relax / Autopilot | [akatsuki-pp-rs](https://github.com/osuAkatsuki/akatsuki-pp-rs) | the `pp-sidecar` (osu! Relax: Akatsuki's own `osu_2019` formula, as akatsuki-pp-py does it) | `sidecars/pp-sidecar/Cargo.lock` |

Vanilla pp has to match between TomYum and the processor, so the newest vanilla version is the one
the processor's newest commit uses (osu! may have released a newer one already; the panel says so).

## The pp updater (`deploy/pp-updater`)

A small Python service on the host (systemd `tomyum-pp-updater`, as root: it drives Docker, so no
container needs Docker's socket). Every 6 hours, or on **Check now**, it compares the pins with the
newest versions; the admin panel (Admin → pp rework → pp libraries) shows both. **Update** (with a
confirmation) asks it to:

- **Relax/Autopilot:** `cargo update -p akatsuki-pp`, build and restart the `pp-sidecar`, check its
  `/version`, commit `Cargo.lock`, then start a rework of Relax and Autopilot.
- **Vanilla:** move the processor to its newest commit and TomYum's packages to its ppy version,
  check TomYum builds, build and restart `server` and `score-processor` (and pull the website's
  difficulty service), commit the pins, then start a rework of the vanilla modes.

A failed build puts the pins back and leaves the running containers alone; the error shows in the
panel. Commits are local (push them yourself). They talk through Redis: `tomyum:pp-libraries`
(status), `tomyum:pp-library-update` (the job), `tomyum:pp-library-requests`,
`tomyum:pp-rework-requests`.

Install: `sudo cp deploy/pp-updater/tomyum-pp-updater.service /etc/systemd/system/ && sudo systemctl enable --now tomyum-pp-updater`
(adjust the paths and `RUN_AS` in it first). `sudo python3 deploy/pp-updater/updater.py check` runs a check by hand.

## pp reworks (`src/TomYum.Server/Admin/PpReworks.cs`)

Started after an update, or by hand (Admin → pp rework: the modes and a reason). While one runs:

- its modes are paused (`RankingPause`): TomYum doesn't recompute their ranks or change their
  in-game pp; the website's ranking page shows a copy of the vanilla stats as they were
  (`tomyum_snapshot_osu_user_stats*`), and Relax/Autopilot rankings stand still by themselves;
- new scores in them wait for their pp (`scores.PpHeld`): not a best, not in totals, no pp on the
  website;
- every passed score's new pp is worked out a batch at a time into `pp_rework_scores` (resumes
  after a restart; scores made meanwhile join in);
- every page shows a banner (the modes, why, how far), and the admin panel shows the progress.

Then it switches over at once: the new pp, each player's best per map, everyone's totals, lazer
Relax/Autopilot plays' pp on the website. Vanilla modes then wait for ppy's processor to redo the
website's scores (its queue); then ranks and in-game pp follow and the rankings are live again.
Cancelling keeps the old pp.
