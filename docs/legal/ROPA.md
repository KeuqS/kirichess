# KiriChess - Record of Processing Activities (GDPR Art. 30)

> **Internal document. Not published, and not to be linked from the site.** Art. 30(4) requires it to
> be made available to a supervisory authority on request; it is not a transparency document, and
> [PRIVACY.md](PRIVACY.md) is the one players read. Everything here is derived from that policy and
> from the code it describes, so if the two ever disagree, the code is right and both are wrong.

**Last updated: 2026-10-02** - Maintained by the controller.

## Why this record exists at all

Art. 30(5) exempts organisations under 250 employees, but only where processing is occasional, poses
no risk to rights and freedoms, and covers no special categories. A live account system with
persistent user records, moderation history and authentication data is **not occasional**, so the
exemption does not apply and this record is kept. It is one file, and this is cheaper than arguing
the point later.

## 1. Controller (Art. 30(1)(a))

| | |
| --- | --- |
| **Controller** | Michiel Gekiere, a private individual resident in Belgium |
| **Contact** | privacy@kirichess.com |
| **Joint controllers** | None |
| **Representative (Art. 27)** | Not required. The controller is established in the EU |
| **Data protection officer (Art. 37)** | Not required. No large-scale systematic monitoring, no large-scale special-category processing. None appointed |

## 2. Categories of data subject (Art. 30(1)(c))

- **Registered players.** Everyone with an account. Declared to be 16 or older at signup (§9 of the
  privacy policy); no age is verified, so the real population may include minors despite the gate.
- **Unauthenticated visitors.** Reached only by IP-based rate limiting and server logs. No account,
  no stored record beyond the counting window and whatever the host retains.

No special categories of personal data (Art. 9) and no criminal-offence data (Art. 10) are processed.
Moderation records concern conduct on this service and are not criminal-conviction data.

## 3. Processing activities (Art. 30(1)(b),(c),(d),(f))

### 3.1 Account creation and authentication

| | |
| --- | --- |
| **Purpose** | Create and secure a player account; sign-in; password reset; optional two-factor authentication and optional Google sign-in |
| **Lawful basis** | Art. 6(1)(b) contract |
| **Data** | Email, PBKDF2/HMAC-SHA256 password hash, display name and its normalised form, account creation time, email-confirmation state, lockout end and failed-login counters, a cropped JPEG avatar when one is set (served at `/api/users/{id}/avatar`, removed on erasure), refresh tokens (SHA-256 hashes), API tokens (SHA-256 hashes, name, scopes, prefix, timestamps), external-login exchange codes (SHA-256 hashes), 2FA challenge tickets, TOTP shared secret and recovery codes where 2FA is enabled, Google's opaque account key where Google sign-in is used |
| **Recipients** | Resend (email delivery, active in production), Google (only if the player chooses Google sign-in, which is not yet configured), Have I Been Pwned (only if `Auth:BreachedPasswordCheckEnabled` is turned on; a 5-character SHA-1 prefix of the candidate password only, via its k-anonymity range API, never the password or the full hash), Fly.io and Neon as hosting and database |
| **Retention** | Account: life of the account, then anonymised per §6. Refresh tokens: expiry + 30 days. API tokens: revoked or expired rows, revocation/expiry + 30 days. A token lasts 90 days unless another life, from 1 to 365 days, is chosen when it is created. Exchange codes and 2FA tickets: expiry + 1 day. TOTP material: until 2FA is disabled or the account is deleted |

### 3.2 Playing and recording games

| | |
| --- | --- |
| **Purpose** | Run live games, keep the game record, compute and publish ratings and leaderboards |
| **Lawful basis** | Art. 6(1)(b) contract |
| **Data** | Full move list per game including per-ply clock state and server-measured think time, the rule configuration used, both players' account ids, start and end timestamps, Glicko-2 rating, deviation, volatility and per-game rating history. While a game is still live, each new ply is also stored as its own `GameMoves` row (the same ply record); those rows are deleted when the game ends and the finished list stays on the game |
| **Recipients** | Fly.io, Neon, Cloudflare (CDN/proxy). Display names, games and ratings are **public on the site by design** |
| **Retention** | The finished game is indefinite. A game is a two-party record; erasing one player's copy destroys the other's, and rating history is the input to every subsequent rating of both players. Identity is removed by anonymisation instead (§6). `GameMoves` rows exist only for the life of the live game and are deleted when it ends |

### 3.3 Moderation, abuse and cheat detection

| | |
| --- | --- |
| **Purpose** | Handle reports between players, issue and enforce sanctions, flag suspected engine assistance and sandbagging for human review, enforce the display-name policy |
| **Lawful basis** | Art. 6(1)(f) legitimate interest - protecting other players and the integrity of the service. Retention after an erasure request rests on Art. 17(3)(e) |
| **Data** | Reports filed and received (reporter id, reported id, reason, moderator-only note), sanctions (type, reason, moderator-only note, issuing moderator), analyser outputs derived from move-time telemetry, a per-seat count on each game of how many times that player dropped with no connection left (including returns inside 30 seconds; shown on the moderator dossier and in that player's own export; not an input to any sanction), and, on an account erased while a full account ban was active, a keyed one-way hash of that account's email address (S-3-1) |
| **Recipients** | Nobody outside the controller and the hosting/database providers. Moderator notes are never shown or exported to any player |
| **Retention** | Indefinite, and deliberately survives account deletion, so that a ban cannot be laundered by deleting and re-registering. The per-seat disconnect count is part of the game row and follows that row's retention (indefinite; §3.2). The ban-evasion hash is the exception with a fixed end: it only refuses a signup while the ban is unlifted and unexpired, and is cleared 30 days after that (docs/decisions/RETENTION.md §1) |
| **Automated decision-making** | **None within Art. 22.** The analysers are advisory and write no sanction; a human issues every sanction. Engine-assistance scores are computed on demand for a named account or named game, typically after a report, and are not stored. There is no scheduled or pool-wide engine scan. Sandbagging has a separate on-demand scan a moderator can start. |

### 3.4 Consent and age record

| | |
| --- | --- |
| **Purpose** | Evidence that the Terms and Privacy Policy were accepted, which version, and that the minimum-age declaration was made |
| **Lawful basis** | Art. 6(1)(b) contract, with Art. 7(1)/Art. 24 accountability as the reason it is recorded rather than assumed |
| **Data** | `TermsAcceptedAt`, `TermsVersion`, `AgeConfirmedAt`. **No date of birth is collected anywhere in this system** |
| **Recipients** | Hosting and database only |
| **Retention** | Life of the account |

### 3.5 Transactional and notification email

| | |
| --- | --- |
| **Purpose** | Account confirmation and password reset (security-critical, no opt-out); game-event notification mail - a challenge you sent was accepted, an opponent wants a rematch (opt-out via a one-click unsubscribe link) |
| **Lawful basis** | Art. 6(1)(b) contract |
| **Data** | Email address and the message body. For the two notification categories, also the per-account opt-out timestamp recording whether that category has been unsubscribed |
| **Recipients** | Resend |
| **Retention** | Not retained by this system beyond the account record itself. Opt-out timestamps: life of the account |
| **Status** | **Active in production.** `Email__ApiKey` is set on the deployed backend and the sending domain `mail.kirichess.com` is verified with DKIM, SPF, MX and DMARC. Still fails closed in any environment without the key, which is every local and test run |

### 3.6 Error monitoring, logging and rate limiting

| | |
| --- | --- |
| **Purpose** | Detect and diagnose faults; slow abuse and denial-of-service |
| **Lawful basis** | Art. 6(1)(f) legitimate interest |
| **Data** | Exception data and stack traces with the account **id** as log scope, never the email address; IP addresses for rate-limit counting; whatever the platform records in request logs |
| **Recipients** | Sentry, Fly.io, Better Stack. Sentry is configured `sendDefaultPii: false`. Since Phase 9e.7 it also receives distributed request traces over the same OTLP channel as its error events - request paths (which can carry a game id or an account id where the route contains one), timings and status codes, never a request body or an email address; still no profiling or session replay. Better Stack receives the unfiltered Fly log stream, including the account-id log scope |
| **Retention** | Rate-limit counters: the counting window only, in memory or Redis with an expiry, never written to a table. Logs: whatever Fly keeps, plus Better Stack's plan default (no explicit retention is set). Sentry: provider default |

### 3.7 Handling data-subject requests

| | |
| --- | --- |
| **Purpose** | Serve access/portability exports and erasure requests |
| **Lawful basis** | Art. 6(1)(c) legal obligation |
| **Data** | The data already listed above, assembled on request |
| **Recipients** | The requesting data subject only. The export deliberately withholds other players' emails and ids, reporter identities, moderator notes and all raw session credentials |
| **Retention** | The export is generated per request and never stored server-side |

### 3.8 Web push notifications

| | |
| --- | --- |
| **Purpose** | Reach a player who is still on the site, immediately, when a game event happens - a challenge accepted, a rematch offered - as a browser notification, alongside (not instead of) the mail in §3.5 |
| **Lawful basis** | Art. 6(1)(b) contract, the same basis as the notification mail it mirrors: a feature of the service the player turned on |
| **Data** | Push subscription endpoint URL, P-256 public key and auth secret; one row per browser the player enabled notifications on (`WebPushSubscriptions`) |
| **Recipients** | The player's own browser's push service - Google FCM, Mozilla, or Apple, whichever the browser selects - receives the destination endpoint and the timing of any push actually sent, never its content: the payload is encrypted to the subscription's own keys before it leaves this server. Also Fly.io and Neon as hosting and database |
| **Retention** | Until the player disables notifications for that browser, or the push service reports the subscription gone, or the account is erased. Not currently swept on a timer. Account erasure deletes the rows |
| **Status** | **Armed but not sending.** `/api/push/subscribe` accepts new subscriptions today; actually sending one requires VAPID signing keys, which are not configured in any deployed environment, so nothing has ever been pushed |

### 3.9 Direct challenges and rematch offers

| | |
| --- | --- |
| **Purpose** | Let one player invite another to a game, either via a challenge link or a rematch after a finished game |
| **Lawful basis** | Art. 6(1)(b) contract |
| **Data** | Challenge: challenger id, accepted-by id, expiry. Rematch offer: both players' ids, timestamps, the finished game id (`Challenges`, `RematchOffers`) |
| **Recipients** | Fly.io, Neon |
| **Retention** | Challenges: until 7 days after the link's own expiry, swept automatically. Rematch offers: deleted when either account is erased; otherwise they live with the finished game they belong to. Neither is a two-party historical record the way a completed game is |

### 3.10 One-off donations and the patron badge

| | |
| --- | --- |
| **Purpose** | Accept a voluntary one-off donation through Stripe Payment Links and, if the donor is signed in, attach a patron badge to their account (Terms §12) |
| **Lawful basis** | Art. 6(1)(f) legitimate interest in recording a gift the player chose to make, so the badge can be shown and later removed. Donating is optional and is not a condition of the account contract |
| **Data** | A random 32-hex handshake (`DonationReferences`) minted by the signed-in browser and returned by Stripe as `client_reference_id`; never the account id. On redemption: amount, Stripe customer id, and badge fields on the account (`PatronTier`, `PatronKind`, `PatronExpiresAt`, `StripeCustomerId`). Card details never reach this system |
| **Recipients** | Stripe Payments Europe, Limited (processor for the payment platform; independent controller for fraud, AML, card-scheme and tax). Fly.io, Neon |
| **Retention** | Handshake rows expire after 24 hours and are unusable after that; they are not currently swept from the table. Account erasure deletes the rows (cascade) and scrubs the Stripe customer id. Badge fields live with the account, or until the badge is removed |
| **Status** | **Live** when a visitor donates. Monthly subscriptions are disabled (Terms §12) |

### 3.11 Off-site database backup

| | |
| --- | --- |
| **Purpose** | Keep a copy of the database that still exists if Neon itself fails, and prove that copy restores |
| **Lawful basis** | Art. 6(1)(f) legitimate interest in being able to recover the service, and Art. 32 security of processing |
| **Data** | A full `pg_dump` of production: every table in §3, including accounts, games, reports and sanctions |
| **Recipients** | Cloudflare R2 (the object, EU jurisdiction, covered by the Cloudflare contract already filed). GitHub, only if the workflow runs: the dump is restored into Postgres on a GitHub-hosted runner to check it. The weekly schedule is off, and the job must not be started by hand, until GitHub's DPA is accepted |
| **Retention** | No dump exists today. When one does, the workflow keeps the eight newest timestamped objects and deletes older ones (`KEEP_DUMPS`, default 8) |
| **Status** | **Not running.** Bucket and credentials exist. The schedule in `.github/workflows/offsite-backup.yml` was removed on 2026-09-28 pending the GitHub DPA |

## 4. Transfers to third countries (Art. 30(1)(e))

Every processor below is a **United States** company, so all processing by them is a transfer outside
the EEA, except the two rows called out here. Stripe's contracting entity is Irish (`Stripe Payments Europe, Limited`)
and the US transfer is the onward leg to `Stripe, LLC`. Have I Been Pwned is operated by Superlative
Enterprises Pty Ltd, a company in Queensland, Australia; there is no contract, so no transfer mechanism
is claimed for that row.

| Processor | Role | Country | Mechanism |
| --- | --- | --- | --- |
| Fly.io | Application hosting | US | EU-U.S. Data Privacy Framework self-certification; SCCs in its DPA as fallback |
| Neon, LLC (a Databricks company) | Managed Postgres | US | DPF self-certification, held by parent Databricks and covering `Neon, LLC`; SCCs as fallback |
| Cloudflare | DNS, proxy, CDN, email routing, and an R2 bucket prepared for an off-site database copy. The bucket is locked to Cloudflare's EU jurisdiction and holds no dump | US company; the backup object, once it exists, is stored under the EU jurisdiction lock | DPF self-certification; SCCs as fallback. The contract already filed on 2026-08-17 is the one that covers R2; it is not a new vendor |
| Sentry | Error monitoring | US | DPF self-certification; SCCs as fallback. EU data residency is offered and **not** currently selected |
| Google | Optional OAuth sign-in | US | DPF self-certification; SCCs as fallback |
| Have I Been Pwned (Superlative Enterprises Pty Ltd) | Breached-password range query, only if `Auth:BreachedPasswordCheckEnabled` is set. A 5-character SHA-1 prefix only; the password and the full hash stay here | Australia (operator). Their privacy policy says personal information they hold is stored in a western-US Azure datacentre, and Cloudflare fronts the API | **Neither claimed.** No account and no DPA, so no DPF or SCC claim. Disclosure only, same shape as the browser push row. Off unless the flag is set; nothing in this deployment records the flag as on |
| Resend | Transactional email | US | **SCCs only** - its DPA makes no DPF claim |
| Browser push service (Google FCM, Mozilla, or Apple) | Web push delivery, if a push is ever sent | US (assumed; determined by the player's browser, not by this deployment) | **Neither claimed.** Not a contracted processor, so there is no DPA to carry a DPF or SCC claim on its behalf. Disclosure only |
| Better Stack (Logtail; contracting entity `Better Stack, Inc.`) | Fly log stream, including account-id log scope | US ingest (`us-west-2`); Delaware corporation | Live. DPF self-certification; SCCs in its DPA as fallback (DPA last updated 25 February 2025, captured 2026-09-04) |
| Stripe (contracting entity `Stripe Payments Europe, Limited`; US operations `Stripe, LLC`) | One-off donation Payment Links; card details never reach KiriChess | Ireland / US | Live only when the visitor donates. First leg is EEA. Onward transfer to `Stripe, LLC`: DPF self-certification on `Stripe, LLC`; SCCs in Stripe's DPA as fallback. Art. 28: **processor** for the payment-platform services we instruct; **independent controller** for fraud/AML (Stripe DPA last updated 18 November 2025, accepted 2026-09-04) |
| GitHub (GitHub, Inc.) | Restore-verify of an off-site database dump on a GitHub-hosted runner (§3.11) | US | **Neither claimed.** No DPA has been accepted, and the job is switched off, so no dump has been restored there. Disclosure only, until the acceptance in DPA-CHECKLIST.md |

**Google is listed above for the transfer basis, but it is not a processor.** For OAuth sign-in Google
decides its own purposes and runs its own relationship with its own user, returning only an opaque
account key; it is an **independent controller**, so Art. 28 does not apply to it and no DPA is owed
or available for this use. A transfer basis is still owed, which is why it stays in the table. The
same is true of the browser push service, for a different reason: whether sending a push even makes
the receiving service a processor or an independent controller is unsettled, and this record does not
resolve that question, it only discloses the transfer. Stripe is a processor for the payment platform
and an independent controller for fraud/AML, which is the split its own DPA writes down; the
processor half is filed below. Fly.io, Neon, Cloudflare (including the empty R2 bucket), Sentry, Resend, Stripe and Better Stack are the
rows in this table that are actual processors under a filed contract; Google, the browser push
service, Have I Been Pwned, and GitHub are not. GitHub becomes one when its DPA is accepted and the backup job is allowed to run, and not before. Have I Been Pwned is absent because this deployment holds no
account with them: the check, when the flag is set, is a query to a public API, and their own privacy
policy is the source for the operator, the Azure sentence, and the statement that a range query stores
no identifying information about whose password it was.

### Art. 28(3) processor contracts, as filed

Four of the original five were already binding through the terms accepted when each account was created; the
act recorded here is filing the applicable version so the contract is **demonstrable**, which is what
Art. 28(3) asks for. Only Fly.io required an executed signature. Stripe was accepted from the
Dashboard on 2026-09-04 and is the same dual-role shape its DPA describes. Better Stack was
captured from betterstack.com/dpa the same day; it is incorporated into their Terms of Use.

| Processor | Version filed | Date | Basis and status |
| --- | --- | --- | --- |
| Sentry (Functional Software, Inc.) | Data Processing Addendum, Revision May 2024 (published as 5.1.0, 29 May 2024) | 2026-08-17 | **Executed.** Signed by both parties via Docusign (envelope E55FD887-383F-8668-808E-A14ADF937713), Michiel Gekiere for the controller and Sentry's General Counsel for the processor, both dated 2026-08-17. This is the strongest form of the five |
| Cloudflare | Customer DPA v6.4 (3 Apr 2026) | 2026-08-17 | Incorporated into the Self-Serve Subscription Agreement, binding since account creation. Published version filed |
| Neon | Product Specific Schedule | 2026-08-17 | **In force now**, auto-incorporated on access to the Platform Services ("By accessing the Platform Services, Customer agrees to the terms of this Schedule"). This is the row that covers Neon today |
| Neon | Neon Data Processing Agreement, pre-signed 2024-11-22 | **Optional, unsigned** | A separately executable copy, pre-signed by Neon's Chief of Staff, binding "as of the date it is executed by the last signing Party". Signing it upgrades the evidence above from incorporation to execution; not signing it changes nothing, because the Schedule already binds. Its Annex 1 is pre-filled by Neon, so there is nothing to complete but the controller signature block |
| Resend (Plus Five Five, Inc.) | Data Processing Addendum, last updated 2025-12-31 | 2026-08-17 | Binding on acceptance of the Terms of Service, and the document says so in terms: "This Addendum shall become legally binding upon Customer entering into the Agreement". SCCs included, no DPF claim, so the SCCs carry the whole transfer. Filed as a complete 24-page capture with the source URL and capture timestamp on every page |
| Fly.io | Pre-signed Addendum, Doc ID 4b9598ab88a5ed962f15d2217d9a1061d8baed46 | **Executed 2026-08-17** (receipt closed 2026-08-20) | Dropbox Sign envelope sent by Fly (`mclaren@fly.io` to the controller), signed 2026-08-17 21:59 UTC, marked **completed** the same minute. That completion is Fly's receipt; a `finance@fly.io` reply is not required. Filed as `DPA-Fly.io-signed-sent-2026-08-17.pdf` |
| Stripe (Stripe Payments Europe, Limited / Stripe, LLC) | Data Processing Agreement, last updated 18 November 2025 | 2026-09-04 | Incorporated into the Stripe services agreement; accepted from the Dashboard. Processor for the payment platform; independent controller for fraud, AML, card-scheme and tax (DPA §§1–2). Belgium account → SPEL as contracting party; onward US transfer to `Stripe, LLC` under DPF, SCCs as fallback. Filed as `DPA-Stripe-2025-11-18-filed-2026-09-04.pdf` |
| Better Stack, Inc. | Data Processing Agreement, last updated 25 February 2025 | 2026-09-04 | Incorporated into the Terms of Use ("All data processing activities by the Service will be governed by the data processing agreement available at betterstack.com/dpa"). Processor for Customer Personal Data (the log stream); independent controller for Account Data. Contracting entity `Better Stack, Inc.` (Delaware). Transfers: DPF, SCCs as fallback (DPA §7). Ingest host for this deployment is `us-west-2`. Filed as `DPA-BetterStack-2025-02-25-filed-2026-09-04.pdf` |

The executed documents are held outside this repository (see `.gitignore`); this table is the record
that points to them. This is not a closed set: run the § 6 review triggers whenever any of them fires
(a new sub-processor, a new category of data, a changed retention rule, money changing hands, a DPA
being signed) rather than waiting for the one addition, Upstash on 8.4's Redis, that this document
used to name as if it were the only one coming.

## 5. Security measures (Art. 30(1)(g), Art. 32)

- Passwords stored only as PBKDF2/HMAC-SHA256 hashes (ASP.NET Core Identity default); plaintext is
  never stored or logged.
- Refresh tokens and one-time external-login codes stored as SHA-256 hashes, never in usable form.
- Optional TOTP two-factor authentication; account lockout on repeated failed sign-ins.
- Rotating refresh tokens, with retention of the rotated row specifically so that replay of a stolen
  token is detectable.
- TLS in any real deployment; Cloudflare proxy in front of both origins.
- IP-based rate limiting on authentication, game creation, move submission and matchmaking.
- Secrets held as platform secrets, not in source control.
- Logs scoped by account id rather than email; Sentry configured to send no default PII.
- Least-privilege API credentials (for example, the mail sending key is restricted to the sending
  subdomain).

**Not claimed:** no external audit, no certification, no penetration test, no bug bounty. Art. 32 asks
for measures appropriate to the risk, and this is the honest list of what exists, not an assurance.

## 6. Review

Revisit when any of the following happens, and not on a calendar: a new sub-processor is added, a new
category of data is collected, the retention job in 8a.3 changes what is deleted, money changes hands
in any direction, or a DPA is actually signed.
