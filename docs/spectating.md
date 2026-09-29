# Spectating (osu!stable)

> Audience: **people** operating or contributing to TomYum.

TomYum supports osu!stable **spectating** — one player watches another play in real time, with the
watched player's gameplay frames relayed to every spectator. Multiple people can watch the same host
and see each other as "fellow spectators".

## How it works

When you click **Spectate** on an online player, your client sends `START_SPECTATING`. The server:

1. Records you as a spectator of that **host** and remembers who you're watching.
2. Tells the host **SPECTATOR_JOINED** — that's the host client's cue to start streaming its gameplay
   frames.
3. Introduces you and any existing spectators to each other (**FELLOW_SPECTATOR_JOINED** both ways),
   so every spectator's client can render the full watcher list.

While the host plays, its client streams `SPECTATE_FRAMES`; the server relays each bundle **verbatim**
to every spectator (`SPECTATE_FRAMES`). Stopping (`STOP_SPECTATING`), or logging out, tells the host
**SPECTATOR_LEFT** and the remaining spectators **FELLOW_SPECTATOR_LEFT**. If you don't have the
host's beatmap, your client sends `CANT_SPECTATE`; the server relays **SPECTATOR_CANT_SPECTATE** to
the host and all spectators (the client uses it to show a "downloading…" state), and you re-send
`START_SPECTATING` once the map finishes downloading.

The host stops streaming automatically when its last spectator leaves.

## The `#spectator` chat channel

While a player is being watched, the server opens an ephemeral **`#spectator`** channel shared by the
host and everyone watching them — the same tab osu!stable shows during spectating. It behaves like the
per-match `#multiplayer` channel: it isn't a permanent, server-wide room, and it never shows up in your
channel list on login. Each host has its own instance.

- The **host** is auto-joined to its own `#spectator` channel when its **first** spectator arrives, and
  is kicked out (the tab closes) when its **last** spectator leaves.
- Every **spectator** joins `#spectator` when they start watching and is kicked when they stop (or log
  out). Switching to a new host moves you from the old host's channel to the new one.
- Anyone in the channel — host or spectator — can chat, and the message fans out to all the other
  members. `!`commands work here too.

You never join or leave `#spectator` by hand; it's driven entirely by who you're spectating.

## Packet map

| You do | Client → server | Server → recipients |
|--------|-----------------|---------------------|
| Start watching B | `START_SPECTATING(16)` = i32 host id | `SPECTATOR_JOINED(13)` → host; `FELLOW_SPECTATOR_JOINED(42)` between you and existing spectators |
| Stop / log out | `STOP_SPECTATING(17)` | `SPECTATOR_LEFT(14)` → host; `FELLOW_SPECTATOR_LEFT(43)` → remaining spectators |
| Host streams play | `SPECTATE_FRAMES(18)` = raw frame bundle | `SPECTATE_FRAMES(15)` = same bundle → every spectator |
| Missing the map | `CANT_SPECTATE(21)` | `SPECTATOR_CANT_SPECTATE(22)` → host + all spectators |

Switching hosts detaches you from the old host first. Re-sending `START_SPECTATING` for the host
you're already watching just re-announces you (no duplicate).

## Teardown

Spectate state is cleaned up on **logout**: if you were watching someone, you're removed from their
spectator list (host gets `SPECTATOR_LEFT`); if you were a host, all your spectators are detached so
none is left pointing at a gone session. (There is no idle-session reaper yet — logout is the only
teardown path today; when a reaper lands it will share the same teardown.)

## Current limitations

- Single-node only — like the rest of online state, spectating lives in one process for now
  (cross-node fan-out over Redis is a later slice, ADR-0003).
- Closing the `#spectator` tab by hand (without stopping spectating) isn't specially handled — the
  channel is managed by the spectate flow, not by manual channel join/part, exactly like `#multiplayer`.

See also: `src/TomYum.Server/Bancho/SpectatorManager.cs` (internals).
