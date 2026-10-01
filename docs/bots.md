# Bot accounts

Chat bots work the way they do on osu!: an account in the **Chat Bots** group (`bot`) that talks over
IRC, in the same channels and private messages as the game, the website, lazer and Discord.

## Making one

Admin or developer: **Staff panel → Bots → Make a bot** (`/panel/bots`). Give it a name, an owner (a
player's name or id; you by default), optionally a website password (random when empty) and what it
does. The panel shows the bot's first IRC password and, when it made one, its website password once.

The bot gets:

- the `bot` group as its main group (osu-web's `User::isBot`), so the website shows it as a bot and
  doesn't ask it for plays before posting in the forum;
- a placeholder email `bot<id>@bots.invalid` (emails are unique; nobody reads a bot's mail);
- a row in `tomyum_bot_accounts` (owner, who made it, description);
- an IRC key in `osu_user_ircauth`, which is its IRC password.

## What a bot can do

| | |
|---|---|
| IRC (`irc.<domain>:6667`, nick = its name with `_` for spaces, password = its IRC key) | yes |
| Sign in to the website (join a team, post in the forum) | yes; new sessions are confirmed with a code sent to its **owner's** email |
| Log in to osu!stable | no: refused with "Bot accounts can't log in to the game. Connect over IRC instead." (`BanchoLoginHandler.BotNotice`) |
| Log in to lazer (osu-web `/oauth/token`) | no |
| Submit scores (stable `osu-submit-modular`, lazer solo and multiplayer score tokens) | no |

## Managing one

The owner (from the user menu, **My bots**), admins and developers open the bot's page
(`/panel/bots/{id}`) to:

- see, reset (random) or set the IRC password (8–50 characters, no spaces);
- rename it (staff-style rename: no monthly limit; the old name is kept in its history);
- upload or remove its avatar and profile banner;
- edit its me! page (BBCode);
- set its website password;
- change what it does; admins and developers can also change the owner.

Every change is in the staff log (`bot.*`). BanchoBot is the server's own and isn't managed here. The
admin panel's account page links to a bot's settings, and its login history shows refused game logins
as "Refused (bot)".

## Using a bot with an OAuth client

osu-web lets a client owned by a bot account act as it (`delegate` scope, client credentials), as osu!
does for chat bots. That's how, for example, QAT's in-game messages can be sent (docs/qat.md).
