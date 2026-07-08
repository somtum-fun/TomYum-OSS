# Spectating (osu!stable)

> Audience: **people** operating or contributing to TomYum. Agent-facing internals live in
> `memories/features/spectating.md`.

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

- **No `#spectator` chat channel yet.** Stable normally opens a `#spectator` tab so watchers can chat.
  This slice implements the core watch-and-relay flow; the ephemeral per-host chat channel is a
  follow-up. Watching, frames, and the join/leave notifications all work without it.
- Single-node only — like the rest of online state, spectating lives in one process for now
  (cross-node fan-out over Redis is a later slice, ADR-0003).

See also: `memories/features/spectating.md` (internals), `memories/protocols/` (packet ground truth).
