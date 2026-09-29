# Backups

`deploy/production/backup.sh` backs up the production server every night (cron, 04:30):

- every database, as one `mysqldump` (`databases.sql.gz`): the website (`osu`, `osu_chat`, `osu_mp`,
  `osu_store`, `osu_updates`) and TomYum (`tomyum`);
- the game server's files (`server-data`: replays, screenshots, custom beatmaps), the website's
  uploads and storage, and Redis (IRC passwords, link codes);
- the config needed to bring it back (`.env`, `tomyum.env`, `osu-web.env`, `Caddyfile`). These hold the
  server's secrets: keep the copies private.

The search index isn't backed up: it's rebuilt from the database.

Each run is a folder `~/backups/<date>_<time>/` (with a `backup.log`); 7 days are kept on the VM.

## Proton Drive

With an [rclone](https://rclone.org) remote named `proton`, every backup is also copied to Proton Drive
(`tomyum-backups/`, 30 days kept). rclone's Proton Drive support needs rclone 1.64 or newer. Once, on
the VM:

```sh
sudo -v && curl -fsSL https://rclone.org/install.sh | sudo bash   # the current rclone
rclone config    # n (new remote) → name: proton → storage: protondrive → your Proton login (and 2FA code)
rclone lsd proton:   # check it works
```

rclone keeps the login in `~/.config/rclone/rclone.conf` on the VM; a separate Proton account for the
server's backups keeps your own account's password off it.

## Restoring

On a fresh VM with the stack set up (docs: deploy), from a backup folder:

```sh
cd ~/TomYum/deploy/production
tar xzf ~/backups/<run>/config.tar.gz                 # .env, tomyum.env, osu-web.env, Caddyfile
docker compose up -d db redis && sleep 20
gzip -dc ~/backups/<run>/databases.sql.gz | docker compose exec -T db sh -c 'MYSQL_PWD="$MYSQL_ROOT_PASSWORD" mysql -uroot'
for v in server-data osu-web-uploads osu-web-storage redis-data; do
  docker run --rm -v tomyum_$v:/v -v ~/backups/<run>:/in alpine sh -c "cd /v && tar xzf /in/$v.tar.gz"
done
docker compose up -d
docker compose run --rm artisan es:index-documents --yes   # rebuild the search index
```
