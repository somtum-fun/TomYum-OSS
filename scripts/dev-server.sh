#!/usr/bin/env bash
#
# dev-server.sh — run TomYum.Server for local development against the compose datastores.
#
# Assumes MySQL + Redis are already up, e.g.:
#   podman compose -f deploy/docker/docker-compose.yml up -d mysql redis
#
# Usage:
#   scripts/dev-server.sh                                  # http://127.0.0.1:5099, MySQL on
#   PORT=5100 scripts/dev-server.sh                        # different port
#   TOMYUM_DATABASE__PASSWORD=secret scripts/dev-server.sh # override the DB password
#   DB=off scripts/dev-server.sh                           # in-memory repo, no database
#
# Why the env vars matter:
#   ASPNETCORE_ENVIRONMENT=Development  -> loads config/appsettings.Development.json; without it
#                                          the host runs in Production and silently falls back to
#                                          the in-memory repository (Database:Enabled never read).
#   TOMYUM_DATABASE__PASSWORD           -> the one secret never committed (ADR-0008); injected here.
#                                          Defaults to "tomyum" to match deploy/docker-compose.yml.
#   --urls                              -> there is no launchSettings.json; the port comes from here.
#
# On success the log prints "Database schema ensured (...)" — that confirms it is talking to MySQL
# and not the in-memory fallback. Then: curl http://127.0.0.1:$PORT/health

set -euo pipefail

# Run from the repo root so the server finds config/ regardless of where you invoke this.
cd "$(dirname "$0")/.."

PORT="${PORT:-5099}"
export ASPNETCORE_ENVIRONMENT="${ASPNETCORE_ENVIRONMENT:-Development}"

# DB=off disables persistence (in-memory repo). Otherwise inject the DB password (compose default).
if [ "${DB:-on}" = "off" ]; then
  export TOMYUM_DATABASE__ENABLED=false
  echo "== TomYum dev server (in-memory, no database) =="
else
  export TOMYUM_DATABASE__PASSWORD="${TOMYUM_DATABASE__PASSWORD:-tomyum}"
  echo "== TomYum dev server (MySQL persistence) =="
fi

echo "env=$ASPNETCORE_ENVIRONMENT  url=http://127.0.0.1:$PORT"
echo

exec dotnet run --project src/TomYum.Server --urls "http://127.0.0.1:$PORT"
