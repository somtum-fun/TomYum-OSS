#!/bin/sh
# First start of the database only (docker-entrypoint-initdb.d): the databases the website, TomYum
# and ppy's services need, and the account they share. The website's databases match its `artisan
# db:create` (config/database.php); TomYum creates `tomyum` itself at startup.
set -e
mysql -uroot -p"$MYSQL_ROOT_PASSWORD" <<SQL
CREATE DATABASE IF NOT EXISTS osu DEFAULT CHARSET utf8mb4;
CREATE DATABASE IF NOT EXISTS osu_charts DEFAULT CHARSET utf8mb4;
CREATE DATABASE IF NOT EXISTS osu_chat DEFAULT CHARSET utf8mb4;
CREATE DATABASE IF NOT EXISTS osu_mp DEFAULT CHARSET utf8mb4;
CREATE DATABASE IF NOT EXISTS osu_store DEFAULT CHARSET utf8mb4;
CREATE DATABASE IF NOT EXISTS osu_updates DEFAULT CHARSET utf8mb4;
CREATE USER IF NOT EXISTS 'osuweb'@'%' IDENTIFIED BY '${DB_PASSWORD}';
GRANT ALL PRIVILEGES ON *.* TO 'osuweb'@'%';
FLUSH PRIVILEGES;
SQL
