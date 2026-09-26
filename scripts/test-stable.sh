#!/usr/bin/env bash
#
# test-stable.sh — exercise the osu!stable surface (registration + login) against a
# running TomYum.Server, using the correct host headers.
#
# Usage:
#   scripts/test-stable.sh                      # against http://127.0.0.1:5099, domain example.com
#   BASE=http://127.0.0.1:5099 DOMAIN=example.com scripts/test-stable.sh
#
# The osu!stable client splits its surface across subdomains:
#   c./c4./ce./cho.<domain>  -> bancho binary protocol (login, poll)
#   osu.<domain>             -> /web/* + in-game registration (POST /users)
# We pass those as Host: headers so a single local server routes correctly.

set -euo pipefail

BASE="${BASE:-http://127.0.0.1:5099}"
DOMAIN="${DOMAIN:-example.com}"

USERNAME="${USERNAME:-TestPlayer}"
EMAIL="${EMAIL:-testplayer@example.com}"
PASSWORD="${PASSWORD:-greatpassword}"

# osu!stable sends md5(plaintext) as the login password field, not the plaintext.
pw_md5() { printf '%s' "$1" | md5sum | cut -d' ' -f1; }

echo "== TomYum stable test =="
echo "base=$BASE domain=$DOMAIN user=$USERNAME"
echo

echo "1) Registration validate-only (check=1) on osu.$DOMAIN -> expect 'ok'"
curl -s -o /dev/null -w '   http %{http_code}\n' \
  -H "Host: osu.$DOMAIN" \
  -F "user[username]=$USERNAME" \
  -F "user[user_email]=$EMAIL" \
  -F "user[password]=$PASSWORD" \
  -F "check=1" \
  "$BASE/users"

echo "2) Registration create (check=0) on osu.$DOMAIN -> expect 'ok'"
curl -s -H "Host: osu.$DOMAIN" \
  -F "user[username]=$USERNAME" \
  -F "user[user_email]=$EMAIL" \
  -F "user[password]=$PASSWORD" \
  -F "check=0" \
  "$BASE/users"
echo

MD5="$(pw_md5 "$PASSWORD")"
BODY=$(printf '%s\n%s\nb20231228.1|0|0|abc:runningunderwine:def:ghi:jkl:|0\n' "$USERNAME" "$MD5")

echo "3) Login as $USERNAME via c.$DOMAIN -> expect a cho-token header"
curl -s -D - -o /dev/null -H "Host: c.$DOMAIN" --data-binary "$BODY" "$BASE/" | grep -i '^cho-token:' || echo "   (no cho-token returned)"

echo "4) Login with WRONG password -> expect cho-token: no-token"
WRONGBODY=$(printf '%s\n%s\nb20231228.1|0|0|abc:runningunderwine:def:ghi:jkl:|0\n' "$USERNAME" "$(pw_md5 wrong-password)")
curl -s -D - -o /dev/null -H "Host: c.$DOMAIN" --data-binary "$WRONGBODY" "$BASE/" | grep -i '^cho-token:' || true

echo "5) Wrong host (example.com) -> expect http 404"
curl -s -o /dev/null -w '   http %{http_code}\n' -H "Host: example.com" "$BASE/"

echo
echo "Done."
