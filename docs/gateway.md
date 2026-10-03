# Gateway (TomYum.Gateway)

osu! builds from before HTTP bancho (roughly up to `b20130815`) talk to bancho over a raw TCP socket,
like `server.titanic.sh:13381` on Titanic!. IRC is raw TCP too. Cloudflare only proxies HTTP(S), so
anything taking raw TCP shows its real address to the world.

TomYum takes TCP bancho itself (`Bancho:Tcp`, ports 13380-13384; docs/old-clients.md): the same
login and packets as HTTP bancho on an open connection, what's queued for the player pushed as it
comes (within 50 ms), a ping after 10 seconds of quiet (old builds reconnect when bancho says nothing
for a while: b1122 after 30), and a dropped connection logs the player out.

**TomYum.Gateway** (`services/TomYum.Gateway`) is for running those raw TCP ports on another box,
so an attack on them takes down only old clients and IRC, and the main server's address is never
shown. It accepts the connections and carries each one's bytes to the main server over a WebSocket
through Cloudflare (HTTPS):

- **Bancho over TCP** (`server.<domain>`, port 13381) → `/_gateway/bancho`, where it's a TCP bancho
  session like one on the main server's own ports.
- **IRC** (`irc.<domain>`, port 6667) → `/_gateway/irc`, an ordinary IRC session (docs/irc.md).

## Two ways to host it

| | One server | Gateway on its own box |
|---|---|---|
| What runs | TomYum's own TCP bancho and IRC (deploy/production) | deploy/gateway there; TomYum's TCP bancho and IRC listeners off |
| DNS (DNS only, not proxied) | `server.<domain>` and `irc.<domain>` → the server | `server.<domain>` and `irc.<domain>` → the gateway box |
| If those ports are attacked | the whole server is hit | only old clients and IRC go down; the main server's address is never shown |

### One server

In deploy/production/.env:

```sh
TCP_BANCHO=true
TCP_BANCHO_BIND=0.0.0.0   # the ports are only on 127.0.0.1 otherwise
```

Then `sudo docker compose up -d`. Open TCP 13380-13384 (and 6667 for IRC).

### Gateway on its own box

The gateway and the main server share a secret, `GATEWAY_KEY`. With it, the gateway passes on each
player's address (`X-TomYum-Gateway-Ip`), and the main server uses it for login IPs, countries and
limits instead of the gateway's own (and ignores Cloudflare's `CF-IPCountry`, which would be the
gateway's country). Without the key, that header is dropped and `/_gateway/*` answers 404.

1. On the main server, set `GATEWAY_KEY` in deploy/production/.env (`openssl rand -hex 32`), leave
   `TCP_BANCHO=false`, and redeploy TomYum. To hand IRC to the gateway too, set
   `TOMYUM_IRC__ENABLED=false` in tomyum.env and remove the `6667` port from the `server` service (in
   docker-compose.override.yml if you don't want to change the tracked file).
2. On the gateway box, with a TomYum checkout: `cd deploy/gateway`, copy `gateway.env.example` to
   `gateway.env`, set the domain and the same key, then `docker compose up -d --build`.
3. DNS (not proxied): `server.<domain>` and `irc.<domain>` → the gateway box. Open TCP 13381 and 6667
   there. Nothing else is exposed: `/health` is on `127.0.0.1:8081`.
4. Through Cloudflare, all old-client traffic comes from the gateway box's one address: add a WAF skip
   rule (rate limiting, bot fight) for that address on `c.<domain>`.

## Settings (`TOMYUM_GATEWAY__*`)

| Setting | Default | |
|---|---|---|
| `DOMAIN` | (required) | the main server's domain |
| `KEY` | (required) | the shared secret |
| `UPSTREAM` | `https://c.<domain>` | where the main server is; requests still say `c.<domain>` as their host |
| `MAXCONNECTIONSPERIP` | 8 | connections at once from one address, per service |
| `BANCHO__ENABLED` / `BANCHO__PORTS__0` | true / 13381 | more ports: `BANCHO__PORTS__1=13382`, ... |
| `IRC__ENABLED` / `IRC__PORT` | false / 6667 | |
