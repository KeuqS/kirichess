# Sub-processor DPAs - working checklist

> **Owner: the repo owner, personally.** Accepting a DPA is entering into a contract as the data
> controller, so the owner signs it. Everything that could be prepared in advance is prepared here.
>
> Tracked by **DH.5**. **Filed 2026-08-17** (Fly receipt closed 2026-08-20). 8a.2's remaining
> DH.5 leftover is Google OAuth (#13), not another DPA. Prepared 2026-08-16.

## Why this has a deadline

Art. 28(3) requires a contract with every processor **before** it processes personal data on your
behalf. Cloudflare Access is still on, so there are no public users until **8.7** removes that
gate. Resend is live (an API key is configured and confirmation/reset mail is actually being sent,
see PRIVACY.md §4). Better Stack has been receiving this deployment's log stream, including
account-id log scope, since 2026-08-19; its DPA was captured 2026-09-04 (L-3-1). Google OAuth has
no client id or secret in any deployed environment
([PROVIDER-ACCOUNTS.md](../ops/PROVIDER-ACCOUNTS.md) §1). It is not a processor either way; see
below. A processor that is not on the list below becomes one the moment its processing starts:
Upstash when 8.4 provisions Redis, and GitHub before the first off-site backup succeeds. Taking
Access down does not by itself add either of them.

## Filed

Seven processors are on file in [ROPA.md](ROPA.md) §4. Four of the original five were already
binding through terms accepted at signup; Fly needed a signature and that envelope **completed
2026-08-17** (receipt closed 2026-08-20). Stripe (L-3-2) and Better Stack (L-3-1) were filed
2026-09-04.

| # | Processor | Mechanism | Link | Done |
| - | --------- | --------- | ---- | ---- |
| 1 | **Cloudflare, Inc.** - DNS, proxy, CDN, Turnstile, Email Routing, and the R2 backup bucket (EU jurisdiction, no dump uploaded) | Incorporated into the Self-Serve Subscription Agreement. Already binding. Current **v6.4, 3 Apr 2026**. R2 is the same contract, not a new vendor | [cloudflare.com/cloudflare-customer-dpa](https://www.cloudflare.com/cloudflare-customer-dpa/) | ☑ 2026-08-17 |
| 2 | **Resend** - transactional email | Binding on acceptance of the Agreement. SCCs included | [resend.com/legal/dpa](https://resend.com/legal/dpa) | ☑ 2026-08-17 |
| 3 | **Functional Software, Inc. (Sentry)** - error monitoring | Electronically accepted. Current **5.1.0, 29 May 2024** | [sentry.io/legal/dpa](https://sentry.io/legal/dpa/) | ☑ 2026-08-17 |
| 4 | **Neon, LLC (a Databricks company)** - managed Postgres | Auto-incorporated: "By accessing the Platform Services, Customer agrees to the terms of this Schedule." Current schedule dated **5 Aug 2026** | [neon.com/dpa](https://neon.com/dpa) - [PDF](https://neon.com/pdf/DPA.pdf) | ☑ 2026-08-17 |
| 5 | **Fly.io** - app hosting, both containers | Dropbox Sign envelope from Fly (`mclaren@fly.io`), completed 2026-08-17 21:59 UTC. Binding is that completion, not a `finance@fly.io` reply. | [fly.io/documents](https://fly.io/documents/) (sign in) | ☑ 2026-08-20 |
| 6 | **Stripe Payments Europe, Limited** - one-off donations | Incorporated into the Stripe services agreement; accepted from the Dashboard. Dual role (processor + independent controller for fraud/AML). Current **18 Nov 2025** | [stripe.com/legal/dpa](https://stripe.com/legal/dpa) | ☑ 2026-09-04 |
| 7 | **Better Stack, Inc.** - logs and uptime | Incorporated into the Terms of Use. Dual role (processor for Customer Personal Data + independent controller for Account Data). Current **25 Feb 2025** | [betterstack.com/dpa](https://betterstack.com/dpa) | ☑ 2026-09-04 |

The priority-order paragraph that used to live under this table is spent. The seven rows are the whole filed set.

### Google is not on this list, deliberately

For OAuth sign-in Google is an **independent controller**, not a processor acting on your
instructions: it runs its own relationship with its own user and hands back an opaque account key.
Art. 28 does not apply, and Google's DPA covers Cloud/Workspace services this project is not a
customer of. What is owed for Google is disclosure and a transfer basis, both already in
[PRIVACY.md](PRIVACY.md) §4. **Do not go looking for a Google DPA for this.**

### Upstash is not on this list *yet*

Redis is unprovisioned (`Redis__Configuration` is empty everywhere; you are single-instance). The
moment 8.4's Redis is provisioned via Fly Redis, that is **Upstash**, a separate company holding
session and rate-limit state, and it becomes another processor needing its own DPA and its own row in
[ROPA.md](ROPA.md) §4.

### Stripe is filed (L-3-2, 2026-09-04)

One-off donations are live (Terms §12): Payment Links hand the donor to Stripe, and card details
never reach KiriChess. The DPA was accepted from the Stripe Dashboard on 2026-09-04. Version:
**last updated 18 November 2025**. Contracting party for a Belgium account is **Stripe Payments
Europe, Limited** (SPEL); US operations are **Stripe, LLC**. Dual role, in Stripe's own words:
processor for the payment platform, independent controller for fraud/AML. PDF:
`DPA-Stripe-2025-11-18-filed-2026-09-04.pdf` (gitignored). Public text: [stripe.com/legal/dpa](https://stripe.com/legal/dpa).

### Better Stack is filed (L-3-1, 2026-09-04)

There is **no "Sign DPA" button** in the Better Stack dashboard. Same shape as Cloudflare / Resend /
Neon: the DPA is incorporated into the Terms of Use, not a countersignature. Captured from
[betterstack.com/dpa](https://betterstack.com/dpa) on 2026-09-04. Version: **last updated 25 February
2025**. Contracting entity: **Better Stack, Inc.** (Delaware). Dual role, in Better Stack's own
words: processor for Customer Personal Data (the log stream), independent controller for Account
Data. Transfers: DPF, SCCs as fallback (DPA §7). This deployment's ingest is `us-west-2`; do not
claim logs stay in the EU. PDF: `DPA-BetterStack-2025-02-25-filed-2026-09-04.pdf` (gitignored).
Public text: [betterstack.com/dpa](https://betterstack.com/dpa).

In the Better Stack account, set an explicit log retention if the plan default is not what you want
(Privacy §5 currently says "plan default"). Narrowing the shipper's `SUBJECT` was considered and
left wide; see Still open, below.

## Fly.io - closed 2026-08-20

Executed via Dropbox Sign: Fly sent the envelope from `mclaren@fly.io`, the controller signed
2026-08-17 21:59 UTC, Dropbox Sign marked the document completed the same minute. That completion
is the receipt; do not chase `finance@fly.io`. PDF: `DPA-Fly.io-signed-sent-2026-08-17.pdf`.

The block below is the fallback that was not used (login-gated countersign, or email if that page
had nothing to sign).

## Fly.io - the one that needs sending (unused fallback)

Sign in at [fly.io/documents](https://fly.io/documents/) first. The documents are login-gated and
pre-signed, so in the good case you countersign there and never send anything. **Only if the DPA is
not available on that page**, send the following through whichever support channel your plan offers
(the dashboard support form, or the community forum if that is all you have - do not guess at a
support address).

```
Subject: GDPR Data Processing Agreement request

Hello,

I am a Fly.io customer (organisation: <YOUR ORG NAME / SLUG>, account email:
<YOUR ACCOUNT EMAIL>) hosting an application that processes personal data of EU
residents, with Fly.io acting as a processor on my behalf.

I would like to execute your Data Processing Agreement. fly.io/documents mentions
a pre-signed DPA available on request, but I was not able to access or countersign
it from that page under my account.

Could you send the current pre-signed DPA for countersignature, or point me at where
to accept it? For the annexes: I am the controller, an individual established in
Belgium. The processing is the hosting of a web application and its API; the data
subjects are registered users of that application; the categories of personal data
are account identifiers, email addresses, authentication credentials in hashed form,
and application usage records.

Please also confirm the transfer mechanism relied on for EEA-to-US transfers
(Data Privacy Framework certification and/or the Standard Contractual Clauses
incorporated into the DPA).

Thank you,
Michiel Gekiere
privacy@kirichess.com
```

## Neon - only if you want a countersigned copy

Not required. The schedule is auto-incorporated, so you are already covered and can simply file it.
Send this only if you would rather hold a signed counterpart. Note the counterparty is **Databricks**
since the acquisition, so route it through Neon support rather than a standalone Neon legal address.

```
Subject: Request for countersigned Data Processing Agreement

Hello,

I am a Neon customer (account email: <YOUR ACCOUNT EMAIL>) processing personal data
of EU residents on Neon Postgres, with Neon acting as processor on my behalf.

I understand the Data Processing Agreement is auto-incorporated by the Product
Specific Schedule. For my Art. 28 records I would like a countersigned counterpart
if one is available.

I am the controller, an individual established in Belgium. The processing is the
storage of a web application's database; the data subjects are registered users;
the categories of personal data are account identifiers, email addresses, hashed
authentication credentials, and application usage records.

Could you also confirm the current contracting entity following the Databricks
acquisition, and the transfer mechanism relied on for EEA-to-US transfers?

Thank you,
Michiel Gekiere
privacy@kirichess.com
```

## What to record, and where

**Not in the repo.** These are account documents that get superseded periodically; a folder in normal
document storage is the right home. What goes in the repo is the dates.

For each processor, keep: the PDF or confirmation email, the **version identifier**, and the **date
you accepted**. Art. 28(3) requires the contract to be demonstrable, and a DPA you cannot produce on
request is functionally an unsigned one.

| Processor | Version accepted | Date | Filed where |
| --------- | ---------------- | ---- | ----------- |
| Cloudflare | Customer DPA v6.4 (3 Apr 2026) | 2026-08-17 | `docs/legal/DPA-Cloudflare-v6.4-filed-2026-08-17.pdf` (gitignored) |
| Resend | Data Processing Addendum, last updated 2025-12-31 | 2026-08-17 | `docs/legal/DPA-Resend-2025-12-31-filed-2026-08-17.pdf` (gitignored) |
| Sentry | DPA 5.1.0, 29 May 2024; Docusign envelope E55FD887-383F-8668-808E-A14ADF937713 | 2026-08-17 | `docs/legal/DPA-Sentry-5.1.0-executed-2026-08-17.pdf` (gitignored) |
| Neon | Product Specific Schedule (auto-incorporated) | 2026-08-17 | `docs/legal/DPA-Neon-schedule-auto-incorporated-2026-08-17-IMAGE-ONLY.pdf` (gitignored) |
| Fly.io | Pre-signed Addendum, Doc ID 4b9598ab88a5ed962f15d2217d9a1061d8baed46. Dropbox Sign complete 2026-08-17 21:59 UTC | 2026-08-17 (receipt closed 2026-08-20) | `docs/legal/DPA-Fly.io-signed-sent-2026-08-17.pdf` (gitignored) |
| Stripe | Data Processing Agreement, last updated 2025-11-18. Dual role (processor + independent controller for fraud/AML). Contracting entity SPEL | 2026-09-04 | `docs/legal/DPA-Stripe-2025-11-18-filed-2026-09-04.pdf` (gitignored) |
| Better Stack | Data Processing Agreement, last updated 2025-02-25. Dual role (processor for Customer Personal Data + independent controller for Account Data). Contracting entity Better Stack, Inc. Ingest `us-west-2` | 2026-09-04 | `docs/legal/DPA-BetterStack-2025-02-25-filed-2026-09-04.pdf` (gitignored) |

Two things worth doing while you are in each account:

- **Check the annexes.** SCCs carry annexes describing the processing, data categories and data
  subjects. Self-serve DPAs pre-fill these. If one asks you to complete them, [ROPA.md](ROPA.md) §3 is
  the answer, which is a large part of why that file exists.
- **Subscribe to sub-processor change notifications** where offered. Art. 28(2) gives you the right to
  object to new sub-processors, and you cannot exercise a right you never hear about.

## Still open

The seven rows above are filed, and the records now name the two processors the backup would add.
What still needs the owner, in an account this repository cannot sign:

1. **Upstash**, the moment 8.4 provisions Redis via Fly Redis. Its own DPA, and its own row in
   [ROPA.md](ROPA.md) §4. See the section above. Redis is still unprovisioned, so there is nothing
   to file today.
2. **Accept GitHub's DPA, then put the Sunday schedule back.** The records are written
   ([PRIVACY.md](PRIVACY.md) §4, [ROPA.md](ROPA.md) §3.11 and §4): R2 is the Cloudflare contract
   already filed, the bucket is EU and empty, and GitHub is not yet a processor.
   `.github/workflows/offsite-backup.yml` has no active schedule; `workflow_dispatch` remains and
   must not be used until this acceptance is recorded in the table above. GitHub's DPA is
   self-serve. In the same change, restore the `41 5 * * 0` cron and delete the
   `github-dpa-before-schedule` marker. `CiWorkflowDriftTests` fails the build if a schedule is
   added while that marker is still there.
3. **Better Stack log retention**, in the Better Stack account, if the plan default is not what
   you want. There is no retention field set in `infra/betterstack/main.tf` on purpose. Narrowing
   the shipper's `SUBJECT` was considered and left wide: at four apps a NATS filter buys a silent
   gap the day an app is added (`fly.logshipper.toml`). Revisit if log volume makes that risk
   worth it.
4. **In each account, if not already done:** complete any SCC annex the vendor left blank
   ([ROPA.md](ROPA.md) §3 is the answer), and subscribe to sub-processor change notifications
   where the vendor offers them.

A countersigned Neon copy is optional and changes nothing about the schedule that already binds.
The Fly email in the unused-fallback section was not the path that closed Fly. Google stays off
this list.
