# Discord bot

TomYum has a built-in Discord bot (`src/TomYum.Server/Discord/`). It reads the website's database,
so stable and lazer are covered alike, and needs the osu-web setup ([osu-web.md](osu-web.md)) for
everything except the chat relay and `/online`.

## What it does

| Where | What |
|---|---|
| Slash commands | `/profile`, `/rank`, `/recent`, `/best` (user and mode optional; the caller's linked account and their main mode by default), `/top` (ranking, optional country), `/leaderboard` (a beatmap id or link), `/online` (stable in-game, plus lazer/website players the website shows online), `/link`, `/unlink` |
| Staff commands | `/restrict`, `/unrestrict`, `/silence` (pick a length), `/unsilence`, `/alts`, `/verify`, `/alert`. The player is a name or an @mention of a linked member. Hidden from members without the *Timeout members* permission (Server Settings → Integrations → the bot changes that); the linked game account must be staff |
| Chat channel | Two-way with in-game `#osu` (which the website and lazer share). Game → Discord is batched about once a second. Discord → game only for linked players, posted as them; silenced or restricted players get a 🔇 reaction, unlinked members a 🔗 |
| Beatmaps channel | Sets ranked, approved, qualified or loved from the BN panel or BanchoBot, with the cover |
| Public log | New #1s (a score beating every earlier score by other players on a ranked/qualified/loved map), plays of at least `BigPlayPp` pp (default 300) that are the player's best on the map, medals, new players |
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
   (view channels, send messages, embed links, read history, add reactions, manage roles).
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
TOMYUM_DISCORD__BIGPLAYPP=300
TOMYUM_DISCORD__CHATCHANNEL=#osu
TOMYUM_DISCORD__SITEURL=        # default https://osu.<Server:Domain>
TOMYUM_DISCORD__AVATARURL=      # default https://a.<Server:Domain>
```

An empty id turns that part off. Restart the server; its log says
`Discord: connected to <server> as <bot>; 10 commands registered.`

## For contributors

- `DiscordBotService` is the only class that talks to Discord.Net; everything else goes through
  `IDiscordGateway` (post, sync roles), so it's tested with a fake (`tests/.../Discord`).
- `OsuWebCommunity` (Persistence) holds the website queries; `DiscordAnnouncer.TickAsync` is one
  pass of the feeds (scores by id, medals by time, users by id, the staff log by id).
- The relay listens to `ChatLog.Recorded`; messages that came from Discord carry
  `ChatOrigin.Discord` and aren't sent back. `!link` messages are never relayed.
- Links live in TomYum's `discord_links` table (migration `DiscordLinks`).
