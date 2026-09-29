# Discord bot

TomYum has a built-in Discord bot (`src/TomYum.Server/Discord/`). It reads the website's database,
so stable and lazer are covered alike, and needs the osu-web setup ([osu-web.md](osu-web.md)) for
everything except the chat relay and `/online`.

## What it does

| Where | What |
|---|---|
| Slash commands | `/profile`, `/rank`, `/recent`, `/best` (user and mode optional; the caller's linked account and their main mode by default), `/top` (ranking, optional country), `/leaderboard` (a beatmap id or link), `/online` (stable in-game, plus lazer/website players the website shows online), `/link`, `/unlink`. `/map` (a map's card: stars, length, BPM, settings and pp at 95/98/99/100%; optional mods like `HDDT` and a ruleset for converts), `/graph` (the 90-day rank graph as an image), `/medals` (how many, per group, the latest, some still to get), `/compare` (two players side by side, or both players' best on a map) |
| BN commands | `/rankmap`, `/lovemap`, `/qualify` (a set or beatmap link; the whole set or one difficulty). Only the caller sees a confirm card; its button makes the change as their linked account (BN, NAT, admins), which the beatmaps channel then announces |
| Staff commands | `/restrict`, `/unrestrict`, `/silence` (pick a length), `/unsilence`, `/alts`, `/verify`, `/alert`, `/user` (the account card: standing, groups, Discord, account history, same-machine accounts, IP addresses, recent plays), `/wipe` (all stable scores, one mode or every mode), `/removescore` (a stable score id from the GMT panel, or its website link), `/clearstanding` (deletes the player's account history records (everything, or only silences, restrictions or notes) so their account standing is clean; an active silence ends at once; a restricted player must be unrestricted before their restriction records can go). `/scan` (any website score, by link or id: circleguard's verdict compared with the map's top 30 here and osu!'s top 30, 5 or none; the first scan of an osu! map can take a few minutes, as osu! allows ten replay downloads a minute). Administrators only: `/addbadge` (image: an osu! badge file name such as `contributor.png`, or a link to any image, which is saved and redrawn at 86×40; plus a tooltip and an optional link) and `/removebadge` (by number, description or image; without one it lists the player's badges). The player is a name or an @mention of a linked member. Hidden from members without the *Timeout members* permission (Server Settings → Integrations → the bot changes that); the linked game account must be staff |
| Chat channel | Two-way with in-game `#osu` (which the website and lazer share). Game → Discord is batched about once a second. Discord → game only for linked players, posted as them; silenced or restricted players get a 🔇 reaction, unlinked members a 🔗 |
| Beatmaps channel | Sets ranked, approved, qualified or loved from the BN panel or BanchoBot, with the cover |
| Public log | New #1s (a score beating every earlier score by other players on a ranked/qualified/loved map), plays of at least `BigPlayPp` pp (default 300) that are the player's best on the map, medals, new players, milestones (global top 1000/500/100/50/10 and #1, #1 in their country, 1k/5k/10k/25k/50k/100k plays; only progress made while the bot watches) |
| Map of the day | A random ranked map (3–6.5★, 1–7 minutes) each UTC day in `MapOfTheDayChannelId` (default: the public log). The next day the best pass on it that day (most pp, then score) is announced, and if the winner linked their Discord they hold the **Map of the Day** role for a week (`MapOfTheDayRoleId`, else the bot makes that role) |
| Reports (staff) | Reports from the website and in-game `!report <player> <reason>` (cheating, multi, insults, spam, chat, other; five an hour at most), and suspicious plays: pp far above the player's best, a pass finished sooner after their previous one than the map is long, a #1 or big play from an account under a week old. Also a verified account logging in from the same machine as a restricted one (once per pair; blocked first logins go to the user log). Each has **Restrict**, **Silence 1 day** and **Dismiss** buttons (staff, as their linked account); the message then says who dealt with it. `ReportsChannelId`, default the user log |
| Status channel | One pinned message the bot edits every minute: online on stable, lazer and the website, playing, multiplayer rooms, plays/peak/new players over 24 hours, players, ranked sets, uptime (`StatusChannelId`). The bot's own status says how many are in-game |
| Any channel | A link to the website's maps (`/b/…`, `/beatmaps/…`, `/beatmapsets/…`) or players (`/u/…`, `/users/…`) gets a card in reply (three at most; links in `<…>` are left alone) |
| User log (staff) | Everything in the staff log about accounts (`user.*`: restrictions, silences, verifications, blocked ban evasion, …) and Discord links |
| Server log (staff) | The rest of the staff log (imports, status changes, recalculations, score removals), "server is up", and every error the server logs |

Restricted players are left out of rankings, leaderboards and announcements; they can see their
own profile privately. Only what happens while the bot runs is announced; lazer scores are held
until ppy's score processor has given them their pp (at most five minutes).

**Linking:** `/link` gives a six-character code (private, valid ten minutes). The player sends
`!link CODE` to BanchoBot, in a PM in-game or on the website, which proves they own the account.
Linked players get the Discord roles of their website groups (`admin`, `gmt`, `nat`, `bng`/`bng_limited`)
and the optional "linked" role; restricted players get none. Roles are checked every 30 minutes and
right after linking; the bot only ever adds or removes the roles it's configured with.

**Staff commands** run BanchoBot's moderation commands as the caller's linked account, with the
same permission checks, staff log and in-game effect; only the caller sees the reply.

## Setting it up

1. <https://discord.com/developers/applications> → New Application. Under **Bot**: *Reset Token*
   (keep it secret) and turn on **Message Content Intent** (for the chat relay).
2. Invite it (replace the client id with your application id):
   `https://discord.com/oauth2/authorize?client_id=<APPLICATION_ID>&scope=bot%20applications.commands&permissions=268520512`
   (view channels, send messages, embed links, read history, add reactions, manage roles). For the
   status message it also needs **Manage Messages** in that channel (to pin), and for the map of the
   day **Manage Roles** (to make and give the role), with its role above that role.
3. In the server's role list, drag the bot's role **above** the roles it gives, or Discord refuses.
4. Settings (env file, e.g. `deploy/production/tomyum.env`; ids via Developer Mode → right-click → Copy ID):

```
TOMYUM_DISCORD__TOKEN=...
TOMYUM_DISCORD__GUILDID=...
TOMYUM_DISCORD__CHATCHANNELID=...
TOMYUM_DISCORD__BEATMAPSCHANNELID=...
TOMYUM_DISCORD__PUBLICLOGCHANNELID=...
TOMYUM_DISCORD__SERVERLOGCHANNELID=...
TOMYUM_DISCORD__USERLOGCHANNELID=...
TOMYUM_DISCORD__ADMINROLEID=...
TOMYUM_DISCORD__GMTROLEID=...
TOMYUM_DISCORD__NATROLEID=...
TOMYUM_DISCORD__BNROLEID=...
# Optional
TOMYUM_DISCORD__LINKEDROLEID=
TOMYUM_DISCORD__STATUSCHANNELID=
TOMYUM_DISCORD__REPORTSCHANNELID=      # default the user log
TOMYUM_DISCORD__CIRCLEGUARDCHANNELID=  # circleguard flags; default the reports channel
TOMYUM_DISCORD__MAPOFTHEDAYCHANNELID=  # default the public log
TOMYUM_DISCORD__MAPOFTHEDAYROLEID=     # default: the bot makes a "Map of the Day" role
TOMYUM_DISCORD__BIGPLAYPP=300
TOMYUM_DISCORD__CHATCHANNEL=#osu
TOMYUM_DISCORD__SITEURL=        # default https://osu.<Server:Domain>
TOMYUM_DISCORD__AVATARURL=      # default https://a.<Server:Domain>
```

An empty id turns that part off. Restart the server; its log says
`Discord: connected to <server> as <bot>; 26 commands registered.`

## For contributors

- `DiscordBotService` is the only class that talks to Discord.Net; everything else goes through
  `IDiscordGateway` (post, edit, pin, roles, the bot's status), so it's tested with a fake (`tests/.../Discord`).
- Buttons carry what they do in their id (`status:…`, `mod:…`, `cancel`); `DiscordInteractions` runs them.
- `DiscordWatch` (reports, suspicious plays, milestones) rides on the announcer's score feed;
  `DiscordDaily` runs the status message and the map of the day. What they need to remember is in
  Redis (`DiscordState`, keys `tomyum:discord:*`).
- Login IPs for `/user` are kept by `LoginIps` (stable logins; Redis, last 10 per player, 180 days).
- `OsuWebCommunity` (Persistence) holds the website queries; `DiscordAnnouncer.TickAsync` is one
  pass of the feeds (scores by id, medals by time, users by id, the staff log by id).
- The relay listens to `ChatLog.Recorded`; messages that came from Discord carry
  `ChatOrigin.Discord` and aren't sent back. `!link` messages are never relayed.
- Links live in TomYum's `discord_links` table (migration `DiscordLinks`).
