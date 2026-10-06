# Old clients (2013–2015)

osu!stable builds from late 2013 to 2015 can play on a TomYum server: log in, chat, osu!direct,
leaderboards and scores, on the **same leaderboards** as today's stable. They have no `-devserver`
option, so players connect them with [Titanic! Hook](https://github.com/osuTitanic/hook), a loader
from the osuTitanic project that redirects the game's `ppy.sh` addresses to your server in memory,
without changing any game files. Plan and status: [ROADMAP.md](ROADMAP.md) ("Old clients").

## For players

1. Get a build from [Osekai Snapshots](https://osekai.net/snapshots/) (for example `b20140624`) and
   unpack it into its own folder. This server hosts no osu! files.
2. From the [Hook releases](https://github.com/osuTitanic/hook/releases), put
   **`Titanic.fallback.exe`** next to `osu!.exe`. Builds before late 2015 run on .NET 2.0 and need
   this .NET 2.0 loader: under `Titanic.exe` (.NET 4) the game runs, but every downloaded beatmap
   fails to import (they all end up in `Songs\Failed`).
3. Windows marks downloaded files, and .NET then refuses to load `osu!.exe` ("loadFromRemoteSources").
   Unblock the whole folder once, in PowerShell:
   ```powershell
   Get-ChildItem -Recurse "C:\path\to\b20140624" | Unblock-File
   ```
4. Create `Titanic!.cfg` in the same folder **before the first start** (otherwise the first start
   connects to Titanic's own server):
   ```
   Version=2
   ServerName=<your domain>
   HookTcpConnections=False
   ```
   Hook fills in the rest. `EnableConsole=True` and `LogToFile=True` show what it does.
   `HookTcpConnections=False` because otherwise Hook looks up `server.<your domain>` at startup (the
   address TCP-era builds connect to) and stops with "No such host is known" when there's no such
   record; the supported builds speak HTTP and don't need it. (A DNS record for `server.<your domain>`
   avoids the error too: sundei.eu has one, pointing at Cloudflare.)
5. Start the game with `Titanic.fallback.exe` and log in with your normal account.

Sign out of a real osu! account in that client first: its login is sent to this server instead.

## Supported builds

| Build | Status |
|---|---|
| `b20140624` | Tested 2026-10-02 (locally and on sundei.eu): login, chat, online users, osu!direct (search, download, import, thumbnails), leaderboards, score submission (pp, medals, ranking screen), being spectated by today's client. Not yet tested: multiplayer, spectating today's client. |
| Late 2013 – 2015 | Expected to work like the above (HTTP bancho, protocol 18/19); not tested build by build yet. |
| `b20130823.3`, `b20130915.4` | HTTP bancho with the 2013 layouts below (era E3). |
| Up to `b20130802.1` | Bancho over raw TCP, which Cloudflare can't carry: TomYum's own TCP bancho (`TCP_BANCHO=true`, ports 13380-13384), or TomYum.Gateway on another box (docs/gateway.md), with a client patched to use `server.<domain>`. Packets are pushed as they're queued, and a ping goes out after 10 s of quiet (b337-b558 reconnect after 20 s of nothing, b595b-b1704 after 30, b1807-2013 after 48). |
| `b1122` (2011) | Tested 2026-10-04 on example.com: login, avatars, leaderboards, score submission, rating on the results screen (`/rating/ingame-rate2.php`). Titanic! Hook redirects only its TCP connection, not its web requests (avatars, osu!direct, leaderboards): use the decompiled client rebuilt with the server's domain. Its osu!direct (no `u` in the search) lists only the sets this server has, its own and the imported osu! ones, not the mirror's. |
| b337 – b20130802.1 (2008 – 2013) | Translated per era (below), from the decompiled clients; covered by tests (`OldBanchoTests`), not yet played build by build. Login, chat, PMs, channels, friends, statuses, the online list, spectating, multiplayer and song select's beatmap info. |

The protocol eras (`ClientBuild.Has(BanchoFeature)` holds the first build of each change; `EarlyPackets`
translates b337–b1704, `LegacyPackets` the rest):

| Builds | Ids | What's different from today |
|---|---|---|
| b337 | own table | Ids 11–50 are today's + 1, from 51 its own. One chat pane (`#osu` and PMs only, no channels). The user update has a "new stats" flag (stats only with the name); status text unless Unknown; refusals -1/-2. |
| b370 – b476 | + 1 from 11 | One user update (12) with a completeness byte, 16-bit rank; channels (`#osu` is created and joined by the client itself); no channel part before b639. Rooms: byte id, 16-bit mods, no password (b595b), teams/scoring (b558), host (b420), mode (b476). Privileges from b452, friends (32-bit count) from b476. |
| b497 – b639 | + 1 from 11 | PROTOCOL_VERSION (sent after the login reply); statuses carry mode and beatmap id; 27-byte score frames before b558; beatmap info with 1 grade before b504, then 3. |
| b699 – b1704 | + 1 from 11 | 32-bit rank (= b1122); full updates end with longitude/latitude from b1218; match player skipped from b1553; refusal -4 from b1700. |
| b1807 – b20120812 | today's | Stats and presence split, but presence has an avatar flag, a location string and a whole permissions byte (and the mode from b20120812); 16-bit mods in statuses, stats and rooms; no pp before b20120812; 16-bit room ids from here. |
| b20120916 – b20121223 | today's | 32-bit mods; mania from b20121003; 4 grades from b20121115; today's presence from b20121203; the menu icon is an image URL until b20121223. Rooms without free mods or seed. |
| b20130303 – b20130802.1 | today's | Messages from the client have the sender id; rooms get free mods; from b20130513 frame bundles start with the extra int and rooms end with a seed (today's 8-slot room). |

Every build up to 2013 gzips client packets of 150 bytes or more (header byte 2); TomYum unpacks them
first. Before b20130513 spectator frame bundles lose today's leading int (and get one, plus the tag byte
on b337–b534's 27-byte score frames, when they come in). A login refusal a build doesn't know reads as a
successful login there, so refusals below its lowest code go out as -1 ("failed"), even before there's a
session (the build is read from the login itself). Not sent to these builds: packets their table doesn't
have (invites, silences, restart, menu icon before b20120916, ...), and to b337–b1704 rooms with an id
past 255 (a byte there). b337 sees other players' stats only with their presence (its update has no
stats-only form).

## What the server does for them

- **Plain HTTP.** Builds before late 2015 talk to the server over plain HTTP and don't follow
  redirects to HTTPS. Caddy answers the game's hosts (`osu.` for `/web/*` and `/release/*`, `c.`,
  `c1.`–`c6.`, `ce.`, `a.`, `b.`, `s.`) on `http://` without redirecting, and Cloudflare must not
  redirect them either ("Always Use HTTPS" off, or a rule exempting those hosts).
- **Build detection.** The login's version (`b20140624`) is kept on the session (`ClientBuild`).
- **Chat.** At login an old build is told it's in the auto-join channels (`#osu`, `#announce`):
  it shows their tabs but, unlike today's client, never asks to join them, so its chat input
  stayed on "Connecting...".
- **Per-build packets** (`LegacyPackets`, after Lekuruu's [chio](https://github.com/Lekuruu/chio.py),
  MIT, one file per build where the protocol changed): packets a build doesn't know are left out
  (104 account restricted before b20140716, 105 before b20140731, 106 match abort and 107 before
  b20141104), and builds before b20140528 get and send multiplayer rooms with 8 slots instead of 16
  (slots past 8 are dropped for them; their rooms get the other 8 locked).
- **Song select** asks bancho for its maps' status and the player's grades (BEATMAP_INFO_REQUEST 68,
  by file name and id); the reply (69) uses the build's statuses (qualified from b20150826, loved
  from b20161101, approved before) and grades in the order osu!, catch, taiko, mania.
- **Invites** reach old builds as MATCH_INVITE (88) with the inviter as the target (osuTitanic's
  way); BanchoBot's `!mp invite <player>` sends the same, for clients whose menu doesn't.
- **8-slot builds** join rooms only in slots 1–8 and aren't sent scores, fails or skips of slots
  9–16; room and spectator chat open as a tab (CHANNEL_AUTO_JOIN before JOIN_SUCCESS); the menu
  icon is "|" rather than empty (from b20121223); client error reports (20) go to the server log;
  `osu-checktweets.php` answers empty.
- **Diagnostics.** The server log names each packet type the first time it goes to or comes from
  an old build's session (`LegacyPacketLog`): that's how the chat problem showed.
- **Downloads and thumbnails.** .NET 2.0 has no TLS 1.2, so these clients can't follow the usual
  redirects to the mirror's or ppy's HTTPS. For a build before 2016 (downloads), or a request that
  came over plain HTTP (thumbnails, by Cloudflare's `Cf-Visitor` header), TomYum fetches the file and
  passes it on.
- **Leaderboards** (`osu-osz2-getscores.php`) in the format the client asks for (`vv`): before
  `vv=3` ranked is 4 and qualified 2, before `vv=4` there's no loved (sent as approved) and no
  replay flag, and `vv=1` has the date as text. (The client reads a status it doesn't know as ranked.)
  Builds before 2013 send the player as `u=<id>` with no `us`; their personal best is found by that id.
  Builds before b20130329 don't send their mods (`mods`), so the Relax or Autopilot leaderboard is
  the one the player's last bancho status says they're on (`WebEndpoints.BoardMods`).
- **Leaderboards of 2008–2012 builds**, each version in its own reply: `osu-getscores2.php` (b337,
  b370: status line, then scores with perfect as `True`/`False` and the avatar file in place of the
  rank; approved, qualified and loved go as ranked), `osu-getscores3.php` (b394a, the same with approved),
  `osu-getscores4.php` (b420, + personal best), `osu-getscores5.php` (b452–b534.test, + offset and
  title) and `osu-getscores6.php` (b558–b20120916, + rating). The title is osu!'s
  `[bold:0,size:20]Artist|Title`. `osu-getstatus.php` (b337) answers `md5,ranked,beatmap,set,` for
  known maps.
- **Score submission** (`osu-submit-modular.php`): today's submission with the old key (no
  `osuver`), the map's checksum from the score, and the old reply (map line and overall chart,
  accuracy as a fraction, medal file names, no empty fields). Builds before 2012 send the score
  unencrypted (then the password is required): b337–b1652 in the query, b1700–b1844.test as the
  form field `score`. b337–b904 post to `osu-submit.php` (to b497) and `osu-submit-new.php`, with 15
  to 17 fields (no mode before b497: osu!), and get `rank\ntoNextRank\nmedals` back (the map rank
  when the play is the player's new best, else 0; a rejection is `0\n0`, which they don't retry).
- **Fair shared leaderboards.** Score v1 and the mod multipliers haven't changed since 2013 in any
  mode, so old clients' scores count as they are (osuTitanic does the same), with three exceptions
  (`LegacyScoreRules`, by the build in the score's version field; a score with no version, or a
  numbered one like `1553`, is from a build before 2012): mod bit 4 was NoVideo before
  2014 and isn't taken as TouchDevice (which lowers pp); Co-op is refused (unranked since April
  2015); and a passed taiko or catch play whose 300s, 100s and misses don't add up to the map's max
  combo in that mode (the old client converted it differently) is kept but off the leaderboard, with
  no pp. Not checked: osu!mania long notes before b20150727 were judged a little harsher (only
  against the old-client player).
- **Medal images** at `s.<domain>/images/achievements/<name>`: today's icons fitted to the old
  images' 133×103 (osu! no longer has the old art; it isn't used).
- **Update checks** (`/release/update.php` and the like) answer "no update", or the client waits on
  "Receiving data..." for good.
- **osu!direct for 2009–2012 builds.** `/d/<set>n` is the set without its video. Downloads over
  plain HTTP are passed on (b476–b639 send no `?u=`, b452 a malformed one), and so is
  `/web/maps/<file>` (an outdated map's new file). `osu-search-set.php` sends the map's status as
  `osu-search.php` does (1 ranked, 2 approved…). Builds without osu!mania (numbered builds and those
  before b20121003, told by the online player named in `u`, or by a request with no player over
  plain HTTP) aren't shown mania-only sets; sets that mix modes stay.
- **Small answers.** `/rating/ingame-rate.php` (b337–b595b) and `ingame-rate2.php` (to b20121119)
  rate maps like `osu-rate.php`, a vote answered with the bare average; `coins.php` (b20150331) is
  `0`; `osu-screenshot.php` takes `u`/`p` from the query; in-game comments style supporters as
  `subscriber` for builds before 2016; `/release/filter.txt` (the chat filter) holds one word nobody
  types; `bancho_connect.php` answers `127.0.0.1` to b20130802.1–b20130915.4 (anything else costs
  them 5 seconds), and `xx` instead of `cn`/`tw` to builds before 2016 (those add osu!'s Chinese
  bancho to their server list).
- **Frames.** Score and spectator frames in an older layout get today's fields added for today's
  clients (`LegacyFrames`); `b20140624` already sends today's layout. The server logs, once per
  session, what an old build's frames looked like.

## Device verification

With `ClientVerification:Enabled`, a new device must be confirmed on the website before it logs in
(`osu.<domain>/p/verify`; an account's first device is trusted). Bancho asks with login reply -8,
which only b20151228 and later understand: every earlier build reads an unknown refusal as a
successful login, so TomYum never sends it -8 (it goes out as -1). What those builds get instead is
`ClientVerification:OldClients` (`TOMYUM_CLIENTVERIFICATION__OLDCLIENTS`):

- `Unverified` (the default): they log in without the check. The device is recorded unverified, so a
  newer client on it is still asked.
- `Refuse`: they're refused (-1) **only when the device needs verifying**: a verified device, or an
  account's first, still gets in. The message links the website's verify page (which can confirm the
  account's newest waiting device without the game's details) and `ClientVerification:DownloadsUrl`
  (default `https://osu.<domain>/home/download`). Builds before b20120812 send no complete hardware
  hashes (b20120522 and b20120529 have no disk signature), so for them any account that already has
  a device counts as a new device.

Today's clients (b20160101 on) always send their hardware hashes, so one that doesn't is refused
for an account that already has a device, whatever `OldClients` says (it would otherwise skip the check).
The website's verify page opened without the game's details (`ch`) offers the account's device that
tried to log in most recently, in the last day.

A patched old client can verify like today's: it adds a last field to its login line,
`version|utc|city|hashes|pm|tomyum-verify` (a comma-separated capability list), and is then sent
-8 and handles it itself (it opens `osu.<domain>/p/verify?u=<name>&ch=<hashes>` once and retries the
login until the device is confirmed). The decompiled clients' patcher does this for b20120812 to
b20151026.3 (step 11; the builds that send a whole device) and for b337 to b20120529 (step 12): those sent
no client hash, or one without the uninstall id or disk, so the patched build sends its install's whole
device instead, as later builds do (`md5(osu!.exe):adapters:md5(adapters):md5(md5(UninstallID)):md5(disk serial)`,
with the same `UninstallID` registry value osu! keeps, so it's the same device for every build on a PC).
The login line is the same in every era once patched (b337 sent only `version|utc`, b370 to b1652
`version|utc|city`): `version|utc|city|hashes|0|tomyum-verify`, over TCP bancho for builds up to
b20130815. Its -8 goes out as -8 in the build's own packet ids; the build retries through its own
"connection failed" retry every 15 s. b20151228.3 handles -8 already and only gets `ch` added to its verify link.
The hook (original builds) follows `OldClients`.

## Testing locally

`deploy/caddy/Caddyfile.local` serves the plain-HTTP hosts for `localtest.me`; Hook's `ServerName`
is then `localtest.me`. osu! runs once per PC, so a second player needs another PC (or the
production server and a friend).
