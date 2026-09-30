#!/bin/sh
# Updates the running server: pulls TomYum, the website and circleguard, rebuilds the images
# (unchanged parts come from the build cache), restarts what changed, and adds the new commits to
# the changelog. Run from anywhere: ./deploy.sh
set -e
cd "$(dirname "$0")"

# Pull TomYum first, then run the pulled version of this script (a shell reads its script as it
# goes: pulled halfway, a new step wouldn't run, as the website's migrations once didn't).
if [ -z "$TOMYUM_DEPLOY_PULLED" ]; then
    git -C ../.. pull -q
    TOMYUM_DEPLOY_PULLED=1 exec ./deploy.sh "$@"
fi

web=$(grep -E '^OSU_WEB_PATH=' .env 2>/dev/null | cut -d= -f2-)
web=${web:-../../../osu-web}

# No downtime alerts while containers restart (watchdog.sh): 10 minutes, ended early below.
mkdir -p "$HOME/.tomyum-watchdog"
echo $(( $(date +%s) + 600 )) > "$HOME/.tomyum-watchdog/paused-until"

git -C "$web" pull -q
[ -d ../../../lazer-circleguard ] && git -C ../../../lazer-circleguard pull -q

docker compose --profile build build osu-web-build
docker compose build
# The website's new tables before anything restarts (TomYum reads some of them, e.g. account locks).
docker compose run --rm artisan migrate --force
docker compose up -d
# Old build layers pile up with every deploy (they once filled the disk): keep a day's worth.
docker builder prune -f --filter until=24h >/dev/null

./changelog.sh

# Back to watching (a minute's grace for everything to come up).
echo $(( $(date +%s) + 60 )) > "$HOME/.tomyum-watchdog/paused-until"
