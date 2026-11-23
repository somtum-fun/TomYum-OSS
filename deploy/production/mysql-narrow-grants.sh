#!/bin/bash
# Narrows the shared database account (osuweb) to the app databases (app-databases.sh), for installs made when
# mysql-init.sh gave it every right on every database. Safe to run again. Run from this folder:
#   ./mysql-narrow-grants.sh
set -euo pipefail
cd "$(dirname "$0")"
. ./app-databases.sh
# Revoking ALL on *.* also drops grants on single databases (MySQL 8.4), so: revoke, then grant, in one batch
# (milliseconds apart; --force so one failing grant doesn't skip the rest), then check every database is there.
sql="REVOKE ALL PRIVILEGES ON *.* FROM 'osuweb'@'%'; REVOKE GRANT OPTION ON *.* FROM 'osuweb'@'%';"
for db in $APP_DATABASES; do sql+=" GRANT ALL PRIVILEGES ON \`$db\`.* TO 'osuweb'@'%';"; done
sql+=" FLUSH PRIVILEGES;"
mysql_root() { docker compose exec -T db sh -c 'MYSQL_PWD="$MYSQL_ROOT_PASSWORD" exec mysql -uroot -N --force'; }
mysql_root <<< "$sql"
grants=$(mysql_root <<< "SHOW GRANTS FOR 'osuweb'@'%';")
echo "$grants"
for db in $APP_DATABASES; do
  if ! grep -qF "ON \`$db\`.*" <<< "$grants"; then
    echo "osuweb has no rights on $db: the services may fail. To put the old rights back at once:" >&2
    echo "  docker compose exec db sh -c 'MYSQL_PWD=\"\$MYSQL_ROOT_PASSWORD\" mysql -uroot -e \"GRANT ALL PRIVILEGES ON *.* TO osuweb@\\\"%\\\"\"'" >&2
    exit 1
  fi
done
echo "osuweb now has rights on: $APP_DATABASES"
