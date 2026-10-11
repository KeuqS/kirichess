# Provider accounts - the DH.5 runbook

> **Prepared 2026-08-17 by reading live state, not the roadmap.** Every "already done" line below was
> confirmed against the deployed apps and public DNS, and the command that confirmed it is given so
> you can re-run it rather than trust it. The DPA half lives in [DPA-CHECKLIST.md](../legal/DPA-CHECKLIST.md);
> this document is the accounts and the keys.

## The headline: DH.5 is much smaller than it reads

DH.5 says "four signups and a payment card". That was true when it was written. It is not true now.
Three of the four accounts exist and their keys are **already set and deployed** on `kirichess-api`:

```bash
fly secrets list --app kirichess-api
```

| Key | State | Which DH.5 account it proves |
| --- | --- | --- |
| `Email__ApiKey`, `Email__FromAddress`, `Email__FromName` | Deployed | **Resend exists** |
| `Sentry__Dsn` | Deployed | **Sentry exists** (backend project). Reconfirmed 2026-10-04 |
| `Turnstile__SecretKey` | Deployed | **Cloudflare/Turnstile done**, never a separate signup. Reconfirmed 2026-09-04 (I-7-1 / I-2-7); `DEPLOYMENT.md` §9.1 and §12 were the stale side |
| `ConnectionStrings__Postgres` | Deployed | Neon exists |
| `Metrics__ScrapeToken`, `Cors__AllowedOrigins__0`, `Jwt__Key`, `Frontend__BaseUrl` | Deployed | not provider accounts |

The frontend's client-side Sentry DSN is not a secret and is already baked in as a build arg at
[fly.ui.toml:17](../../fly.ui.toml#L17), which is correct: `NEXT_PUBLIC_*` is inlined into the shipped
bundle, so a secret set after build would be invisible to it.

**A payment card is therefore also not outstanding.** Fly is running two paid apps and Neon is
provisioned; both were paid for already. Sentry and Resend are on free tiers. AD.7 sized launch-day
mail well under Resend's 100/day (25 signups in 14 days); stay on free. See `DEPLOYMENT.md` §8.4.

## What is actually left

_**Owner pass 2026-08-20.** Apex DMARC is already in place. All five processor DPAs are filed
([ROPA.md](../legal/ROPA.md) §4); Fly's Dropbox Sign envelope completed 2026-08-17, receipt closed
2026-08-20. **Delivery is proven** - see §3. Google OAuth is unchanged and waits on Access._

One thing left, and it is a signup sequenced after Access comes off.

### 1. Google Cloud OAuth client - the one real account

The only provider whose keys are genuinely absent. Confirmed: `Authentication__Google__ClientId` and
`Authentication__Google__ClientSecret` do not appear in `fly secrets list`, and
[Program.cs:574](../../src/KiriChess.Server/Program.cs#L574) logs "External sign-in is disabled" without
them. The code fails closed, so today the Google button is simply inert rather than broken.

**Sequence this after the Access gate comes off (DH.3 / 8.7), not before.** The consent screen wants a
privacy-policy URL and a terms URL, and Google fetches them. Both `kirichess.com/privacy` and
`/terms` currently sit behind Cloudflare Access, so Google's fetcher meets a login wall and the
verification step fails for a reason that looks like a bad URL. This is the one ordering trap in the
whole item.

Console: [console.cloud.google.com/apis/credentials](https://console.cloud.google.com/apis/credentials).
Create project, then OAuth consent screen (External), then Credentials → OAuth client ID → Web
application.

| Field | Exact value |
| --- | --- |
| Application home page | `https://kirichess.com` |
| Privacy policy URL | `https://kirichess.com/privacy` |
| Terms of service URL | `https://kirichess.com/terms` |
| Authorised JavaScript origin | `https://kirichess.com` |
| Authorised redirect URI | `https://api.kirichess.com/signin-google` |

The redirect URI is the ASP.NET Core Google handler's default callback path and belongs to the **API**
host, not the site host. Getting this wrong is the single most common failure in this setup, and it
surfaces as `redirect_uri_mismatch` at the end of the flow rather than at configuration time.

Then, from the repo root:

```powershell
fly secrets set --app kirichess-api `
  Authentication__Google__ClientId='<client id>.apps.googleusercontent.com' `
  Authentication__Google__ClientSecret='<client secret>'
```

Setting secrets restarts the machine, so expect a short interruption; see the note in
[fly.backend.toml:24](../../fly.backend.toml#L24) about in-memory game state.

**Publishing status:** leave the consent screen in Testing while you are the only user. Moving to
Production triggers Google's verification review, which is only needed once real strangers sign in
with Google, and which wants the URLs above to be publicly reachable.

### 2. ~~A DMARC record at the apex~~ **Closed 2026-08-19**

The sending domain is fully verified and correct. All four records resolve, checked 2026-08-17:

```powershell
Resolve-DnsName resend._domainkey.mail.kirichess.com -Type TXT -Server 1.1.1.1   # DKIM, present
Resolve-DnsName send.mail.kirichess.com -Type TXT -Server 1.1.1.1                # SPF, v=spf1 include:amazonses.com ~all
Resolve-DnsName send.mail.kirichess.com -Type MX  -Server 1.1.1.1                # feedback-smtp.eu-west-1.amazonses.com
Resolve-DnsName _dmarc.mail.kirichess.com -Type TXT -Server 1.1.1.1              # p=none, rua=mailto:dmarc@kirichess.com
```

**Closed 2026-08-19.** Cloudflare refused a second `_dmarc` TXT because an identical record already
exists: `v=DMARC1; p=none; rua=mailto:dmarc@kirichess.com; pct=100; adkim=s; aspf=s` at
`_dmarc.kirichess.com`. The 17 Aug line that only `_dmarc.mail.kirichess.com` existed is stale. Leave
`p=none` until a couple of weeks of aggregate reports, then tighten to `quarantine` as § 8.2 says.

### 3. ~~Confirm an email has actually been delivered~~ **Closed 2026-08-20**

Production Resend delivered. A throwaway register on kirichess.com received **Confirm your KiriChess
account**, and a password-reset for the same account arrived too. That is the launch proof. The 19
Aug attempt died on Turnstile double-redeem; the UI deploy unblocked it.

mail-tester 10/10 was not run. DKIM/SPF/DMARC already resolve; tighten DMARC from `p=none` after
aggregate reports as § 8.2 says, not as a second delivery test.

### 4. ~~The five DPAs~~ **Closed 2026-08-20**

Versions and dates are in [ROPA.md](../legal/ROPA.md) §4. The PDFs are on disk under `docs/legal/`
and stay gitignored. Fly's envelope was sent via Dropbox Sign from `mclaren@fly.io`, signed
2026-08-17 21:59 UTC, and marked completed the same minute - that is the receipt. The unsigned Neon
counterpart is optional.

## Optional, and genuinely optional

**`SENTRY_DSN` on `kirichess-ui`.** The UI app currently has no secrets at all (`fly secrets list
--app kirichess-ui` returns an empty table). The client-side DSN is baked in and working; this
runtime one covers errors thrown in Next's **server and edge** runtimes, which today go unreported.
[fly.ui.toml:24](../../fly.ui.toml#L24) documents it. One command, and it may be the same project as the
client DSN:

```powershell
fly secrets set --app kirichess-ui SENTRY_DSN='<same or separate Sentry project DSN>'
```

Worth doing before launch, since a server-render exception on the home page is exactly the failure a
stranger meets first and never reports.

## What this document deliberately does not do

- **No key is printed here, and none should be.** Everything above goes through `fly secrets set`,
  which is why every value is a placeholder.
- **No account is created for you.** Signing up is a contract, and accepting a DPA is a signature.
  What is prepared here is every field each console will ask for,
  so the clicking is mechanical rather than exploratory.
