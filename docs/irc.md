# IRC

TomYum has an IRC server like osu!'s `irc.ppy.sh`: chat and bots from any IRC client, in the same
channels and PMs as the game, the website, lazer and Discord (`src/TomYum.Server/Irc/`).

## Connecting

1. Send `!irc` to BanchoBot (in the game or on the website). It answers with your IRC password in
   a private message, and the server and port. Asking again makes a new password.
2. In your IRC client: server `irc.<domain>` (see below), port **6667**, no TLS, nick = your
   username with `_` for spaces, server password = the IRC password.
3. `/join #osu` (or any channel). PMs to players and to BanchoBot (`!help`) work as in the game.

One session per player: while you're in the game, IRC refuses you; logging into the game ends your
IRC session. Restricted players can't use IRC. Silences apply as in the game.

## How it works

Each IRC connection is an ordinary online session. What an IRC user says goes through the game's
own chat handlers (as the client's packets would), so channels, BanchoBot commands, silences, the
chat log and the website/lazer/Discord bridges all apply; the chat queued for the session comes out
as IRC lines. Players in the game see IRC users like anyone else.

## Reaching it

IRC is raw TCP, which the Cloudflare tunnel doesn't carry (it's for web traffic). The production
compose file opens port 6667 on the VM (`IRC_PORT` in `.env` changes the host port), so it works on
the VM's own network. For players elsewhere, either:

- forward the port on the router to the VM, and point a DNS record for `irc.<domain>` at it
  (grey cloud: not proxied); or
- add `irc.<domain>` as a TCP public hostname of the tunnel (to `tcp://server:6667`), and have
  players run `cloudflared access tcp --hostname irc.<domain> --url localhost:6667` and connect
  their client to `localhost:6667`.

Settings (`tomyum.env`): `TOMYUM_IRC__ENABLED` (default true), `TOMYUM_IRC__PORT` (6667),
`TOMYUM_IRC__SERVERNAME` (default `irc.<domain>`).
