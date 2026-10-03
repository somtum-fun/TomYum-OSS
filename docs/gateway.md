# Gateway (TomYum.Gateway)

osu! builds from before HTTP bancho (roughly up to `b20130815`) talk to bancho over a raw TCP socket,
like `server.titanic.sh:13381` on Titanic!. IRC is raw TCP too. Cloudflare only proxies HTTP(S), so
anything taking raw TCP shows its real address to the world. **TomYum.Gateway** (`services/TomYum.Gateway`)
is the one piece that takes those connections and passes them to the main server over HTTP(S):

- **Bancho over TCP** (`server.<domain>`, port 13381): the packets are the same as HTTP bancho's;
  only the transport differs. Each connection becomes an ordinary HTTP bancho session on the main
  server: the three-line login is POSTed as a login, the `cho-token` is kept, and from then on what
  the client sends goes out in the next poll (only whole packets; TCP may split one anywhere) and
  what comes back is written to the socket. It polls every 50 ms while things happen and every
  500 ms when idle. When the socket drops, it sends the client's exit packet for it, so the player
  doesn't stay online.
- **IRC** (`irc.<domain>`, port 6667): each connection is carried over a WebSocket to the main
  server's `/_gateway/irc`, where it's an ordinary IRC session (docs/irc.md).

The patched client and any server changes for those builds' protocol are separate; the gateway only
moves bytes.

## Two ways to host it

| | Same server | Its own box |
|---|---|---|
| What runs where | the `gateway` service in deploy/production, next to TomYum | deploy/gateway on another machine |
| Reaches the main server by | the compose network (`http://server:8080`) | HTTPS through Cloudflare (`https://c.<domain>`), or a private network |
| IRC | stays in TomYum (its own listener) | the gateway takes it; TomYum's IRC listener is turned off |
| DNS (DNS only, not proxied) | `server.<domain>` and `irc.<domain>` → this server | `server.<domain>` and `irc.<domain>` → the gateway box |
| If those ports are attacked | the whole server is hit | only old clients and IRC go down; the main server's address is never shown |

The gateway and the main server share a secret, `GATEWAY_KEY`. With it, the gateway passes on each
player's address (`X-TomYum-Gateway-Ip`), and the main server uses it for login IPs, countries and
limits instead of the gateway's own (and ignores Cloudflare's `CF-IPCountry`, which would be the
gateway's country). Without the key, that header is dropped and `/_gateway/irc` answers 404. Leave
`GATEWAY_KEY` empty to turn the link off.

### Same server

In deploy/production/.env:

```sh
GATEWAY_KEY=<openssl rand -hex 32>
COMPOSE_PROFILES=gateway
# GATEWAY_BANCHO_PORT=13381
```

Then `sudo docker compose build server gateway && sudo docker compose up -d`. Point `server.<domain>`
at the server (DNS only) and open TCP 13381.

### Its own box

1. On the main server, set `GATEWAY_KEY` in deploy/production/.env and redeploy TomYum. To hand IRC to
   the gateway, also set `TOMYUM_IRC__ENABLED=false` in tomyum.env and remove the `6667` port from the
   `server` service (in docker-compose.override.yml if you don't want to change the tracked file).
2. On the gateway box, with a TomYum checkout: `cd deploy/gateway`, copy `gateway.env.example` to
   `gateway.env`, set the domain and the same key, then `docker compose up -d --build`.
3. DNS (not proxied): `server.<domain>` and `irc.<domain>` → the gateway box. Open TCP 13381 and 6667
   there. Nothing else is exposed: `/health` is on `127.0.0.1:8081`.
4. Through Cloudflare, all old-client traffic comes from the gateway box's one address, polling often:
   add a WAF skip rule (rate limiting, bot fight) for that address on `c.<domain>`.

## Settings (`TOMYUM_GATEWAY__*`)

| Setting | Default | |
|---|---|---|
| `DOMAIN` | (required) | the main server's domain |
| `KEY` | (required) | the shared secret |
| `UPSTREAM` | `https://c.<domain>` | where the main server is; requests still say `c.<domain>` as their host |
| `MAXCONNECTIONSPERIP` | 8 | connections at once from one address, per service |
| `BANCHO__ENABLED` / `BANCHO__PORTS__0` | true / 13381 | more ports: `BANCHO__PORTS__1=13382`, ... |
| `BANCHO__IDLEPOLLMS` / `BANCHO__BUSYPOLLMS` | 500 / 50 | how often a session asks the main server for news |
| `IRC__ENABLED` / `IRC__PORT` | false / 6667 | |
