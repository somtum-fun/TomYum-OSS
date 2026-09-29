#!/bin/sh
# Adds the new commits of TomYum and the website to the website's changelog (lazer's too): the
# streams "TomYum" and "Website", a build per day. Run after pulling (deploy.sh does). Only commits
# since the server went live are taken the first time; after that, only ones not yet there.
# Run from anywhere: ./changelog.sh
cd "$(dirname "$0")" || exit 1

web=$(grep -E '^OSU_WEB_PATH=' .env 2>/dev/null | cut -d= -f2-)
web=${web:-../../../osu-web}

# import <checkout> <stream id> <stream> <name> <repository> <category for unprefixed commits>
import() {
	git -C "$1" log --reverse --no-merges --since="${SINCE:-2026-09-28}" --format='%H%x1f%cI%x1f%s%x1f%b%x1e' \
		| docker compose exec -T php php artisan tomyum:changelog-import "$2" "$3" "$4" "$5" "$6"
}

import ../.. 5 tomyum TomYum sundeiii/TomYum Server
import "$web" 1 web Website sundeiii/osu-web Website
