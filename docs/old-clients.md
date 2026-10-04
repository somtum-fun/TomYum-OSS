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
| Up to `b20130815` | Bancho over raw TCP, which Cloudflare can't carry, on `server.<domain>` (DNS only) ports 13380–13384: the game server's own TCP bancho (`Bancho:Tcp:Enabled`, as on example.com), or TomYum.Gateway (docs/gateway.md) when old clients should be on another box. Idle connections get a ping every 10 s (these builds log in again after 30 s of silence). |
| Numbered builds (`b1122`, 2011) | Their protocol (read from the decompiled b1122, example-org/private-repo) is translated by `EarlyPackets`: ids from 11 up are one higher, a user's status, stats and presence are one packet (12). Login, channels, chat, PMs, friends, statuses and the online list. Multiplayer, spectator frames and beatmap info are left out for now. Titanic! Hook redirects only their TCP connection, not their web requests (avatars, osu!direct, leaderboards): use the decompiled client rebuilt with the server's domain. Tested 2026-10-04 on example.com: login, avatars, leaderboards, score submission; rating on the results screen at `/rating/ingame-rate2.php`. |

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
  icon is "|" rather than empty; client error reports (20) go to the server log;
  `osu-checktweets.php` answers empty.
- **Diagnostics.** The server log names each packet type the first time it goes to or comes from
  an old build's session (`LegacyPacketLog`): that's how the chat problem showed.
- **Downloads and thumbnails.** .NET 2.0 has no TLS 1.2, so these clients can't follow the usual
  redirects to the mirror's or ppy's HTTPS. For a build before 2016 (downloads), or a request that
  came over plain HTTP (thumbnails, by Cloudflare's `Cf-Visitor` header), TomYum fetches the file and
  passes it on.
- **Leaderboards** (`osu-osz2-getscores.php`) in the format the client asks for (`vv`): before
  `vv=3` ranked is 4 and qualified 2, before `vv=4` there's no loved (sent as approved) and no
  replay flag, and `vv=1` has the date as text. An unknown status left a 2014 build waiting forever.
- **Score submission** (`osu-submit-modular.php`): today's submission with the old key (no
  `osuver`), the map's checksum from the score, and the old reply (map line and overall chart,
  accuracy as a fraction, medal file names, no empty fields).
- **Fair shared leaderboards.** Score v1 and the mod multipliers haven't changed since 2013 in any
  mode, so old clients' scores count as they are (osuTitanic does the same), with three exceptions
  (`LegacyScoreRules`, by the build in the score's version field): mod bit 4 was NoVideo before
  2014 and isn't taken as TouchDevice (which lowers pp); Co-op is refused (unranked since April
  2015); and a passed taiko or catch play whose 300s, 100s and misses don't add up to the map's max
  combo in that mode (the old client converted it differently) is kept but off the leaderboard, with
  no pp. Not checked: osu!mania long notes before b20150727 were judged a little harsher (only
  against the old-client player).
- **Medal images** at `s.<domain>/images/achievements/<name>`: today's icons fitted to the old
  images' 133×103 (osu! no longer has the old art; it isn't used).
- **Update checks** (`/release/update.php` and the like) answer "no update", or the client waits on
  "Receiving data..." for good.
- **Frames.** Score and spectator frames in an older layout get today's fields added for today's
  clients (`LegacyFrames`); `b20140624` already sends today's layout. The server logs, once per
  session, what an old build's frames looked like.

## Testing locally

`deploy/caddy/Caddyfile.local` serves the plain-HTTP hosts for `localtest.me`; Hook's `ServerName`
is then `localtest.me`. osu! runs once per PC, so a second player needs another PC (or the
production server and a friend).
