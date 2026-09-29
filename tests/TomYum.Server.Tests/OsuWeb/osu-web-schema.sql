-- osu-web tables TomYum reads/writes, for the OsuWeb* integration tests.
--
-- Accounts:
-- Hand-translated from osu-web's migrations (database/migrations/2015_01_01_133337_base_tables.php
-- plus every later alter of these tables: 2019_12_04 add_extra_columns_to_groups,
-- 2020_01_23 nullable_groups_display_order, 2020_05_15 sync_structure, 2020_10_01 has_playmodes /
-- playmodes, 2023_01_30 datetime supporter expiry, 2023_09_11 lock_email_changes,
-- 2024_02_20 cover columns, 2025_10_15 support_length, 2026_09_14 nullable user_permissions).
-- Every column is kept so an insert that misses a NOT NULL column fails here as it would in
-- osu-web. Groups are what osu-web's GroupSeeder creates.

DROP TABLE IF EXISTS osu_achievements;
DROP TABLE IF EXISTS osu_user_achievements;
DROP TABLE IF EXISTS scores;
DROP TABLE IF EXISTS osu_user_stats;
DROP TABLE IF EXISTS osu_user_stats_taiko;
DROP TABLE IF EXISTS osu_user_stats_fruits;
DROP TABLE IF EXISTS osu_user_stats_mania;
DROP TABLE IF EXISTS osu_beatmaps;
DROP TABLE IF EXISTS osu_beatmapsets;
DROP TABLE IF EXISTS phpbb_user_group;
DROP TABLE IF EXISTS phpbb_groups;
DROP TABLE IF EXISTS phpbb_users;

CREATE TABLE phpbb_users (
  user_id int unsigned NOT NULL AUTO_INCREMENT,
  user_type tinyint NOT NULL DEFAULT 0,
  group_id mediumint unsigned NOT NULL DEFAULT 2,
  user_permissions longtext NULL,
  user_perm_from mediumint unsigned NULL DEFAULT 0,
  user_ip varchar(50) NOT NULL DEFAULT '',
  user_regdate int unsigned NOT NULL DEFAULT 0,
  username varchar(255) NOT NULL DEFAULT '',
  username_clean varchar(255) NOT NULL DEFAULT '',
  user_password varchar(64) NOT NULL DEFAULT '',
  user_passchg int unsigned NOT NULL DEFAULT 0,
  user_email varchar(100) NULL,
  user_birthday varchar(10) NOT NULL DEFAULT '',
  user_lastvisit int unsigned NOT NULL DEFAULT 0,
  user_lastmark int unsigned NOT NULL DEFAULT 0,
  user_lastpost_time int unsigned NOT NULL DEFAULT 0,
  user_lastpage varchar(200) NOT NULL DEFAULT '',
  user_last_confirm_key varchar(10) NOT NULL DEFAULT '',
  user_last_search int unsigned NOT NULL DEFAULT 0,
  user_warnings tinyint(1) NOT NULL DEFAULT 0,
  user_last_warning int unsigned NOT NULL DEFAULT 0,
  user_login_attempts tinyint(1) NOT NULL DEFAULT 0,
  user_inactive_reason tinyint(1) NOT NULL DEFAULT 0,
  user_inactive_time int unsigned NOT NULL DEFAULT 0,
  user_posts mediumint unsigned NOT NULL DEFAULT 0,
  user_lang varchar(30) NOT NULL DEFAULT '',
  user_timezone decimal(5,2) NOT NULL DEFAULT 0.00,
  user_dst tinyint(1) unsigned NOT NULL DEFAULT 0,
  user_dateformat varchar(30) NOT NULL DEFAULT 'd M Y H:i',
  user_style smallint unsigned NOT NULL DEFAULT 0,
  user_rank mediumint unsigned NOT NULL DEFAULT 0,
  user_colour varchar(6) NOT NULL DEFAULT '',
  user_new_privmsg smallint NOT NULL DEFAULT 0,
  user_unread_privmsg smallint NOT NULL DEFAULT 0,
  user_last_privmsg int unsigned NOT NULL DEFAULT 0,
  user_message_rules tinyint(1) unsigned NOT NULL DEFAULT 0,
  user_full_folder int NOT NULL DEFAULT -3,
  user_emailtime int unsigned NOT NULL DEFAULT 0,
  user_topic_show_days smallint unsigned NOT NULL DEFAULT 0,
  user_topic_sortby_type char(1) NOT NULL DEFAULT 't',
  user_topic_sortby_dir char(1) NOT NULL DEFAULT 'd',
  user_post_show_days smallint unsigned NOT NULL DEFAULT 0,
  user_post_sortby_type char(1) NOT NULL DEFAULT 't',
  user_post_sortby_dir char(1) NOT NULL DEFAULT 'a',
  user_notify tinyint(1) unsigned NOT NULL DEFAULT 0,
  user_notify_pm tinyint(1) unsigned NOT NULL DEFAULT 1,
  user_notify_type tinyint(1) NOT NULL DEFAULT 0,
  user_allow_pm tinyint(1) unsigned NOT NULL DEFAULT 1,
  user_allow_viewonline tinyint(1) unsigned NOT NULL DEFAULT 1,
  user_allow_viewemail tinyint(1) unsigned NOT NULL DEFAULT 1,
  user_allow_massemail tinyint(1) unsigned NOT NULL DEFAULT 1,
  user_options int unsigned NOT NULL DEFAULT 895,
  user_avatar varchar(255) NOT NULL DEFAULT '',
  user_avatar_type tinyint(1) NOT NULL DEFAULT 0,
  user_avatar_width smallint unsigned NOT NULL DEFAULT 0,
  user_avatar_height smallint unsigned NOT NULL DEFAULT 0,
  user_sig mediumtext NOT NULL,
  user_sig_bbcode_uid varchar(5) NOT NULL DEFAULT '',
  user_sig_bbcode_bitfield varchar(255) NOT NULL DEFAULT '',
  user_from varchar(100) NOT NULL DEFAULT '',
  user_lastfm varchar(255) NOT NULL DEFAULT '',
  user_lastfm_session varchar(255) NOT NULL DEFAULT '',
  user_twitter varchar(255) NOT NULL DEFAULT '',
  user_msnm varchar(255) NOT NULL DEFAULT '',
  user_jabber varchar(255) NOT NULL DEFAULT '',
  user_website varchar(200) NOT NULL DEFAULT '',
  user_occ mediumtext NOT NULL,
  user_interests mediumtext NOT NULL,
  user_actkey varchar(32) NOT NULL DEFAULT '',
  user_newpasswd varchar(32) NOT NULL DEFAULT '',
  osu_mapperrank float NOT NULL DEFAULT 0,
  osu_testversion tinyint(1) NOT NULL DEFAULT 0,
  osu_subscriber tinyint(1) NOT NULL DEFAULT 0,
  osu_subscriptionexpiry datetime NULL,
  support_length int unsigned NULL,
  osu_kudosavailable smallint NOT NULL DEFAULT 0,
  osu_kudosdenied smallint unsigned NOT NULL DEFAULT 0,
  osu_kudostotal smallint NOT NULL DEFAULT 0,
  country_acronym char(2) NOT NULL DEFAULT '',
  userpage_post_id mediumint unsigned NULL,
  username_previous varchar(1024) NULL,
  osu_featurevotes smallint unsigned NOT NULL DEFAULT 0,
  osu_playstyle tinyint unsigned NOT NULL DEFAULT 0,
  osu_playmode tinyint NOT NULL DEFAULT 0,
  remember_token varchar(100) NULL,
  lock_email_changes tinyint(1) NULL,
  cover_preset_id mediumint unsigned NULL,
  custom_cover_filename varchar(255) NULL,
  PRIMARY KEY (user_id),
  UNIQUE KEY user_email_unique (user_email),
  UNIQUE KEY username_clean (username_clean),
  UNIQUE KEY username_id (username, user_id),
  KEY country_acronym (country_acronym)
) AUTO_INCREMENT = 2 DEFAULT CHARSET = utf8mb4 COLLATE = utf8mb4_unicode_ci;

CREATE TABLE phpbb_groups (
  group_id mediumint unsigned NOT NULL AUTO_INCREMENT,
  group_type tinyint NOT NULL DEFAULT 1,
  group_founder_manage tinyint unsigned NOT NULL DEFAULT 0,
  group_name varchar(255) NOT NULL DEFAULT '',
  group_desc text NOT NULL,
  group_desc_bitfield varchar(255) NOT NULL DEFAULT '',
  group_desc_options int unsigned NOT NULL DEFAULT 7,
  group_desc_uid varchar(5) NOT NULL DEFAULT '',
  group_display tinyint unsigned NOT NULL DEFAULT 0,
  group_avatar varchar(255) NOT NULL DEFAULT '',
  group_avatar_type tinyint NOT NULL DEFAULT 0,
  group_avatar_width smallint unsigned NOT NULL DEFAULT 0,
  group_avatar_height smallint unsigned NOT NULL DEFAULT 0,
  group_rank mediumint unsigned NOT NULL DEFAULT 0,
  group_colour varchar(6) NOT NULL DEFAULT '',
  group_sig_chars mediumint unsigned NOT NULL DEFAULT 0,
  group_receive_pm tinyint unsigned NOT NULL DEFAULT 0,
  group_message_limit mediumint unsigned NOT NULL DEFAULT 0,
  group_legend tinyint unsigned NOT NULL DEFAULT 1,
  identifier varchar(255) NULL,
  short_name varchar(255) NULL,
  display_order int NULL,
  colour varchar(255) NULL,
  has_playmodes tinyint(1) NOT NULL DEFAULT 0,
  PRIMARY KEY (group_id),
  UNIQUE KEY identifier (identifier),
  KEY group_legend (group_legend)
) DEFAULT CHARSET = utf8mb4 COLLATE = utf8mb4_unicode_ci;

CREATE TABLE phpbb_user_group (
  group_id mediumint unsigned NOT NULL DEFAULT 0,
  user_id mediumint unsigned NOT NULL DEFAULT 0,
  group_leader tinyint(1) unsigned NOT NULL DEFAULT 0,
  user_pending tinyint(1) unsigned NOT NULL DEFAULT 1,
  playmodes varchar(255) NULL,
  PRIMARY KEY (group_id, user_id),
  KEY user_id (user_id)
) DEFAULT CHARSET = utf8mb4 COLLATE = utf8mb4_unicode_ci;

-- GroupSeeder: one group per Group::PRIV_IDENTIFIERS entry.
INSERT INTO phpbb_groups (group_desc, group_name, group_type, has_playmodes, identifier, short_name) VALUES
  ('', 'admin', 2, 0, 'admin', 'admin'),
  ('', 'alumni', 2, 0, 'alumni', 'alumni'),
  ('', 'announce', 2, 0, 'announce', 'announce'),
  ('', 'bng', 2, 1, 'bng', 'bng'),
  ('', 'bng_limited', 2, 1, 'bng_limited', 'bng_limited'),
  ('', 'bot', 2, 0, 'bot', 'bot'),
  ('', 'default', 2, 0, 'default', 'default'),
  ('', 'dev', 2, 0, 'dev', 'dev'),
  ('', 'gmt', 2, 0, 'gmt', 'gmt'),
  ('', 'loved', 2, 0, 'loved', 'loved'),
  ('', 'nat', 2, 0, 'nat', 'nat'),
  ('', 'no_profile', 2, 0, 'no_profile', 'no_profile');

-- Beatmaps: base_tables + 2016_02_25 cover_updated_at, 2017_08_28 discussion_enabled/deleted_at,
-- 2017_09_04 deleted_at, 2018_01_03 hype, 2018_01_11 nominations, 2018_04_20 queue time,
-- 2018_06_25 datetimes -> TIMESTAMP, 2019_04_17 discussion_locked, 2019_11_27 + 2022_01_17 +
-- 2023_08_17 bpm, 2020_05_15 sync_structure, 2020_08_27 nsfw, 2021_08_30 track_id,
-- 2022_04_30 spotlight, 2022_05_16 approvedby_id, 2022_06_08 + 2023_08_17 comment_locked,
-- 2023_09_21 max_combo, 2024_05_17 eligible_main_rulesets, 2025_06_27 osu_file_version,
-- 2025_10_20 anime_cover, 2026_06_18 lazer_only.

CREATE TABLE osu_beatmapsets (
  beatmapset_id mediumint unsigned NOT NULL AUTO_INCREMENT,
  user_id int unsigned NOT NULL DEFAULT 0,
  thread_id mediumint unsigned NOT NULL DEFAULT 0,
  artist varchar(80) NOT NULL DEFAULT '',
  artist_unicode varchar(80) NULL,
  title varchar(80) NOT NULL DEFAULT '',
  title_unicode varchar(80) NULL,
  creator varchar(80) NOT NULL DEFAULT '',
  source varchar(200) NOT NULL DEFAULT '',
  tags varchar(1000) NOT NULL DEFAULT '',
  video tinyint(1) NOT NULL DEFAULT 0,
  storyboard tinyint(1) NOT NULL DEFAULT 0,
  epilepsy tinyint(1) NOT NULL DEFAULT 0,
  bpm float NOT NULL DEFAULT 0,
  versions_available tinyint(1) unsigned NOT NULL DEFAULT 1,
  approved tinyint NOT NULL DEFAULT 0,
  approvedby_id int unsigned NULL,
  approved_date timestamp NULL DEFAULT NULL,
  submit_date timestamp NULL DEFAULT NULL,
  last_update timestamp NOT NULL DEFAULT CURRENT_TIMESTAMP,
  filename varchar(120) NULL,
  active tinyint(1) NOT NULL DEFAULT 1,
  rating float unsigned NOT NULL DEFAULT 0,
  `offset` smallint NOT NULL DEFAULT 0,
  displaytitle varchar(200) NOT NULL DEFAULT '',
  genre_id smallint unsigned NOT NULL DEFAULT 1,
  language_id smallint unsigned NOT NULL DEFAULT 1,
  star_priority smallint NOT NULL DEFAULT 0,
  filesize bigint NOT NULL DEFAULT 0,
  filesize_novideo bigint NULL,
  body_hash binary(16) NULL,
  header_hash binary(16) NULL,
  osz2_hash binary(16) NULL,
  download_disabled tinyint(1) unsigned NOT NULL DEFAULT 0,
  download_disabled_url varchar(100) NULL,
  thread_icon_date timestamp NULL DEFAULT NULL,
  favourite_count mediumint unsigned NOT NULL DEFAULT 0,
  play_count int unsigned NOT NULL DEFAULT 0,
  difficulty_names varchar(2048) NULL,
  cover_updated_at timestamp NULL DEFAULT NULL,
  discussion_enabled tinyint(1) NOT NULL DEFAULT 0,
  discussion_locked tinyint(1) NOT NULL DEFAULT 0,
  deleted_at timestamp NULL,
  hype int NOT NULL DEFAULT 0,
  nominations int NOT NULL DEFAULT 0,
  previous_queue_duration int NOT NULL DEFAULT 0,
  queued_at timestamp NULL DEFAULT NULL,
  storyboard_hash varchar(32) NULL,
  nsfw tinyint(1) NOT NULL DEFAULT 0,
  anime_cover tinyint(1) NOT NULL DEFAULT 0,
  track_id int unsigned NULL,
  spotlight tinyint(1) NOT NULL DEFAULT 0,
  comment_locked tinyint NULL DEFAULT 0,
  eligible_main_rulesets json NULL,
  PRIMARY KEY (beatmapset_id),
  KEY approved (approved, active, approved_date)
) DEFAULT CHARSET = utf8mb4 COLLATE = utf8mb4_unicode_ci;

CREATE TABLE osu_beatmaps (
  beatmap_id mediumint unsigned NOT NULL AUTO_INCREMENT,
  beatmapset_id mediumint unsigned NULL,
  user_id int unsigned NOT NULL DEFAULT 0,
  filename varchar(150) NULL,
  checksum varchar(32) NULL,
  version varchar(80) NOT NULL DEFAULT '',
  total_length mediumint unsigned NOT NULL DEFAULT 0,
  hit_length mediumint unsigned NOT NULL DEFAULT 0,
  countTotal mediumint unsigned NOT NULL DEFAULT 0,
  countNormal mediumint unsigned NOT NULL DEFAULT 0,
  countSlider mediumint unsigned NOT NULL DEFAULT 0,
  countSpinner mediumint unsigned NOT NULL DEFAULT 0,
  diff_drain float unsigned NOT NULL DEFAULT 0,
  diff_size float unsigned NOT NULL DEFAULT 0,
  diff_overall float unsigned NOT NULL DEFAULT 0,
  diff_approach float unsigned NOT NULL DEFAULT 0,
  playmode tinyint unsigned NOT NULL DEFAULT 0,
  approved tinyint NOT NULL DEFAULT 0,
  last_update timestamp NOT NULL DEFAULT CURRENT_TIMESTAMP,
  difficultyrating float NOT NULL DEFAULT 0,
  max_combo mediumint unsigned NOT NULL DEFAULT 0,
  playcount int unsigned NOT NULL DEFAULT 0,
  passcount int unsigned NOT NULL DEFAULT 0,
  youtube_preview varchar(50) NULL,
  score_version tinyint NOT NULL DEFAULT 1,
  osu_file_version tinyint unsigned NOT NULL DEFAULT 14,
  deleted_at timestamp NULL,
  bpm float NOT NULL DEFAULT 60,
  lazer_only tinyint(1) NOT NULL DEFAULT 0,
  PRIMARY KEY (beatmap_id),
  KEY beatmapset_id (beatmapset_id),
  KEY checksum (checksum)
) DEFAULT CHARSET = utf8mb4 COLLATE = utf8mb4_unicode_ci;

-- Scores: 2024_01_12 update_scores_table_final (verbatim DDL), 2024_01_31 renamed solo_scores ->
-- scores, 2024_06_09 user_recent index.
CREATE TABLE scores (
  `id` bigint unsigned NOT NULL AUTO_INCREMENT,
  `user_id` int unsigned NOT NULL,
  `ruleset_id` smallint unsigned NOT NULL,
  `beatmap_id` mediumint unsigned NOT NULL,
  `has_replay` tinyint NOT NULL DEFAULT '0',
  `preserve` tinyint NOT NULL DEFAULT '0',
  `ranked` tinyint NOT NULL DEFAULT '1',
  `rank` char(2) NOT NULL DEFAULT '',
  `passed` tinyint NOT NULL DEFAULT '0',
  `accuracy` float NOT NULL DEFAULT '0',
  `max_combo` int unsigned NOT NULL DEFAULT '0',
  `total_score` int unsigned NOT NULL DEFAULT '0',
  `data` json NOT NULL,
  `pp` float DEFAULT NULL,
  `legacy_score_id` bigint unsigned DEFAULT NULL,
  `legacy_total_score` int unsigned NOT NULL DEFAULT '0',
  `started_at` timestamp NULL DEFAULT NULL,
  `ended_at` timestamp NOT NULL,
  `unix_updated_at` int unsigned NOT NULL DEFAULT (unix_timestamp()),
  `build_id` smallint unsigned DEFAULT NULL,
  PRIMARY KEY (`id`,`preserve`,`unix_updated_at`),
  KEY `user_ruleset_index` (`user_id`,`ruleset_id`),
  KEY `beatmap_user_index` (`beatmap_id`,`user_id`),
  KEY `legacy_score_lookup` (`ruleset_id`,`legacy_score_id`),
  KEY `user_recent` (`user_id`, `id`)
);

-- Per-ruleset stats: base_tables (identical columns for all four), 2017_12_20 xh/sh rank
-- counts, 2022_12_05 + 2025_03_14 rank_score_exp added then removed, 2025_03_18 index.

CREATE TABLE osu_user_stats (
  user_id mediumint NOT NULL,
  count300 int NOT NULL DEFAULT 0,
  count100 int NOT NULL DEFAULT 0,
  count50 int NOT NULL DEFAULT 0,
  countMiss int NOT NULL DEFAULT 0,
  accuracy_total bigint unsigned NOT NULL,
  accuracy_count bigint unsigned NOT NULL,
  accuracy float NOT NULL,
  playcount mediumint NOT NULL,
  total_seconds_played bigint NOT NULL DEFAULT 0,
  ranked_score bigint NOT NULL,
  total_score bigint NOT NULL,
  x_rank_count mediumint NOT NULL,
  xh_rank_count mediumint NOT NULL DEFAULT 0,
  s_rank_count mediumint NOT NULL,
  sh_rank_count mediumint NOT NULL DEFAULT 0,
  a_rank_count mediumint NOT NULL,
  `rank` mediumint NOT NULL,
  level float unsigned NOT NULL,
  replay_popularity mediumint unsigned NOT NULL DEFAULT 0,
  fail_count mediumint unsigned NOT NULL DEFAULT 0,
  exit_count mediumint unsigned NOT NULL DEFAULT 0,
  max_combo smallint unsigned NOT NULL DEFAULT 0,
  country_acronym char(2) NOT NULL DEFAULT '',
  rank_score float unsigned NOT NULL,
  rank_score_index int unsigned NOT NULL,
  accuracy_new float unsigned NOT NULL,
  last_update timestamp NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  last_played timestamp NOT NULL DEFAULT CURRENT_TIMESTAMP,
  PRIMARY KEY (user_id),
  KEY ranked_score (ranked_score),
  KEY rank_score (rank_score),
  KEY country_ranked_score (country_acronym, ranked_score)
) DEFAULT CHARSET = utf8mb4 COLLATE = utf8mb4_unicode_ci;

CREATE TABLE osu_user_stats_taiko (
  user_id mediumint NOT NULL,
  count300 int NOT NULL DEFAULT 0,
  count100 int NOT NULL DEFAULT 0,
  count50 int NOT NULL DEFAULT 0,
  countMiss int NOT NULL DEFAULT 0,
  accuracy_total bigint unsigned NOT NULL,
  accuracy_count bigint unsigned NOT NULL,
  accuracy float NOT NULL,
  playcount mediumint NOT NULL,
  total_seconds_played bigint NOT NULL DEFAULT 0,
  ranked_score bigint NOT NULL,
  total_score bigint NOT NULL,
  x_rank_count mediumint NOT NULL,
  xh_rank_count mediumint NOT NULL DEFAULT 0,
  s_rank_count mediumint NOT NULL,
  sh_rank_count mediumint NOT NULL DEFAULT 0,
  a_rank_count mediumint NOT NULL,
  `rank` mediumint NOT NULL,
  level float unsigned NOT NULL,
  replay_popularity mediumint unsigned NOT NULL DEFAULT 0,
  fail_count mediumint unsigned NOT NULL DEFAULT 0,
  exit_count mediumint unsigned NOT NULL DEFAULT 0,
  max_combo smallint unsigned NOT NULL DEFAULT 0,
  country_acronym char(2) NOT NULL DEFAULT '',
  rank_score float unsigned NOT NULL,
  rank_score_index int unsigned NOT NULL,
  accuracy_new float unsigned NOT NULL,
  last_update timestamp NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  last_played timestamp NOT NULL DEFAULT CURRENT_TIMESTAMP,
  PRIMARY KEY (user_id),
  KEY ranked_score (ranked_score),
  KEY rank_score (rank_score),
  KEY country_ranked_score (country_acronym, ranked_score)
) DEFAULT CHARSET = utf8mb4 COLLATE = utf8mb4_unicode_ci;

CREATE TABLE osu_user_stats_fruits (
  user_id mediumint NOT NULL,
  count300 int NOT NULL DEFAULT 0,
  count100 int NOT NULL DEFAULT 0,
  count50 int NOT NULL DEFAULT 0,
  countMiss int NOT NULL DEFAULT 0,
  accuracy_total bigint unsigned NOT NULL,
  accuracy_count bigint unsigned NOT NULL,
  accuracy float NOT NULL,
  playcount mediumint NOT NULL,
  total_seconds_played bigint NOT NULL DEFAULT 0,
  ranked_score bigint NOT NULL,
  total_score bigint NOT NULL,
  x_rank_count mediumint NOT NULL,
  xh_rank_count mediumint NOT NULL DEFAULT 0,
  s_rank_count mediumint NOT NULL,
  sh_rank_count mediumint NOT NULL DEFAULT 0,
  a_rank_count mediumint NOT NULL,
  `rank` mediumint NOT NULL,
  level float unsigned NOT NULL,
  replay_popularity mediumint unsigned NOT NULL DEFAULT 0,
  fail_count mediumint unsigned NOT NULL DEFAULT 0,
  exit_count mediumint unsigned NOT NULL DEFAULT 0,
  max_combo smallint unsigned NOT NULL DEFAULT 0,
  country_acronym char(2) NOT NULL DEFAULT '',
  rank_score float unsigned NOT NULL,
  rank_score_index int unsigned NOT NULL,
  accuracy_new float unsigned NOT NULL,
  last_update timestamp NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  last_played timestamp NOT NULL DEFAULT CURRENT_TIMESTAMP,
  PRIMARY KEY (user_id),
  KEY ranked_score (ranked_score),
  KEY rank_score (rank_score),
  KEY country_ranked_score (country_acronym, ranked_score)
) DEFAULT CHARSET = utf8mb4 COLLATE = utf8mb4_unicode_ci;

CREATE TABLE osu_user_stats_mania (
  user_id mediumint NOT NULL,
  count300 int NOT NULL DEFAULT 0,
  count100 int NOT NULL DEFAULT 0,
  count50 int NOT NULL DEFAULT 0,
  countMiss int NOT NULL DEFAULT 0,
  accuracy_total bigint unsigned NOT NULL,
  accuracy_count bigint unsigned NOT NULL,
  accuracy float NOT NULL,
  playcount mediumint NOT NULL,
  total_seconds_played bigint NOT NULL DEFAULT 0,
  ranked_score bigint NOT NULL,
  total_score bigint NOT NULL,
  x_rank_count mediumint NOT NULL,
  xh_rank_count mediumint NOT NULL DEFAULT 0,
  s_rank_count mediumint NOT NULL,
  sh_rank_count mediumint NOT NULL DEFAULT 0,
  a_rank_count mediumint NOT NULL,
  `rank` mediumint NOT NULL,
  level float unsigned NOT NULL,
  replay_popularity mediumint unsigned NOT NULL DEFAULT 0,
  fail_count mediumint unsigned NOT NULL DEFAULT 0,
  exit_count mediumint unsigned NOT NULL DEFAULT 0,
  max_combo smallint unsigned NOT NULL DEFAULT 0,
  country_acronym char(2) NOT NULL DEFAULT '',
  rank_score float unsigned NOT NULL,
  rank_score_index int unsigned NOT NULL,
  accuracy_new float unsigned NOT NULL,
  last_update timestamp NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  last_played timestamp NOT NULL DEFAULT CURRENT_TIMESTAMP,
  PRIMARY KEY (user_id),
  KEY ranked_score (ranked_score),
  KEY rank_score (rank_score),
  KEY country_ranked_score (country_acronym, ranked_score)
) DEFAULT CHARSET = utf8mb4 COLLATE = utf8mb4_unicode_ci;

-- Medals: base_tables + 2015_08_10 slug, 2016_01_29 description, 2016_05_10 enabled/mode/image
-- nullable/unsigned id, 2025_10_10 achieved_count, 2026_01_16 client_side. osu-web seeds no rows.
CREATE TABLE osu_achievements (
  achievement_id mediumint unsigned NOT NULL,
  name varchar(40) NOT NULL,
  slug varchar(255) NOT NULL,
  description text NULL,
  image varchar(50) NULL,
  `grouping` varchar(30) NOT NULL DEFAULT '-',
  ordering tinyint unsigned NOT NULL,
  progression tinyint unsigned NOT NULL,
  quest_ordering tinyint NULL,
  quest_instructions text NULL,
  enabled tinyint(1) NOT NULL DEFAULT 1,
  mode tinyint NULL,
  achieved_count int unsigned NOT NULL DEFAULT 0,
  client_side tinyint(1) NOT NULL DEFAULT 0,
  PRIMARY KEY (achievement_id),
  KEY display_order (`grouping`, ordering),
  KEY name (name)
) DEFAULT CHARSET = utf8mb4 COLLATE = utf8mb4_unicode_ci;

CREATE TABLE osu_user_achievements (
  user_id mediumint NOT NULL,
  achievement_id mediumint NOT NULL,
  `date` timestamp NOT NULL DEFAULT CURRENT_TIMESTAMP,
  beatmap_id mediumint NULL,
  PRIMARY KEY (user_id, achievement_id),
  KEY user_id (user_id, `date`),
  KEY achievement_id (achievement_id)
) DEFAULT CHARSET = utf8mb4 COLLATE = utf8mb4_unicode_ci;

-- Account history (notes, restrictions, silences): base_tables + later alters, as on a current
-- osu-web database (SHOW CREATE TABLE osu_user_banhistory).
CREATE TABLE osu_user_banhistory (
  ban_id int unsigned NOT NULL AUTO_INCREMENT,
  user_id int unsigned NULL,
  reason varchar(8000) NULL DEFAULT 'Blanket Cheating Action',
  supporting_url varchar(255) NULL,
  ban_status tinyint(1) NULL DEFAULT 1,
  period int unsigned NOT NULL DEFAULT 0,
  `timestamp` timestamp NULL DEFAULT CURRENT_TIMESTAMP,
  banner_id int unsigned NULL,
  permanent tinyint(1) NOT NULL DEFAULT 0,
  PRIMARY KEY (ban_id),
  KEY user_id (user_id, `timestamp`),
  KEY `timestamp` (`timestamp`)
) DEFAULT CHARSET = utf8mb4 COLLATE = utf8mb4_unicode_ci;

-- Per-player beatmap play counts ("Most Played Beatmaps"): as on a current osu-web database.
CREATE TABLE osu_user_beatmap_playcount (
  user_id int unsigned NOT NULL DEFAULT 0,
  beatmap_id mediumint unsigned NOT NULL,
  playcount smallint unsigned NOT NULL,
  PRIMARY KEY (user_id, beatmap_id)
) DEFAULT CHARSET = utf8mb4 COLLATE = utf8mb4_unicode_ci;

-- Online statistics (front page graph) and lazer's multiplayer rooms (only what TomYum reads).
CREATE TABLE osu_banchostats (
  banchostats_id int unsigned NOT NULL AUTO_INCREMENT,
  users_irc smallint NOT NULL DEFAULT 0,
  users_osu mediumint NOT NULL DEFAULT 0,
  users_lazer mediumint unsigned NOT NULL DEFAULT 0,
  multiplayer_games smallint NOT NULL DEFAULT 0,
  multiplayer_games_lazer smallint NOT NULL DEFAULT 0,
  `date` timestamp NOT NULL DEFAULT CURRENT_TIMESTAMP,
  PRIMARY KEY (banchostats_id)
) DEFAULT CHARSET = utf8mb4 COLLATE = utf8mb4_unicode_ci;

CREATE TABLE multiplayer_rooms (
  id bigint unsigned NOT NULL AUTO_INCREMENT,
  user_id int unsigned NOT NULL DEFAULT 0,
  name varchar(100) NOT NULL DEFAULT '',
  starts_at timestamp NOT NULL DEFAULT CURRENT_TIMESTAMP,
  ends_at timestamp NULL DEFAULT NULL,
  type enum('playlists', 'head_to_head', 'team_versus', 'matchmaking') NOT NULL DEFAULT 'playlists',
  deleted_at timestamp NULL DEFAULT NULL,
  category enum('normal', 'spotlight', 'featured_artist', 'daily_challenge') NOT NULL DEFAULT 'normal',
  PRIMARY KEY (id)
) DEFAULT CHARSET = utf8mb4 COLLATE = utf8mb4_unicode_ci;

CREATE TABLE multiplayer_playlist_items (
  id bigint unsigned NOT NULL AUTO_INCREMENT,
  room_id bigint unsigned NOT NULL,
  owner_id int unsigned NOT NULL DEFAULT 0,
  beatmap_id mediumint unsigned NOT NULL,
  ruleset_id smallint unsigned NOT NULL,
  allowed_mods json DEFAULT NULL,
  required_mods json DEFAULT NULL,
  PRIMARY KEY (id)
) DEFAULT CHARSET = utf8mb4 COLLATE = utf8mb4_unicode_ci;

CREATE TABLE multiplayer_score_links (
  user_id int unsigned NOT NULL,
  playlist_item_id bigint unsigned NOT NULL,
  score_id bigint unsigned NOT NULL,
  PRIMARY KEY (score_id)
) DEFAULT CHARSET = utf8mb4 COLLATE = utf8mb4_unicode_ci;

-- Lazer's daily challenge queue: 2024_05_09 + 2026_08_20 description.
CREATE TABLE daily_challenge_queue (
  id bigint unsigned NOT NULL AUTO_INCREMENT,
  beatmap_id mediumint unsigned NOT NULL,
  ruleset_id smallint unsigned NOT NULL,
  allowed_mods json DEFAULT NULL,
  required_mods json DEFAULT NULL,
  `order` smallint unsigned DEFAULT NULL,
  multiplayer_room_id bigint unsigned DEFAULT NULL,
  description varchar(255) DEFAULT NULL,
  PRIMARY KEY (id)
) DEFAULT CHARSET = utf8mb4 COLLATE = utf8mb4_unicode_ci;

-- Notifications (medal unlocks among them): as on a current osu-web database.
CREATE TABLE notifications (
  id bigint unsigned NOT NULL AUTO_INCREMENT,
  notifiable_type varchar(255) NOT NULL,
  notifiable_id bigint unsigned NOT NULL,
  source_user_id int unsigned DEFAULT NULL,
  priority int NOT NULL DEFAULT 0,
  name varchar(255) NOT NULL,
  details json NOT NULL,
  created_at timestamp NULL DEFAULT NULL,
  updated_at timestamp NULL DEFAULT NULL,
  PRIMARY KEY (id)
) DEFAULT CHARSET = utf8mb4 COLLATE = utf8mb4_unicode_ci;

CREATE TABLE user_notifications (
  id bigint unsigned NOT NULL AUTO_INCREMENT,
  notification_id bigint unsigned NOT NULL,
  user_id int unsigned NOT NULL DEFAULT 0,
  is_read tinyint(1) NOT NULL DEFAULT 0,
  delivery tinyint unsigned NOT NULL DEFAULT 1,
  created_at timestamp NULL DEFAULT NULL,
  updated_at timestamp NULL DEFAULT NULL,
  PRIMARY KEY (id),
  UNIQUE KEY unique_user_notification (user_id, notification_id)
) DEFAULT CHARSET = utf8mb4 COLLATE = utf8mb4_unicode_ci;

-- Rank history (profile rank graph) and the counters naming its current column: base_tables +
-- 2022_10_18 highest rank.
CREATE TABLE osu_counts (
  name varchar(200) NOT NULL,
  count bigint unsigned NOT NULL,
  PRIMARY KEY (name)
) DEFAULT CHARSET = utf8mb4 COLLATE = utf8mb4_unicode_ci;

CREATE TABLE osu_user_performance_rank (
  user_id int unsigned NOT NULL,
  mode tinyint(1) NOT NULL,
  r0 int NOT NULL DEFAULT 0,
  r1 int NOT NULL DEFAULT 0,
  r2 int NOT NULL DEFAULT 0,
  r3 int NOT NULL DEFAULT 0,
  r4 int NOT NULL DEFAULT 0,
  r5 int NOT NULL DEFAULT 0,
  r6 int NOT NULL DEFAULT 0,
  r7 int NOT NULL DEFAULT 0,
  r8 int NOT NULL DEFAULT 0,
  r9 int NOT NULL DEFAULT 0,
  r10 int NOT NULL DEFAULT 0,
  r11 int NOT NULL DEFAULT 0,
  r12 int NOT NULL DEFAULT 0,
  r13 int NOT NULL DEFAULT 0,
  r14 int NOT NULL DEFAULT 0,
  r15 int NOT NULL DEFAULT 0,
  r16 int NOT NULL DEFAULT 0,
  r17 int NOT NULL DEFAULT 0,
  r18 int NOT NULL DEFAULT 0,
  r19 int NOT NULL DEFAULT 0,
  r20 int NOT NULL DEFAULT 0,
  r21 int NOT NULL DEFAULT 0,
  r22 int NOT NULL DEFAULT 0,
  r23 int NOT NULL DEFAULT 0,
  r24 int NOT NULL DEFAULT 0,
  r25 int NOT NULL DEFAULT 0,
  r26 int NOT NULL DEFAULT 0,
  r27 int NOT NULL DEFAULT 0,
  r28 int NOT NULL DEFAULT 0,
  r29 int NOT NULL DEFAULT 0,
  r30 int NOT NULL DEFAULT 0,
  r31 int NOT NULL DEFAULT 0,
  r32 int NOT NULL DEFAULT 0,
  r33 int NOT NULL DEFAULT 0,
  r34 int NOT NULL DEFAULT 0,
  r35 int NOT NULL DEFAULT 0,
  r36 int NOT NULL DEFAULT 0,
  r37 int NOT NULL DEFAULT 0,
  r38 int NOT NULL DEFAULT 0,
  r39 int NOT NULL DEFAULT 0,
  r40 int NOT NULL DEFAULT 0,
  r41 int NOT NULL DEFAULT 0,
  r42 int NOT NULL DEFAULT 0,
  r43 int NOT NULL DEFAULT 0,
  r44 int NOT NULL DEFAULT 0,
  r45 int NOT NULL DEFAULT 0,
  r46 int NOT NULL DEFAULT 0,
  r47 int NOT NULL DEFAULT 0,
  r48 int NOT NULL DEFAULT 0,
  r49 int NOT NULL DEFAULT 0,
  r50 int NOT NULL DEFAULT 0,
  r51 int NOT NULL DEFAULT 0,
  r52 int NOT NULL DEFAULT 0,
  r53 int NOT NULL DEFAULT 0,
  r54 int NOT NULL DEFAULT 0,
  r55 int NOT NULL DEFAULT 0,
  r56 int NOT NULL DEFAULT 0,
  r57 int NOT NULL DEFAULT 0,
  r58 int NOT NULL DEFAULT 0,
  r59 int NOT NULL DEFAULT 0,
  r60 int NOT NULL DEFAULT 0,
  r61 int NOT NULL DEFAULT 0,
  r62 int NOT NULL DEFAULT 0,
  r63 int NOT NULL DEFAULT 0,
  r64 int NOT NULL DEFAULT 0,
  r65 int NOT NULL DEFAULT 0,
  r66 int NOT NULL DEFAULT 0,
  r67 int NOT NULL DEFAULT 0,
  r68 int NOT NULL DEFAULT 0,
  r69 int NOT NULL DEFAULT 0,
  r70 int NOT NULL DEFAULT 0,
  r71 int NOT NULL DEFAULT 0,
  r72 int NOT NULL DEFAULT 0,
  r73 int NOT NULL DEFAULT 0,
  r74 int NOT NULL DEFAULT 0,
  r75 int NOT NULL DEFAULT 0,
  r76 int NOT NULL DEFAULT 0,
  r77 int NOT NULL DEFAULT 0,
  r78 int NOT NULL DEFAULT 0,
  r79 int NOT NULL DEFAULT 0,
  r80 int NOT NULL DEFAULT 0,
  r81 int NOT NULL DEFAULT 0,
  r82 int NOT NULL DEFAULT 0,
  r83 int NOT NULL DEFAULT 0,
  r84 int NOT NULL DEFAULT 0,
  r85 int NOT NULL DEFAULT 0,
  r86 int NOT NULL DEFAULT 0,
  r87 int NOT NULL DEFAULT 0,
  r88 int NOT NULL DEFAULT 0,
  r89 int NOT NULL DEFAULT 0,
  PRIMARY KEY (user_id, mode)
) DEFAULT CHARSET = utf8mb4 COLLATE = utf8mb4_unicode_ci;

CREATE TABLE osu_user_performance_rank_highest (
  user_id int unsigned NOT NULL,
  mode tinyint NOT NULL,
  `rank` int NOT NULL DEFAULT 0,
  updated_at timestamp NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  PRIMARY KEY (user_id, mode)
) DEFAULT CHARSET = utf8mb4 COLLATE = utf8mb4_unicode_ci;

-- A player's plays per month ("Play History"): as on a current osu-web database.
CREATE TABLE osu_user_month_playcount (
  user_id int unsigned NOT NULL DEFAULT 0,
  `year_month` char(4) NOT NULL,
  playcount smallint unsigned NOT NULL,
  PRIMARY KEY (user_id, `year_month`)
) DEFAULT CHARSET = utf8mb4 COLLATE = utf8mb4_unicode_ci;

-- Beatmap packs and their sets: as on a current osu-web database.
CREATE TABLE osu_beatmappacks (
  pack_id smallint NOT NULL AUTO_INCREMENT,
  url varchar(1024) NOT NULL,
  name varchar(255) NOT NULL,
  author varchar(255) NOT NULL,
  tag varchar(5) NOT NULL,
  `date` timestamp NOT NULL DEFAULT CURRENT_TIMESTAMP,
  playmode tinyint unsigned DEFAULT NULL,
  hidden tinyint(1) NOT NULL DEFAULT 0,
  no_diff_reduction tinyint(1) NOT NULL DEFAULT 0,
  PRIMARY KEY (pack_id),
  UNIQUE KEY tag (tag)
) DEFAULT CHARSET = utf8mb4 COLLATE = utf8mb4_unicode_ci;

CREATE TABLE osu_beatmappacks_items (
  item_id mediumint unsigned NOT NULL AUTO_INCREMENT,
  pack_id smallint unsigned NOT NULL,
  beatmapset_id mediumint unsigned NOT NULL,
  PRIMARY KEY (item_id),
  KEY pack_id (pack_id, beatmapset_id),
  KEY set_lookup (beatmapset_id)
) DEFAULT CHARSET = utf8mb4 COLLATE = utf8mb4_unicode_ci;

-- ppy's score processor's record of handled scores (a view over solo_scores_process_history on
-- a real osu-web database; a plain table is enough here).
CREATE TABLE score_process_history (
  score_id bigint NOT NULL,
  processed_version tinyint NOT NULL,
  processed_at timestamp NOT NULL DEFAULT CURRENT_TIMESTAMP,
  PRIMARY KEY (score_id)
) DEFAULT CHARSET = utf8mb4 COLLATE = utf8mb4_unicode_ci;

-- Passport (2016_06_01_000002_create_oauth_access_tokens_table): lazer's logins.
DROP TABLE IF EXISTS oauth_access_tokens;
CREATE TABLE oauth_access_tokens (
  id varchar(100) NOT NULL,
  user_id bigint unsigned NULL,
  client_id bigint unsigned NOT NULL,
  name varchar(255) NULL,
  scopes text NULL,
  revoked tinyint(1) NOT NULL,
  created_at timestamp NULL DEFAULT NULL,
  updated_at timestamp NULL DEFAULT NULL,
  expires_at datetime NULL,
  PRIMARY KEY (id),
  KEY oauth_access_tokens_user_id_index (user_id)
) DEFAULT CHARSET = utf8mb4 COLLATE = utf8mb4_unicode_ci;

-- Reports (the website's, and BanchoBot's !report): as on a current osu-web database.
DROP TABLE IF EXISTS osu_user_reports;
CREATE TABLE osu_user_reports (
  report_id int unsigned NOT NULL AUTO_INCREMENT,
  user_id int unsigned NOT NULL DEFAULT 0,
  reportable_type enum('user','comment','score_best_osu','score_best_taiko','score_best_fruits','score_best_mania','beatmapset_discussion_post','forum_post','beatmapset','solo_score','message','team') DEFAULT NULL,
  reportable_id bigint unsigned DEFAULT NULL,
  score_id bigint unsigned NOT NULL DEFAULT 0,
  mode tinyint NOT NULL DEFAULT 0,
  reason enum('Insults','Spam','Cheating','UnwantedContent','Nonsense','Other','MultipleAccounts','InappropriateChat') DEFAULT NULL,
  reporter_id int unsigned NOT NULL DEFAULT 0,
  comments text,
  `timestamp` timestamp NOT NULL DEFAULT CURRENT_TIMESTAMP,
  PRIMARY KEY (report_id),
  KEY `timestamp` (`timestamp`),
  KEY user_lookup (user_id),
  KEY user_report (reporter_id, user_id, reportable_type, reportable_id)
) DEFAULT CHARSET = utf8mb4 COLLATE = utf8mb4_unicode_ci;

-- Recent activity (medals, ranks, …): as on a current osu-web database.
DROP TABLE IF EXISTS osu_events;
CREATE TABLE osu_events (
  event_id int unsigned NOT NULL AUTO_INCREMENT,
  `text` varchar(1000) NOT NULL,
  text_clean varchar(8000) DEFAULT NULL,
  beatmap_id mediumint unsigned DEFAULT NULL,
  beatmapset_id mediumint unsigned DEFAULT NULL,
  user_id int unsigned DEFAULT NULL,
  `date` timestamp NOT NULL DEFAULT CURRENT_TIMESTAMP,
  epicfactor tinyint unsigned NOT NULL DEFAULT 0,
  private tinyint unsigned NOT NULL DEFAULT 0,
  legacy_score_event tinyint(1) DEFAULT NULL,
  PRIMARY KEY (event_id, `date`),
  KEY user_id_legacy_score_event (user_id, event_id, legacy_score_event)
) DEFAULT CHARSET = utf8mb4 COLLATE = utf8mb4_0900_ai_ci;

-- First places: the lazer view (beatmap_leaders) and stable's (osu_leaders per ruleset).
DROP TABLE IF EXISTS beatmap_leaders;
CREATE TABLE beatmap_leaders (
  score_id bigint unsigned NOT NULL,
  beatmap_id mediumint unsigned NOT NULL,
  ruleset_id smallint unsigned NOT NULL,
  user_id int unsigned NOT NULL,
  PRIMARY KEY (score_id),
  UNIQUE KEY beatmap_leaders_beatmap_id_ruleset_id_unique (beatmap_id, ruleset_id),
  KEY beatmap_leaders_user_id_ruleset_id_index (user_id, ruleset_id)
) DEFAULT CHARSET = utf8mb4 COLLATE = utf8mb4_unicode_ci;

DROP TABLE IF EXISTS osu_leaders;
CREATE TABLE osu_leaders (
  beatmap_id mediumint unsigned NOT NULL,
  user_id int unsigned NOT NULL DEFAULT 0,
  score_id bigint unsigned NOT NULL,
  PRIMARY KEY (beatmap_id),
  KEY user_id (user_id, score_id)
) DEFAULT CHARSET = utf8mb4 COLLATE = utf8mb4_unicode_ci;

DROP TABLE IF EXISTS osu_leaders_taiko;
CREATE TABLE osu_leaders_taiko (
  beatmap_id mediumint unsigned NOT NULL,
  user_id int unsigned NOT NULL DEFAULT 0,
  score_id bigint unsigned NOT NULL,
  PRIMARY KEY (beatmap_id),
  KEY user_id (user_id, score_id)
) DEFAULT CHARSET = utf8mb4 COLLATE = utf8mb4_unicode_ci;

DROP TABLE IF EXISTS osu_leaders_fruits;
CREATE TABLE osu_leaders_fruits (
  beatmap_id mediumint unsigned NOT NULL,
  user_id int unsigned NOT NULL DEFAULT 0,
  score_id bigint unsigned NOT NULL,
  PRIMARY KEY (beatmap_id),
  KEY user_id (user_id, score_id)
) DEFAULT CHARSET = utf8mb4 COLLATE = utf8mb4_unicode_ci;

DROP TABLE IF EXISTS osu_leaders_mania;
CREATE TABLE osu_leaders_mania (
  beatmap_id mediumint unsigned NOT NULL,
  user_id int unsigned NOT NULL DEFAULT 0,
  score_id bigint unsigned NOT NULL,
  PRIMARY KEY (beatmap_id),
  KEY user_id (user_id, score_id)
) DEFAULT CHARSET = utf8mb4 COLLATE = utf8mb4_unicode_ci;

-- osu-web database (SHOW CREATE TABLE osu_badges).
DROP TABLE IF EXISTS osu_badges;
CREATE TABLE osu_badges (
  user_id int unsigned NOT NULL DEFAULT 0,
  image varchar(255) NOT NULL,
  description varchar(255) NOT NULL,
  awarded timestamp NULL DEFAULT CURRENT_TIMESTAMP,
  url varchar(255) NOT NULL DEFAULT '',
  PRIMARY KEY (user_id, image)
) DEFAULT CHARSET = utf8mb4 COLLATE = utf8mb4_unicode_ci;

-- osu-web database (SHOW CREATE TABLE osu_favouritemaps).
DROP TABLE IF EXISTS osu_favouritemaps;
CREATE TABLE osu_favouritemaps (
  user_id int unsigned NOT NULL DEFAULT 0,
  beatmapset_id mediumint unsigned NOT NULL,
  dateadded timestamp NOT NULL DEFAULT CURRENT_TIMESTAMP,
  PRIMARY KEY (user_id, beatmapset_id)
) DEFAULT CHARSET = utf8mb4 COLLATE = utf8mb4_unicode_ci;

-- osu-web database (SHOW CREATE TABLE beatmap_owners): difficulties' mappers (guest difficulties).
DROP TABLE IF EXISTS beatmap_owners;
CREATE TABLE beatmap_owners (
  beatmap_id mediumint unsigned NOT NULL,
  user_id int unsigned NOT NULL,
  PRIMARY KEY (beatmap_id, user_id)
) DEFAULT CHARSET = utf8mb4 COLLATE = utf8mb4_unicode_ci;

-- osu-web migration 2023_04_07_051227_create_user_ircauth: IRC keys from the account settings.
DROP TABLE IF EXISTS osu_user_ircauth;
CREATE TABLE osu_user_ircauth (
  user_id int unsigned NOT NULL PRIMARY KEY,
  token varchar(50) NULL UNIQUE,
  `timestamp` timestamp NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP
) DEFAULT CHARSET = utf8mb4 COLLATE = utf8mb4_unicode_ci;

-- osu-web migrations (phpbb_zebra): friends (friend = 1) and blocks (foe = 1).
DROP TABLE IF EXISTS phpbb_zebra;
CREATE TABLE phpbb_zebra (
  user_id int unsigned NOT NULL DEFAULT 0,
  zebra_id int unsigned NOT NULL DEFAULT 0,
  friend tinyint unsigned NOT NULL DEFAULT 0,
  foe tinyint unsigned NOT NULL DEFAULT 0,
  PRIMARY KEY (user_id, zebra_id),
  KEY zebra_friend (zebra_id, friend)
) DEFAULT CHARSET = utf8mb4 COLLATE = utf8mb4_unicode_ci;
