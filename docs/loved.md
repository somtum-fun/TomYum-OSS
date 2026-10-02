# Project Loved

[mnhttn-cafe/project-loved-web](https://github.com/mnhttn-cafe/project-loved-web), the site osu!'s
Project Loved team uses for submissions, captains' reviews, rounds, nominations and the forum polls
that decide which maps become Loved, runs next to TomYum as `loved.<domain>`. Players sign in with
their account on this server. Our fork is `example-org/private-repo`, branch `prod`; its
changes are marked `production`.

## How it's deployed

| Piece | Where |
|---|---|
| `loved-api` | The API server, built from `../../../project-loved-web` (`docker/Dockerfile-production`, target `api`); runs pending SQL migrations at start |
| `loved-web` | nginx with the built site, port `127.0.0.1:3102`; passes `/api` to `loved-api` |
| Database | `project_loved` on `db` (user `project_loved`), sessions included; uploads in volume `tomyum_loved-storage` |
| Settings | `deploy/production/loved.env` (secrets, untracked) |
| Sign-in | OAuth client "Project Loved" on osu-web (`oauth_clients`, owner blueskychan_), redirect `https://loved.<domain>/api/auth/callback` |
| HTTPS | Host Caddy site `loved.<domain>` → `127.0.0.1:3102` |

Rebuild after changing the fork:

```sh
G=../../../project-loved-web
sudo docker compose build --build-arg GIT_COMMIT=$(git -C $G rev-parse HEAD) \
  --build-arg GIT_COMMIT_TIMESTAMP=$(git -C $G log -1 --format=%at) loved-api loved-web
sudo docker compose up -d loved-api loved-web
```

## The team comes from the website

Every 10 minutes, at start and whenever someone signs in, the site reads the website's groups
(`/_lio/tomyum/loved-team`):

| Site role | From |
|---|---|
| Captain, per mode | The **Project Loved** group (`loved`), with the modes the member has there |
| Admin | The Project Loved group's leaders, Administrators (`admin`) and osu! Developers (`dev`) |

So give someone Project Loved with their modes in the admin panel; make them group leader to make them
a site admin. Admin and captain are set only this way (changes made on the site are undone at the next
sync). The other roles (metadata, moderator, news editor and so on) are still given on the site,
under Manage → Roles.

## Maps from osu!

Nominations are usually osu! maps the server doesn't have yet: the site asks the website to import
them (`/_lio/tomyum/import-beatmapset/{id}`, TomYum's import). Their osu! mappers have no account
here (osu! user ids are other people on this server), so the site keeps them as name-only users,
shown as "name (osu!)" and linked to their osu! profile.

`LOVED_MIN_OSU_FAVORITES` (0 here; 30 on osu!) is how many favourites an osu!standard nomination
needs. Favourites are this server's own.

## Running a round

osu!'s team posts the polls with a separate management program. Here it's done from the round's page
(Picks → the round), by an admin:

1. Set the round's name and **news author** (Edit). Captains review maps, nominate them and write
   each nomination's description.
2. The news author (and any captain who wants to post their own nominations' topics) allows the site
   to post on the forum: Manage → Forum opt-in.
3. **Open polls**: a topic with a Yes/No poll (10 days) for each nomination and a main topic per mode
   in the **Project Loved** forum (#120 under Beatmaps).
4. When the polls have ended, **Post results and Love the maps**: the votes are read, the results are
   posted in the topics, and every map that reached the mode's threshold becomes **Loved** on the
   server (its difficulties in that mode, without the excluded ones), as the BN panel does it, with
   the "has just been loved!" activity entry.

Set each mode's voting threshold under Manage → Settings ("default voting threshold") before the
first round: it starts at 0%, which passes every map.

Forum rules that apply: the Project Loved team and the staff can post there however much they've
played (`tomyum.loved_forum_id`); voting in a poll needs `FORUM_POST_MINIMUM_PLAYS` plays (5 here, in
`osu-web.env`; 200 on osu!).

## Left out

- The compliance checks (omc-api, osu!'s licensing checker) and the Google survey: not set up.
- osu!'s Project Loved history (archive.loved.sh): its user ids are osu!'s, so the site started empty.
- News posts for osu!'s wiki: not used.
