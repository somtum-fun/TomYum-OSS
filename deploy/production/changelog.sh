#!/bin/sh
# Adds the new commits of the game server and the website to the website's changelog (lazer's too).
# The game server's go to the stream of the part they change (Stable, Lazer, Server, Discord) or
# Wiki (documentation), the website's to Web; up to five changes make a version. Run after pulling
# (deploy.sh does). Only commits since the server went live are taken the first time; after that,
# only ones not yet there. RESET=1 ./changelog.sh imports everything again from scratch.
# Run from anywhere: ./changelog.sh
cd "$(dirname "$0")" || exit 1

web=$(grep -E '^OSU_WEB_PATH=' .env 2>/dev/null | cut -d= -f2-)
web=${web:-../../../osu-web}

# The layout changed (2026-09-30, then without authors): rebuild once.
layout=3
state="$HOME/.tomyum-changelog-layout"
reset=
if [ -n "$RESET" ] || [ "$(cat "$state" 2>/dev/null)" != "$layout" ]; then
	reset=--reset
fi

# import <checkout> <source> <repository> [--reset]
import() {
	git -C "$1" log --reverse --no-merges --since="${SINCE:-2026-09-28}" --name-only \
		--format='%x1e%H%x1f%cI%x1f%s%x1f%b%x1f%ae%x1f' \
		| docker compose exec -T php php artisan tomyum:changelog-import "$2" --repository="$3" $4
}

import ../.. server sundeiii/TomYum $reset && import "$web" web sundeiii/osu-web && echo "$layout" > "$state"
