#!/bin/sh
# First start of the database only (docker-entrypoint-initdb.d): the account the website, TomYum
# and ppy's services share. It creates the databases it needs (osu, osu_chat, tomyum, ...).
set -e
mysql -uroot -p"$MYSQL_ROOT_PASSWORD" <<SQL
CREATE USER IF NOT EXISTS 'osuweb'@'%' IDENTIFIED BY '${DB_PASSWORD}';
GRANT ALL PRIVILEGES ON *.* TO 'osuweb'@'%';
FLUSH PRIVILEGES;
SQL
