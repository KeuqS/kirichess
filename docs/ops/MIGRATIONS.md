# Schema migrations - the rollback policy

6a.5 made schema migration a deliberate deploy step, and `release_command` aborts the deploy if the
migration fails. That covers the easy failure. This document covers the one it does not: **the
migration succeeded and the application is broken.**

It is the engineering answer to roadmap item 9c.3, written **before the first production migration**
rather than during the first incident, and it is grounded in a drill (§ 5) rather than in what EF
Core is supposed to do.

## 1. The decision

> **Roll forward only. A `Down()` migration is never run against production.**
>
> - **Broken code, intact data** → roll the *application* back and leave the schema at head (§ 3,
>   Play A), but check Play A's **rollback floor** first. Play A is not unconditional: no image
>   older than `20260825204345_ReplaceAvatarKeyWithUpload` may be deployed against a head schema,
>   because it would 500 every read of `AspNetUsers`.
> - **Broken schema** → write a new forward migration (Play B). Never a `Down()`.
> - **Damaged or destroyed data** → restore a **Neon branch** from a timestamp before the migration
>   and repoint (Play C), per [DEPLOYMENT.md § 5](DEPLOYMENT.md#5-backup--restore-6a7).

The generated `Down()` methods stay in the repo. They are how `dotnet ef` unwinds a bad migration on
a **developer's local database**, which is a real and frequent use, and they are the reason § 5's
drill could run at all. They are not a production tool, and § 2 is why.

Three facts drove this, in ascending order of how much they cost to learn:

**The production image cannot run a `Down()` at all.** `Dockerfile.backend`'s runtime stage is
`mcr.microsoft.com/dotnet/aspnet:10.0`, no SDK, no project source. `dotnet KiriChess.Server.dll
--migrate` calls `Database.MigrateAsync()`, which only ever moves *forward*; there is no
`--rollback` and adding one would mean shipping a "destroy schema" switch inside the serving image.
Reverting therefore means running `dotnet ef database update <target>` from a workstation against
the production connection string, and `DesignTimeDbContextFactory` hardcodes
`Host=localhost;Database=kirichess`, so even that connects to the developer's own machine unless
somebody edits source first. **That guard is deliberate and should stay.** The practical shape of a
production down-migration in this stack is "a human edits a source file so their laptop can point at
Neon, at 3am, mid-incident", which is not a recovery plan.

**Every `Down()` here is destructive by construction.** EF generates the inverse of the schema
change, and the inverse of "add a table" is `DROP TABLE`. Nothing consults whether the table has
rows. § 2 prices each one.

**Whether a `Down()` runs at all depends on production data.** The head migration's `Down()` fails
outright on any database where two accounts have been erased, see § 2's last row and § 5's drill.
This is the worst possible property for a recovery mechanism: it passes in staging, whose database
is clean, and fails in production, which is the only place you would ever need it.

**A `release_command` failure is not always a migration failure (C-12-5).** `--migrate` runs the
whole application startup path before it reaches the migration branch, and that path includes
`StartupValidation.ValidateJwt` (`Program.cs:686`, ahead of the `--migrate` check at `:691`). A
deploy whose `Jwt__Key` is missing or malformed aborts at `release_command` with a JWT error before
the migration itself ever runs, and `release_command` is documented, here and in
`fly.backend.toml:24-25`, as the thing that fails when *the migration* fails - so the first reading
of an aborted deploy is naturally "the schema change is bad" when the schema was never touched.
`docker-compose.e2e.yml` already carries the knowledge that averts this: it anchors a shared env
block specifically to get `Jwt__Key` into the migrate service, but that reasoning lives only there.
Moving the `--migrate` branch above `ValidateJwt` would remove the false signal outright, but it is a
production-code change and the coupling is currently load-bearing for the compose paths; this note
is the cheaper fix, and the one this packet took.

**The serving role does not own the schema (C-12-4).** `RunMigrationsOnStartup=false` only stops the
process from *choosing* to migrate. It does not take away the right. On Neon the long-lived process
connects as `kirichess_app`, which has `SELECT`, `INSERT`, `UPDATE`, and `DELETE` on the
existing tables and on tables `neondb_owner` creates later, and no `CREATE` on `public`.
`--migrate` uses `ConnectionStrings__Migrator`, the `neondb_owner` string, when that secret is set
(`ServingConnection`). Local Postgres and compose leave `Migrator` unset, so `--migrate` keeps using
the serving string. `ConnectionStrings__Postgres` on both Fly apps is `kirichess_app` as of
2026-10-06. The image Fly runs contains `ServingConnection`, so the next `release_command` migrates
as `neondb_owner`.

## 2. What each `Down()` actually costs

Ordered as applied. "Recoverable" means the value can be reconstructed from data that survives,
not that anything reconstructs it automatically.

| Migration                       | `Down()` drops                                                                | Cost if run against production                                                                                                            |
| ------------------------------- | ----------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------ |
| `InitialCreate`                 | every table                                                                   | **Total.** The whole database. Nothing survives; this is a restore, not a rollback.                                                        |
| `AddRatingGamesPlayed`          | `Ratings.GamesPlayed`                                                         | Recoverable, it is a count of that user's `RatingHistories` rows.                                                                          |
| `AddRefreshTokens`              | `RefreshTokens`                                                               | **Every session on the site ends** within the 15-minute access-token window, and 6b.1's reuse-detection chain state goes with it. Users can log in again; nothing is permanently lost, but a rollback becomes a site-wide forced logout. |
| `AddGameResultIndex`            | `IX_Games_Result`                                                             | **None.** Index only, the one genuinely free reversal in the chain.                                                                        |
| `AddReports`                    | `Reports`                                                                     | **Irrecoverable.** Every abuse report ever filed. 6e.2's entire evidence surface.                                                           |
| `AddExternalLoginCodes`         | `ExternalLoginCodes`                                                          | Near-free, the rows live 60 seconds. In-flight Google sign-ins fail and are retried by the user.                                          |
| `AddGamePlyCount`               | `Games.PlyCount`, `IX_Games_RatingPool_EndedAt`                               | Recoverable, `PlyCount` is `jsonb_array_length("Moves")`, which is how the Up backfilled it.                                              |
| `AddModeration`                 | `UserSanctions`, `Reports.ReviewedAt`/`ReviewedByUserId`/`ModeratorNote`      | **Irrecoverable, and it unbans everyone.** Every active ban evaporates; the accounts 8.5 removed are live again on the next boot. There is no second record of a sanction. |
| `AddDisplayNameModeration`      | `AspNetUsers.NormalizedDisplayName`, `DisplayNameChangedAt`, its unique index | Mixed. `NormalizedDisplayName` is *derived* and the Up backfills it, so it comes back on roll-forward; `DisplayNameChangedAt` is *recorded* and does not, so every user's rename cooldown silently resets. Impersonation-squatting is unpoliced while the index is absent. |
| `AddAccountErasureAndConsent`   | `AspNetUsers.DeletedAt`, `TermsAcceptedAt`, `TermsVersion`, `AgeConfirmedAt`  | **Fails outright on ≥2 erased accounts** (§ 5). Where it does succeed: the erasure tombstone and the consent record are destroyed, and neither is derivable from anything. The user's data really was scrubbed, but the *record that it was scrubbed on a given date*, the thing you produce when asked, is gone. |

The pattern the table makes visible, and the one to carry into every future migration: **a `Down()`
is cheap exactly when the column it drops is derived, and irreversible when the column is a
record.** `PlyCount` and `NormalizedDisplayName` are computed from surviving data and come back on
roll-forward. `DeletedAt`, `TermsAcceptedAt`, `DisplayNameChangedAt` and every `UserSanctions` row
are the sole record of an event that happened once, and re-applying the `Up()` gives you an empty
column back.

The first table stopped at `AddAccountErasureAndConsent` (2026-08-06). Nineteen migrations landed
after it, of which only two were ever priced, and the gap was itself the 10c.2 pattern in miniature:
a document that records what shipped and has no mechanism for noticing what stopped being
maintained. The table below is the rest of the chain, read off
`src/KiriChess.Server/Data/Migrations/` on **2026-09-06** and current to
`AddBanEvasionEmailHash`. The **Drops a column** flag is the one to read first, because that is
the property that sets Play A's rollback floor (§ 3); it is derived from what each migration's
`Up()` actually calls, not from its name.

| Migration                     | Drops a column | `Down()` drops                                                                                                              | Cost if run against production                                                                                                                                                                                                                                                                                                    |
| ----------------------------- | -------------- | --------------------------------------------------------------------------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `AddTwoFactorChallenges`      | no             | `TwoFactorChallenges`                                                                                                       | Near-free. The rows are short-lived login tickets, not a record; anyone mid-login retries and succeeds. Nothing derives from them and nothing outlives them.                                                                                                                                                                       |
| `AddChallenges`               | no             | `Challenges`                                                                                                                | Recoverable in the sense that nothing historical is lost: a `Challenge` is a pending invitation, and games already created from one are rows in `Games`. Every outstanding invitation vanishes and the sender is not told.                                                                                                          |
| `AddPatronStatus`             | no             | `AspNetUsers.IsPatron`, `PatronSince`                                                                                       | **Irrecoverable in the schema, recoverable from Stripe.** Both are *records* of a payment relationship. Nothing in this database derives them; the reconstruction path is the payment provider, by hand.                                                                                                                           |
| `AddDataProtectionKeys`       | no             | `DataProtectionKeys`                                                                                                        | **Irrecoverable, and it is the exact failure 10a.1 exists to end.** The key ring is a *record*: nothing derives it, and the payloads it protects outlive it. Dropping it invalidates every email-confirmation and password-reset link currently in somebody's inbox and fails every Google sign-in in flight ("Correlation failed"). Nothing is lost permanently, users request a new link, but a rollback is a silent, site-wide invalidation of mail this system already sent. |
| `AddKillSwitches`             | no             | `KillSwitches`                                                                                                              | **Fails closed, not open, and that is the safe direction.** `KillSwitchService` treats a row it cannot see as *enabled* (§ the service's own doc comment), so dropping the table does not disable anything - it silently turns every switch back into "not there yet", the pre-10c.4 state. Recoverable in the sense that nothing is lost, but a moderator who had disabled one during an incident would not know it stopped being disabled. |
| `AddRematchOffers`            | no             | `RematchOffers`                                                                                                             | Near-free. An offer is transient state attached to a finished game; the game itself is untouched. Offers in flight are silently withdrawn.                                                                                                                                                                                         |
| `AddNotificationPreferences`  | no             | `AspNetUsers.PairingFoundEmailUnsubscribedAt`, `RematchOfferedEmailUnsubscribedAt`                                          | **Irrecoverable, and it re-subscribes people who opted out.** Both are *records* of a stated preference, nothing derives them, and the columns coming back empty on roll-forward reads as consent. This is the one row in this table where the harm lands on users rather than on operators.                                        |
| `AddWebPushSubscriptions`     | no             | `WebPushSubscriptions`                                                                                                      | Recoverable only by each user re-granting permission in their browser. The endpoint and keys are issued by the browser and held nowhere else.                                                                                                                                                                                      |
| `AddDailyCounters`            | no             | `DailyCounters`                                                                                                             | Near-free operationally, and it resets whatever the counters cap for the current day.                                                                                                                                                                                                                                             |
| `AddApiTokens`                | no             | `ApiTokens`                                                                                                                 | **Every bot on the site stops authenticating**, and irrecoverably: only the SHA-256 hash is stored, so nothing can be reissued. Each owner must mint a new `kirichess_pat_` token and edit it into their bot.                                                                                                                                 |
| `DropChallengeRated`          | **yes**        | re-adds `Challenges.Rated` as `NOT NULL DEFAULT false`                                                                      | **The earlier of the two rollback floors** (§ 3). The `Down()` itself is cheap, it adds a column back; the cost is on the way *forward*, where an app image older than this migration reads a column that no longer exists.                                                                                                        |
| `RenamePoolKeysToNeutralIds`  | no             | rewrites the six pool keys back to their old spellings                                                                       | Data-only, no schema change. Reversible by construction, both directions are `UPDATE`s over `Games.RatingPool`, `Ratings.Pool`, `RatingHistories.Pool` and `Challenges.Pool`. It is still a Play A hazard of its own kind, see § 3.                                                                                                |
| `AddPatronTiers`              | no             | `DonationReferences`, `IX_AspNetUsers_StripeSubscriptionId`, `AspNetUsers.PatronExpiresAt`, `PatronKind`, `PatronTier`, `StripeCustomerId`, `StripeSubscriptionId` | **Irrecoverable, and it breaks the revoke path.** `StripeSubscriptionId` is the only link from a Stripe webhook back to an account, so losing it means `customer.subscription.deleted` can no longer find whom to revoke. Reconstructable from Stripe by hand, and only by hand.                                                    |
| `AddAvatarKey`                | no             | `AspNetUsers.AvatarKey`                                                                                                     | Superseded by the next row; the column no longer exists at head. Priced here only so the chain reads continuously.                                                                                                                                                                                                                 |
| `ReplaceAvatarKeyWithUpload`  | **yes**        | `AspNetUsers.AvatarImage`, `AvatarUpdatedAt`; re-adds `AvatarKey`                                                            | **The current rollback floor** (§ 3), and the most expensive row in this table on the way back: every uploaded avatar is stored *in the column*, so the `Down()` deletes the images themselves. There is no object store holding a second copy.                                                                                     |
| `AddChallengeCustomConfig`    | no             | `Challenges.CustomConfig`                                                                                                   | Near-free. Nullable, and it only decorates pending invitations; a custom-config challenge loses its config, which is the same outcome as the invitation expiring.                                                                                                                                                                  |
| `AddGameNoShowAtFaultPlayerId` | no            | `Games.NoShowAtFaultPlayerId`                                                                                               | **Irrecoverable per game, and narrow.** Nullable, so an older image reads and writes around it, but the column is a *record* of which seat B12a's `MatchNoShowWatcher` attributed a no-show to; nothing else derives it, and a rollback erases that attribution for every game decided since. `AbandonmentPolicy`'s 24-hour window is the only consumer, so the exposure is bounded to whatever is still inside it. |
| `AddRematchOfferNotificationOwed` | no         | `RematchOffers.NotificationDueAt`, `NotificationSentAt`                                                                     | Near-free. Both nullable and additive; the mail itself is not stored here, only the schedule for whether and when to send a reach-back rematch mail, so the worst case on a rollback is a duplicate or a missed one, not a lost game or a lost offer.                                                                             |
| `AddBanEvasionEmailHash`      | no             | `AspNetUsers.BanEvasionEmailHash`, `IX_AspNetUsers_BanEvasionEmailHash`                                                     | **Irrecoverable for any tombstone already marked, and it reopens S-3-1.** The hash is written once, at erasure, from the address that same operation nulls; nothing else in the database can reproduce it afterward. Dropping the column silently re-opens exactly the ban-evasion path S-3-1 exists to close: a banned account erased and re-registered with the same address stops being detected. |
| `AddGameDisconnectCounts`     | no             | `Games.WhiteDisconnectCount`, `Games.BlackDisconnectCount`                                                                  | **Irrecoverable as a record, and narrow.** Both are additive integers defaulting to 0, so an older image reads and writes around them. They count grace-timer arms per seat. Nothing derives them, and a rollback erases the count for every game played since. No cooldown or sanction reads them. |
| `AddRatingLastPlayedAt`       | no             | `Ratings.LastPlayedAt`                                                                                                      | Recoverable. The `Up()` backfills it from the latest `RatingHistories` row for that user and pool, and falls back to `UpdatedAt` only when a row has no history. An image older than this migration does not advance the column, so under Play A an active player drops off the established leaderboard 28 days after the value this migration wrote. The column's database default is `now()`, so that older image's inserts still succeed. |
| `AddRatingConcurrencyToken`   | no             | nothing executed. `Down()` calls `DropColumn` on `Ratings.xmin`, and Npgsql emits no SQL for that system column | **None.** No column is added or dropped. An image older than this migration updates `Ratings` without the token, so the lost-update race returns for that image only. |
| `UniqueRatingHistoryPerGameUser` | no          | `IX_RatingHistories_GameId_UserId`; re-adds the non-unique `IX_RatingHistories_GameId`                                       | **None for stored rows.** Index only. The unique index is the C-1-4 / C-8-9 backstop: a second history row for the same game and user is rejected. `Down()` removes that backstop and the double-count is possible again; it deletes no rating. It is not the `NormalizedDisplayName` hazard (§ 3): every image since `InitialCreate` already writes one row per player per game, so an older image's inserts still satisfy the index. `Up()` fails, and the deploy aborts, if a duplicate `(GameId, UserId)` is already stored. That failure is the point; do not delete a row to make it pass. |
| `UniqueStripeSubscriptionId`  | no             | the unique filtered `IX_AspNetUsers_StripeSubscriptionId`; re-adds it non-unique and unfiltered                              | **None for stored rows.** Index only. The unique filtered index is the C-12-3 backstop: two accounts cannot share a subscription id, which is what `RevokeBySubscriptionAsync` already assumes. `Down()` restores the plain index and deletes no badge. It does not move the Play A floor (§ 4 rule 1). It is not the `NormalizedDisplayName` hazard (§ 3): nulls stay out of the index, and every image since `AddPatronTiers` writes a given id onto at most one account, so an older image's inserts still satisfy it. A duplicate write from that older image is rejected. `Up()` fails, and the deploy aborts, if two accounts already share a non-null `StripeSubscriptionId`. That failure is the point; do not clear a badge to make it pass. |
| `AddHistoryAndRetentionIndexes` | no          | the history and retention indexes this migration adds; re-adds `IX_AspNetUsers_DeletedAt`                                    | **None.** Index only. `Up()` adds partial `(WhitePlayerId, EndedAt)` and `(BlackPlayerId, EndedAt)` indexes on `Games` (I-1-4), `ExpiresAt` indexes on the five swept tables, and a `RevokedAt` index on `ApiTokens` (C-12-2), and drops the unused `IX_AspNetUsers_DeletedAt` (C-12-6). `Down()` reverses that and deletes no row. `DropIndex` does not move the Play A floor (§ 4 rule 1). |
| `AddExternalLoginCodeAccountCreated` | no    | `ExternalLoginCodes.AccountCreated`                                                                                          | Near-free. A non-null bool defaulting to false on a row that lives 60 seconds. `Down()` drops the flag that tells the exchange response a handshake registered the account (M-4-1). In-flight Google sign-ins lose that bit and are not counted as signups; the accounts themselves are untouched. An older image does not select the column, so Play A still works and the floor does not move. |

**Two of the nineteen drop a column, and they are the two that matter for § 3.** Everything else in
this stretch of the chain is additive, which is why Play A stayed cheap for as long as it did.

## 3. The three plays

### Play A - broken code, intact data: roll the app back, leave the schema

> ### The rollback floor
>
> **No `kirichess-api` image older than `20260825204345_ReplaceAvatarKeyWithUpload` may be deployed
> against a head schema.** That was the floor as of 2026-09-03 and is still the floor on
> 2026-10-03: the migrations that landed since (§ 2's table), including `AddGameDisconnectCounts`,
> `AddRatingLastPlayedAt`, `UniqueRatingHistoryPerGameUser`, `UniqueStripeSubscriptionId`,
> `AddHistoryAndRetentionIndexes`, and `AddExternalLoginCodeAccountCreated`, are additive. `AddHistoryAndRetentionIndexes` drops
> `IX_AspNetUsers_DeletedAt` and adds indexes; it drops no column. The rating one
> replaces `IX_RatingHistories_GameId` with a unique `(GameId, UserId)` index, and
> `UniqueStripeSubscriptionId` replaces `IX_AspNetUsers_StripeSubscriptionId` with a unique index
> filtered to non-null values. `DropIndex` does not move the floor (§ 4 rule 1). Null subscription
> ids stay out of that index, and every image since `AddPatronTiers` writes a given id onto at most
> one account, so an older image's inserts still satisfy it. `AddRatingConcurrencyToken` records `Ratings.xmin` and emits no SQL,
> so it does not move the floor either. `AppendGameMoves` (new table) and `BackfillApiTokenExpiry` (data only) landed after
> `AddBanEvasionEmailHash` and are not priced in the table above; neither drops a column, so neither
> moves the floor. The floor moves forward on every `DropColumn`.
>
> Below it, in the same way and for the same reason: **no image older than
> `20260819235137_DropChallengeRated` may be deployed against a head schema**, for the challenges
> feature. That is the earlier floor, and it is the milder one.
>
> If the image you are about to deploy predates the current floor, this play is not available. Go to
> Play C.

Check the floor first, then read the rest of this section. The floor is a version, and `fly releases`
prints the date each image was built, so it is answerable in one command and does not require
knowing what went wrong.

The default and the one to reach for first. EF ignores columns that are not in its model, so a
`kirichess-api` at version N−1 runs happily against a schema at version N **as long as every
migration between N−1 and N was additive**: the extra columns are invisible to it. Every `NOT NULL`
column added by the chain above carries a database-level default (`GamesPlayed` 0, `PlyCount` 0,
`WhiteDisconnectCount`/`BlackDisconnectCount` 0,
`NormalizedDisplayName` `''`, `IsPatron` false, `PatronKind`/`PatronTier` `'None'`), so the older
code's `INSERT`s still satisfy the newer table.

```bash
fly releases --app kirichess-api        # find the previous good image, and its date
fly deploy --app kirichess-api --image <previous-image-ref>
```

**Why a dropped column is different from an added one, and why it is a floor rather than a check.**
"EF ignores columns that are not in its model" is true of a column the model has never heard of. It
is false of a column the model *does* have and the database does not, because EF Core does not
`SELECT *`: it emits an explicit column list on every read, built from the model the image was
compiled with. So an image built before `ReplaceAvatarKeyWithUpload` issues

```sql
SELECT a."Id", ..., a."AvatarKey", ... FROM "AspNetUsers" AS a
```

against a head schema where `AvatarKey` no longer exists, and Postgres answers:

```
ERROR: 42703: column a.AvatarKey does not exist
```

There is no partial degradation here and no feature that merely stops working. **Every query
touching `AspNetUsers` fails**, which is login, registration, token refresh, profile and moderation.
The rollback that was supposed to end the incident becomes a total outage, produced by the recovery
procedure, at the moment nobody is looking for a second cause.

`DropChallengeRated` is the same mechanism one table over and one feature wide: an image older than
it selects `Challenges."Rated"` and takes the same `42703` on every read of `Challenges`. Challenges
break; login does not. That is why it is recorded as the earlier floor rather than the current one.

**How to tell whether a future migration moves the floor.** Open the migration and look at its
`Up()`. A `DropColumn` sets a new floor at that migration, and § 2's table carries a **Drops a
column** flag for exactly this lookup. `DropTable` does not: a table the older image still queries
would be a floor too, but every `DropTable` in this repo lives in a `Down()`, which § 1 says is
never run. `AddColumn`, `CreateIndex` and `CreateTable` never move it.

**Two hazards that are not floors, and still need a check rather than a reflex.**

Rolling the app back past `AddDisplayNameModeration` (8a.4) breaks registration. That version of the
code does not set `NormalizedDisplayName`, so every row it inserts takes the column default `''`,
and the unique index the migration created means the **second** registration collides with the
first:

```
ERROR: duplicate key value violates unique constraint "IX_AspNetUsers_NormalizedDisplayName"
DETAIL: Key ("NormalizedDisplayName")=() already exists.
```

That is measured, not predicted (§ 5). One new account works, the next one 500s, and the cause is a
schema/code mismatch nobody will look for while chasing whatever prompted the rollback. If the
rollback must cross that migration, drop the unique index by hand first (`DROP INDEX
"IX_AspNetUsers_NormalizedDisplayName"`) and re-create it in the roll-forward, the same index the
Up creates, filtered on `"DeletedAt" IS NULL`. It is not a floor because it is repairable in place
with one statement, and because it is moot anyway: 8a.4 is far below both column-drop floors.

Rolling the app back past `RenamePoolKeysToNeutralIds` reads *values* the older image does not know.
The schema is identical either way, so nothing 42703s, but `Games.RatingPool`, `Ratings.Pool`,
`RatingHistories.Pool` and `Challenges.Pool` hold `steal1`/`bounty1`-style keys and the older image
was compiled against `flagfall`/`zeitnot`. The migration is a reversible data-only backfill in both
directions (§ 2), so the repair, if this rollback is ever both necessary and below that migration,
is to run its `Down()` UPDATEs by hand. Also moot below the current floor.

**A rolled-back app must never be pointed at a rolled-back schema.** Rolling both is Play A plus
Play C's damage for no gain.

### Play B - broken schema: write a new forward migration

An index on the wrong column, a constraint too tight, a default that turned out wrong: fix it with
`dotnet ef migrations add FixWhatever` and deploy through the normal `release_command`. This is the
only mechanism that is version-controlled, reviewed, tested, and applied by the same code path as
every other migration. A `Down()` is none of those things.

Migration history moves forward only; a database that has run migration N and then N+1 "undoing" it
is in a state the next developer can read. A database that has had a `Down()` run against it is in a
state that exists nowhere in the repository.

### Play C - data damaged or destroyed: Neon branch and repoint

The only recovery for lost rows, and the reason DH.2 chose Neon. See
[DEPLOYMENT.md § 5](DEPLOYMENT.md#5-backup--restore-6a7) for the procedure. In outline:

1. Branch from a timestamp **before** the release_command ran, `fly releases` gives the time.
2. Point staging at the branch and confirm the data is what you expect.
3. Repoint production at the branch.

The cost is every write since that timestamp: finished games, ratings, reports. Weigh that against
what the migration destroyed, for `AddModeration` or `AddReports` there is no other option, because
nothing else holds a copy.

## 4. Rules for writing migrations, so that Play A keeps working

The policy above is only cheap while migrations stay additive. Four rules keep it that way:

1. **Additive first, and a `DropColumn` moves the rollback floor.** A release adds columns and
   tables; it does not drop or rename them. That is what makes "leave the schema, roll the app back"
   a one-command recovery. When a drop is unavoidable, the migration that performs it becomes Play
   A's new floor, and **the release that ships it updates § 3's floor block and § 2's table in the
   same commit**. Recording it when it is written costs a line; recording it when it is needed costs
   an outage, which is how the floor came to be two migrations stale (C-12-1). `DropTable`,
   `CreateIndex` and `DropIndex` do not move the floor; only a dropped column does, because only a
   dropped column appears in an older image's `SELECT` list.
2. **Expand/contract across two releases.** To remove or rename a column: release N adds the new
   one and writes to both; release N+1, once N is proven, stops writing the old one; release N+2
   drops it. Never in the release that first stops using it, until that release is unquestionably
   staying, the old column is the rollback path.

   **`DropChallengeRated` (2026-08-19) did not do this**: it is a single, bare `DropColumn` on
   `Challenges.Rated` with no prior release that stopped writing the column first. It shipped before
   this rule was written down here, § 2's table already prices it as a rollback floor, and there is
   nothing to fix retroactively, EF migrations are not rewritten after landing (C-1-9). Recorded so a
   future reader auditing this rule against the migration history does not conclude the rule has
   always held; it has held since, not from the start.
3. **`NOT NULL` needs a database default**, not just a C# one, or the previous app version's
   `INSERT`s start failing the moment the migration lands, before anything has gone wrong.
4. **A new unique index is a rollback hazard, not just a constraint.** `AddDisplayNameModeration` is
   the worked example in Play A: a unique index over a column an older release does not populate
   turns an app rollback into an outage. Adding one means either accepting that the rollback boundary
   stops there, or shipping the index one release after the code that fills the column.

## 5. The drill - 2026-08-08

The purpose was to answer whether `Down()` is a usable production tool, since the `Down()` methods
have existed since Phase 2 and had **never been executed by anyone**. Method: `postgres:16-alpine`
in a throwaway container, migrated to head with `dotnet ef database update`, seeded with the
minimum realistic data, then driven down and back up.

**Result 1, the head migration's `Down()` cannot run.** With two erased accounts present (both
carrying `DisplayName = 'Deleted account'` → `NormalizedDisplayName = 'deletedaccount'`, which the
filtered unique index exists to permit), reverting `AddAccountErasureAndConsent` fails:

```
SqlState: 23505
MessageText: could not create unique index "IX_AspNetUsers_NormalizedDisplayName"
```

`Down()` drops the filtered index and re-creates it **unfiltered**, and two live tombstones then
collide. EF wrapped the revert in a transaction, so the failure was atomic, the database was
verified still at head with all four columns and the filtered index intact, but the migration is
simply unrunnable on that data. Note what this means for testing it: delete one tombstone and it
succeeds. **A staging database with no erasures will pass this rollback and production will not.**

**Result 2, where it succeeds, it destroys the record.** With one tombstone, the revert completed
and the previously-erased account came back with no `DeletedAt`, no `TermsAcceptedAt`, no
`TermsVersion`, no `AgeConfirmedAt`, indistinguishable in the schema from an account that was never
erased. (It cannot be logged into: 8a.2 nulls `PasswordHash`. The data loss is the *record* of the
erasure, which is what a subject-access or regulator request asks for.)

**Result 3, the full chain reverts cleanly, and that is the problem.** Reverting all the way to
`InitialCreate` succeeded with no error: 11 tables left, `RefreshTokens`, `Reports`,
`ExternalLoginCodes` and `UserSanctions` gone, including a seeded permanent ban. Rolling forward
again to head restored every table and every column, and:

```
 sanctions | reports | tombstones
-----------+---------+------------
         0 |       0 |          0
```

A full down-and-up round trip returns the **schema** exactly and returns **none of the data**. The
banned account is unbanned; the reports are gone. Nothing warned, nothing failed, and the migration
history reads as if nothing happened.

**Result 4, the Play A boundary is real.** Against a head schema, two `INSERT`s that omit
`NormalizedDisplayName` (what a pre-8a.4 app version does) produced `INSERT 0 1` and then a 23505
duplicate-key error on `()`. This is the exception documented in Play A.

### What the drill does not prove

- **It ran against `postgres:16-alpine` on a laptop, not against Neon.** Neon is Postgres, and
  nothing exercised here is provider-specific, but the branch-and-repoint half of Play C is
  untested, that is 9c.4's drill, and it is still open.
- **It used synthetic rows**, inserted with SQL rather than produced by the application. The
  `deletedaccount` collision follows from `AccountDeletionService` setting a constant
  `DisplayName`, which is read from the code, not observed in a real erasure.
- **No rollback has ever been performed on a running system.** Nothing is deployed (DH.1/DH.2 are
  open), so Play A's `fly deploy --image` and Play C's repoint are documented procedures, not
  rehearsed ones.
- **The policy is untested by an incident.** It is a decision made from measured properties of the
  code, which is the best available evidence before there is production to have an incident in.
