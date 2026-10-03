#!/bin/bash
# Downtime and health alerts, by Discord DM to the admin, once when something goes wrong and once
# when it's fine again (docs/monitoring.md):
# - the website, bancho and lazer's servers, through their public addresses (so Cloudflare and the
#   tunnel too): down after two failed checks in a row;
# - the VM: disk nearly full, memory nearly used up, CPU overloaded;
# - the containers: a service stopped, unhealthy, or crashed and restarted by Docker;
# - slow requests: several requests in the last five minutes that took seconds (Caddy's request log;
#   websockets, downloads and uploads left out, they're long on purpose).
# Nothing is checked while deploy.sh runs (it pauses this). Uses the Discord bot's token from
# tomyum.env; it runs apart from the game server, so it still works when that's down (not when the
# whole VM is: that needs an outside monitor). Cron (crontab -e): * * * * * ~/TomYum/deploy/production/watchdog.sh
# ./watchdog.sh --test sends a DM to check the bot can reach the admin; --status prints the readings.
set -uo pipefail
cd "$(dirname "$0")" || exit 1

state=${WATCHDOG_DIR:-$HOME/.tomyum-watchdog}
mkdir -p "$state"
# deploy.sh pauses the checks while containers restart (the file holds when the pause ends).
if [ "${1:-}" != "--status" ] && [ -f "$state/paused-until" ] && [ "$(date +%s)" -lt "$(cat "$state/paused-until")" ]; then
	exit 0
fi

domain=$(grep -E '^DOMAIN=' .env | cut -d= -f2-)
admin=${WATCHDOG_DISCORD_USER:-1113690068113170484}
# The bot's token; on a server whose game server runs without the bot (one bot can only be logged in
# once), WATCHDOG_DISCORD_TOKEN in .env gives it to the alerts alone (they only send DMs).
token=$(grep -E '^WATCHDOG_DISCORD_TOKEN=' .env | cut -d= -f2-)
[ -n "$token" ] || token=$(grep -E '^TOMYUM_DISCORD__TOKEN=' tomyum.env | cut -d= -f2-)

# Limits (override in the crontab line, e.g. WATCHDOG_DISK_PERCENT=90 ~/TomYum/.../watchdog.sh).
disk_percent=${WATCHDOG_DISK_PERCENT:-85}       # disk use, %
memory_percent=${WATCHDOG_MEMORY_PERCENT:-10}   # memory still available, %
load_factor=${WATCHDOG_LOAD_FACTOR:-2}          # 5-minute load average per CPU
slow_seconds=${WATCHDOG_SLOW_SECONDS:-3}        # a request this long is slow
slow_count=${WATCHDOG_SLOW_COUNT:-10}           # this many slow requests in five minutes alerts

# name|url|the status that means it's up
checks=(
	"Website|https://osu.$domain/home/news|200"
	"Bancho (osu!stable)|https://c.$domain/|200"
	"Game server (stable's /web)|https://osu.$domain/web/bancho_connect.php|200"
	"lazer servers|https://spectator.$domain/spectator/negotiate?negotiateVersion=1|401"
)

# Services that run once and stop (not an outage when they aren't running).
one_shot="volumes-init artisan osu-web-build"

dm() {
	[ -n "$token" ] || return 0
	local channel body
	channel=$(curl -s -m 15 -X POST "https://discord.com/api/v10/users/@me/channels" \
		-H "Authorization: Bot $token" -H "Content-Type: application/json" \
		-d "{\"recipient_id\":\"$admin\"}" | grep -oE '"id": ?"[0-9]+"' | head -1 | grep -oE '[0-9]+')
	[ -n "$channel" ] || return 1
	# JSON string: backslashes, quotes and newlines escaped. Starts with the server's domain, for an
	# admin who gets alerts from more than one.
	body=$(printf '**%s** · %s' "$domain" "$1" | sed -e 's/\\/\\\\/g' -e 's/"/\\"/g' | awk '{printf "%s%s", (NR > 1 ? "\\n" : ""), $0}')
	curl -s -m 15 -o /dev/null -X POST "https://discord.com/api/v10/channels/$channel/messages" \
		-H "Authorization: Bot $token" -H "Content-Type: application/json" \
		-d "{\"content\":\"$body\"}"
}

if [ "${1:-}" = "--test" ]; then
	dm "✅ Alerts are on: you'll get a DM here when the website, bancho or lazer's servers stop answering, when the VM runs short of disk, memory or CPU, when a container stops or crashes, or when requests get slow; and again when it's fine." \
		&& echo "Sent." || { echo "Couldn't send (check the bot's token, and that the admin shares a server with it and allows DMs)." >&2; exit 1; }
	exit 0
fi

now=$(date +%s)
status_only=$([ "${1:-}" = "--status" ] && echo 1 || echo 0)

# track <name> <bad: 0|1> <checks in a row before alerting> <alert text> <all-clear text>
# Keeps "up|down <since> <failures in a row>" per name and DMs on the change either way.
track() {
	local name=$1 bad=$2 need=$3 alert=$4 clear=$5 key previous status since failures minutes
	if [ "$status_only" = 1 ]; then
		printf '%-34s %s\n' "$name" "$([ "$bad" = 1 ] && echo "PROBLEM: $alert" || echo ok)"
		return
	fi
	key=$(printf '%s' "$name" | tr -c 'a-zA-Z0-9' '_')
	previous=$(cat "$state/$key" 2>/dev/null || echo "up 0 0")
	read -r status since failures <<<"$previous"

	if [ "$bad" = 0 ]; then
		if [ "$status" = "down" ]; then
			minutes=$(( (now - since + 59) / 60 ))
			dm "🟢 **$name**: $clear (after about $minutes min)."
		fi
		echo "up $now 0" > "$state/$key"
	else
		failures=$((failures + 1))
		if [ "$status" = "up" ] && [ "$failures" -ge "$need" ]; then
			dm "🔴 **$name**: $alert"
			echo "down $now $failures" > "$state/$key"
		elif [ "$status" = "down" ]; then
			echo "down $since $failures" > "$state/$key"
		else
			echo "up $since $failures" > "$state/$key"
		fi
	fi
}

# ── Public addresses ─────────────────────────────────────────────────────────────────────────────
for check in "${checks[@]}"; do
	IFS='|' read -r name url expected <<<"$check"
	code=$(curl -s -o /dev/null -m 15 -w '%{http_code}' "$url")
	track "$name" "$([ "$code" = "$expected" ] && echo 0 || echo 1)" 2 \
		"isn't answering (got \`$code\` instead of \`$expected\` from $url)." "is back up"
done

# ── The VM ───────────────────────────────────────────────────────────────────────────────────────
# The details in an alert (docker stats, docker system df) take seconds: only worked out when needed.
read -r disk_used disk_free <<<"$(df -P -BG / | awk 'NR == 2 { gsub("%", "", $5); gsub("G", "", $4); print $5, $4 }')"
bad=$([ "$disk_used" -ge "$disk_percent" ] && echo 1 || echo 0)
detail=""
if [ "$bad" = 1 ]; then
	cache=$(docker system df --format '{{.Type}} {{.Size}}' 2>/dev/null | awk '/^Build Cache/ { print $3 }')
	detail="${disk_used}% used, ${disk_free} GB free (Docker's build cache: ${cache:-?}; \`docker builder prune -af --max-used-space 20GB\` clears it)."
fi
track "Disk" "$bad" 3 "$detail" "back under ${disk_percent}% (${disk_used}% used)"

# The containers using the most of a resource: busiest <MemPerc|CPUPerc>
busiest() {
	docker stats --no-stream --format "{{.Name}} {{.$1}}" 2>/dev/null | sort -k2 -h -r | head -3 | paste -sd ',' | sed 's/,/, /g'
}

read -r mem_total mem_available <<<"$(awk '/^MemTotal:/ { t = $2 } /^MemAvailable:/ { a = $2 } END { print t, a }' /proc/meminfo)"
mem_left=$(( mem_available * 100 / mem_total ))
bad=$([ "$mem_left" -lt "$memory_percent" ] && echo 1 || echo 0)
detail=""
[ "$bad" = 1 ] && detail="only ${mem_left}% available ($(( mem_available / 1024 )) MB of $(( mem_total / 1024 )) MB). Biggest: $(busiest MemPerc)."
track "Memory" "$bad" 3 "$detail" "${mem_left}% available again"

cpus=$(nproc)
load=$(cut -d' ' -f2 /proc/loadavg)
bad=$(awk -v l="$load" -v c="$cpus" -v f="$load_factor" 'BEGIN { print (l > c * f) ? 1 : 0 }')
detail=""
[ "$bad" = 1 ] && detail="overloaded: 5-minute load $load on $cpus CPUs. Busiest: $(busiest CPUPerc)."
track "CPU" "$bad" 5 "$detail" "load back to $load"

# ── Containers ───────────────────────────────────────────────────────────────────────────────────
problems=()
while IFS='|' read -r service container_state health; do
	[ -n "$service" ] || continue
	case " $one_shot " in *" $service "*) continue ;; esac
	if [ "$container_state" != "running" ]; then
		problems+=("$service is $container_state")
	elif [ "$health" = "unhealthy" ]; then
		problems+=("$service is unhealthy")
	fi
done < <(docker compose ps -a --format '{{.Service}}|{{.State}}|{{.Health}}' 2>/dev/null)
track "Containers" "$([ ${#problems[@]} -gt 0 ] && echo 1 || echo 0)" 2 \
	"$(IFS=';'; echo "${problems[*]:-}" | sed 's/;/, /g')." "all running"

# A crash Docker recovered from on its own: its restart count went up since the last check.
if [ "$status_only" = 0 ]; then
	while read -r id service restarts; do
		[ -n "$id" ] || continue
		last=$(cat "$state/restarts-$id" 2>/dev/null || echo "$restarts")
		echo "$restarts" > "$state/restarts-$id"
		if [ "$restarts" -gt "$last" ]; then
			dm "🟠 **$service** crashed and was restarted by Docker (restart #$restarts). Last lines:
\`\`\`
$(docker logs --tail 8 "$id" 2>&1 | cut -c1-180 | tr -d '`')
\`\`\`"
		fi
	done < <(docker compose ps -q 2>/dev/null | xargs -r docker inspect -f '{{.Id}} {{index .Config.Labels "com.docker.compose.service"}} {{.RestartCount}}' 2>/dev/null)
	# Containers that are gone (replaced by a deploy) don't need their counts.
	find "$state" -name 'restarts-*' -mmin +60 -delete 2>/dev/null
fi

# ── Slow requests (Caddy's request log, last five minutes) ──────────────────────────────────────
# Long on purpose, so left out: websockets (notifications, lazer's hubs), downloads and uploads.
slow=$(docker compose exec -T caddy tail -n 5000 /data/logs/access.log 2>/dev/null | awk \
	-v since=$(( now - 300 )) -v limit="$slow_seconds" '
	{
		if (!match($0, /"ts":[0-9.]+/)) next
		ts = substr($0, RSTART + 5, RLENGTH - 5) + 0
		if (ts < since) next
		if (!match($0, /"duration":[0-9.e-]+/)) next
		d = substr($0, RSTART + 11, RLENGTH - 11) + 0
		match($0, /"uri":"[^"?]*/); uri = substr($0, RSTART + 7, RLENGTH - 7)
		if ($0 ~ /"status":101/ || uri ~ /^\/(home\/notifications\/feed|signalr|spectator|multiplayer|metadata|d\/|preview\/|web\/maps\/|beatmap-submission|ss\/)/ \
			|| uri ~ /(download|osz2|screenshot|getreplay|replay|upload)/) next
		if (d > limit) { n++; gsub(/[0-9]+/, "N", uri); c[uri]++ }
	}
	END {
		top = ""
		for (u in c) top = top sprintf("%d× %s\n", c[u], u)
		printf "%d\n%s", n, top
	}')
slow_n=$(head -1 <<<"$slow")
slow_top=$(tail -n +2 <<<"$slow" | sort -rn | head -3 | paste -sd ',' | sed 's/,/, /g')
track "Slow requests" "$([ "${slow_n:-0}" -ge "$slow_count" ] && echo 1 || echo 0)" 2 \
	"$slow_n requests over ${slow_seconds}s in the last five minutes: $slow_top." "requests are quick again"
