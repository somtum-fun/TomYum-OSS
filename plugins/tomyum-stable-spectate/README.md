# tomyum-stable-spectate (experimental)

TumThai (osu!lazer) players see osu!(stable) players in **Currently online** and their **friends**, and spectate
them, with TumThai's setting **Settings > Online > osu!(stable) players > "Show osu!(stable) players
(experimental)"** (off by default). As on stable, anyone online can be seen and spectated; other lazer clients,
and TumThai with the setting off, get nothing new.

It works with tomyum-spectator's `TomYumStableSpectate.cs` through Redis:

| Key | Written by | What |
|---|---|---|
| `tomyum:stable-spectate:players` | this plugin, every 2 s (10 s TTL) | the stable players online (not the bot): name, status, map, mode, mods |
| `tomyum:stable-spectate:watched` | spectator server, every 2 s (10 s TTL) | the stable players someone on lazer is watching |
| `tomyum:stable-spectate:frames` (pub/sub) | this plugin | their frame bundles, read into frames and a score |

- TumThai calls `TomYumWatchStablePlayers(bool)` on MetadataHub (a method TomYum's spectator build adds). Only
  those connections get the stable presences ("Playing on osu!(stable) (experimental)" while they play, so lazer
  offers Spectate), and only their users may spectate a stable player. A player also on lazer shows lazer's own
  presence.
- While someone watches, the server's bot spectates the stable player (they see it join), which makes their
  client stream its frames. The bot is detached only from players this plugin attached it to, and only when
  they aren't playing: osu!dan uses the bot too, and detaches it itself when an attempt ends.
- The frames go to lazer as they are (lazer's spectator frames are stable's), with the mods as lazer's plus
  Classic.

Needs Redis, `IHostSpectating` and `IHostPlayerView` (no database). Config (`plugins:tomyum.stable-spectate:*`):
`intervalSeconds` (2).

Known gaps: lazer's spectator leaderboard shows 0 / 0.00% for the stable player (it reads lazer's own scoring
numbers, which stable doesn't have; the HUD's combo and accuracy are right); a play joined midway starts from the
first frame received.

Tests: `tests/TomYum.Server.Tests/StableSpectatePluginTests.cs` (compiles the folder as the server does; frame
bundles, today's and old builds').
