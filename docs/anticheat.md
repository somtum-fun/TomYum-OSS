# Anti-cheat: circleguard

Every ranked pass, stable and lazer, goes to **circleguard**
([sundeiii/lazer-circleguard](https://github.com/sundeiii/lazer-circleguard), a C# service run next
to TomYum). It judges the replay and gives a risk score (0–100) with the reasons. This works like
production's circleguard, with its rules and thresholds.

## What's checked

`CircleguardWatch` follows the website's scores (so stable and lazer alike) and sends a play when:

- it passed, on a ranked, approved or loved map, with a combo of 5 or more;
- it isn't Autopilot, and Relax only in osu!;
- the player isn't restricted.

The replay is TomYum's own file for a stable score. For a lazer score, it's the spectator server's
file (mounted read-only). A score waits up to three minutes for its replay.

Each osu! play is compared with the map's best plays here (up to five other players). On official
maps it's also compared with the **top 5 on osu!**, so a replay stolen from osu! is caught too. Those
come from the osu! API: `get_scores`, then `get_replay`. osu! allows ten replays a minute, so they're
fetched one at a time; they're kept in `data/circleguard/official`, and top lists for a day.

What circleguard looks at is in its README:

- unstable rate, frametime, snaps, hold times and similarity for osu!;
- aim under Relax, tapping under Autopilot;
- timing in taiko and mania, teleports in catch;
- timewarp by play length.

## What happens

| Risk | |
|---|---|
| ≥ `ReportAt` (15) | Posted to Discord's circleguard channel (`TOMYUM_DISCORD__CIRCLEGUARDCHANNELID`, default the reports channel) with the reasons and numbers, with Restrict / Silence 1 day / Dismiss buttons |
| > `RestrictAbove` (15) | The player is restricted at once by BanchoBot and told *"[circleguard] Suspicious activity detected on your recent play."* (no details). The staff log has the details. The Discord post says so and has an **Unrestrict (false alarm)** button. Staff accounts are only reported, never restricted |

The two thresholds are production's. To only report and never restrict automatically, set
`TOMYUM_CIRCLEGUARD__RESTRICTABOVE=101`.

## Scanning a score by hand

Staff can check any score with `/scan <score link or id>` on Discord. It's compared with more replays
than the automatic check: the map's top 30 here, and osu!'s top 30 on osu! maps (`official`: top 5 or
none to go faster). osu!'s replays are downloaded at ten a minute and kept, so a map's first scan can
take a few minutes and later ones are quick. The answer is only for the person who asked; nobody is
restricted by a scan, but the Restrict / Silence buttons are on it.

The embed (for scans and automatic flags alike) shows the verdict and a risk bar, the reasons, the
play, the unstable rate, frametime and snaps, the closest top play (a copy is spelled out, with a
link to the original) and how many replays it was compared with.

## Setting it up

1. Next to TomYum on the server: `git clone https://github.com/sundeiii/lazer-circleguard`.
2. In `deploy/production/.env`, set `CIRCLEGUARD_KEY` to a long random string (e.g. `openssl rand -hex 32`).
   An empty key turns the checks off.
3. `docker compose build circleguard server && docker compose up -d`.

Settings (`Circleguard` section; the compose file sets the first three):

| Setting | Default | |
|---|---|---|
| `Url` | | `http://circleguard:8555` |
| `Key` | | The shared key |
| `LazerReplaysPath` | | Where lazer replays are (`/app/lazer-replays`); empty: lazer plays aren't checked |
| `ReportAt` | 15 | Report at this risk or more |
| `RestrictAbove` | 15 | Restrict above this risk (101: never) |
| `CompareOfficial` | true | Compare with osu!'s top plays (needs `OSU_API_KEY`) |
| `CachePath` | `data/circleguard` | Where official replays are kept |
