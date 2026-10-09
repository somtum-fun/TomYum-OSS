# tomyum.session — website session/auth plugin

A **loose-source** TomYum plugin that provides the website (frontend) authentication layer: it
issues opaque session tokens on login, validates them, and lets a user see and revoke their active
sessions. It is a clean-room port of the auth core of a bancho.py fork's `app/api/domains/session.py`,
adapted to TomYum's schema and plugin ABI.

> This is the **core auth subsystem only**. The upstream `session.py` also carries ~11k lines of
> frontend feature endpoints (clans, donations, admin moderation, avatar/banner upload,
> password/email/name change, beatmap mode-status, DAN tagging, TrueMoney/PromptPay). Those are
> intentionally **not** ported here — they belong in later passes.

## What it does

Mounted on the **`session` subdomain** (`session.<domain>`), all routes:

| Method | Route | Purpose |
|--------|-------|---------|
| `POST` | `/login` | Verify credentials, mint a token, set the `session` cookie. |
| `POST` | `/check-session` | Return the user id for a token (frontend bootstrap). |
| `POST` | `/logout` | Delete the current session + expire the cookie. |
| `GET`  | `/user/sessions/web` | List this user's active sessions (others masked to their last 8 chars). |
| `DELETE` | `/user/sessions/web/{target_token}` | Revoke another of your sessions (full token or masked suffix). |
| `GET`  | `/health` | Liveness + whether the trusted DB grant is present. No auth, no DB. |

Tokens resolve from (first wins): the `session` **cookie** → `Authorization: Bearer` → a
`session_token` field in the JSON body (only while `allowBodyToken`).

## Requirements

- **Trusted DB access.** The plugin owns its own `sessions` table, so its id must be in
  `Plugins:Trusted` and the `Database` section must be enabled. Without it, every DB-backed route
  returns `503` and `/health` reports `"trusted": false`.
- Loose source is compiled at runtime by Roslyn, so `Plugins:AllowLooseSource` must be `true`
  (the default).

## Config (`Plugins:tomyum.session`)

```yaml
plugins:
  trusted:
    - "tomyum.session"
  "tomyum.session":
    ttlHours: 720            # session lifetime (default 720 = 30 days)
    bindClient: false        # enforce IP-prefix (/24, /64) + UA-hash binding
    allowBodyToken: true     # accept a session_token field in the JSON body
    passwordIsMd5: false     # true if the frontend already md5s the password client-side
    requireNormalPrivilege: true  # block accounts with the Normal privilege bit cleared (restricted/banned)
    cookieSecure: true       # add "; Secure" to the cookie (set false only for local http testing)
    cookieSameSite: "Lax"
    cookieDomain: ""         # optional "; Domain=" (empty = host-only cookie)
    frontendProxyToken: ""   # shared secret; when X-TomYum-Proxy-Token matches, trust X-TomYum-Client-IP
    rateLimitWindowSeconds: 300
    rateLimitMaxAttempts: 10
    reaperIntervalMinutes: 60
    disallowedPasswords: []  # reject these at login with 403
```

## Password + identity model (TomYum specifics)

- Passwords are stored as `bcrypt(md5_hex(plaintext))` — the same scheme the in-game bancho login
  uses. The website posts the **plaintext** password; the plugin md5-hexes it, then bcrypt-verifies
  via `BCrypt.Net`. If your frontend already sends the md5 hex (osu-web style), set `passwordIsMd5: true`.
- Users are looked up by `SafeName` (lowercase, spaces→underscores) or `Email`.
- TomYum has **no `verified` or `silence_end` column yet**, so the upstream "unverified → 401"
  gate is replaced by the `requireNormalPrivilege` gate (a restricted/banned account has the
  `Normal` bit cleared). This will align with the Phase 2 moderation model.

## Known limitations (vs upstream)

- **Rate limiting and the last-active write-throttle are in-memory**, because plugins are not handed
  redis. This is correct on a single node; multi-node parity waits on the Redis infra (ADR-0003).
- **Client IP** comes only from headers the host exposes — `X-TomYum-Client-IP` (when the proxy
  token matches) or Cloudflare's `CF-Connecting-IP`. There is no socket-peer IP in a
  `PluginHttpRequest`, and `X-Forwarded-For` is deliberately not trusted. Behind Cloudflare this is
  the real client IP; without a trusted proxy, IP-based features degrade to "unknown".
- One `Set-Cookie` per response (the host response model carries a single header value per key).

## Local testing

The host matches the subdomain on the first `Host` label, so you don't need real DNS — fake the
host header:

```bash
# health (no DB needed)
curl -s -H 'Host: session.localhost' http://127.0.0.1:8080/health

# login (returns a token + Set-Cookie); use a real account
curl -s -i -H 'Host: session.localhost' -H 'Content-Type: application/json' \
  -d '{"username":"YourName","password":"secret"}' \
  http://127.0.0.1:8080/login

# check a token
curl -s -H 'Host: session.localhost' -H 'Authorization: Bearer <token>' \
  -X POST http://127.0.0.1:8080/check-session
```

> For cookie round-trips over plain http locally, set `cookieSecure: false` (a `Secure` cookie is
> not sent back over http). Bearer-token auth works regardless.

## Promoting to a compiled `.dll`

This folder is loose source so you can iterate without a build step. To ship it compiled:

1. Create a `net10.0` class library referencing `TomYum.Plugins.Abstractions` and the
   `BCrypt.Net-Next` NuGet package (the same version the server uses).
2. Drop these `.cs` files in (they need no changes) and build.
3. Put the resulting `.dll` in `plugins/` instead of this folder. At runtime the ALC resolves
   `BCrypt.Net-Next` from the host, so no extra deps ship.

See `docs/session-plugin.md` (human) and `memories/features/session.md` (agent) for the full spec.
