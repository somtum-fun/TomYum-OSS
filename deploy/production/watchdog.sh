#!/bin/bash
# Downtime alerts: checks the website, bancho and lazer's servers through their public addresses
# (so Cloudflare and the tunnel too) and DMs the admin on Discord when one stops answering, and again
# when it's back. A service counts as down after two failed checks in a row, and nothing is checked
# while deploy.sh runs (it pauses this). Uses the Discord bot's token from tomyum.env; it runs apart
# from the game server, so it still works when that's down (not when the whole VM is: that needs an
# outside monitor). Cron (crontab -e): * * * * * ~/TomYum/deploy/production/watchdog.sh
set -uo pipefail
cd "$(dirname "$0")" || exit 1

state=${WATCHDOG_DIR:-$HOME/.tomyum-watchdog}
mkdir -p "$state"
# deploy.sh pauses the checks while containers restart (the file holds when the pause ends).
if [ -f "$state/paused-until" ] && [ "$(date +%s)" -lt "$(cat "$state/paused-until")" ]; then
	exit 0
fi

domain=$(grep -E '^DOMAIN=' .env | cut -d= -f2-)
admin=${WATCHDOG_DISCORD_USER:-1113690068113170484}
token=$(grep -E '^TOMYUM_DISCORD__TOKEN=' tomyum.env | cut -d= -f2-)

# name|url|the status that means it's up
checks=(
	"Website|https://osu.$domain/home/news|200"
	"Bancho (osu!stable)|https://c.$domain/|200"
	"Game server (stable's /web)|https://osu.$domain/web/bancho_connect.php|200"
	"lazer servers|https://spectator.$domain/spectator/negotiate?negotiateVersion=1|401"
)

dm() {
	[ -n "$token" ] || return 0
	local channel
	channel=$(curl -s -m 15 -X POST "https://discord.com/api/v10/users/@me/channels" \
		-H "Authorization: Bot $token" -H "Content-Type: application/json" \
		-d "{\"recipient_id\":\"$admin\"}" | grep -oE '"id": ?"[0-9]+"' | head -1 | grep -oE '[0-9]+')
	[ -n "$channel" ] || return 1
	curl -s -m 15 -o /dev/null -X POST "https://discord.com/api/v10/channels/$channel/messages" \
		-H "Authorization: Bot $token" -H "Content-Type: application/json" \
		-d "{\"content\":\"$1\"}"
}

now=$(date +%s)
for check in "${checks[@]}"; do
	IFS='|' read -r name url expected <<<"$check"
	key=$(printf '%s' "$name" | tr -c 'a-zA-Z0-9' '_')
	code=$(curl -s -o /dev/null -m 15 -w '%{http_code}' "$url")
	previous=$(cat "$state/$key" 2>/dev/null || echo "up 0 0")
	read -r status since failures <<<"$previous"

	if [ "$code" = "$expected" ]; then
		if [ "$status" = "down" ]; then
			minutes=$(( (now - since + 59) / 60 ))
			dm "🟢 **$name** is back up (down for about $minutes min)."
		fi
		echo "up $now 0" > "$state/$key"
	else
		failures=$((failures + 1))
		if [ "$status" = "up" ] && [ "$failures" -ge 2 ]; then
			dm "🔴 **$name** isn't answering (got \`$code\` instead of \`$expected\` from $url)."
			echo "down $now $failures" > "$state/$key"
		elif [ "$status" = "down" ]; then
			echo "down $since $failures" > "$state/$key"
		else
			echo "up $since $failures" > "$state/$key"
		fi
	fi
done
