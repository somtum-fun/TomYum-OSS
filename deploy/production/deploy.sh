#!/bin/sh
# Updates the running server: pulls TomYum, the website and circleguard, rebuilds the images
# (unchanged parts come from the build cache), restarts what changed, and adds the new commits to
# the changelog. Run from anywhere: ./deploy.sh
set -e
cd "$(dirname "$0")"

web=$(grep -E '^OSU_WEB_PATH=' .env 2>/dev/null | cut -d= -f2-)
web=${web:-../../../osu-web}

git -C ../.. pull -q
git -C "$web" pull -q
[ -d ../../../lazer-circleguard ] && git -C ../../../lazer-circleguard pull -q

docker compose --profile build build osu-web-build
docker compose build
docker compose up -d
# Old build layers pile up with every deploy (they once filled the disk): keep a day's worth.
docker builder prune -f --filter until=24h >/dev/null

./changelog.sh
