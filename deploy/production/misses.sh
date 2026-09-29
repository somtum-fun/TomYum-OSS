#!/bin/sh
# What clients asked the website/API and lazer's servers for and didn't get: failed requests
# (400, 404, 405, 5xx; not 401/403, which are just "log in first") by status, host and path,
# with numbers in paths folded to N. Run from deploy/production: ./misses.sh [lines]
cd "$(dirname "$0")" || exit 1
docker compose exec -T caddy sh -c 'cat /data/logs/access*.log 2>/dev/null' \
  | grep -E '"status":(400|404|405|5[0-9][0-9])[,}]' \
  | sed -E 's/.*"method":"([A-Z]+)".*"host":"([^"]*)".*"uri":"([^"?]*)[^"]*".*"status":([0-9]+).*/\4 \1 \2\3/' \
  | sed -E 's#/[0-9]+#/N#g' \
  | sort | uniq -c | sort -rn | head -n "${1:-40}"
