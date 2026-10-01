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
panel); GMT is the existing one.

## How mail gets in

- The addresses are aliases of the Zoho Mail account NoCodeAPI uses (`NOCODEAPI_MAIL_URL`); mail to an
  address that isn't set up in Zoho bounces. `TICKETS_MAIL_DOMAIN` is their domain (`example.com`).
- Every minute `php artisan tomyum:tickets-fetch` (the scheduler) reads the Inbox's latest 50 emails.
  An email to a ticket address becomes a ticket with the automatic reply, or joins the ticket its
  subject names (`[Ticket #N]`, from the same sender; a closed ticket reopens). Mail from before the
  system started, the server's own addresses, bounces and automatic replies are skipped.
- NoCodeAPI's Zoho connector only gives the **first ~250 characters** of an email; the panel links to
  the whole email in Zoho Mail. (Zoho IMAP or Zoho's own API would give the whole text.)
- `TICKETS_ENABLED=false` stops the fetching.

## Replying

Replies go from the ticket's address (or `MAIL_ADDRESS` if Zoho refuses it) as
"Hello, … Regards, production support", with `[Ticket #N]` in the subject so the answer threads back.
Staff can add notes (staff only), take a ticket or add other handlers, move it to another category,
close it with a reason, and reopen it.
