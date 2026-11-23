# The databases the website, TomYum and ppy's services use: the only ones their account (osuweb) has rights on.
# A new one (a plugin's own database, a new osu-web connection) goes here, then run mysql-narrow-grants.sh.
APP_DATABASES="osu osu_charts osu_chat osu_mp osu_store osu_updates tomyum bpy_import"
