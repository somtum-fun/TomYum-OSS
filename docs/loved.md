# Project Loved (loved requests)

`loved.<domain>` is our own small site, [example-org/private-repo](https://github.com/example-org/private-repo)
(Node + Vue): players ask for maps to be Loved and the Project Loved team decides. No rounds, polls
or seasons. (It replaced a port of osu!'s Project Loved site, example-org/private-repo, which
was too much for a private server; that repo is kept but not deployed.)

## What players and the team do

- **Sign in** with their account here.
- **Ask for a map:** set link or id, mode, why. Graveyard/WIP/pending only; one request per map and
  mode (a second asker is sent to review the first request). Missing osu! maps are imported.
- **Review:** for or against, with why. Open requests are listed by score (the submitter counts as
  one "for").
- **The team** decides, with a reason players see: **Love it** (the map's difficulties in that mode
  become Loved on TomYum straight away, as from the BN panel, with the "has just been loved!"
  activity entry), **Reject**, **Remove** (off the list; it can be asked for again) or **Blacklist**
  the map (its open requests are removed and nobody can ask for it). Rejected and removed requests
  can be opened again.

The team is the website's **Project Loved** group: members decide for the modes they have in the
group; its leaders, Administrators and osu! Developers decide for every mode. Changes on the website
show within a minute.

## How it's deployed

| Piece | Where |
|---|---|
| `loved-requests` | Built from `../../../project-loved-request` (`docker-compose.override.yml`), port `127.0.0.1:3102` |
| Database | `loved_requests` on `db` (user `loved_requests`); tables made at start, sessions included |
| Settings | `deploy/production/loved-requests.env` (secrets, untracked) |
| Sign-in | OAuth client "Project Loved" (id 7) on osu-web, redirect `https://loved.<domain>/auth/callback` |
| HTTPS | Host Caddy site `loved.<domain>` → `127.0.0.1:3102` |
| Website side | InterOp `tomyum/loved-team`, `tomyum/import-beatmapset/{id}`, `tomyum/loved-beatmapsets` (TomYumInterOpController) |

Rebuild after changing it: `sudo docker compose build loved-requests && sudo docker compose up -d loved-requests`.

The Project Loved forum (#120) and its posting rule (`tomyum.loved_forum_id`) came with the old site
and aren't used by this one.
