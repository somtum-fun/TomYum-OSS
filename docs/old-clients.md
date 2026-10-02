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
   ```
   Hook fills in the rest. `EnableConsole=True` and `LogToFile=True` show what it does.
5. Start the game with `Titanic.fallback.exe` and log in with your normal account.

Sign out of a real osu! account in that client first: its login is sent to this server instead.

## Supported builds

| Build | Status |
|---|---|
| `b20140624` | Tested 2026-10-02 (locally): login, chat, online users, osu!direct (search, download, import, thumbnails), leaderboards, score submission (pp, medals, ranking screen), being spectated by today's client. Not yet tested: multiplayer, spectating today's client. |
| Late 2013 – 2015 | Expected to work like the above (HTTP bancho, protocol 18/19); not tested build by build yet. |
| Up to `b20130815` | Not supported: these speak bancho over raw TCP, which a Cloudflare Tunnel can't carry. |

## What the server does for them

- **Plain HTTP.** Builds before late 2015 talk to the server over plain HTTP and don't follow
  redirects to HTTPS. Caddy answers the game's hosts (`osu.` for `/web/*` and `/release/*`, `c.`,
  `c1.`–`c6.`, `ce.`, `a.`, `b.`, `s.`) on `http://` without redirecting, and Cloudflare must not
  redirect them either ("Always Use HTTPS" off, or a rule exempting those hosts).
- **Build detection.** The login's version (`b20140624`) is kept on the session (`ClientBuild`).
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
