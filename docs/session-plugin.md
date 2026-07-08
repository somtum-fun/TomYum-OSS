# Session plugin (`tomyum.session`) — website auth

> Audience: **people** deploying or extending the Session plugin. Agent-facing internals live in
> `memories/features/session.md`. This is the human reference for the loose-source plugin at
> `plugins/tomyum-session/`.

The Session plugin is TomYum's **website (frontend) authentication layer**: a browser or frontend
app posts credentials, gets back an opaque session token (also set as a cookie), and uses it to stay
logged in and to manage its active sessions. It is a clean-room port of the auth core of
bancho.py+prod's `app/api/domains/session.py`, adapted to TomYum's schema and the plugin ABI.

It ships as **one trusted, loose-source plugin** mounted on the **`session` subdomain**
(`session.<domain>`). "Loose source" means the `.cs` files are compiled at runtime by Roslyn — no
build step; drop the folder in and restart.

## Scope

This first cut is the **auth core only**:

| Method | Route | Auth | Purpose |
|--------|-------|------|---------|
| `POST` | `/login` | — | Verify credentials, mint a token, set the `session` cookie. |
| `POST` | `/check-session` | token | Return the user id for a token (frontend bootstrap). |
| `POST` | `/logout` | token | Delete the current session and expire the cookie. |
| `GET`  | `/user/sessions/web` | token | List this user's active sessions (others masked to their last 8 chars). |
| `DELETE` | `/user/sessions/web/{token}` | token | Revoke another of your sessions (full token or masked suffix). |
| `GET`  | `/health` | — | Liveness + whether the trusted DB grant is present. No DB needed. |

Upstream `session.py` is ~11.7k lines because it also carries the frontend **feature** endpoints
(clans, donations, admin moderation, avatar/banner upload, password/email/name change, beatmap
mode-status, DAN tagging, TrueMoney/PromptPay). Those are **intentionally not in this port** — they
are separate features layered on top of this auth spine later.

## How auth works

- A token is `secrets.token_urlsafe(32)` — 32 random bytes, URL-safe, ~43 chars — stored as the
  primary key of the plugin's own `sessions` table. It is both the cookie value and a bearer token.
- On every request the plugin resolves the token from, in order: the **`session` cookie** →
  `Authorization: Bearer <token>` → a `session_token` field in the JSON body (only while
  `allowBodyToken`).
- **Password**: TomYum stores `bcrypt(md5_hex(plaintext))` (same as the in-game login). The website
  posts the *plaintext* password; the plugin md5-hexes it, then bcrypt-verifies. If your frontend
  already md5s client-side (osu-web style), set `passwordIsMd5: true`.
- **Optional binding** (`bindClient: true`): the login captures the client IP prefix (/24 for IPv4,
  /64 for IPv6) and a SHA-256 of the User-Agent; later requests must match. Roaming inside a subnet
  keeps you logged in. Off by default. The columns are always captured, so you can turn binding on
  later without logging everyone out.

## Deploying

1. **DNS / vhost.** The plugin serves on the `session` subdomain — point `session.<your-domain>` at
   the server. The host matches on the first label of the `Host` header, so anything arriving as
   `session.<anything>` is routed here.
2. **Trust the plugin.** Add its id to `Plugins:Trusted` so it gets raw DB access to own its
   `sessions` table (`ctx.Data`), and make sure the `Database` section is enabled. Without this,
   every DB-backed route returns `503` and `/health` reports `"trusted": false`.
3. **Loose source.** Keep `Plugins:AllowLooseSource: true` (the default) so the folder compiles.
4. Restart. The startup log shows `plugin loaded: tomyum.session …` and the plugin creates its
   `sessions` table (`CREATE TABLE IF NOT EXISTS`) and starts an hourly expiry reaper.

### Config

Under `Plugins:tomyum.session` in `config/tomyum.yaml`:

```yaml
plugins:
  trusted:
    - "tomyum.session"
  "tomyum.session":
    ttlHours: 720                 # session lifetime (default 720h = 30 days)
    bindClient: false             # enforce IP-prefix + UA-hash binding
    allowBodyToken: true          # accept a session_token field in the JSON body
    passwordIsMd5: false          # true if the frontend md5s the password client-side
    requireNormalPrivilege: true  # block restricted/banned accounts (Normal bit cleared)
    cookieSecure: true            # add "; Secure" (set false only for local http testing)
    cookieSameSite: "Lax"
    cookieDomain: ""              # optional "; Domain=" (empty = host-only cookie)
    frontendProxyToken: ""        # shared secret; trust X-TomYum-Client-IP when X-TomYum-Proxy-Token matches
    rateLimitWindowSeconds: 300
    rateLimitMaxAttempts: 10
    reaperIntervalMinutes: 60
    disallowedPasswords: []       # reject these passwords at login (403)
```

## Local testing (no DNS needed)

The host matches the subdomain on the `Host` header, so fake it with curl:

```bash
BASE=http://127.0.0.1:8080

# health — no DB needed, reports the trusted grant
curl -s -H 'Host: session.localhost' $BASE/health

# create a throwaway account through the in-game registration endpoint (Host: localhost)
curl -s -H 'Host: localhost' -F 'user[username]=Tester' -F 'user[user_email]=t@ex.com' \
  -F 'user[password]=SmokeTest123' -F 'check=0' $BASE/users

# login -> token + Set-Cookie
curl -s -i -H 'Host: session.localhost' -H 'Content-Type: application/json' \
  -d '{"username":"Tester","password":"SmokeTest123"}' $BASE/login

# use the token
curl -s -H 'Host: session.localhost' -H 'Authorization: Bearer <token>' -X POST $BASE/check-session
curl -s -H 'Host: session.localhost' -H 'Authorization: Bearer <token>'          $BASE/user/sessions/web
curl -s -H 'Host: session.localhost' -H 'Authorization: Bearer <token>' -X POST $BASE/logout
```

> A `Secure` cookie is not sent back over plain http, so for cookie round-trips locally set
> `cookieSecure: false`. Bearer-token auth works either way.

## Differences from upstream (and why)

- **TomYum has no `verified` / `silence_end` column yet**, so the upstream "unverified → 401" gate is
  replaced by `requireNormalPrivilege`: an account whose `Normal` privilege bit is cleared
  (restricted/banned) can't get a website session. This lines up with the Phase 2 moderation model.
- **Rate limiting and the last-active write-throttle are in-memory**, because plugins aren't handed
  redis. Correct on a single node; multi-node parity waits on the Redis infra (ADR-0003).
- **Client IP** comes only from headers the host exposes (`X-TomYum-Client-IP` behind the proxy
  token, or Cloudflare's `CF-Connecting-IP`) — a `PluginHttpRequest` has no socket-peer IP, and
  `X-Forwarded-For` is deliberately not trusted. Behind Cloudflare this is the real IP; otherwise
  IP-derived fields read as empty (`unknown`).

## Shipping it compiled (`.dll`)

Loose source is for fast iteration. To ship compiled: make a `net10.0` class library referencing
`TomYum.Plugins.Abstractions` + the `BCrypt.Net-Next` NuGet (same version the server uses), drop these
`.cs` files in unchanged, build, and put the `.dll` in `plugins/`. At runtime the plugin's load
context resolves `BCrypt.Net-Next` from the host, so no extra files ship.

See also: `plugins/tomyum-session/README.md`, `docs/plugins.md`, and ADR-0007.
