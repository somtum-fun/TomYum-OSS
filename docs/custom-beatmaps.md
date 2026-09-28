# Custom beatmaps

Beatmap sets that exist only on this server. They use ids from **10,000,000**
(`Beatmap.CustomIdFloor`) for sets and difficulties, so they never collide with osu!'s. The
website's id columns stop at 16,777,215.

## Uploading (BN panel)

BN panel → **Upload a custom beatmap set**: choose an `.osz` exported from the osu! editor, up to
100 MB.

- **New set**: leave "Update custom set" empty. The set gets the next free set id, and each
  difficulty the next free beatmap id.
- **Update**: give the custom set's link or id. Difficulties keep their id (and their scores) when
  their name is the same. New ones get new ids, and ones left out are removed from the website.
  A ranked, qualified or loved set can't be updated; set it back to pending first.

What happens to an upload (`CustomBeatmapImporter`):

1. Every `.osu` in the archive is read. All difficulties need the same artist and title, and
   different names.
2. The ids go into each file (`BeatmapID`, `BeatmapSetID` in `[Metadata]`). The client and the
   website then agree on them, and the map's checksum is that of the rewritten file.
3. The set is stored with the rewritten files (`Beatmaps:Path`, default `data/beatmaps`). This keeps
   the archive for downloads, the background, and the audio as the preview (mp3 up to 20 MB). The
   `.osu` files go into the `.osu` cache, so pp works (TomYum's and ppy's score processor, which reads
   `/osu/{id}`).
4. Star rating and max combo are computed from the files. The maps are saved as **pending** and
   mirrored to the website.
5. On the website, the mapper owns the set (the account named in the files' `Creator`, else the
   uploader). It gets the unicode artist/title and a cover, and is indexed for the listing.
6. The upload is written to the staff log (`beatmapset.upload`). It shows under "Recent uploads" in
   the BN panel.

Refused: files that aren't `.osz`, sets with no difficulties, and difficulties already on the server
(e.g. a custom set downloaded from here and uploaded again as a new one).

Ranking works like any other set: BN panel sets page, or `!map` in-game.

### How the upload travels

The browser sends the file straight to TomYum, so PHP's upload limits don't apply:

1. The website signs a ticket for the upload. `POST /panel/bn/upload-ticket` returns
   `/web/tomyum/beatmapset-upload?user=&set=&expires=&signature=`, with an HMAC-SHA256 keyed by
   `SHARED_INTEROP_SECRET`, valid for an hour.
2. The browser posts the `.osz` there as form field `osz`. Caddy sends `/web/*` on `osu.` to TomYum,
   and the request body limit on `osu.` is 110 MB (Cloudflare's own limit is 100 MB).

## Where custom sets are served

| What | Where |
|---|---|
| Download (`.osz`) | `/d/{setId}` (osu!direct, and the website's download button through its mirror) |
| osu!direct search | custom sets first on the first page, matched on artist/title/creator/difficulty |
| osu!direct set info | `/web/osu-search-set.php` by set id, beatmap id or checksum |
| In-game status / leaderboards | resolved from TomYum's own beatmaps when the osu! API doesn't know the checksum |
| Thumbnails, preview | `b.<domain>/thumb/{id}.jpg`, `{id}l.jpg`, `/preview/{id}.mp3` |
| Website covers | `/uploads/default/beatmaps/{id}/covers/{size}.jpg` (cover, card, list, slimcover, each @2x, and raw) |
| Website audio preview | `/uploads/default/beatmaps/{id}/preview.mp3` (the fork's `Beatmapset::previewUrl`) |

Caddy sends `/uploads/default/beatmaps/<8+ digit id>/…` to TomYum, and official sets' covers to
assets.ppy.sh. Covers and thumbnails are cut from the background the first time they're asked for,
and kept.

## Not yet

- The stable client's own submission (editor → "Submit", `.osz2`).
- Lazer's beatmap submission.
