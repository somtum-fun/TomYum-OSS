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

# Commit authors: email=GitHub login:GitHub account id:website user id.
authors="--author=sundei@sundei.ee=sundeiii:76597738:2"

# Commits left off the changelog (changes to the changelog itself, not worth listing; a commit can
# also leave itself out with "[no changelog]" in its message).
skip="--skip=c2ed1c4 --skip=35fde80 --skip=7d6df42 --skip=6cb387e --skip=fe43f38 --skip=9a12c3a"

# The layout changed (2026-09-30; 2026-10-03: commit trailers left out, and a fixed start: a date
# alone means that day at the current time of day, so each rebuild started somewhere else): rebuild once.
layout=6
state="$HOME/.tomyum-changelog-layout"
reset=
if [ -n "$RESET" ] || [ "$(cat "$state" 2>/dev/null)" != "$layout" ]; then
	reset=--reset
fi

# import <checkout> <source> <repository> [--reset]
import() {
	git -C "$1" log --reverse --no-merges --since="${SINCE:-2026-09-28T00:00:00Z}" --name-only \
		--format='%x1e%H%x1f%cI%x1f%s%x1f%b%x1f%ae%x1f' \
		| docker compose exec -T php php artisan tomyum:changelog-import "$2" --repository="$3" $authors $skip $4
}

import ../.. server sundeiii/TomYum $reset && import "$web" web sundeiii/osu-web && echo "$layout" > "$state"
