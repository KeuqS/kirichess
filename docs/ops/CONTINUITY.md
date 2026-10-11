# Continuity - if the maintainer disappears

KiriChess is run by one person: Michiel Gekiere, Belgium, `privacy@kirichess.com` (the controller
named in `docs/legal/PRIVACY.md` section 1). There is no company, no second admin, and no DPO. This
document is the inventory a successor - or the maintainer's own future self, after a long gap -
would need to either keep `kirichess.com` running or wind it down deliberately instead of by
accident.

**This document is split in two, on purpose.** What follows is the *structure*: which providers are
load-bearing, what each one does, what breaks if it lapses, and how to think about the secrets
involved. It contains no account credentials, no recovery emails, and no payment details, and it
never should - see "Owner-filled placeholders" at the end for exactly what is missing here and why.
The access half - recovery email per provider, the payment card, where the password vault lives, a
named emergency contact - exists only in a copy the maintainer keeps in a password manager or a
sealed document, **never committed to this repository**. `.claude/rules/security.md` already treats
any secret-shaped string that lands in a tracked file as a live incident, not a style nit; this file
is deliberately built to never be that string.

## 1. The providers, and what a successor is actually looking at

| Provider | What it does | What breaks if it lapses | For a successor |
| --- | --- | --- | --- |
| **Domain registrar** (kirichess.com) | Owns the name itself | Everything - no domain, no site, no email on that domain | **Must obtain.** Nothing about this repository recreates a lost domain; it has to be transferred or the registrar account regained |
| **Cloudflare** | DNS, edge proxy, and (since 10c.1) the R2 bucket holding off-site Postgres dumps | DNS resolution and the edge proxy in front of Fly; also the only off-site copy of production data outside Neon | **Must obtain**, or rebuild the zone at a new DNS provider once the domain itself is controlled. If Cloudflare access is lost before a fresh R2 dump has been pulled down elsewhere, the off-site backup goes with it - see the open question at the end of section 3 |
| **Neon** (Postgres) | The production database: accounts, games, ratings, everything | All player data. Neon PITR is the primary backup path (`DEPLOYMENT.md` section 5) | **Must obtain** if at all possible - PITR only restores *into* the same Neon project. If the Neon account itself is unrecoverable, the weekly off-site dump in R2 (`DEPLOYMENT.md` section 5.3) is the fallback, restorable into a fresh Postgres anywhere |
| **Fly.io** | Compute: `kirichess-api`, `kirichess-ui`, `kirichess-log-shipper`, plus staging twins | The running site, but not the data | **Can rebuild.** The app definitions are the committed `fly.*.toml` files; a new Fly account (or a non-Fly host via `docker-compose.prod.yml`, see section 4) can stand the same software back up. What must move is the database and the secrets, not the compute definition |
| **GitHub** | Repository host, Actions (CI, the scheduled backup/drill/probe workflows), repository secrets | Code hosting, the automated backup/drill schedule, CI | **Can rebuild** for the code itself - Apache-2.0 (`LICENSE`, `NOTICE`) permits recloning and re-hosting from any existing clone. The scheduled workflows and their secrets would need to be recreated on the new host |
| **Stripe** | One-off donations only - no subscription, no paid tier (`docs/legal/TERMS.md` section 12) | Nothing player-facing; donations pause | **Droppable.** A successor can obtain the account for payout continuity, or drop it and re-enable donations later under a new account. Nothing recurring breaks either way |
| **Resend** | Transactional email (confirmation, password reset) | Email falls back to `LoggingEmailSender` (fails closed, logs instead of sending) - registration and password reset degrade but do not hard-fail | **Can rebuild** elsewhere. This is the code-cheap, DNS-expensive tradeoff described in `DEPLOYMENT.md` section 8 - a new provider needs its own SPF/DKIM/DMARC observation window measured in weeks |
| **Sentry** | Error visibility, not user-facing | Nothing player-facing; errors just go unreported | **Droppable.** A new project and a new DSN costs nothing but blind spots until it is set up again |
| **Better Stack** | Uptime/threshold alerting plus log retention (`infra/betterstack/`) | Alerting and the log destination beyond Fly's own short buffer | **Rebuildable.** `terraform apply` against a new account recreates every resource in `infra/betterstack/main.tf` |
| **Google Cloud** (OAuth) | Google sign-in | Nothing today - per `PROVIDER-ACCOUNTS.md`, the client id/secret are not yet set and the feature is inert | **Droppable or reobtainable** at leisure; not load-bearing yet |

## 2. Secrets: what needs regenerating versus what needs the database (B-1-3)

`Jwt__Key`, like every Fly secret, is **write-only** - `fly secrets list` shows names, never values,
so it cannot be read back even with full account access (`PROVIDER-ACCOUNTS.md:23`). That sounds
alarming and is not, once each secret is classified by what losing it actually costs:

- **`Jwt__Key` is regenerate-and-move-on.** Generate a fresh one (`openssl rand -base64 48`, at
  least 32 bytes) and set it with `fly secrets set --app kirichess-api Jwt__Key="..."`. Refresh
  tokens are opaque random values compared against a SHA-256 hash, not signed by this key
  (`src/KiriChess.Server/Data/Entities/RefreshToken.cs:8`), so a new signing key invalidates only the
  15-minute access tokens currently in flight (`JwtOptions.ExpiryMinutes`, per
  `docs/decisions/AUTH.md` section 1.1). Every signed-in user simply re-authenticates on their next
  silent refresh. This is an inconvenience, not data loss, and not a mass-logout catastrophe.
- **The Data Protection key ring is not in this category at all.** Since 10a.1 it is persisted
  unconditionally to Postgres (`DataProtectionKeys` table, `src/KiriChess.Server/Program.cs:177-179`),
  so it is restored automatically with the database - by Neon PITR or by the R2 off-site dump,
  whichever recovery path is used. There is nothing separate to regenerate or protect for this one;
  it travels with the data.
- **Provider API tokens** (the Neon management key, R2 access keys, `Metrics__ScrapeToken`,
  `Turnstile__SecretKey`, `Email__ApiKey`, the Sentry DSN, Google OAuth client secret, the GitHub
  Actions deploy tokens `FLY_API_TOKEN_API` and `FLY_API_TOKEN_UI`) are **re-minted from each provider's own console**, not recovered from
  anywhere. None of them is readable back out of Fly or GitHub secrets once set. If the provider
  *account* itself is lost rather than just the value, re-minting is not possible until account
  access is regained - at which point this collapses back into section 1's "must obtain / can
  rebuild" table for that provider.
- **`ConnectionStrings__Postgres`** is a live database credential, not a static secret - read it from
  the Neon console or `neonctl` while account access exists (see `DEPLOYMENT.md` section 2.1 for the
  exact command shape). If Neon access itself is gone, section 4 below is the path: restore the R2
  dump into a fresh Postgres and mint a new connection string there.

## 3. Keeping the hosted instance alive versus forking (B-1-4)

`docs/audits/O.md` answers a different question: a stranger wants to run **an** instance of the
software, under a different name, with no continuity of players or data. That path is well documented
(README's forkability guidance, `docker-compose.prod.yml`, the licence stack) and does not need this
section.

This section is for the other case: **a trusted person wants to keep *this* instance running** - the
same domain, the same players' accounts, the same donation history - not start a fresh one. That
needs concrete transfers, not just a clone of the repository:

| What | To keep the hosted instance alive | To fork instead |
| --- | --- | --- |
| **Domain** (kirichess.com) | Must be transferred to the successor | Not needed - a fork picks its own name and domain, and must not use "KiriChess" per `NOTICE`'s trademark paragraph |
| **DNS / Cloudflare zone** | Must be transferred or recreated pointed at the same domain | Not needed |
| **Neon project** | Must be obtained, or the R2 off-site dump restored, to keep the actual players and games | Not needed - a fork starts with an empty database |
| **Fly apps** (or an equivalent host) | Can be rebuilt from the committed `fly.*.toml` / `docker-compose.prod.yml` against a new account, once the data above is in hand | Same - this is the forkable part in both cases |
| **Stripe** | Obtain for payout continuity, or drop and start fresh - donations are one-off, nothing recurring is orphaned either way | Not needed |
| **Resend** | Can rebuild under a new account; costs the DNS/deliverability weeks described in `DEPLOYMENT.md` section 8 | Same, on the fork's own domain |
| **Sentry** | Droppable; a new project costs nothing but temporary blind spots | Droppable |
| **Better Stack** | Rebuildable via `terraform apply`; not required to keep serving traffic, only to keep watching it | Same |

For the clean-fork path in the right-hand column, point at `docs/audits/O.md` rather than
duplicating it here. For the data a keep-alive successor needs, point at the R2 off-site dump
(`DEPLOYMENT.md` section 5.3) alongside whatever direct Neon access can be obtained.

## 4. Moving off Fly or off Neon (I-10-1)

There is currently no written procedure for "the front-door provider evicted me" - a billing lapse,
a ToS action, an account lockout, or a price change the hobby budget cannot absorb. The answer on the
merits is yes, this is portable (`docker-compose.prod.yml` is the latent target, already used for
local development and by the E2E suite), but without a written sequence a provider eviction turns a
weekend migration into an improvisation performed while the site is down.

**Target:** `docker-compose.prod.yml` on any VPS or container host that can run two Docker images and
reach a Postgres instance - the same file `DEPLOYMENT.md` section 6 already documents for running the
production images locally.

**Steps:**

1. **Get a Postgres reachable from the new host** - a fresh Neon project, another managed Postgres,
   or the `db` service `docker-compose.prod.yml` itself defines.
2. **Restore into it from the off-site dump**, the tested disaster path that needs no Neon
   credentials at all, only the R2 secrets:
   ```bash
   RESTORE_DATABASE_URL='postgresql://...' bash scripts/offsite-backup.sh restore
   ```
   (`DEPLOYMENT.md` section 5.3). This is the path to use if Neon itself is the thing being left, not
   just Fly.
3. **Build the two images** `docker-compose.prod.yml` defines (`Dockerfile.backend`,
   `Dockerfile.frontend`) on or for the new host.
4. **Set the same six must-have secrets** any fresh deployment needs (the list `docs/audits/O.md`
   finding O-5-3 and this file's section 2 both name): `Jwt__Key`, `ConnectionStrings__Postgres`,
   `Frontend__BaseUrl`, `NEXT_PUBLIC_API_BASE`, `NEXT_PUBLIC_SITE_URL`, `Cors__AllowedOrigins__0`.
5. **Run the migration gate explicitly before serving traffic**:
   ```bash
   docker compose -f docker-compose.prod.yml --profile migrate run --rm migrate
   ```
   This replaces what Fly's `release_command` (`dotnet KiriChess.Server.dll --migrate`,
   `fly.backend.toml:27`) does automatically as part of a `fly deploy` - on a non-Fly host nothing
   runs migrations for you, so this step has to be run by hand, and by hand *before* `up`.
6. **Re-express the private metrics port.** `fly.backend.toml`'s `[metrics]` block (lines 93-95) is
   what lets Fly's own Prometheus scraper reach port 9091 without a bearer token, because Fly's
   private network boundary makes that port unreachable from outside. A non-Fly host has no
   equivalent private network, so `/metrics` there is reachable only through the public port and
   `Metrics__ScrapeToken` - the same fail-closed gate that already protects the public port on Fly.
7. **Repoint DNS** at the new host once it is confirmed working.
8. **If email also moves** - a new Resend account, or a different provider - budget the DMARC
   `p=none` observation window again (`DEPLOYMENT.md` section 8.2), per this file's own note on email
   portability in `DEPLOYMENT.md` section 8: a provider change there is weeks of DNS and
   deliverability work, not a code change.

**Open question, not resolved by this document:** if Cloudflare is lost at the same time as Fly (a
single-provider-family failure rather than "just Fly"), the R2 bucket holding the off-site dump goes
with it, and step 2 above has nothing to restore from unless a copy of the dump was already pulled
down somewhere else beforehand. Nothing in this repository currently keeps a copy of the R2 dump
outside Cloudflare's own infrastructure. Whether that is an acceptable residual risk at this project's
size, or worth a second, independent copy, is left open here rather than answered speculatively.

## 5. Owner-filled placeholders

Everything below is intentionally blank in this repository and must stay that way. Filling these in
is the owner's task, in a password manager or a sealed document kept outside version control - never
in a commit, never in an issue, never in a chat transcript that could be logged.

- [ ] Recovery email / phone number on file for: domain registrar, Cloudflare, Neon, Fly.io, GitHub,
      Resend, Sentry, Better Stack, Stripe, Google Cloud.
- [ ] The payment card (or cards) billing Fly, Neon, the domain registrar, and any paid tier of the
      above - which card, and who else can authorize a charge or a change to it if the owner cannot.
- [ ] Where the password manager or sealed document holding the above actually lives, and who else
      knows how to reach it.
- [ ] A named contact who should be notified first if the maintainer is unreachable for an extended
      period, and what that person is and is not authorized to do (post to Discord per
      `docs/ops/INCIDENTS.md`'s step 0 versus, at the far end, requesting an account transfer from a
      provider).

This list is deliberately a checklist, not a form to fill in inline: ticking a box here means "done in
the private copy," never "filled in here."
