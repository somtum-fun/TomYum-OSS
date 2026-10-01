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

## Applying for BN

`applyRequirements` in `qat-config.json` sets what an application (and a rejoin) needs. Here:
`{"kudosu": 0, "connections": true}`:

- no kudosu (osu!'s 150 when `kudosu` isn't set; 0 turns it off);
- Discord and the player's osu! account (osu.ppy.sh) linked in their account settings → Connections
  (osu-web `App\Libraries\TomYumConnections`; QAT reads `GET /api/v2/me/tomyum-connections` with the
  applicant's token);
- no osu! restriction in the last 12 months.

osu!'s API never shows past restrictions (only silences and tournament bans of the last 28 days, even
to the player). What's known: restricted when they link (`is_restricted`), and restricted at a later
check (once a day while linked; a restricted account's public profile is gone). Each is kept and counts
for 12 months. A restriction lifted before they linked can't be seen: the NAT reviewing the
application can still look the player up.

The osu! link uses the same osu! OAuth app as TomYum (`OSU_LINK_CLIENT_ID`/`SECRET` in osu-web.env); its
callback on osu.ppy.sh must be `https://osu.<domain>/home/account/connections/osu_bancho/callback`.
Discord needs its own application (`DISCORD_OAUTH_CLIENT_ID`/`SECRET`, callback
`https://osu.<domain>/home/account/connections/discord/callback`); a Discord link is also written to
TomYum's `discord_links`, so the Discord bot knows it.

## Off by default

- `enableAutomation`: QAT's scheduled jobs (evaluation reminders, report feeds, badge checks, …). Most
  post to Discord webhooks (`qat-webhooks.json`); turn on once those are set.
- `enableMessages`: in-game messages from a bot account (`bot` client credentials with `delegate
  chat.write`, and `botUserId`). Until then messages are only logged.
