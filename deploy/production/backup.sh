#!/bin/bash
# Nightly backup of the server: every database (a dump), the game server's and the website's files
# (replays, screenshots, custom maps, uploads) and the config (.env files, Caddyfile) needed to
# restore. Kept 7 days in ~/backups; with an rclone remote named "proton" set up (docs/backups.md),
# also copied to Proton Drive (tomyum-backups/, 30 days kept there). The search index isn't kept:
# it's rebuilt from the database. Cron (crontab -e): 30 4 * * * ~/TomYum/deploy/production/backup.sh
set -euo pipefail
cd "$(dirname "$0")"

dest=${BACKUP_DIR:-$HOME/backups}
stamp=$(date -u +%Y-%m-%d_%H%M)
dir="$dest/$stamp"
mkdir -p "$dir"
exec > >(tee "$dir/backup.log") 2>&1
echo "Backup $stamp"

docker compose exec -T db sh -c 'MYSQL_PWD="$MYSQL_ROOT_PASSWORD" exec mysqldump -uroot --single-transaction --routines --events --triggers --databases osu osu_chat osu_mp osu_store osu_updates tomyum' \
	| gzip > "$dir/databases.sql.gz"
# A dump that stopped early has no closing line.
gzip -dc "$dir/databases.sql.gz" | tail -n 1 | grep -q 'Dump completed' || { echo "The database dump is incomplete." >&2; exit 1; }

for volume in server-data osu-web-uploads osu-web-storage redis-data; do
	docker run --rm -v "tomyum_$volume:/v:ro" -v "$dir:/out" alpine tar czf "/out/$volume.tar.gz" -C /v .
done
tar czf "$dir/config.tar.gz" .env tomyum.env osu-web.env Caddyfile

du -sh "$dir"
find "$dest" -mindepth 1 -maxdepth 1 -type d -mtime +7 -exec rm -rf {} +

if command -v rclone >/dev/null && rclone listremotes 2>/dev/null | grep -qx 'proton:'; then
	rclone copy "$dir" "proton:tomyum-backups/$stamp"
	rclone delete --min-age 30d proton:tomyum-backups
	rclone rmdirs --leave-root proton:tomyum-backups
	echo "Copied to Proton Drive."
else
	echo "No rclone remote \"proton\": kept on this machine only."
fi
