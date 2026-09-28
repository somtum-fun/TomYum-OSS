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

## Submitting from the game

Mappers submit their own sets from the editor, as on osu!. The rules are ppy's
(`BeatmapSubmissions`):

- Only the set's owner can change it. Nobody can once it's ranked, qualified or loved.
- Not while restricted or silenced.
- 1 to 128 difficulties.
- A limited number of sets in progress: 4, plus one per ranked set (up to 8). Supporters get 8,
  up to 20. Administrators have no limit.
  - Sets untouched for four weeks don't count, but coming back to one needs a free slot.
  - Ids handed out and never used stop counting after a day. They stay taken, so no id is ever
    handed out twice.

Every submission first gets ids for the set and each difficulty. The client writes them into its
files and uploads the package. TomYum keeps the files **exactly** as sent, because the client knows
its maps by their checksums. So every difficulty must carry one of the set's ids, and every id must
have its difficulty. Owners and handed-out ids are kept in `custom_beatmapsets` and
`custom_beatmap_slots`. Uploads from the BN panel are recorded there too, with the mapper as owner,
so they can be updated from the game later.

Submissions, updates and refusals go to the staff log (`beatmapset.upload`) and show under "Recent
uploads" in the BN panel.

### osu!stable

Editor → **Submit**. Nothing to set up: the client sends it to the server it's connected to.

| Step | Endpoint | What happens |
|---|---|---|
| Ids | `GET /web/osu-osz2-bmsubmit-getid.php` | Answers `0`, the set id, the difficulty ids in the editor's order, `1` (always a full upload), sets left. Errors: `1` not yours, `3` ranked, `5` anything else, with the reason. |
| Upload | `POST /web/osu-osz2-bmsubmit-upload.php` | The encrypted `.osz2`, checked against the MD5 the editor sends, then decrypted (`Osz2Package`) and stored as an `.osz`. |
| Description | `POST /web/osu-osz2-bmsubmit-post.php` | "Finished" (pending) or a work in progress, and the description, which becomes the set's description post on the website. |

TomYum never asks the editor for a patch (bsdiff) against an earlier `.osz2`: it always wants the
whole package, as osu! does when it has no earlier version.

### osu!(lazer)

Editor → **Submit** (with LazerAuthlibInjection, docs/lazer.md). Leave the ruleset's "Beatmap
Submission Service Url" empty: lazer then uses `{API URL}/beatmap-submission`, which Caddy sends to
TomYum.

| Step | Endpoint |
|---|---|
| Ids | `PUT /beatmap-submission/beatmapsets`, with difficulties to keep and how many to create, and WIP or Pending. The answer includes the current files with their SHA-256. |
| Upload | `PUT /beatmap-submission/beatmapsets/{id}` (the whole `.osz`), or `PATCH` with only the changed and removed files |

Lazer's login is checked the way ppy's services check it. The token must be signed with the
website's Passport key (`OsuWeb:OAuthPublicKeyPath`, default `oauth-public.key`; production mounts
the website's). It must be for lazer's client (`OsuWeb:LazerClientId`, default 5), and still be
valid in `oauth_access_tokens`.

Not supported yet: guest difficulties uploaded on their own (lazer's
`PATCH …/beatmaps/{id}`). The owner includes them in the set instead.

### Dropping a difficulty

A difficulty left out of a new version stops being playable here (not submitted). It's also removed
from the website. Its scores stay in the database.
