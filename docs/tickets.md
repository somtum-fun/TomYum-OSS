# Support tickets

Email to the server's addresses becomes tickets in the staff panel (**Tickets**, `/panel/tickets`),
osu-web `App\Libraries\TomYumTickets`.

| Address | Category | Who handles it |
|---|---|---|
| support@ | General support | SPT (Support Team) |
| accounts@ | Account support | GMT |
| appeal-review@ | Appeal review | GMT |
| abuse@ | Abuse report | admins, developers |
| privacy@ | Privacy request | admins, developers |
| dmca@ | DMCA request | admins, developers |
| tournaments@ | Tournaments | TC (Tournament Committee) |

Admins and developers see every category. SPT and TC are website groups (give them in the admin
panel); GMT is the existing one. `accounts@` is also the address the website shows for account
problems (a disabled account, changing the email).

The addresses are aliases of one mailbox, on the domain `TICKETS_MAIL_DOMAIN` (by default the
site's, without `osu.`). Mail to an address that isn't set up as an alias bounces. The mailbox is
read one of two ways.

## IMAP (any provider)

`TICKETS_IMAP_HOST` (e.g. Spacemail's `mail.spacemail.com`) in `osu-web.env` turns it on. The login is
the SMTP one (`MAIL_USERNAME`, `MAIL_PASSWORD`) unless `TICKETS_IMAP_USERNAME` / `TICKETS_IMAP_PASSWORD`
are set; `TICKETS_IMAP_PORT` (993, TLS) and `TICKETS_IMAP_FOLDER` (`INBOX`) rarely need changing.

- The panel gets the **whole email**: the plain-text part (or the HTML one as text), without
  attachments and without the earlier messages a reply quotes under it.
- The folder is opened read-only: nothing is marked read or moved, so the mailbox can still be used
  in webmail. The first run only notes the newest message; mail from then on becomes tickets.
- Replies go out through the site's mail server (`MAIL_*`, SMTP).

## Zoho through NoCodeAPI

Without `TICKETS_IMAP_HOST`, the Zoho Mail account NoCodeAPI uses (`NOCODEAPI_MAIL_URL`) is read and
replies are sent through it. NoCodeAPI's Zoho connector only gives the **first ~250 characters** of
an email; the panel links to the whole email in Zoho Mail. (Zoho's IMAP, `imap.zoho.com`, gives the
whole text the other way.)

## Fetching

Every minute `php artisan tomyum:tickets-fetch` (the scheduler) reads the new mail. An email to a
ticket address becomes a ticket with the automatic reply, or joins the ticket its subject names
(`[Ticket #N]`, from the same sender; a closed ticket reopens). Mail from before the system started,
the server's own addresses, bounces and automatic replies are skipped. `TICKETS_ENABLED=false` stops
the fetching.

## Replying

Replies go from the ticket's address (or `MAIL_ADDRESS` if the mail server refuses it; by SMTP the
ticket's address is then the one to reply to) as "Hello, … Regards, <server name> support", with `[Ticket #N]` in the
subject so the answer threads back. Staff can add notes (staff only), take a ticket or add other
handlers, move it to another category, close it with a reason, and reopen it.
