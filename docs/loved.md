# Project Loved

Project Loved is part of the website: **Community → Project Loved** (`osu.<domain>/community/loved`;
`loved.<domain>` redirects there). It's sundei's Loved pages, kept simple for a private server: no
rounds, polls or seasons. Code: osu-web's `LovedController`, `App\Libraries\TomYumLoved`,
`resources/views/loved/`.

## What players and the team do

- **Ask for a map:** set link or id, mode, why. Graveyard/WIP/pending only; one request per map and
  mode (a second asker is sent to the request to review it); 5 a day. Missing osu! maps are imported.
- **Review:** for or against, with why; changeable while the request is open. Open requests are
  listed by score (fors minus againsts; the asker counts as a for).
- **The team** decides, with a reason players see: **Love it** (the set's difficulties in that mode
  become Loved straight away through `Beatmapset::love`, which tells TomYum), **Reject** (can't be
  asked for again until reopened), **Remove** (off the list; can be asked for again), **Open again**,
  or **Blacklist the map** (its open requests are removed; nobody can ask for it).
- Tabs: Requests, Loved, Rejected, Removed (team only), Blacklist.

The team is the website's **Project Loved** group: members decide for the modes they have in the
group (all modes when none are set); admins decide for every mode. The staff panel's front page links
to it for them.

## History

- 2026-10-02: a port of osu!'s Project Loved site (`example-org/private-repo`), then our own
  separate site (`example-org/private-repo`). Both retired: too many separate websites.
- 2026-10-03: sundei's in-website version (rounds and forum polls) reworked into the simple model
  above. Forum #120 (Project Loved) is left over from the polls and unused.
