#!/bin/sh
# First start of the database only (docker-entrypoint-initdb.d): the databases the website and ppy's
# services need, and the account they share with TomYum. The website's databases match its `artisan
# db:create` (config/database.php); TomYum creates `tomyum` itself at startup. The account has rights on
# the app's databases only (app-databases.sh lists them; mysql-narrow-grants.sh narrows an account made
# before this did).
set -e
. /docker-entrypoint-initdb.d/app-databases.sh
{
  for db in osu osu_charts osu_chat osu_mp osu_store osu_updates; do
    echo "CREATE DATABASE IF NOT EXISTS \`$db\` DEFAULT CHARSET utf8mb4;"
  done
  echo "CREATE USER IF NOT EXISTS 'osuweb'@'%' IDENTIFIED BY '${DB_PASSWORD}';"
  for db in $APP_DATABASES; do echo "GRANT ALL PRIVILEGES ON \`$db\`.* TO 'osuweb'@'%';"; done
  echo "FLUSH PRIVILEGES;"
} | mysql -uroot -p"$MYSQL_ROOT_PASSWORD"
