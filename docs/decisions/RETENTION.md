# Data retention

This is the engineering-facing twin of [PRIVACY.md §5](../legal/PRIVACY.md#5-how-long-it-is-kept):
that document is the player-facing statement of what is kept and why; this one is how it is
enforced, the config keys, the sweep cadence, which rows the job actually deletes, and the
triggers for revisiting any of it.

## 1. The schedule

The whole retention schedule. It is deliberately short: five tables are swept by code, and
everything else is a stated decision, not an oversight.

| Category                  | Where                             | Rule                                                | Enforcement                       |
| ------------------------- | ---------------------------------- | ---------------------------------------------------- | ---------------------------------- |
| Refresh tokens            | `RefreshTokens`                   | delete when `ExpiresAt < now − 30d`                  | **swept** by `RetentionSweeper`   |
| API tokens                | `ApiTokens`                       | delete when `ExpiresAt` or `RevokedAt` `< now − 30d`. Issue always sets `ExpiresAt` (default 90 days, 1–365). A leftover null `ExpiresAt` is not swept | **swept** by `RetentionSweeper`   |
| OAuth exchange codes      | `ExternalLoginCodes`              | delete when `ExpiresAt < now − 1d`                   | **swept** by `RetentionSweeper`   |
| 2FA login challenge tickets | `TwoFactorChallenges`           | delete when `ExpiresAt < now − 1d`                   | **swept** by `RetentionSweeper`   |
| Direct challenge links (9d.8) | `Challenges`                   | delete when `ExpiresAt < now − 7d`                   | **swept** by `RetentionSweeper`   |
| Account + consent record  | `AspNetUsers`                     | life of the account; anonymised on request (§6)      | user-triggered or operator-invoked erasure (8a.2 / L-2-3) |
| Dormant accounts          | `AspNetUsers`                     | **no automatic rule**, reported to a moderator only | **scan** only, see § 4            |
| Games + move lists        | `Games`                           | indefinite, two-party record                        | none, by decision                 |
| Rating history            | `Ratings`, `RatingHistories`      | indefinite, derived from games                      | none, by decision                 |
| Reports                   | `Reports`                         | indefinite, Art. 17(3)(e) legal claims              | none, by decision                 |
| Sanctions                 | `UserSanctions`                   | indefinite while a sanction is active; a lifted or expired sanction is a candidate for human review, not an automatic timer | none, by decision |
| Ban-evasion marker (S-3-1) | `AspNetUsers.BanEvasionEmailHash` | written only when a **full account ban** is active at erasure; blocks a registration only while that ban is still unlifted and unexpired; cleared once no such ban has been active for 30 days | **query-time condition first, then swept** by `RetentionSweeper` |
| Anonymous daily counters  | `DailyCounters`                   | indefinite, aggregate with no identifier            | none, by decision                 |
| Rate-limit counters       | memory / Redis                    | the counting window only; never a table              | expiry, already true              |
| Logs                      | stdout → Fly → Better Stack       | Better Stack plan default (no explicit retention set); Fly's own retain | out of scope of the sweeper; I-9-5 is the owner setting a Better Stack retention |
| Web push subscriptions (11b.1 follow-up) | `WebPushSubscriptions`  | kept until you unsubscribe, or the push service reports the endpoint gone (which only happens on an actual send attempt), **or the account is erased** | prune-on-send; **erasure deletes the rows** (`AccountDeletionService`) |
| Rematch offers            | `RematchOffers`                   | until accepted, or either account is erased     | **erasure deletes both directions**; no timer sweep |
| Data Protection key ring  | `DataProtectionKeys`              | indefinite; the ring must outlive every payload it has ever protected | none, by decision, never swept |
| Kill switches             | `KillSwitches`                    | fixed row set, one per switch, no growth             | none needed                       |

The six "indefinite, by decision" rows are the interesting part of this policy, not an omission.
The four account/game/moderation rows are already justified by 8a.2's erasure design, see
[PRIVACY.md §6](../legal/PRIVACY.md#6-erasure--what-delete-my-account-actually-does), and this
document records them as a retention position rather than re-deriving them. `DailyCounters` is a
different argument: the table holds no personal data, so nothing obliges deletion, **and** it
exists precisely because launch traffic cannot be recounted later - a sweep with any horizon
would eventually destroy the only record of the week the project was trying to measure (AD.6).
`DataProtectionKeys` is a third: ASP.NET Core never prunes its own key ring, ever-growing (one key
per 90 days) is the cost of a payload encrypted under an old key still being decryptable, and
[MIGRATIONS.md](../ops/MIGRATIONS.md) already prices losing it as irrecoverable and site-affecting.
`KillSwitches` is not indefinite in the same sense at all: it is a fixed row per
`KiriChess.Server.Infrastructure.KillSwitch` member, so it does not grow and needs no sweep.

`WebPushSubscriptions` is pruned on send when the endpoint is gone, and account erasure
(`AccountDeletionService`) deletes the rows. Nothing sweeps a subscription that outlives its
usefulness on its own (no TTL). That remaining gap is a timer, not a privacy hole on deletion.

**The ban-evasion marker is the one row here that is not a table.** S-3-1: `UserSanctions` surviving
erasure was never enough on its own to keep TERMS § 6's promise, because the surviving row points at a
tombstone id and erasure hands the email address back to the pool. `AccountDeletionService` now writes
a keyed HMAC of the account's normalised email onto the tombstone before nulling the column, and
registration refuses an address that hashes to a tombstone still carrying an active ban. Three
retention properties, and each is deliberate:

- **It is written for one sanction type only.** A `Warning` or a `RatedPlayBan` writes nothing, and
  nor does an unsanctioned account. Erasure for every account that was not fully banned is byte for
  byte what it was before this existed, which is what keeps the marker proportionate: it exists for
  the one case a fresh account would actually escape.
- **Its life is the ban's life, not the tombstone's.** The refusal is a query-time condition, so
  lifting a ban or letting a temporary one expire stops the refusal on the very next request with no
  job in the loop. The sweep then clears the column itself once no account ban on that row has been
  active for `Retention:BanEvasionMarkerGraceDays` (default 30). A permanent, unlifted ban has no
  end to measure from, so its marker is kept, which is the same answer the `UserSanctions` row above
  already gets.
- **It is pseudonymous under the same Art. 17(3)(e) basis as `Reports`, and it does not weaken the
  erasure.** The value is keyed on a secret that never leaves configuration, so it is not
  brute-forceable back to an address the way a bare hash of an email would be (real addresses are a
  small enough space to enumerate), and nothing in the system can reconstitute the address, the
  display name, or anything else the scrub destroyed.

**Operator-invoked erasure.** A full account ban closes sign-in, so after the current access
token expires a banned account cannot hit `POST /api/account/delete` or `GET /api/account/export`.
While that token is still live, both self-serve routes still work: a ban is not a second
precondition on deletion (PRIVACY §6 / §8). `GET /api/moderation/users/{id}/export` and
`POST /api/moderation/users/{id}/erase` are the same two operations, run by an admin, for
Art. 15/17/20 requests that arrive at `privacy@kirichess.com` after the token has died, and for
closing an under-16 account on credible notice (TERMS §2, PRIVACY §9). Both reuse
`AccountExportBuilder` and `AccountDeletionService`; sanctions still survive.

## 2. What the job does

A `RetentionSweeper` hosted service (`src/KiriChess.Server/Retention/`) runs `RetentionSweep` once
a day. Each rule in `RetentionSchedule` is a pure declaration, name, grace window past expiry, and
a reason, and the sweeper walks the list, deleting rows whose `ExpiresAt` is older than the rule's
cutoff.

**Refresh tokens are purged by `ExpiresAt`, never by `RevokedAt` and never by `CreatedAt`.** A
revoked-but-not-yet-expired row is not dead weight, it is the tripwire that lets
`RefreshTokenService.RotateAsync` tell a replayed **stolen** token from an unknown one: every
rotation leaves the old row behind with `RevokedAt` set, and that row is the only thing that
distinguishes "someone replayed a stolen token" (revoke every session for the account) from
"someone sent garbage" (a silent 401). Deleting a revoked row before it naturally expires would
disarm that detection for a token family that is still live. Sweeping by `ExpiresAt` instead means
the tripwire stays armed for the token's entire nominal life plus the grace period.

**One rule clears a column instead of deleting a row.** `ban-evasion-markers` (S-3-1) nulls
`AspNetUsers.BanEvasionEmailHash` on tombstones whose account ban stopped being active longer ago than
the grace. It cannot delete anything: the row is a tombstone that `Restrict` foreign keys require to
survive, so what expires is the marker on it. Its contribution to the sweep's count and to the
`kirichess.retention.rows_deleted` counter is therefore rows *cleared*, which is the one place that
metric name reads loosely; a second counter for a single rule was judged worse than saying so here.

**API tokens are the opposite: they do not rotate, so a revoked row is not a tripwire.** The sweep
deletes a token when its `ExpiresAt` or `RevokedAt` is older than the grace. S-1-4: `IssueAsync`
always writes `ExpiresAt` (default 90 days; the caller may choose 1–365). A migration backfills
rows that were minted with a null expiry to `CreatedAt` plus 90 days. A null `ExpiresAt` is no
longer a way to mint a permanent token. The sweep still skips one if it appears, because a null is
not a date to measure a grace from. Signing out everywhere, and turning 2FA on or off, must not
revoke them - independence from the browser session is the point.

Every sweep emits one structured `Information` log line carrying the per-rule deleted counts and
the elapsed milliseconds, and increments the `kirichess.retention.rows_deleted` counter on the
existing `KiriChessMetrics` meter, tagged `rule` (a bounded tag, one value per rule in the
schedule). There is no sweep-status endpoint; the log line and the counter are the whole
observability surface, and that is deliberate, see § 5.

## 3. Configuration

Nine `Retention:*` keys, bound from `RetentionOptions`:

| Key                                  | Default | Meaning                                                                 |
| ------------------------------------- | ------- | ------------------------------------------------------------------------ |
| `Retention:Enabled`                   | `true`  | Master switch. When `false`, no sweep runs and nothing else changes.    |
| `Retention:SweepIntervalHours`        | `24`    | Hours between sweeps.                                                   |
| `Retention:InitialDelaySeconds`       | `60`    | Delay before the first sweep after boot, so it is not competing with migrations, leaderboard warmup and game recovery for the same connection pool. |
| `Retention:RefreshTokenGraceDays`     | `30`    | Days past a refresh token's own `ExpiresAt` before its row is removed.  |
| `Retention:ExternalCodeGraceDays`     | `1`     | Days past an external login code's `ExpiresAt` before its row is removed. |
| `Retention:TwoFactorChallengeGraceDays` | `1`   | Days past a 2FA login challenge ticket's `ExpiresAt` before its row is removed. |
| `Retention:ChallengeGraceDays`        | `7`     | Days past a direct-challenge link's `ExpiresAt` before its row is removed (9d.8). Longer than the other three: an accepted challenge is the audit trail for how a game came to exist. |
| `Retention:ApiTokenGraceDays`         | `30`    | Days past an API token's `ExpiresAt` or `RevokedAt` before its row is removed (9f.5). New tokens always have an `ExpiresAt`. |
| `Retention:BanEvasionMarkerGraceDays` | `30`    | Days past the instant a tombstone's account ban stopped being active (lifted, or expired) before the ban-evasion marker on that row is cleared (S-3-1). Measured from the ban, not from the erasure. |

**The marker's own secret is not a `Retention:*` key.** `BanEvasion:MarkerSecret` (env
`BanEvasion__MarkerSecret`) is the HMAC key, it lives with the other secrets, and unset means the
whole mechanism is inert: no marker is written and no registration is refused. See
[DEPLOYMENT.md § 9.2](../ops/DEPLOYMENT.md).

**One real constraint: `RefreshTokenGraceDays` must not be set below `Jwt:RefreshTokenExpiryDays`.**
If it is, a token family that is still within its nominal life could have its revoked rows purged
before they expire, which is exactly the reuse-detection tripwire described in § 2 going dark for
tokens that are still live.

## 4. The dormant-account report

`GET /api/moderation/retention/dormant`, admin-only (`ModerationPolicies.Admin`), read-only.
Query parameters: `inactiveDays` (default 365, clamped 30..3650), `take` (default 100, clamped
1..500), `skip`.

It reports accounts under one of three reasons:

- `never-activated`, registered, never confirmed the email address, never played.
- `never-played`, confirmed, but has never played a single game.
- `inactive`, played, then stopped; the last game is older than the window.

The response returns a user id and a display name per row, never an email address. A moderator
who needs the email uses `/api/moderation/users/{userId:guid}`.

**Nothing erases these accounts.** This is a decision, not a half-build:

- Nothing is deployed and nobody has signed up, so an automatic erasure job would go live having
  never run against a real account, and its first real execution would be against real players.
- Erasure is irreversible by design: 8a.2's tombstone scrubs the email, password hash and display
  name inside one transaction. A bug in the *predicate* that selects dormant accounts, not in the
  deletion itself, which is already tested, would destroy accounts with no undo, and the predicate has
  never run against a real account to find that bug in.

The third reason this decision used to rest on, that the warning email could not be sent, no
longer holds: Resend is live in production and the sending domain is verified (§4 of
[PRIVACY.md](../legal/PRIVACY.md)). The mail path is no longer what is gating this.

**The revisit trigger, updated: real signups plus a predicate observed against real data.** The
mail path has been verified; what remains is that there is nobody to run the scan against yet, and
an unobserved predicate is not one to trust with an irreversible action. Once there are real
accounts to observe the scan against and the predicate has been checked against them, the scan
becomes a candidate for an opt-in automatic reaper. Not before.

## 5. Known limits

1. **The delete is unbounded.** One `DELETE` statement per rule per sweep, no batching, no `Take`.
   The cutoff columns are indexed (`AddHistoryAndRetentionIndexes`, C-12-2), so finding the rows
   is a seek. At this project's size a bounded sweeper would be machinery guarding against a
   volume that does not exist. When a single delete stops being fine, the answer is a `Take` on
   that same indexed predicate, **not** a longer sweep interval, which only makes each sweep
   bigger, and **not** a walk of the primary key, which still has to find the expired rows.
2. **Every instance sweeps.** Deletes are idempotent, a sweep against an already-empty set removes
   zero rows, so N instances running the same sweep is duplicated work, never a wrong answer.
   Production is pinned to a single machine (DH.1) regardless. No Redis lock, no leader election, no
   heartbeat hook was added; this was considered and rejected as unneeded machinery.
3. **A replayed refresh token older than about 60 days degrades to a silent 401** instead of
   triggering revoke-all-sessions, once its row has aged past `ExpiresAt + RefreshTokenGraceDays`
   and been swept. That is the price of the detection window ending somewhere. It is stated here
   rather than left to be discovered.
4. **`DailyCounters` is kept, not swept.** The table landed with AD.6/11a.3. Its rule is the
   documented decision in §1 rather than a `RetentionSchedule` record: it holds no personal data,
   and a horizon of any length would destroy the launch-week counts that cannot be recollected.
   The one real consequence is unbounded - if slow - growth, bounded by `|keys| × |sources| × days`
   (19 keys × 13 sources, including `direct`, so 247 rows per calendar day).

## 6. Backups

Backup and point-in-time recovery are the *other* half of roadmap 8a.3. Production backup inside
Neon is Neon's automated PITR, with the restore drill in
[DEPLOYMENT.md § 5.2](../ops/DEPLOYMENT.md#52-the-restore-drill-scripted-and-scheduled-9c4--9e8). The copy
that still exists if Neon itself is the failure is the dump to Cloudflare R2 in
[DEPLOYMENT.md § 5.3](../ops/DEPLOYMENT.md#53-off-site-backup-out-of-neon-10c1) (roadmap **10c.1**).
Plumbing, bucket and credentials are in. The Sunday schedule is off as of 2026-09-28 until GitHub's
DPA is accepted ([DPA-CHECKLIST.md](../legal/DPA-CHECKLIST.md)): a run would restore the dump on a
GitHub-hosted runner, and that contract is not filed. Neon is still the only copy. Do not dispatch
the workflow by hand before that acceptance. This document does not restate the procedure.

A restore from a dump (or a PITR branch) older than an erasure or a later sanction silently
reverses both: erasure anonymises in place, so the restored database has no request log to walk,
and a ban issued after the restore point is simply missing. Re-run those from the
`privacy@kirichess.com` mailbox and the moderation log as
[DEPLOYMENT.md § 5.3](../ops/DEPLOYMENT.md#53-off-site-backup-out-of-neon-10c1) requires (I-4-6;
L-2).
