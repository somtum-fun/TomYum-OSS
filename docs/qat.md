# BN Management (QAT)

[pishifat/qat](https://github.com/pishifat/qat), the site osu!'s Beatmap Nominators and NAT use for BN
applications, evaluations, reports, vetoes and qualified-map checks, runs next to TomYum as
`bn.<domain>`. Players sign in with their account on this server. Our fork (`example-org/private-repo`, branch
`prod`) makes the osu! server configurable; see its README, "Running on another osu! server".

## How it's deployed

| Piece | Where |
|---|---|
| `qat` | Built from `../../../qat` (`deploy/production/docker-compose.override.yml`), port `127.0.0.1:3101` |
| `qat-mongo` | MongoDB 7, volume `tomyum_qat-mongo-data` (QAT's own data: users, applications, evaluations, events) |
| Settings | `deploy/production/qat-config.json` (secrets, untracked), mounted as `/app/config.json`; Discord webhooks in `qat-webhooks.json` |
| Sign-in | OAuth client "BN Management (QAT)" on osu-web (`oauth_clients`, owner BanchoBot), redirect `https://bn.<domain>/api/callback` |
| HTTPS | Host Caddy site `bn.<domain>` → `127.0.0.1:3101` (websocket included) |

Rebuild after changing the fork: `sudo docker compose build qat && sudo docker compose up -d qat`.

## Roles

QAT reads a player's groups from `/api/v2/me` when they sign in. `groups` in `qat-config.json` maps
this server's ids (they differ from osu!'s, and `7` is everyone's default group here):

| QAT role | Our group (id) |
|---|---|
| NAT | `nat` (11) |
| BN | `bng` (4), `bng_limited` (5) |
| "gmt" (GMT and developers) | `gmt` (9), `dev` (8) |

Give someone BN or NAT on the website (admin panel → groups, with their modes for BN); they get it in
QAT the next time they sign in. QAT's own BN/NAT management (evaluations, adding modes, moves) lives in
its database and doesn't change groups on the website. `admin` in the config (osu ids) are QAT's site
admins.

## Nomination events

QAT's activity numbers come from map events. `eventFeed` polls this server's
`/api/v2/beatmapsets/events` every 5 minutes (nominate, qualify, disqualify, nomination reset, rank; the
first poll goes back 30 days). Loved maps aren't part of QAT's data.

## Off by default

- `enableAutomation`: QAT's scheduled jobs (evaluation reminders, report feeds, badge checks, …). Most
  post to Discord webhooks (`qat-webhooks.json`); turn on once those are set.
- `enableMessages`: in-game messages from a bot account (`bot` client credentials with `delegate
  chat.write`, and `botUserId`). Until then messages are only logged.
