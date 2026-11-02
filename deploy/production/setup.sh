#!/usr/bin/env bash
# TomYum's installer: a whole server (TomYum, the website, ppy's services, the databases) on one machine
# with Docker Compose, the steps of docs.somtum.fun/guide/production done for you.
#
#   ./setup.sh                       ask for what it needs, then install (run it again to resume)
#   ./setup.sh --domain example.com --tunnel-token <token>
#   ./setup.sh --domain example.com --cloudflare-api-token <token> --cloudflare-account <account id>
#                                    also makes the Cloudflare Tunnel and its DNS records
#   ./setup.sh --local               try it on this machine: no tunnel, the site on http://127.0.0.1:8880
#   ./setup.sh admin <username>      make a registered player an administrator
#   ./setup.sh check                 see whether everything answers
#
# It never overwrites a setting you've filled in, and it remembers which steps are done (.setup-done), so
# running it again picks up where it stopped. Everything it writes stays in this folder.
set -euo pipefail
cd "$(dirname "$0")"

DONE_FILE=.setup-done
OSU_WEB_REPO=${OSU_WEB_REPO:-https://github.com/somtum-fun/osu-web-tomyum.git}
SPECTATOR_REPO=${SPECTATOR_REPO:-https://github.com/somtum-fun/tomyum-spectator.git}
HOSTS=(osu c ce c1 c2 c3 c4 c5 c6 cho a b assets s spectator api)

bold() { printf '\033[1m%s\033[0m\n' "$*"; }
step() { printf '\n\033[1;36m== %s\033[0m\n' "$*"; }
warn() { printf '\033[33m! %s\033[0m\n' "$*" >&2; }
die() { printf '\033[31mx %s\033[0m\n' "$*" >&2; exit 1; }
done_step() { grep -qx "$1" "$DONE_FILE" 2>/dev/null; }
mark() { echo "$1" >> "$DONE_FILE"; }

# Docker needs root unless the user is in the docker group.
DOCKER=(docker)
if ! docker info >/dev/null 2>&1; then
	if sudo -n docker info >/dev/null 2>&1 || sudo docker info >/dev/null 2>&1; then DOCKER=(sudo docker); fi
fi
compose() { "${DOCKER[@]}" compose "$@"; }
artisan() { compose run --rm -T artisan "$@" </dev/null; }

# .env helpers: read a value; set one only when it's empty (or missing).
env_get() { sed -n "s/^$1=//p" .env | tail -n 1; }
env_fill() {
	local key=$1 value=$2
	if grep -q "^$key=" .env; then
		[[ -n "$(env_get "$key")" ]] && return 0
		local tmp; tmp=$(mktemp); awk -v k="$key" -v v="$value" 'BEGIN{FS=OFS="="} $1==k && !s {print k "=" v; s=1; next} {print}' .env > "$tmp"
		cat "$tmp" > .env; rm -f "$tmp"
	else
		echo "$key=$value" >> .env
	fi
}
ask() { # ask VAR "question" [default]
	local var=$1 question=$2 default=${3:-} answer
	if [[ -n "${!var:-}" ]]; then return; fi
	if [[ ! -t 0 ]]; then printf -v "$var" '%s' "$default"; return; fi
	read -r -p "$question${default:+ [$default]}: " answer
	printf -v "$var" '%s' "${answer:-$default}"
}

DOMAIN=${DOMAIN:-} TUNNEL_TOKEN=${TUNNEL_TOKEN:-} CF_API_TOKEN=${CF_API_TOKEN:-} CF_ACCOUNT=${CF_ACCOUNT:-} CF_ZONE=${CF_ZONE:-}
LOCAL=0 COMMAND=install
while [[ $# -gt 0 ]]; do
	case $1 in
		--domain) DOMAIN=$2; shift 2 ;;
		--tunnel-token) TUNNEL_TOKEN=$2; shift 2 ;;
		--cloudflare-api-token) CF_API_TOKEN=$2; shift 2 ;;
		--cloudflare-account) CF_ACCOUNT=$2; shift 2 ;;
		--cloudflare-zone) CF_ZONE=$2; shift 2 ;;
		--local) LOCAL=1; shift ;;
		admin) COMMAND=admin; ADMIN_NAME=${2:-}; shift $(( $# > 1 ? 2 : 1 )) ;;
		check) COMMAND=check; shift ;;
		-h|--help) sed -n '2,16p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
		*) die "Unknown option $1 (see ./setup.sh --help)" ;;
	esac
done

# ---------------------------------------------------------------- check
check() {
	step "Checking the server"
	local domain; domain=$(env_get DOMAIN)
	compose ps --format '{{.Service}} {{.State}}' | awk '$2!="running"{bad=1; print "  not running: " $1} END{exit bad}' \
		&& echo "  every service is running" || warn "some services aren't running (docker compose ps, docker compose logs <service>)"
	local host code
	for host in osu c a b spectator; do
		# Any HTTP answer means Caddy routed it (b. and spectator. have no front page: 404 is fine there).
		code=$(compose exec -T caddy wget -q -S -O /dev/null --header "Host: $host.$domain" http://127.0.0.1/ 2>&1 | awk 'match($0, /HTTP\/[0-9.]+ [0-9][0-9][0-9]/) {c = substr($0, RSTART + RLENGTH - 3, 3)} END {print c + 0}' || true)
		printf '  %-28s %s\n' "$host.$domain" "$( [[ "${code:-0}" == 0 ]] && echo "no answer" || echo "$code" )"
	done
	if compose logs server 2>/dev/null | grep -q "Now listening on"; then echo "  TomYum is listening"; else warn "TomYum hasn't said 'Now listening on' yet: docker compose logs -f server"; fi
}

# ---------------------------------------------------------------- admin
admin() {
	[[ -n "${ADMIN_NAME:-}" ]] || die "Usage: ./setup.sh admin <username> (register on the website or in the game first)"
	local name=${ADMIN_NAME//\'/}
	local out
	out=$(artisan tinker --execute "\$u = App\\Models\\User::where('username', '$name')->first(); if (\$u) { \$u->addToGroup(app('groups')->byIdentifier('admin')); echo 'ADMIN-OK' . PHP_EOL; } else { echo 'ADMIN-NONE' . PHP_EOL; }" 2>&1)
	if grep -q ADMIN-OK <<<"$out"; then echo "  $name is an administrator now. Log in to the game again."
	elif grep -q ADMIN-NONE <<<"$out"; then die "No player called $name: register on the website or in the game first."
	else printf '%s\n' "$out" | tail -n 5; die "Couldn't make $name an administrator."; fi
}

case $COMMAND in
	admin) admin; exit ;;
	check) check; exit ;;
esac

# ---------------------------------------------------------------- 1. this machine
step "1. This machine"
command -v docker >/dev/null || die "Docker isn't installed: https://docs.docker.com/engine/install/"
"${DOCKER[@]}" info >/dev/null 2>&1 || die "Docker isn't running, or this user can't use it."
compose_version=$("${DOCKER[@]}" compose version --short 2>/dev/null || true)
[[ -n "$compose_version" ]] || die "Docker Compose v2 is missing (the docker-compose-plugin package)."
if [[ "$(printf '%s\n' 2.25.0 "${compose_version#v}" | sort -V | head -n 1)" != 2.25.0 ]]; then
	die "Docker Compose $compose_version is too old: 2.25 or newer is needed (volume subpaths)."
fi
engine=$("${DOCKER[@]}" version --format '{{.Server.Version}}' 2>/dev/null || echo 0)
if [[ "$(printf '%s\n' 27.0.0 "${engine%%+*}" | sort -V | head -n 1)" != 27.0.0 ]]; then
	die "Docker Engine $engine can't mount the website's key file into the other services (a volume subpath to a file needs 27 or newer). Install Docker's own packages: https://docs.docker.com/engine/install/ (distributions' docker.io is often older)."
fi
for tool in git openssl; do command -v $tool >/dev/null || die "$tool isn't installed."; done
mem_mb=$(awk '/MemTotal/{print int($2/1024)}' /proc/meminfo 2>/dev/null || echo 0)
disk_gb=$(df -Pk . | awk 'NR==2{print int($4/1024/1024)}')
echo "  Docker $engine, Compose $compose_version, $(uname -m), ${mem_mb} MB memory, ${disk_gb} GB free"
(( mem_mb >= 6000 )) || warn "Less than 6 GB of memory: the website's build and Elasticsearch may not fit."
(( disk_gb >= 40 )) || warn "Less than 40 GB free: the images and databases need about 25 GB to start with."

# ---------------------------------------------------------------- 2. the code
step "2. The website and the spectator server"
[[ -f .env ]] || cp .env.example .env
osu_web_path=$(env_get OSU_WEB_PATH); osu_web_path=${osu_web_path:-../../../osu-web}
spectator_path=$(env_get SPECTATOR_PATH); spectator_path=${spectator_path:-../../../tomyum-spectator}
[[ -d "$osu_web_path/.git" || -f "$osu_web_path/artisan" ]] || git clone --filter=blob:none "$OSU_WEB_REPO" "$osu_web_path"
[[ -d "$spectator_path/.git" || -f "$spectator_path/Dockerfile" ]] || git clone --filter=blob:none "$SPECTATOR_REPO" "$spectator_path"
echo "  website: $(cd "$osu_web_path" && pwd)"
echo "  spectator server: $(cd "$spectator_path" && pwd)"

# ---------------------------------------------------------------- 3. settings
step "3. Settings"
[[ -f tomyum.env ]] || cp tomyum.env.example tomyum.env
[[ -f osu-web.env ]] || cp osu-web.env.example osu-web.env
chmod 600 .env tomyum.env osu-web.env
current=$(env_get DOMAIN)
[[ -n "$DOMAIN" ]] || { [[ -n "$current" && "$current" != example.com ]] && DOMAIN=$current; }
if [[ $LOCAL == 1 && -z "$DOMAIN" ]]; then DOMAIN=localhost; fi
ask DOMAIN "Your domain (the site will be osu.<domain>; players use -devserver <domain>)"
[[ -n "$DOMAIN" && "$DOMAIN" != example.com ]] || die "A domain is needed (--domain example.com)."
if [[ "$current" != "$DOMAIN" ]]; then
	sed -i "s/^DOMAIN=.*/DOMAIN=$DOMAIN/" .env
fi
env_fill DB_ROOT_PASSWORD "$(openssl rand -hex 24)"
env_fill DB_PASSWORD "$(openssl rand -hex 24)"
env_fill SHARED_INTEROP_SECRET "$(openssl rand -hex 32)"
echo "  domain $DOMAIN; database passwords and the shared secret are in .env"

# ---------------------------------------------------------------- 4. the way in
step "4. The way in"
cf() { # cf METHOD PATH [JSON]: Cloudflare's API; prints the JSON answer
	curl -fsS -X "$1" "https://api.cloudflare.com/client/v4$2" -H "Authorization: Bearer $CF_API_TOKEN" \
		-H "Content-Type: application/json" ${3:+--data "$3"}
}
json() { python3 -c "import json,sys; d=json.load(sys.stdin); print($1)"; }
if [[ $LOCAL == 1 ]]; then
	if [[ ! -f docker-compose.override.yml ]]; then
		cat > docker-compose.override.yml <<'YAML'
# Written by setup.sh --local: no Cloudflare Tunnel; the stack's Caddy on http://127.0.0.1:8880.
services:
  cloudflared:
    profiles: [tunnel]
  caddy:
    ports:
      - "127.0.0.1:8880:80"
YAML
	fi
	echo "  local: http://127.0.0.1:8880 (send Host: osu.$DOMAIN, e.g. with an /etc/hosts entry)"
elif [[ -n "$(env_get CLOUDFLARE_TUNNEL_TOKEN)" ]]; then
	echo "  the Cloudflare Tunnel token is already set"
elif [[ -n "$TUNNEL_TOKEN" ]]; then
	env_fill CLOUDFLARE_TUNNEL_TOKEN "$TUNNEL_TOKEN"
	bold "  Tunnel token saved. In Cloudflare Zero Trust (Networks > Tunnels > your tunnel > Public hostnames),"
	bold "  add each of these with the service http://caddy:80:"
	printf '    %s\n' "${HOSTS[@]/%/.$DOMAIN}"
elif [[ -n "$CF_API_TOKEN" ]]; then
	command -v python3 >/dev/null || die "python3 is needed to talk to Cloudflare's API."
	[[ -n "$CF_ACCOUNT" ]] || die "--cloudflare-account <account id> is needed with --cloudflare-api-token."
	zone=${CF_ZONE:-$(cf GET "/zones?name=$DOMAIN" | json "d['result'][0]['id'] if d['result'] else ''")}
	[[ -n "$zone" ]] || die "Cloudflare has no zone called $DOMAIN (use --cloudflare-zone <zone id>)."
	name="tomyum-$DOMAIN"
	tunnel=$(cf GET "/accounts/$CF_ACCOUNT/cfd_tunnel?name=$name&is_deleted=false" | json "d['result'][0]['id'] if d['result'] else ''")
	if [[ -z "$tunnel" ]]; then
		tunnel=$(cf POST "/accounts/$CF_ACCOUNT/cfd_tunnel" "{\"name\":\"$name\",\"config_src\":\"cloudflare\"}" | json "d['result']['id']")
		echo "  made the tunnel $name"
	fi
	ingress=$(printf '{"hostname":"%s.'"$DOMAIN"'","service":"http://caddy:80"},' "${HOSTS[@]}")
	cf PUT "/accounts/$CF_ACCOUNT/cfd_tunnel/$tunnel/configurations" "{\"config\":{\"ingress\":[${ingress}{\"service\":\"http_status:404\"}]}}" >/dev/null
	for host in "${HOSTS[@]}"; do
		existing=$(cf GET "/zones/$zone/dns_records?name=$host.$DOMAIN" | json "d['result'][0]['id'] if d['result'] else ''")
		record="{\"type\":\"CNAME\",\"name\":\"$host.$DOMAIN\",\"content\":\"$tunnel.cfargotunnel.com\",\"proxied\":true}"
		if [[ -z "$existing" ]]; then cf POST "/zones/$zone/dns_records" "$record" >/dev/null
		else cf PUT "/zones/$zone/dns_records/$existing" "$record" >/dev/null; fi
	done
	env_fill CLOUDFLARE_TUNNEL_TOKEN "$(cf GET "/accounts/$CF_ACCOUNT/cfd_tunnel/$tunnel/token" | json "d['result']")"
	echo "  tunnel $name: ${#HOSTS[@]} hostnames routed to the stack, DNS records made"
else
	bold "  No way in yet. Run again with one of:"
	echo "    --tunnel-token <token>     (Cloudflare Zero Trust > Networks > Tunnels > Create; copy the token)"
	echo "    --cloudflare-api-token <token> --cloudflare-account <id>   (a token with Tunnel and DNS edit rights)"
	echo "    --local                    (try it on this machine)"
	exit 1
fi

# ---------------------------------------------------------------- 5. images
if ! done_step build; then
	step "5. Building the images (the first time takes 10-20 minutes)"
	compose --profile build build osu-web-build
	compose build
	mark build
fi

# ---------------------------------------------------------------- 6. database
step "6. The website's key and database"
if [[ -z "$(env_get OSU_WEB_APP_KEY)" ]]; then
	key=$(artisan key:generate --show | tr -d '\r' | grep -E '^base64:' | tail -n 1)
	[[ -n "$key" ]] || die "The website didn't give a key (docker compose run --rm artisan key:generate --show)."
	env_fill OSU_WEB_APP_KEY "$key"
	echo "  website key made"
fi
if ! done_step database; then
	artisan migrate --force
	artisan passport:keys || true   # already there on a second run
	artisan tomyum:setup
	mark database
fi

# ---------------------------------------------------------------- 7. start
step "7. Starting"
compose up -d
artisan tomyum:es-ensure
echo -n "  waiting for TomYum"
for _ in $(seq 1 60); do
	compose logs server 2>/dev/null | grep -q "Now listening on" && break
	echo -n "."; sleep 5
done
echo
check

step "Done"
cat <<EOF
  The website: https://osu.$DOMAIN$( [[ $LOCAL == 1 ]] && echo "  (here: http://127.0.0.1:8880 with Host: osu.$DOMAIN)" )
  Stable: osu!.exe -devserver $DOMAIN    lazer: a build pointed at $DOMAIN (docs.somtum.fun/clients/lazer)

  Next:
    1. Register on the website or in the game, then:   ./setup.sh admin <your name>
    2. IRC (port 6667) and old clients' TCP bancho don't go through the tunnel: see docs.somtum.fun/guide/domains
    3. Backups: ./backup.sh from cron (docs.somtum.fun/operations/backups)
  The settings are in .env, tomyum.env and osu-web.env (keep them private). Updating: ./deploy.sh
EOF
