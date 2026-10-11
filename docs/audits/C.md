# C — Code quality, correctness, consistency

Merged packet reports for category **C**. Individual packets were `docs/audits/C-N.md`; they now live as sections below. Index: [INDEX.md](INDEX.md).

---

## C-1 — Sectioned code quality
Date: 2026-08-22
Packet: docs/audits/INDEX.md

The section reports, the improvement plan, and the run log follow the findings below, in this same file.

### Verdict

The code is in good health and the audit's value is not the bug list. Across 11 sections and roughly
330 files, nothing was found that loses data or hurts a stranger today, and no finding survived
verification as Critical. What the run actually surfaced is a single recurring shape:
**enforcement by convention rather than by construction.** A rule is written down in `.claude/rules/`
or in a doc comment, applied correctly at four call sites, and missed at the fifth, with no test and
no type that would notice. Two passes over different folders independently found the same missing
sanction check in different endpoints, which is what turns that from an anecdote into the finding.
The three invariants most expensive to get wrong all held: `IGameClock` discipline is followed
everywhere with no wall-clock interval anywhere near think-time or flag-fall, the engine is pure and
deterministic, and the three-way rule mirror (`config/kirichess.rules.json`, generated `docs/RULES.md`,
`ui/lib/costPreview.ts`) shows no drift. Admin gating has no gap and sanction attribution cannot be
spoofed. Of the three High findings, two are "correct today, wrong later" and one is live: the ban
check missing from Google sign-in works only because a second gate catches it downstream, the REST
router bypass fires the day a second instance exists, and the reconnect data loss is visible to
players now.

Counts: **0 Critical, 3 High, 29 Medium, 40 Low.**

### Findings

Packet-level findings only. Section-level detail, including all 40 Low findings, follows under "C-1 section reports".

#### C-1-1 — Sanction enforcement is hand-applied per endpoint, and two endpoints were missed
- Severity: High
- Where: `src/KiriChess.Server/Auth/ExternalAuthEndpoints.cs:101-128` and
  `src/KiriChess.Server/Account/AccountDeletionService.cs:31-42`, against the checks present at
  `src/KiriChess.Server/Auth/AuthEndpoints.cs:172`, `:231`, `:281`
- Impact: `/api/auth/external/exchange` mints a token pair with no ban check, so a banned user who can
  complete the Google handshake keeps getting fresh 15-minute access tokens. Play stays blocked by
  `PlayEligibilityPolicy` and refresh does check, which caps this below Critical. Separately
  `POST /api/account/delete` is not ban-gated, so inside one access-token lifetime a just-banned player
  can free their email and handle. `SanctionPolicy.cs:23-27` enumerates its own enforcement points and
  names neither, so both read as omissions rather than decisions.
- Fix: add `BlockingSanctionAsync` to both, then close the class of miss: a table-driven test over every
  token-minting path, or a shared filter so a new endpoint opts out deliberately.

#### C-1-2 — A player who reconnects sees less of their own game than a spectator does
- Severity: High
- Where: `ui/app/game/[id]/page.tsx:392-401` and `:415-418`, against
  `ui/app/watch/[id]/page.tsx:158-175`; root cause at `src/KiriChess.Server/Games/RealtimeDtos.cs:7`
  versus `:80-85`
- Impact: every `onreconnected` and every mid-game reload clears the move list, captured material and
  the clock-economy graph for the player, while a spectator watching the same game keeps all three. The
  board and clocks stay correct. The cause is a DTO asymmetry, not a client oversight: `GameSnapshotDto`
  carries state and legal moves only, while `SpectatorSnapshotDto` carries a full move log. The clock
  graph is the feature that makes the variant readable, and this is live today.
- Fix: add the move log to `GameSnapshotDto`, declaring it in `HubContract.cs` first and regenerating
  OpenAPI per `.claude/rules/realtime.md`. An HTTP refetch on rejoin is a smaller interim fix.

#### C-1-3 — Four defects that are invisible on one machine and wrong on the first day of Phase 8.4
- Severity: Medium (trigger: Redis provisioned, a second instance exists)
- Where: `Games/GameEndpoints.cs:87-165` and `Games/PublicApiEndpoints.cs:297-355` (bypass
  `GameCommandRouter`); `Games/MatchNoShowWatcher.cs:57-58` (process-local `IsConnected`);
  `Infrastructure/Redis/RedisRegistration.cs:36-37` (multiplexer never disposed);
  `Ratings/LeaderboardWarmup.cs:41-83` (documented hourly re-warm never happens)
- Impact: a bot playing over REST gets a 404 for a running game; a player connected to another instance
  is aborted as a no-show after joining; the Redis connection is not closed on `fly deploy`; the
  leaderboard cache stops answering an hour after boot and never recovers. Redis being unprovisioned is
  a standing position, so none of this is a defect today. Shipping it unfixed into the moment that
  changes is the risk.
- Fix: route both REST surfaces through `IGameActionExecutor`, switch to `IsConnectedAsync`, register
  Redis via a factory, and either add the `PeriodicTimer` or drop the claim from the comment. Batch 3
  of the plan.

#### C-1-4 — A rating can be applied twice with nothing in the schema to stop it
- Severity: Medium (trigger: a process restart that re-persists an already-rated game)
- Where: `src/KiriChess.Server/Ratings/RatingService.cs:32-66`;
  `src/KiriChess.Server/Data/KiriChessDbContext.cs:143-150`
- Impact: the only guard against a double apply is an in-memory `TryRemove` in one caller on one
  instance. The `RatingHistory` index on `(UserId, Pool, CreatedAt)` is not unique and there is no
  constraint on `(GameId, UserId)`, so a duplicate silently double-counts rather than failing. A rating
  is accumulated over months and a silent double-count is not visible afterwards.
- Fix: a unique index on `RatingHistory(GameId, UserId)`. That is a migration: generate it, do not apply
  it without asking.

#### C-1-5 — The engine does not hold the fail-loudly guarantee engine.md states for it
- Severity: Medium (trigger: any caller reaching `KiriChessConfig` without going through the custom-game
  endpoint's own separate validation)
- Where: `src/KiriChess.Engine/Config/KiriChessConfig.cs:94-126` (no upper bound on any rule variable,
  no `Enum.IsDefined` on `MoveCostMode`) and `:345` (`Presets` is a public static mutable dictionary of
  shared mutable configs); unchecked economy math at `KiriChessEngine.cs:208`, `:228`, `:236`, `:245-246`
- Impact: `.claude/rules/engine.md` promises an invalid config fails at game creation. Today
  `GameEndpoints.BuildCustomConfig` validates separately and gets there first, so nothing is reachable
  through the API. The enforcement is duplicated rather than centralised, and large custom values would
  wrap int32 and sign-flip a clock delta in the creator's own game.
- Fix: per-field ceilings and `Enum.IsDefined` in `Validate()`; expose `Presets` read-only. A changed
  rule variable needs a cost-vector fixture entry, not only a unit test. Overlaps packet **C-8**.

#### C-1-6 — Turnstile loads on every route, contradicting the premise AUTH.md reasons from
- Severity: Medium
- Where: `ui/app/layout.tsx:64-70` against `docs/decisions/AUTH.md:178-180`
- Impact: AUTH.md states as fact that the only external script is Turnstile's, "on one route", and that
  premise carries weight in its localStorage-token risk accounting, since §1.7's trigger 2 is what
  converts a supply-chain compromise into a session-database compromise. The script is in the root
  layout, so it executes on every authenticated route. No new third party was added, so the trigger has
  not fired in the sense that reopens the cookie decision, but the exposure is wider than the document
  accounts for.
- Fix: scope the `<Script>` to the routes that need it, gated on the site key being set. Change the code
  to match the document rather than the other way round.

#### C-1-7 — The Stockfish worker leaks and can resolve the wrong game's move
- Severity: Medium
- Where: `ui/lib/bot.ts:1-2`, `:11-29`, `:36`
- Impact: a module-level worker singleton with one global resolver, never terminated. It keeps running
  after the game ends and after navigation, and a second overlapping search overwrites the resolver, so
  a stale `bestmove` can resolve the wrong promise. No tests exist for this file.
- Fix: scope the worker to the game, terminate on unmount, and key each search so a superseded reply is
  discarded.

#### C-1-8 — Modal dialogs in the one state axe is not allowed to see
- Severity: Medium
- Where: `ui/components/GameOverOverlay.tsx:102-104`, with `ReportDialog.tsx:63-67` and
  `BotGameDialog.tsx:56-60` as the correct shape; `ui/app/profile/page.tsx:88` for the skip link
- Impact: the end-of-game overlay is a bare `div` with no dialog role, no `aria-modal` and no label, and
  none of the three modals traps focus or handles `Escape`, so a keyboard user tabs into the hidden page
  behind. `e2e/tests/accessibility.spec.ts` deliberately excludes `game/[id]`, so nothing automated
  covers this. Separately the skip-to-content link is a no-op on `/profile`.
- Fix: match the dialog shape the other two already use and add one shared focus trap. Add
  `id="main-content"` to the profile `main`. Overlaps packet **P-5**.

#### C-1-9 — Documented policy and shipped artefact disagree in four places
- Severity: Medium
- Where: `Data/Migrations/20260819235137_DropChallengeRated.cs` (bare `DropColumn` against
  `docs/ops/MIGRATIONS.md` §4 expand-and-contract, and §2's cost table is six migrations stale);
  `Auth/AuthEndpoints.cs:37-39` versus `:137-141` (kill-switch 503 undeclared for codegen);
  `Program.cs:219` and `:234` (options read eagerly, the shape that already caught `EmailOptions` and
  `VapidOptions`); `ui/app/auth/callback/page.tsx:42-49` (an untested weaker duplicate of
  `ui/lib/challengeLink.ts:24-32`)
- Impact: none of these is broken in production; each is a place where the next person reading the doc
  is misled about what the code does.
- Fix: batch 8 of the plan. Overlaps packet **C-9**, which should take these as input.

#### C-1-10 — CI hygiene gaps on a repo whose Actions minutes have already run out
- Severity: Medium
- Where: `docker-compose.yml:3` (Postgres 15) versus `docker-compose.prod.yml:3` (16-alpine);
  `.github/workflows/ci.yml`, `deploy.yml`, `restore-drill.yml` (no `permissions:` block);
  `ci.yml:9`, `:64`, `codeql.yml`, `semgrep.yml` (no `timeout-minutes`, no `concurrency`)
- Impact: a contributor's local loop never exercises the Postgres major that E2E and self-hosters run;
  three workflows inherit a broader `GITHUB_TOKEN` scope than they need while five siblings already
  scope it; and the jobs that fire on every push are the ones with no runtime bound, on the constraint
  that currently blocks ROADMAP 10c.1 and 10c.5.
- Fix: bump the compose image, add `permissions: contents: read`, add timeouts and a concurrency group.
  Overlaps packet **I-3**.

#### C-1-11 — CancellationToken is missing on the read paths most likely to be slow
- Severity: Low (trigger: a moderator navigating away from a running detection query)
- Where: `Moderation/Detection/EngineDetectionEndpoints.cs:29-70`, `:77-107`;
  `Moderation/Detection/SandbaggingEndpoints.cs:60-118`, `:120-198`; `Ratings/RatingEndpoints.cs:15-107`;
  `Analytics/AnalyticsEndpoints.cs:42-63`, `:67-117`; `Moderation/ModerationEndpoints.cs:25`, `:54`,
  `:85`; `Moderation/ReportEndpoints.cs:20`, `:86`; `Account/StripeWebhookEndpoints.cs:53`, `:60`
- Impact: the two detection routes whose own doc comments describe them as multi-second queries are the
  two with no token at all. `CounterEndpoints.cs` in the same folder threads it correctly, so this is
  drift from a local convention rather than an unknown rule.
- Fix: one sweep, batch 10.

### Already covered

- localStorage tokens, CSP limits, moderator 2FA: [AUTH.md](../decisions/AUTH.md). C-1-6 is a drift
  against that document, not a reopening of its decision.
- Export, erasure, retention rights: [L.md](L.md). BE-4 explicitly did not re-report it.
- INDEX.md's O-3 row calling the repo public: [O.md:83](O.md) and [O.md:14](O.md) already caught
  it. `gh repo view` confirms the repo is private as of 2026-08-22, and one section reasoned from
  the stale line, so the line is now actively misleading audits.
- Redis unprovisioned, tournaments parked, Access on until launch, offsite backup and ops-probes never
  green: standing positions in [ROADMAP.md](../ROADMAP.md) and [DEPLOYMENT.md](../ops/DEPLOYMENT.md).
- Fly staging versus production drift and the `immediate` deploy strategy: INF-1 checked both closely
  and found them justified, so neither is filed.

### Action list

In order:

1. Batch 1, sanction enforcement plus the test that closes the class (C-1-1).
2. Batch 2, the reconnect snapshot and the suppressed challenge notice (C-1-2).
3. Batch 3, the four Phase 8.4 landmines (C-1-3), before 8.4 rather than during it.
4. Batch 5, engine config validation (C-1-5), coordinated with packet C-8.
5. Batch 6, frontend correctness (C-1-7).
6. Batch 8, doc-versus-code drift (C-1-9), coordinated with packet C-9.
7. Batch 10, the `CancellationToken` sweep (C-1-11).

Generate but do not apply: batch 4's unique index on `RatingHistory(GameId, UserId)` (C-1-4).

Owner:

1. Decide whether batch 3 lands now or is booked against Phase 8.4.
2. Decide whether optional extra guardrail files under `.claude/rules/` are wanted. Every finding in batches 1, 3 and 8
   is a miss against a rule that is already written down, which argues for tests rather than an eighth
   rule file.
3. Fix INDEX.md's O-3 row, which has now misled two audits.
4. Batch 9 item 3 touches the Actions-minutes constraint blocking 10c.1 and 10c.5.

Packets to hand this to rather than duplicate: **S-1** and **S-3** (C-1-1), **C-8** (C-1-5), **C-9**
(C-1-9), **P-5** (C-1-8), **C-10** (the ranked test gaps in batch 11), **I-3** (C-1-10).

---


### C-1 section reports

The packet summary is above. The section inspections, the improvement plan, and the run log from that same pass follow here.

### Section BE-1: Auth/Security (`src/KiriChess.Server/Auth/`)

#### Summary

20 `.cs` files exist under `src/KiriChess.Server/Auth/` (the task packet said 21; glob confirms only
20 on disk, all read in full). Plus the JWT/cookie/external-login registration block in `Program.cs`
(roughly lines 282-430, 701-714). Read against `.claude/rules/project.md`, `backend.md`,
`security.md`, `testing.md`, and `docs/decisions/AUTH.md` (not re-derived; AUTH.md already covers
localStorage token placement, the CSP's real limits, and the moderator/2FA gap — none of that is
repeated here).

Overall the folder is unusually well documented and internally consistent: every token/code/ticket
service follows the same "random bytes, SHA-256 hash only, never store the raw value" shape, the
lockout/ban/2FA ordering in `AuthEndpoints.cs` is deliberate and commented, and open-redirect handling
(`SafeReturnPath`) is careful. The one real defect found is a genuine gap, not a style nit: the OAuth
token-exchange endpoint never checks for an account ban, while every other path that mints a fresh
token pair does.

**Counts: 1 Blocker (Critical), 2 Should-fix (Medium), 2 Nits (Low).**

#### Findings

##### Blockers

###### BE-1-1 — `/api/auth/external/exchange` never checks for an account ban; a banned user can sign back in via Google indefinitely
- Severity: **Critical**
- Where: `src/KiriChess.Server/Auth/ExternalAuthEndpoints.cs:101-128` (the `/external/exchange`
  handler — no `SanctionPolicy` dependency, no `BlockingSanctionAsync` call anywhere in the file).
  Contrast with the same check present at three other token-issuing sites:
  `src/KiriChess.Server/Auth/AuthEndpoints.cs:172-174` (`/login`), `:231-233` (`/login/2fa`),
  `:281-283` (`/refresh`).
- Impact: `AuthEndpoints.cs` enforces an `AccountBan` at every point a token pair is minted or renewed
  — password login, the 2FA step, and refresh (the refresh check exists specifically so a token
  already in a banned player's hands dies at its next renewal; see the Phase 8.5 roadmap entry and
  `AuthEndpoints.cs:278-280`'s own comment on why refresh is "the enforcement point that actually
  closes the ban hole"). `/external/exchange` mints a token pair through the exact same
  `AuthEndpoints.IssueAuthResponseAsync` helper (`ExternalAuthEndpoints.cs:125`) but does so with no
  ban check at all. `ExternalLoginService.SignInOrCreateAsync` (`ExternalLoginService.cs:60-70`,
  step 3 at `:82-97`) will happily sign in an existing banned account, or auto-link a fresh Google
  login onto one via its verified email — bans are not consulted there either. A banned user who has
  (or creates) a Google account with the same verified email as their banned KiriChess account can
  repeat the OAuth handshake to mint a brand-new 15-minute access token whenever they like, without
  ever calling `/refresh` (the one path that would catch them), fully bypassing moderation. The 9d.2
  2FA gate *was* correctly ported to this endpoint (`ExternalAuthEndpoints.cs:118-123`); the earlier
  8.5 ban gate was not — this reads as an omission, not a documented decision. No test exercises this:
  `tests/KiriChess.Server.Tests/ExternalLoginTests.cs` has no ban-related assertions at all, and no 2FA
  ones either (the exchange endpoint's `TwoFactorRequired` branch is also untested).
- Fix: add a `SanctionPolicy` dependency to the `/external/exchange` handler and call
  `sanctions.BlockingSanctionAsync(user.Id, SanctionScope.Account, ct)` before issuing tokens, mirroring
  `AuthEndpoints.cs:172-174` exactly (same message shape, same 403), and add the equivalent
  `.Produces<ErrorResponse>(StatusCodes.Status403Forbidden)` metadata. Add one test in
  `ExternalLoginTests.cs` asserting a banned account's exchange returns 403, alongside the missing
  `TwoFactorRequired`-branch test.

##### Should fix

###### BE-1-2 — `/api/auth/register`'s kill-switch 503 is undeclared in the endpoint's OpenAPI metadata
- Severity: **Medium**
- Where: `src/KiriChess.Server/Auth/AuthEndpoints.cs:37-39` (the actual `Status503ServiceUnavailable`
  response, gated by `KillSwitch.Registration`) vs the `.Produces` chain at `:137-141`, which only
  declares `200` and `ProducesValidationProblem()`.
- Impact: `backend.md` states `.Produces<T>()` for OpenAPI is load-bearing because client codegen
  depends on it. The identical kill-switch-503 pattern is correctly declared elsewhere in the same
  codebase — `src/KiriChess.Server/Games/PublicApiEndpoints.cs:244-252` and
  `src/KiriChess.Server/Games/SeekEndpoints.cs:27-34` both pair the raw `Status503ServiceUnavailable`
  response with a matching `.Produces(StatusCodes.Status503ServiceUnavailable)`. `/api/auth/register`
  is the one place this pairing was dropped, so `docs/api/openapi.json` and the generated
  `ui/lib/api.generated.ts` client have no knowledge of this real, reachable response shape.
- Fix: add `.Produces(StatusCodes.Status503ServiceUnavailable)` to the chain at
  `AuthEndpoints.cs:137-141` and regenerate the OpenAPI doc / UI client
  (`dotnet run --project src/KiriChess.Server -- --export-openapi docs/api/openapi.json` then
  `npm run gen:api`).

###### BE-1-3 — `/external/exchange`'s 2FA-required branch has zero test coverage
- Severity: **Medium**
- Where: `src/KiriChess.Server/Auth/ExternalAuthEndpoints.cs:118-123` (the `TwoFactorRequiredResponse`
  branch); `tests/KiriChess.Server.Tests/ExternalLoginTests.cs` (no `TwoFactor`/`2fa` match anywhere in
  the file) and `tests/KiriChess.Server.Tests/TwoFactorTests.cs` (no `external`/`External` match
  either).
- Impact: this branch is exactly the kind of cross-feature interaction `testing.md` calls out as
  needing coverage (it is the one place OAuth and 2FA logic intersect — a linked Google account must
  not bypass TOTP). It currently has no automated check that a 2FA-enabled account routes through the
  ticket flow instead of getting tokens directly, so a regression here (e.g. someone reordering the
  `if (user.TwoFactorEnabled)` check while fixing BE-1-1) would ship silently.
- Fix: add a test to `ExternalLoginTests.cs` that links/creates a 2FA-enabled account and asserts
  `/external/exchange` returns `TwoFactorRequiredResponse` rather than a token pair, matching the shape
  of the existing `/login` 2FA tests.

##### Nits

###### BE-1-4 — Stale comment: the `/api/auth` group comment claims the whole group is IP-partitioned "since the caller is by definition not yet authenticated," but `/logout-all` requires auth
- Severity: **Low** (cosmetic; only triggers if someone reads the comment as a design constraint)
- Where: `src/KiriChess.Server/Auth/AuthEndpoints.cs:22-24` (the comment) vs `:307-316`
  (`/logout-all`, which has `.RequireAuthorization()` and reads `principal.GetUserId()`).
- Fix: reword the comment to note `/logout-all` is the one authenticated route in the group and is
  IP-partitioned anyway for consistency, not because the caller is anonymous.

###### BE-1-5 — Unused `CancellationToken ct` parameters in two `TwoFactorEndpoints` handlers
- Severity: **Low**
- Where: `src/KiriChess.Server/Auth/TwoFactorEndpoints.cs:22-23` (`GET /api/account/2fa`) and
  `:37-39` (`POST /enrol`) — both declare `CancellationToken ct` but neither handler body uses it
  (the only calls inside are `UserManager<ApplicationUser>` methods, none of which accept a
  `CancellationToken`).
- Fix: drop the unused parameter from both handler signatures, or note inline why it is kept for
  signature-shape consistency with the other 2FA handlers that do use it.

#### Files inspected

- `src/KiriChess.Server/Auth/ExternalAuthOptions.cs`
- `src/KiriChess.Server/Auth/ExternalLoginCodeService.cs`
- `src/KiriChess.Server/Auth/ExternalLoginService.cs`
- `src/KiriChess.Server/Auth/JwtOptions.cs`
- `src/KiriChess.Server/Auth/TokenService.cs`
- `src/KiriChess.Server/Auth/TwoFactorChallengeService.cs`
- `src/KiriChess.Server/Auth/TwoFactorDtos.cs`
- `src/KiriChess.Server/Auth/TwoFactorOptions.cs`
- `src/KiriChess.Server/Auth/ExternalAuthEndpoints.cs`
- `src/KiriChess.Server/Auth/RefreshTokenService.cs`
- `src/KiriChess.Server/Auth/AuthDtos.cs`
- `src/KiriChess.Server/Auth/ApiTokenClaims.cs`
- `src/KiriChess.Server/Auth/ApiTokenService.cs`
- `src/KiriChess.Server/Auth/ApiTokenAuthenticationHandler.cs`
- `src/KiriChess.Server/Auth/HubAuthOverlayMiddleware.cs`
- `src/KiriChess.Server/Auth/TermsVersion.cs`
- `src/KiriChess.Server/Auth/BreachedPasswordChecker.cs`
- `src/KiriChess.Server/Auth/ClaimsPrincipalExtensions.cs`
- `src/KiriChess.Server/Auth/TurnstileVerifier.cs`
- `src/KiriChess.Server/Auth/TwoFactorEndpoints.cs`
- `src/KiriChess.Server/Auth/AuthEndpoints.cs`
- `src/KiriChess.Server/Program.cs` (auth/JWT/cookie/external-login wiring only: ~lines 282-430,
  701-714)

Also read for ground truth / cross-checks (not audited as part of this section, no findings written
against them): `docs/decisions/AUTH.md`, `docs/audits/L.md`, `docs/audits/L.md`,
`src/KiriChess.Server/Data/Entities/ApplicationUser.cs`, `src/KiriChess.Server/Account/AccountDeletionService.cs`,
`src/KiriChess.Server/Retention/RetentionSweep.cs` + `RetentionSchedule.cs`,
`src/KiriChess.Server/Games/ChallengeService.cs`, `src/KiriChess.Server/Games/PublicApiEndpoints.cs`,
`src/KiriChess.Server/Games/SeekEndpoints.cs`, `src/KiriChess.Server/Infrastructure/RateLimiting.cs`,
`tests/KiriChess.Server.Tests/TokenServiceTests.cs`, `tests/KiriChess.Server.Tests/ExternalLoginTests.cs`,
`tests/KiriChess.Server.Tests/TwoFactorTests.cs`, `tests/KiriChess.Server.Tests/AuthLifecycleTests.cs`.

### Section BE-2: Games/Realtime

#### Summary

Read every `.cs` file under `src/KiriChess.Server/Games/` (51 files, including `Games/Distributed/`). This is, on the whole, exceptionally disciplined code for a one-person hobby project: `IGameClock` is respected everywhere (no `DateTime.Now`/`UtcNow` subtraction used as a duration anywhere near think-time or `FlagFallMonitor`), the Redis-optional pattern (`_connection.Enabled` checked before every Redis call, safe fallback on outage) is applied with total consistency across every Distributed/ file, and every fire-and-forget task except one has a catch-all guard. `HubContract.cs` is honoured; nothing found calls `SendAsync`/`InvokeAsync` off-contract, and rate limiting / connection budget usage was not touched by anything in this section (no new hub methods added here to check against the guardrail).

The one architectural gap worth escalating: the **raw HTTP move/resign endpoints never route through `GameCommandRouter`/`IGameActionExecutor`**, unlike every hub action. This is dormant today (Redis is unprovisioned, so every instance is its own owner and `GameRegistry.Get` always finds a live game if it exists), but it directly contradicts the standing rule in `realtime.md` ("any new game action needs to go through the router") and would silently 404 a legitimate move or resign the day a second Fly machine is added, for both the first-party UI (if it ever calls the REST fallback) and every third-party bot on `/api/v1/games/{id}/moves`.

Total: **0 Critical, 1 High, 3 Medium, 3 Low.**

#### Findings

##### Blockers

None. Nothing here loses data or is exploitable by a stranger today.

##### Should fix

**1. [High] HTTP move/resign endpoints bypass distributed ownership entirely — silently wrong the day a second instance exists.**
`src/KiriChess.Server/Games/GameEndpoints.cs:87-165` (`GET /{id:guid}`, `POST /{id:guid}/moves`, `POST /{id:guid}/resign`) and `src/KiriChess.Server/Games/PublicApiEndpoints.cs:297-355` (`POST /api/v1/games/{id}/moves`, `/resign`) all call `registry.Get(id)` directly and, on a miss, return `Results.NotFound()`. Every hub action (`GameHub.SubmitMove`, `Resign`, etc.) goes through `IGameActionExecutor`, which resolves ownership via `GameCommandRouter` and forwards to the owning instance when the session isn't local (`GameActionExecutor.cs:113-141`). The two REST endpoints have no such path — they were never migrated onto `GameActionExecutor` when Phase 8.4 landed. With Redis disabled (today's deployment) this is invisible because there is only one instance and `registry.Get` always succeeds. The moment Redis/multi-instance is turned on, any bot playing over `/api/v1/games/{id}/moves` against an instance it isn't pinned to (or any first-party fallback call) gets a 404 for a game that is very much still ongoing, indistinguishable from the game having vanished. `TwoInstanceGameTests.cs:167-175` only exercises the hub's `SubmitMove`, confirming this path is untested.
*Fix:* Route `GameEndpoints`'/`PublicApiEndpoints`' move/resign/join handlers through `IGameActionExecutor.SubmitMoveAsync`/`ResignAsync` the same way `GameHub` does, instead of touching `GameRegistry` directly, or explicitly document these two REST surfaces as single-instance-only until they're migrated.

**2. [Medium] `MatchNoShowWatcher` checks only local connectivity, not cluster-wide — can wrongly abort a game as a no-show.**
`src/KiriChess.Server/Games/MatchNoShowWatcher.cs:57-58` calls `_connections.IsConnected(gameId, whiteId)` / `IsConnected(gameId, blackId)` (the process-local, synchronous check). Every other place in this codebase that has to answer "is this player actually connected" after Phase 8.4 uses the cross-instance-aware `IsConnectedAsync` instead — see `GameConnectionTracker.RunGraceAsync` (`GameConnectionTracker.cs:251`) and `GameRecoveryService.SweepUnresumedAsync` (`GameRecoveryService.cs:142`), both of which were updated for exactly this reason. A player matched and connected to a *different* instance than the one that armed this 45-second join-deadline timer would read as disconnected here and have their game silently `AbortNoShow`'d even though they did join. Dormant under today's single-instance deployment for the same reason as finding 1, and there is no test file for `MatchNoShowWatcher` at all (`tests/KiriChess.Server.Tests` has no `MatchNoShowWatcherTests.cs`), so this would not be caught even when Redis is enabled.
*Fix:* Change lines 57-58 to `await _connections.IsConnectedAsync(gameId, whiteId)` / `blackId` (both already awaitable in an `async` method), matching the sibling call sites.

**3. [Medium] `ChallengeEndpoints.AcceptChallengeAsync` ties the post-creation broadcast/notification to the accepting request's own cancellation token.**
`src/KiriChess.Server/Games/ChallengeEndpoints.cs:200-206`: after the game is created and persisted, the group broadcast (`hub.Clients.Group(...).SendAsync("ChallengeAccepted", ..., ct)`) and the best-effort email/push (`notifications.NotifyChallengeAcceptedAsync(..., ct)`) both receive the handler's own `CancellationToken ct`, which ASP.NET Core binds to the accepting player's `HttpContext.RequestAborted`. If that player's connection drops between claiming the challenge and this point (mobile network flap, tab close), the token fires and both the SignalR notice to the *challenger* and the fallback email are dropped — the game is real and playable, but the person who created the link never finds out it was accepted through any channel until they refresh. `NotifyChallengeAcceptedAsync` swallows the resulting `OperationCanceledException` silently (its catch-all logs at Warning), so this fails invisibly.
*Fix:* Pass `CancellationToken.None` (or a short-lived independent token) to the broadcast and notification calls once the game itself is committed — the accepting player's own connection state should not be able to suppress a notice meant for someone else.

##### Nits

**4. [Low] `GameConnectionTracker.RunGraceAsync` has no top-level catch, unlike its two siblings.**
`src/KiriChess.Server/Games/GameConnectionTracker.cs:229-267`, invoked fire-and-forget from `OnDisconnected` (`GameConnectionTracker.cs:214`, `_ = RunGraceAsync(...)`). `MatchNoShowWatcher.RunAsync` (`MatchNoShowWatcher.cs:50` / `79-83`) and `RematchNotificationService.RunAsync` (`RematchNotificationService.cs:60` / `85-90`) both wrap their entire body in `try { ... } catch (Exception ex) { _logger.LogError/Warning(...); }` specifically because they run detached from any request. `RunGraceAsync` only catches `OperationCanceledException` around the `Task.Delay` and `InvalidGameActionException`/`HubException` around the final `AbandonAsync` call — an exception from the initial `SendAsync("OpponentDisconnected", ...)` (line 235) or any other unexpected exception type from `AbandonAsync` becomes an unobserved task fault, and (if it happens before line 238) the grace timer for that disconnect silently never arms at all.
*Fix:* Wrap the method body in the same top-level `try/catch (Exception ex) => _logger.LogError(...)` pattern the other two detached-timer classes already use.

**5. [Low] `GameSession` constructor logs via `Console.WriteLine`, not `ILogger`.**
`src/KiriChess.Server/Games/GameSession.cs:93-94`. Every other class in this section (`GameActionExecutor`, `GameRegistry`, `GameOwnership`, `GameConnectionTracker`, etc.) injects and uses `ILogger<T>`; `GameSession` is constructed directly (`new GameSession(...)` in `GameRegistry.CreateAsync`), not resolved from DI, so it has no logger to inject — but a raw `Console.WriteLine` bypasses structured logging, log levels, and whatever sink production actually uses (Better Stack per the security/testing rules), so this line is invisible wherever the real logs are read.
*Fix:* Either drop the line (Phase-2-era debug aid, `_metrics.GameStarted` already records this) or thread an `ILogger` through and log structurally.

**6. [Low] `LatencyProbe` hardcodes `SystemGameClock.Instance` instead of taking `IGameClock` via DI.**
`src/KiriChess.Server/Games/LatencyProbe.cs:25`: `private readonly IGameClock _clock = SystemGameClock.Instance;`. The clock usage itself is correct (monotonic `Timestamp`/`Elapsed` for the round-trip measurement, `testing.md`'s rule is followed in substance), but every other clock-sensitive type in this section (`GameSession`, `GameRecoveryService` via `GameSession.Restore`) accepts `IGameClock` through its constructor so tests can substitute `FakeGameClock`. This one can't be, without a code change, which is a minor inconsistency against the stated testing convention rather than a correctness bug.
*Fix:* Add an `IGameClock` constructor parameter (default to `SystemGameClock.Instance` if not supplied) for consistency and testability, low priority.

#### Files inspected

- `src/KiriChess.Server/Games/HubContract.cs`
- `src/KiriChess.Server/Games/IGameClock.cs`
- `src/KiriChess.Server/Games/GameHub.cs`
- `src/KiriChess.Server/Games/PresenceHub.cs`
- `src/KiriChess.Server/Games/GameSession.cs`
- `src/KiriChess.Server/Games/GameActionExecutor.cs`
- `src/KiriChess.Server/Games/GameRegistry.cs`
- `src/KiriChess.Server/Games/GameConnectionTracker.cs`
- `src/KiriChess.Server/Games/FlagFallMonitor.cs`
- `src/KiriChess.Server/Games/AbandonmentPolicy.cs`
- `src/KiriChess.Server/Games/LagCompensationOptions.cs`
- `src/KiriChess.Server/Games/LatencyProbe.cs`
- `src/KiriChess.Server/Games/MatchNoShowWatcher.cs`
- `src/KiriChess.Server/Games/PresenceBroadcaster.cs`
- `src/KiriChess.Server/Games/PresenceHeartbeat.cs`
- `src/KiriChess.Server/Games/PresenceTracker.cs`
- `src/KiriChess.Server/Games/SpectatorTracker.cs`
- `src/KiriChess.Server/Games/LiveGamePersister.cs`
- `src/KiriChess.Server/Games/SnapshotPersistenceOptions.cs`
- `src/KiriChess.Server/Games/ChallengeOptions.cs`
- `src/KiriChess.Server/Games/PlayEligibilityPolicy.cs`
- `src/KiriChess.Server/Games/GameReplayBuilder.cs`
- `src/KiriChess.Server/Games/ReplayDtos.cs`
- `src/KiriChess.Server/Games/GameNameResolver.cs`
- `src/KiriChess.Server/Games/GameRecoveryService.cs`
- `src/KiriChess.Server/Games/RematchService.cs`
- `src/KiriChess.Server/Games/RematchNotificationService.cs`
- `src/KiriChess.Server/Games/RealtimeDtos.cs`
- `src/KiriChess.Server/Games/SeekEndpoints.cs`
- `src/KiriChess.Server/Games/MatchQueue.cs`
- `src/KiriChess.Server/Games/MatchmakingService.cs`
- `src/KiriChess.Server/Games/PairingNotificationService.cs`
- `src/KiriChess.Server/Games/GameHistoryQuery.cs`
- `src/KiriChess.Server/Games/PublicApiDtos.cs`
- `src/KiriChess.Server/Games/PgnBuilder.cs`
- `src/KiriChess.Server/Games/ReplayEndpoints.cs`
- `src/KiriChess.Server/Games/GameDtos.cs`
- `src/KiriChess.Server/Games/GameEndpoints.cs`
- `src/KiriChess.Server/Games/ChallengeDtos.cs`
- `src/KiriChess.Server/Games/ChallengeService.cs`
- `src/KiriChess.Server/Games/ChallengeEndpoints.cs`
- `src/KiriChess.Server/Games/PublicApiEndpoints.cs`
- `src/KiriChess.Server/Games/Distributed/GameCommandRouter.cs`
- `src/KiriChess.Server/Games/Distributed/GameDistribution.cs`
- `src/KiriChess.Server/Games/Distributed/GameReconnectNotice.cs`
- `src/KiriChess.Server/Games/Distributed/LiveGameIndex.cs`
- `src/KiriChess.Server/Games/Distributed/OrphanedGameSweeper.cs`
- `src/KiriChess.Server/Games/Distributed/GameOwnership.cs`
- `src/KiriChess.Server/Games/Distributed/GameOwnershipHeartbeat.cs`
- `src/KiriChess.Server/Games/Distributed/GameCommandEnvelope.cs`
- `src/KiriChess.Server/Games/Distributed/LiveGameStateDto.cs`

Also cross-referenced (not part of this section's line count, read to confirm test-coverage gaps): `tests/KiriChess.Server.Tests/TwoInstanceGameTests.cs`, and confirmed no `MatchNoShowWatcherTests.cs` exists in `tests/KiriChess.Server.Tests/`.

### Section BE-5: Data + Infrastructure + Program.cs

#### Summary

Read every file in `src/KiriChess.Server/Data/` (entities, `KiriChessDbContext.cs`, `DesignTimeDbContextFactory.cs`,
`DevSeeder.cs`, `JsonValueConverter.cs`, migration file names/coherence, skipping generated migration
bodies per instructions), all 48 files in `src/KiriChess.Server/Infrastructure/`, and `Program.cs`
in full.

Overall quality is very high: every Redis-backed path checks `Enabled` before touching the
connection, every Redis key/channel goes through `IRedisConnection.Key()`/`Channel()` (so it is
always prefix-namespaced), every cache write carries a TTL, and the deferred-options-read discipline
(`Configure<T>` + `IOptions<T>`, or a factory resolved after `Build()`) is followed almost
everywhere, with doc comments that explicitly explain *why* for each choice. No blockers were found.
Findings below are all narrow, cited gaps against the codebase's own stated rules, not
speculative style complaints.

Counts: **0 Blockers, 3 Should-fix, 4 Nits.**

#### Findings

##### Blockers

None.

##### Should fix

1. **`Program.cs:219` and `Program.cs:234` — `BreachedPasswordCheckOptions`/`TurnstileOptions` are
   read eagerly off `builder.Configuration` and baked into a DI singleton before `Build()`, the exact
   shape that already broke `EmailOptions`/`VapidOptions` overridability in `WebApplicationFactory`
   tests.**
   ```
   var breachCheckOptions = BreachedPasswordCheckOptions.From(builder.Configuration);   // :219
   builder.Services.AddSingleton(breachCheckOptions);
   ...
   var turnstileOptions = TurnstileOptions.From(builder.Configuration);                  // :234
   builder.Services.AddSingleton(turnstileOptions);
   ```
   Unlike `EmailOptions`/`ExternalAuthOptions`/`RedisOptions` (which are read eagerly *because* the
   value decides what else to register, and that constraint is documented at each call site), both
   comments here say the DI registration is "unconditional" and only the `Enabled` flag read *inside*
   `IsBreachedAsync`/`IsValidAsync` gates the outbound call — nothing forces these two off
   `builder.Configuration`. `AuthLifecycleTests.cs:55` sets `Auth:BreachedPasswordCheckEnabled` via a
   `WebApplicationFactory` config override, but only to `"false"`, which is already the default, so it
   never exercises the failure mode. A future test flipping either flag to `true` through
   `ConfigureAppConfiguration` will silently see the old value, same as the EmailOptions/VapidOptions
   incident this pattern already caused once.
   **Fix:** switch both to `services.Configure<T>(section)` + `IOptions<T>` (or a factory resolved
   from `IConfiguration` at first use), the same pattern already used for `RetentionOptions`,
   `ChallengeOptions` and `ContentModerationOptions` a few lines away in the same file.

2. **`src/KiriChess.Server/Data/Migrations/20260819235137_DropChallengeRated.cs` is a bare
   `DropColumn`, violating `docs/ops/MIGRATIONS.md` §4's own rules 1 and 2.**
   ```csharp
   protected override void Up(MigrationBuilder migrationBuilder)
   {
       migrationBuilder.DropColumn(name: "Rated", table: "Challenges");
   }
   ```
   MIGRATIONS.md §4 states plainly: "Additive first. A release adds columns and tables; it does not
   drop or rename them... that is what makes 'leave the schema, roll the app back' a one-command
   recovery," and rule 2 requires an expand/contract split across two releases for any removal. This
   migration drops `Challenges.Rated` in one step with no prior "stop writing" release. Rolling the
   *app* back (Play A, MIGRATIONS.md §3) to any version before this migration while the schema is at
   head would break the moment that older code tries to `INSERT` a value into a column that no longer
   exists — exactly the failure mode §4 rule 4 warns about for a different case (unique index) and
   rule 1 exists to prevent generally. Separately, MIGRATIONS.md §2's cost table already documents
   itself as stale ("four migrations have landed since AddAccountErasureAndConsent... unpriced") but
   is now six migrations behind, including this one, the only destructive one among them, and it is not
   mentioned at all.
   **Fix:** no retroactive schema fix needed — `Rated` held no real data (Challenges are always
   unrated by design, per `Challenge.cs`'s own doc comment) — but add a row to MIGRATIONS.md §2 for
   this drop, and follow the two-release expand/contract pattern for any future column removal.

3. **`src/KiriChess.Server/Infrastructure/Redis/RedisRegistration.cs:36-37` — `RedisConnection` is
   registered as a pre-built instance, so the DI container never disposes it.**
   ```csharp
   var connection = new RedisConnection(options, instance);       // :36
   services.AddSingleton<IRedisConnection>(connection);            // :37
   ```
   `RedisConnection` implements `IDisposable`/`IAsyncDisposable` specifically to close the
   `ConnectionMultiplexer` on shutdown (`RedisConnection.cs:57-69`), but the built-in
   `Microsoft.Extensions.DependencyInjection` `ServiceProvider` only disposes singletons it creates
   itself via a type or factory registration — a service registered by handing the container an
   already-constructed instance is treated as externally owned and is never disposed. Because
   `connection` is `new`'d directly and passed to `AddSingleton<IRedisConnection>(connection)` rather
   than a factory, `Dispose()`/`DisposeAsync()` are dead code as far as the host's shutdown path is
   concerned. Confirmed by `RedisFoundationTests.cs` (lines 24, 35, 39, 81, 136, 151, 172, 186-187):
   every test that exercises `Dispose()` constructs and disposes `RedisConnection` directly, none goes
   through `AddKiriChessRedis`, so this gap has no test coverage either.
   **Trigger:** any graceful shutdown (e.g. a `fly deploy` release) once `Redis:Configuration` is set.
   Currently dormant since Redis is unprovisioned in production.
   **Fix:** register via a factory (`services.AddSingleton<IRedisConnection>(sp => new
   RedisConnection(...))`) or register the concrete `RedisConnection` type itself and resolve
   `IRedisConnection` from it, so the container owns and disposes the one instance it constructs.

##### Nits

4. **`RedisRegistration.cs:29` and `:35` construct two independent `InstanceIdentity` objects.**
   ```csharp
   services.AddSingleton<InstanceIdentity>();          // :29 — DI-owned, resolved by InstanceRegistry/InstanceHeartbeat/ownership services
   var instance = new InstanceIdentity();               // :35 — separate, used only to build RedisConnection
   var connection = new RedisConnection(options, instance);
   ```
   `InstanceIdentity`'s fallback constructor (`InstanceIdentity.cs:16-22`) appends a random 8-character
   suffix whenever `FLY_MACHINE_ID` is unset — true for every environment this project runs in today
   (local dev, CI, tests; nothing is deployed to Fly yet). The two instances will therefore usually
   carry different `Id` values. The DI-resolved one is what `InstanceRegistry`, `InstanceHeartbeat`,
   and the game-ownership/presence services all share consistently; the second, orphaned instance is
   used only to build `RedisConnection.CreateMultiplexer()`'s `ClientName`
   (`RedisConnection.cs:53`), which its own comment says is meant to be "readable in CLIENT LIST
   during an incident" — but it won't match the id shown anywhere else (the `instances` Redis set,
   game-ownership logs, Fly machine id in the app-boot log line). Diagnostic-only impact; no
   correctness effect on ownership or routing.

5. **`src/KiriChess.Server/Data/Entities/WebPushSubscription.cs:14` — `Id` has no `= Guid.NewGuid()`
   default**, unlike every other entity in `Data/Entities/` (`Game`, `Rating`, `RefreshToken`,
   `ApiToken`, `Challenge`, `Report`, `UserSanction`, etc. all default `Id`). Currently harmless —
   `PushSubscriptionService.SubscribeAsync` (`PushSubscriptionService.cs:37`) always sets it
   explicitly — but it breaks the established convention that a future caller following the pattern
   used everywhere else in this codebase (`new Entity { ... }` relying on the default) would silently
   produce a `Guid.Empty` row.

6. **Two test-coverage gaps, both consistent with the project's own stated pattern of guarding
   against drift (`testing.md`'s "Contract/drift tests" section):**
   - No test exercises the DI-driven disposal path for `IRedisConnection` (see finding 3) — every
     existing test in `RedisFoundationTests.cs` bypasses `RedisRegistration.AddKiriChessRedis` and
     disposes `RedisConnection` directly.
   - No test asserts the EF model has no pending changes against the last migration/snapshot (EF
     Core's `context.Database.GetPendingModelChanges()`, available since EF Core 9).
     `DatabaseSmokeTests.cs` runs `MigrateAsync()` against a real Postgres but does not check that
     `OnModelCreating`'s current output still matches `KiriChessDbContextModelSnapshot.cs` — the same
     class of drift `OpenApiContractTests`/`MetricsContractTests`/`LegalDocumentDriftTests` exist to
     catch for other generated artifacts.

7. **`Infrastructure/Redis/InstanceRegistry.cs`'s `IsRedisOutage` filter (line ~151) only catches
   `RedisConnectionException`/`RedisTimeoutException`, and `InstanceHeartbeat.StartAsync`
   (`InstanceHeartbeat.cs:38-41`) awaits `_registry.RegisterAsync(...)` with no surrounding
   try/catch.** Any other Redis-thrown exception during the heartbeat hosted service's `StartAsync`
   (e.g. `RedisServerException`) would propagate out of host startup — the outcome the class's own
   header comment says must never happen ("A Redis outage degrades to single-instance behaviour; it
   must never take the process down"), and the opposite of `RedisRateLimiter`'s deliberately broad
   `catch (Exception)` for the same stated goal. Low severity because `InstanceHeartbeat` is only
   registered when `RedisOptions.Enabled` is true, and Redis is unprovisioned in production today —
   worth revisiting before Redis is actually turned on.

#### Files inspected

**Data/**
- `Data/KiriChessDbContext.cs`
- `Data/DesignTimeDbContextFactory.cs`
- `Data/DevSeeder.cs`
- `Data/JsonValueConverter.cs`
- `Data/Entities/ApplicationUser.cs`, `Game.cs`, `GameEnums.cs`, `MoveRecord.cs`, `Rating.cs`,
  `RatingHistory.cs`, `RefreshToken.cs`, `ApiToken.cs`, `ExternalLoginCode.cs`,
  `TwoFactorChallenge.cs`, `Challenge.cs`, `RematchOffer.cs`, `Report.cs`, `UserSanction.cs`,
  `WebPushSubscription.cs`, `KillSwitchState.cs`, `DailyCounter.cs`
- `Data/Migrations/` — all 20 migration file names/ordering checked for coherence (naming,
  monotonic timestamps, additive-vs-destructive shape); `20260819235137_DropChallengeRated.cs` and
  `20260818014854_AddKillSwitches.cs` read in full; generated `.Designer.cs` bodies and
  `KiriChessDbContextModelSnapshot.cs` spot-checked for table-name coverage only, per scope
  instructions (not read line-by-line).

**Infrastructure/** (all 48 files)
- `Email/EmailDispatchService.cs`, `EmailMessage.cs`, `EmailOptions.cs`, `QueuedEmailSender.cs`,
  `ResendEmailSender.cs`, `NotificationCategory.cs`, `UnsubscribeTokenService.cs`,
  `NotificationPreferenceService.cs`, `UnsubscribeLink.cs`, `NotificationEndpoints.cs`,
  `EmailTemplates.cs`
- `Push/VapidOptions.cs`, `IPushSender.cs`, `PushSubscriptionService.cs`, `PushDtos.cs`,
  `PushNotificationService.cs`, `PushEndpoints.cs`
- `Redis/InstanceHeartbeat.cs`, `IRedisConnection.cs`, `InstanceIdentity.cs`, `InstanceRegistry.cs`,
  `RedisConnection.cs`, `RedisOptions.cs`, `RedisRegistration.cs`
- `OpenApi/MultiShapeResponseTransformer.cs`, `NumericSchemaTransformer.cs`,
  `StringConstantsAttribute.cs`, `StringEnumAttribute.cs`, `StringEnumSchemaTransformer.cs`,
  `HubPayloadDocumentTransformer.cs`, `OpenApiExport.cs`
- `GameMetricsObserver.cs`, `HealthEndpoints.cs`, `HubObservabilityFilter.cs`,
  `HubRateLimitFilter.cs`, `SecurityHeaders.cs`, `StartupValidation.cs`,
  `UnhandledExceptionHandler.cs`, `UserScopeMiddleware.cs`, `KiriChessMetrics.cs`,
  `ErrorResponse.cs`, `MetricsEndpoint.cs`, `HubConnectionBudget.cs`, `KillSwitch.cs`,
  `KillSwitchService.cs`, `KillSwitchEndpoints.cs`, `RedisRateLimiter.cs`, `RateLimiting.cs`

**Program.cs** — read in full (877 lines).

**Rules/docs read:** `.claude/rules/project.md`, `.claude/rules/backend.md`,
`.claude/rules/security.md`, `.claude/rules/testing.md`, `docs/ops/MIGRATIONS.md`,
`docs/decisions/RETENTION.md`.

**Cross-referenced (not full-file audited, used to confirm/refute findings):**
`tests/KiriChess.Server.Tests/RedisFoundationTests.cs`,
`tests/KiriChess.Server.Tests/AuthLifecycleTests.cs`,
`tests/KiriChess.Server.Tests/BreachedPasswordCheckerTests.cs`,
`tests/KiriChess.Server.Tests/TurnstileVerifierTests.cs`,
`tests/KiriChess.Server.Tests/DatabaseSmokeTests.cs`,
`src/KiriChess.Server/Auth/BreachedPasswordChecker.cs` (for `BreachedPasswordCheckOptions.From`'s
config keys).

### Section ENG-1: Engine

#### Summary

`src/KiriChess.Engine/` is small, deterministic, and disciplined: no wall-clock reads, no
randomness, no I/O in the game-logic path (`KiriChessEngine.cs`, `Core/`). `GamePosition` is
genuinely treated as immutable in practice, `KiriChessConfig` is config-driven with no rule
constants hand-baked into `KiriChessEngine.cs`, and `docs/RULES.md` / `config/kirichess.rules.json`
/ `ui/lib/costPreview.ts` agree on every value checked (piece values, capture multiplier, toll
table, check-chip amounts, promotion-is-free default). No blockers found. The issues below are
either latent landmines (currently mitigated by convention, not by the type system) or narrow
edge-case correctness gaps, all scoped to a hobby project's actual attack surface: custom-config
games are self/bot-only (`GameEndpoints.cs`'s `CreateGameRequest.OpponentId` is self-only;
`ChallengeEndpoints.cs` — real-opponent games — only ever builds configs from named presets, never
`CustomConfigDto`), so nothing here reaches a stranger's game.

#### Findings

##### Blockers

None found.

##### Should fix

1. **No upper bound on any rule variable lets custom-game economy math silently overflow int32.**
   `KiriChessConfig.Validate()` (`src/KiriChess.Engine/Config/KiriChessConfig.cs:94-126`) only
   checks non-negativity, never a ceiling, on `PieceValues`, `CaptureMult`, `EconomyScale`,
   `MoveCostFlat`, `MoveCostPerSquare`, `MoveCostPerPiece`, `CastleCostFlat`, `PromotionCostMult`,
   `CheckCheckerGain`, `CheckVictimLoss`, `DoubleCheckChipMultiplier`. Those values multiply
   together with plain, unchecked `int` arithmetic (`KiriChess.Engine.csproj` sets no
   `CheckForOverflowUnderflow`) in `ApplyEconomy`: `value * config.CaptureMult *
   config.EconomyScale` at `src/KiriChess.Engine/KiriChessEngine.cs:208`, and similarly at lines
   228, 236, 245-246. `GameEndpoints.BuildCustomConfig`
   (`src/KiriChess.Server/Games/GameEndpoints.cs:344-391`) and its DTO
   (`src/KiriChess.Server/Games/GameDtos.cs:27-47`, plain `int`, no `[Range]`) impose no ceiling
   either, so a custom-game creator can type e.g. `pieceValues.queen = 60000` with
   `captureMult = 60000` and get a wrapped, sign-flipped `baseAmount`, corrupting the clock deltas
   persisted for their own game (`Game.ConfigSnapshot`/move records). Custom games are self/bot-only
   today, so this affects only the creator's own data, not a stranger's — hence Medium, not
   Critical. Fix: add a reasonable per-field ceiling (e.g. 10,000) to `Validate()`, matching the
   existing non-negativity checks in style.

2. **`KiriChessConfig.Presets` is a public static mutable `Dictionary<string, KiriChessConfig>` of
   shared, mutable objects.** `src/KiriChess.Engine/Config/KiriChessConfig.cs:345`:
   `public static readonly Dictionary<string, KiriChessConfig> Presets = ...`. Its own doc comment
   (lines 342-344) acknowledges "these are the shared master copies: callers that intend to mutate
   one must `Clone()` first" — but nothing in the type enforces that. `PieceValues` and
   `MoveCostPerPiece` on `KiriChessConfig` are plain mutable `Dictionary<PieceType,int>` with public
   setters, so `KiriChessConfig.Presets["flagfall"].PieceValues[PieceType.Queen] = 0;` compiles and
   would corrupt the shared Flagfall preset for every game on the process from that point on. All
   four current call sites (`CostVectorGenerator.cs:69,118`,
   `src/KiriChess.Server/Games/ChallengeEndpoints.cs:192`,
   `src/KiriChess.Server/Games/MatchmakingService.cs:115`) correctly call `.Clone()` first, so there
   is no live bug today — this is a landmine for the next call site, not an active exploit. Fix:
   expose `Presets` as `IReadOnlyDictionary<string, KiriChessConfig>` (still lets a caller mutate
   the leaf `KiriChessConfig` via a cast, but removes the accidental "just index and mutate"
   footgun) or change the accessor to return a fresh `Clone()` per lookup.

3. **`KiriChessConfig.Validate()` never checks `MoveCostMode` is a defined enum value.** An
   out-of-range `MoveCostMode` passes `Validate()` (`KiriChessConfig.cs:94-126` has no
   `Enum.IsDefined` check) and then silently falls into `MovementToll`'s `_ => 0` default case
   (`src/KiriChess.Engine/KiriChessEngine.cs:282-290`) instead of failing loudly at game creation —
   the exact guarantee `.claude/rules/engine.md:24` promises ("a rule change that produces an
   invalid config fails loudly at game creation, not silently at some later move"). Today the only
   live entry point, `GameEndpoints.BuildCustomConfig`
   (`src/KiriChess.Server/Games/GameEndpoints.cs:346-348`), parses the enum from a string with its
   own `Enum.TryParse` + throw before it ever reaches `Validate()`, so this isn't reachable through
   the API today; it's a duplicated, not centralized, enforcement. Fix: add
   `if (!Enum.IsDefined(MoveCostMode)) throw ...` to `Validate()` so the engine's own guarantee
   holds regardless of caller.

##### Nits

4. **`GamePosition`'s constructor doesn't defensively copy `RepetitionCounts`.**
   `src/KiriChess.Engine/Core/GamePosition.cs:27-39` takes an
   `IReadOnlyDictionary<string, int> repetitionCounts` and stores the reference as-is. Every current
   caller (`KiriChessEngine.cs:48-54`, `:181-187`, and
   `src/KiriChess.Server/Games/GameSession.cs:416-419`) passes a freshly built or already-immutable
   dictionary, so nothing is exploited today, but the class's documented immutability
   (`.claude/rules/engine.md:16`) depends entirely on caller discipline rather than the constructor
   itself. Fix: wrap the incoming dictionary in a fresh `Dictionary<string,int>(...)` inside the
   constructor if this is ever made public-API-facing beyond the engine's own call sites.

5. **`HasMatingMaterial` misclassifies two same-colored-square bishops as sufficient mating
   material.** `src/KiriChess.Engine/KiriChessEngine.cs:341-366`: any two minor pieces
   (`minors >= 2`) count as "has mating material," including two bishops confined to the same
   color complex, which cannot force checkmate against a lone king in standard chess (needs
   opposite-colored bishops, or a bishop+knight). Trigger: a player's flag falls while the opponent
   holds only king + two same-colored-square bishops (all other material captured/promoted away) —
   the "flag vs insufficient material is a draw" rule (`config/kirichess.rules.json:53`,
   `docs/RULES.md:93`) would incorrectly rule a win instead of a draw. Narrow, end-game-specific
   trigger; fix would require checking bishop square colors, likely not worth the complexity for a
   hobby project unless it's actually been observed.

6. **`RulesDocument.Validate()` never validates `ToDefaultConfig()` on its own, only each pool's
   merged config.** `src/KiriChess.Engine/Config/RulesDocument.cs:63-85` calls
   `ToConfig(pool).Validate()` per pool (line 83) but never `ToDefaultConfig().Validate()` directly.
   Today every one of the six pools in `config/kirichess.rules.json` only overrides
   `startTimeSeconds`/`captureMult`/`captureCaptorGainEnabled`/`captureVictimLossEnabled`, so the
   remaining default fields (piece values, move costs, check-chip amounts, etc.) are validated
   incidentally through every pool anyway — no live gap. If a future pool ever overrode every single
   field, an invalid default in an untouched field could slip past startup validation and only
   surface when the custom-game builder (which starts from `ToDefaultConfig()`) used it. Fix: add an
   explicit `ToDefaultConfig().Validate()` call alongside the per-pool loop.

7. **Test file names for Rule 3 and Rule 4 are swapped relative to the published rule numbers.**
   `docs/RULES.md:55` and `:71` (and `.claude/rules/engine.md:20`) number the four rules
   capture / movement toll / **check chip (Rule 3)** / **repetition guard (Rule 4)**. But
   `tests/KiriChess.Engine.Tests/Rule3RepetitionGuardTests.cs:5` tests the repetition guard, and
   `tests/KiriChess.Engine.Tests/Rule4CheckChipTests.cs:5` tests the check chip — the numbers are
   swapped. Cosmetic only (the coverage itself is correct), but confusing for anyone cross-referencing
   the docs against the test suite. Fix: rename the two files to match the doc's numbering.

8. **`engine.md`'s fixture-coverage rule doesn't mention the cost-vector generator's own documented
   exclusion list.** `.claude/rules/engine.md:34` states unconditionally "a new or changed rule
   variable needs a fixture entry," but `CostVectorGenerator.cs:14-21`'s own doc comment says the
   vector format deliberately excludes check-chip *amount*, repetition nullification, and base
   increment — only whether a check chip applies (`hasCheckChip`) is captured, never
   `CheckCheckerGain`/`CheckVictimLoss`/`DoubleCheckChipMultiplier`'s actual magnitude. In practice
   `DoubleCheckChipMultiplier` has zero numeric assertion in any cost-vector fixture (it's covered
   only by `tests/KiriChess.Engine.Tests/Rule4CheckChipTests.cs`'s plain unit tests, which per
   `engine.md:34`'s own reasoning "only proves the engine agrees with itself"). Not a functional bug
   — `costPreview.ts` genuinely can't compute a hover-preview magnitude for a value that depends on
   the position after the move — but `engine.md` should note the exclusion so the rule doesn't read
   as violated by design.

#### Files inspected

- `src/KiriChess.Engine/KiriChessEngine.cs`
- `src/KiriChess.Engine/Core/GamePosition.cs`
- `src/KiriChess.Engine/Core/Move.cs`
- `src/KiriChess.Engine/Core/ApplyResult.cs`
- `src/KiriChess.Engine/Core/ClockEvent.cs`
- `src/KiriChess.Engine/Core/PieceType.cs`
- `src/KiriChess.Engine/Core/Side.cs`
- `src/KiriChess.Engine/Config/KiriChessConfig.cs`
- `src/KiriChess.Engine/Config/RulesDocument.cs`
- `src/KiriChess.Engine/Tooling/CostVectorFixture.cs`
- `src/KiriChess.Engine/Tooling/CostVectorGenerator.cs`
- `src/KiriChess.Engine/KiriChess.Engine.csproj`
- `config/kirichess.rules.json`
- `docs/RULES.md` (worked-example sections)
- `ui/lib/costPreview.ts`
- Cross-referenced for reachability/drift only: `src/KiriChess.Server/Games/GameEndpoints.cs`,
  `src/KiriChess.Server/Games/GameDtos.cs`, `src/KiriChess.Server/Games/GameSession.cs`,
  `src/KiriChess.Server/Games/ChallengeEndpoints.cs`, `src/KiriChess.Server/Games/MatchmakingService.cs`
- `tests/KiriChess.Engine.Tests/` (directory listing + spot checks: `Rule3RepetitionGuardTests.cs`,
  `Rule4CheckChipTests.cs`, `RulesDocumentTests.cs`, `ConfigLoadingTests.cs`, `Rule2MovementTollTests.cs`)

### Section BE-3: Ratings + Analytics

#### Summary

Scope: `src/KiriChess.Server/Ratings/` (8 files), `src/KiriChess.Server/Analytics/` (9 files), and the
leaderboard cache path (`Ratings/LeaderboardCache.cs`, `Ratings/LeaderboardWarmup.cs`).

Overall the code is well above average for the repo: extensive doc comments state design intent and
trade-offs explicitly, the Glicko-2 implementation matches Glickman's published algorithm and passes
the worked-example test to within tolerance, and the `DailyCounters` privacy claim (no cookie, IP, UA,
or account id) checks out against the entity and the write endpoint. Two real issues stand out: the
leaderboard cache's "hourly re-warm" doc comment does not match what the code actually does (it only
warms once, at boot, ever), and `RatingService.ApplyAsync` has no defense-in-depth against being
invoked twice for the same game — it relies entirely on a caller-side in-memory guard, with no
supporting DB constraint. Neither is exploitable by a stranger and neither loses data today (Redis is
unprovisioned in production), which caps severity at Medium.

Counts: 0 Blockers, 3 Should fix, 4 Nits.

#### Findings

##### Blockers

None.

##### Should fix

1. **`LeaderboardWarmup` claims an hourly re-warm that the code does not perform; the cache silently and
   permanently stops answering after ~1 hour of uptime.** `LeaderboardWarmup.cs:47-48` says "the 1-hour
   TTL is deliberate, not just a lock timeout: it means a long-running deployment re-warms hourly," but
   `ExecuteAsync` (`LeaderboardWarmup.cs:41-83`) runs exactly once per process lifetime — there is no
   loop, timer, or `PeriodicTimer` that re-invokes the warm-up after the `{p}lb:built` marker
   (`LeaderboardWarmup.cs:49-50`) expires. Once that marker's 1-hour TTL lapses, `IsWarmAsync`
   (`LeaderboardCache.cs:143-155`) starts returning `false` forever, so `TopAsync`
   (`LeaderboardCache.cs:70-88`) returns `null` for the rest of the process's life and every leaderboard
   read falls back to Postgres — even though the ZSETs are still being kept perfectly in sync via
   `UpsertAsync` on every rated game (`GameRegistry.cs:134-136`, out of section scope but the caller).
   This degrades safely (Postgres is a fully working fallback, per the class's own stated design) and
   has zero live impact today since Redis is not deployed to production, but it means the Phase 8.4
   feature has never actually worked as documented and the abandoned ZSETs (`lb:{pool}`,
   `lb:{pool}:established`) sit in Redis with no TTL of their own once orphaned this way.
   **Fix:** either loop `LeaderboardWarmup.ExecuteAsync` on a `PeriodicTimer` matching the 1-hour TTL
   (matching the doc comment's stated intent), or drop the "re-warms hourly" claim from the comment and
   rely solely on write-through (`UpsertAsync`) plus a restart to re-warm — whichever is actually
   wanted, but the comment and the code need to agree.

2. **`RatingService.ApplyAsync` has no idempotency guard against being applied twice to the same game;
   nor does the schema.** `RatingService.cs:32-66` mutates `Rating.Value`/`RatingDeviation`/`Volatility`
   and inserts two `RatingHistory` rows purely from the passed-in `Game` entity's fields — it never
   checks whether a `RatingHistory` row already exists for `(GameId, UserId, Pool)` before writing
   another one. The only protection against a double-apply is the caller's in-memory
   `_sessions.TryRemove` guard in `GameRegistry.PersistIfOverAsync` (outside this section's scope, but
   the sole caller), which only guards concurrent callers on the same live in-memory session — it does
   nothing for a process restart that re-persists an already-rated game. Confirmed the DB provides no
   backstop either: `KiriChessDbContext.cs:143-150` indexes `RatingHistory` on
   `(UserId, Pool, CreatedAt)` but that index is **not** unique and there is no unique constraint on
   `(GameId, UserId)` anywhere, so a duplicate call would silently double-count a rating change rather
   than fail loudly. Given the audit brief's explicit focus on "whether a rating write can happen twice
   for one game," this is the risk surface: the guarantee lives entirely in one in-memory dictionary on
   one instance, with no defense-in-depth in the layer that actually owns rating correctness.
   **Fix:** the cheapest fix that matches this project's scale is a unique index on
   `RatingHistory(GameId, UserId)` so a duplicate insert fails loudly (raises an exception the caller
   already logs) instead of silently corrupting a rating — no application-level check needed.

3. **`RatingEndpoints.cs` and `AnalyticsEndpoints.cs`'s balance/retention handlers accept no
   `CancellationToken` and don't propagate one to their EF queries, unlike the rest of the codebase's
   read endpoints.** None of the four handlers in `RatingEndpoints.cs` (`RatingEndpoints.cs:15-107`)
   take a `CancellationToken` parameter, and their `ToListAsync()` calls
   (`RatingEndpoints.cs:19,29,57-60,69-74,92-97`) pass none. Same for `/api/analytics/balance` and
   `/api/analytics/retention` (`AnalyticsEndpoints.cs:42-63,67-117`) — `ToListAsync()` at
   `AnalyticsEndpoints.cs:57,85-90,102-107` takes no `ct`. This is inconsistent both with
   `.claude/rules/backend.md`'s "`CancellationToken` on async methods" convention and with the neighbor
   file in the same folder: `CounterEndpoints.cs`'s two handlers both accept `CancellationToken ct` and
   thread it through (`CounterEndpoints.cs:58-59,67-69,77,90,104,113,118`). A slow admin analytics query
   or a slow leaderboard read currently can't be cancelled by a disconnecting client.
   **Fix:** add `CancellationToken ct` to the six handlers and pass it to each `ToListAsync`/`FirstOrDefaultAsync` call, matching `CounterEndpoints.cs`'s existing pattern in the same folder.

##### Nits

1. **`RatingEndpoints.cs` inlines EF queries directly in the endpoint lambdas rather than delegating to
   a service class**, unlike the "thin handler → service" split the backend rules describe and unlike
   `ChallengeEndpoints.cs`/`AccountEndpoints.cs` (the stated templates). `RatingService.cs` exists but
   only covers the write path (`ApplyAsync`); all four read endpoints
   (`RatingEndpoints.cs:15-107`) query `db.Ratings`/`db.RatingHistories` directly. Low priority — the
   queries are simple and already covered by `LeaderboardEndpointParityTests.cs` — but worth folding
   into a `RatingQueryService` if this file grows further.

2. **Glicko-2 volatility solver (`Glicko2.cs:52-79`) has no explicit maximum-iteration cap** on either
   the bracket-widening loop (`Glicko2.cs:65`) or the Illinois regula-falsi loop (`Glicko2.cs:71-78`).
   Traced through the failure mode where `fB - fA` could reach exactly zero: the loop still terminates
   because `A` is only reassigned inside the `fC * fB <= 0` branch, and `NaN` comparisons are always
   false, so a corrupted `B`/`fB` stops updating `A` and the `Math.Abs(B - A) > Epsilon` guard exits on
   the next `NaN` compare — so this is not believed to be a live infinite-loop risk, just a robustness
   gap worth naming since the audit brief calls out "the volatility iteration and its convergence
   guard" by name. No action needed unless a real non-convergent input is ever observed.

3. **No test exercises the `else` branch of the volatility solver's initial bracket** (`Glicko2.cs:62-67`,
   the `d2 <= phi2 + v` case — a small "surprise" relative to variance, the common case for an
   established player against a similarly-rated opponent). `Glicko2Tests.cs` covers the worked example
   (which exercises the `d2 > phi2 + v` branch, `Glicko2.cs:58-60`) and three other basic
   win/loss/draw checks, but nothing pins down the bracket-widening loop's behavior directly. Given
   Ratings' own testing convention calls for exactly this kind of edge-case coverage, a small targeted
   test would close the gap (e.g., two closely-rated established players drawing, verifying `k` search
   converges to a sane volatility).

4. **No dedicated unit test for `RatingService.ApplyAsync`'s self-play guard** (`RatingService.cs:38`,
   `game.WhitePlayerId == game.BlackPlayerId`) beyond the integration-level
   `Solo_practice_game_writes_no_ratings` test in `RatingPersistenceTests.cs`, which only covers the
   "no opponent at all" solo path, not a rated custom game where both seats resolve to the same account
   id with an opponent explicitly set. Low priority since the guard itself is a one-line, clearly
   correct check.

#### Files inspected

- `src/KiriChess.Server/Ratings/Glicko2.cs`
- `src/KiriChess.Server/Ratings/RatingConstants.cs`
- `src/KiriChess.Server/Ratings/RatingCalculator.cs`
- `src/KiriChess.Server/Ratings/RatingService.cs`
- `src/KiriChess.Server/Ratings/RatingDtos.cs`
- `src/KiriChess.Server/Ratings/RatingEndpoints.cs`
- `src/KiriChess.Server/Ratings/LeaderboardCache.cs`
- `src/KiriChess.Server/Ratings/LeaderboardWarmup.cs`
- `src/KiriChess.Server/Analytics/BalanceAnalytics.cs`
- `src/KiriChess.Server/Analytics/AnalyticsDtos.cs`
- `src/KiriChess.Server/Analytics/AnalyticsEndpoints.cs`
- `src/KiriChess.Server/Analytics/CounterKeys.cs`
- `src/KiriChess.Server/Analytics/RetentionDtos.cs`
- `src/KiriChess.Server/Analytics/TrafficAnalytics.cs`
- `src/KiriChess.Server/Analytics/TrafficDtos.cs`
- `src/KiriChess.Server/Analytics/CounterEndpoints.cs`
- `src/KiriChess.Server/Analytics/RetentionAnalytics.cs`

Read for context only (out of section scope, cited where directly relevant to a finding):
`src/KiriChess.Server/Data/Entities/DailyCounter.cs`, `src/KiriChess.Server/Data/KiriChessDbContext.cs`,
`src/KiriChess.Server/Games/GameRegistry.cs`, `src/KiriChess.Server/Games/ChallengeEndpoints.cs`,
`src/KiriChess.Server/Infrastructure/Redis/RedisConnection.cs`,
`tests/KiriChess.Server.Tests/RatingCalculatorTests.cs`, `tests/KiriChess.Server.Tests/Glicko2Tests.cs`,
`tests/KiriChess.Server.Tests/RatingConstantsTests.cs`, `tests/KiriChess.Server.Tests/RatingPersistenceTests.cs`,
`tests/KiriChess.Server.Tests/LeaderboardCacheTests.cs`.

### Section BE-4: Moderation + Account + Retention

#### Summary

32 files inspected (20 `Moderation/`, 5 `Account/`, 7 `Retention/`). No Blockers. 2 Should-fix at
Medium, 3 Should-fix at Low, 3 Nits.

This is code-quality coverage, not a second GDPR pass; `docs/audits/L.md` already owns the
export/erasure/retention *rights* analysis (web push survives erasure, rematch offers survive
erasure, patron fields untouched and unexported, banned accounts have no working rights route,
export omissions, "indefinite" retention needing a review trigger, the dormant-account trigger's
stale justification, account id in logs) and none of that is repeated here.

What this pass verified as correct, because the task asked specifically to check rather than
assume it: `IssuedByUserId`/`LiftedByUserId`/`ReviewedByUserId`/reporter id all come from
`ClaimsPrincipal.GetUserId()`, never a request body, across `ModerationEndpoints.cs` and
`ReportEndpoints.cs`; `ModerationPolicies.Admin` gates every admin route with no gap (checked
`ModerationEndpoints.cs`, `ContentEndpoints.cs`'s admin group, `EngineDetectionEndpoints.cs`,
`SandbaggingEndpoints.cs`, `RetentionEndpoints.cs`); `UserSanction`'s two foreign keys are
`DeleteBehavior.Restrict` (`KiriChessDbContext.cs:227-230`), so a sanction genuinely cannot be
deleted by deleting the account it targets; `AccountDeletionService.DeleteAsync` is one retried,
replay-safe transaction with the live-game guard running first; `NormalizedDisplayName`'s unique
index is correctly partial (`DeletedAt IS NULL`, `KiriChessDbContext.cs:65-67`), so the "every
tombstone is named 'Deleted account'" design does not break on the second erasure the way a naive
total unique index would. `RetentionSweep`'s five rules match `RETENTION.md` exactly, and a
mid-sweep exception is safely recoverable by the next tick because every rule's delete is
independently idempotent.

The one gap this pass found that neither L-2 nor the retention doc names: **account self-deletion
is not one of `SanctionPolicy`'s enforcement points.** A banned player holding a still-valid
access token (issued before the ban; the ban revokes refresh tokens, not the current JWT) can call
`POST /api/account/delete` and free their email/display name for reuse before that token expires,
because `AccountDeletionService.DeleteAsync` never calls `SanctionPolicy.BlockingSanctionAsync`.

#### Findings

##### Blockers

None found.

##### Should fix

1. **Account self-deletion is not gated on an active ban** - Medium
   - Where: `src/KiriChess.Server/Account/AccountDeletionService.cs:31-42` (the two guards: not-found,
     already-deleted, in-game; no ban check), `src/KiriChess.Server/Account/AccountEndpoints.cs:47-72`
     (the `/delete` handler, `.RequireAuthorization()` only, no sanction check).
     `src/KiriChess.Server/Moderation/SanctionPolicy.cs:23-27`'s own doc comment enumerates
     "login, refresh, queue join and game creation" as the exhaustive list of enforcement points and
     says nothing about deletion, so this reads as an omission rather than a stated decision.
   - Trigger: a moderator issues an `AccountBan`; the target still holds an access token minted before
     the ban (revoking refresh tokens does not invalidate an already-issued JWT until its own expiry or
     the next refresh attempt). Within that window they call `/api/account/delete`.
   - Impact: the sanction row survives (Restrict FK, verified above), so the ban itself is not erased -
     but the account's email and display name are freed for immediate reuse while under an active ban,
     which is exactly the "ban cannot be laundered" property `PRIVACY.md`/`TERMS.md` claim for this
     design. This does not add a fresh identity (nothing here stops registering a new email regardless),
     but it does let a just-banned player immediately vacate the flagged email/handle inside the
     detection window a moderator would otherwise have.
   - Fix: add a `SanctionPolicy.BlockingSanctionAsync(userId, SanctionScope.Account, ct)` check in
     `DeleteAsync` alongside the existing in-game guard, returning a new `AccountDeletionOutcome` value
     that the endpoint maps to 409, the same shape already used for `GameInProgress`.

2. **CancellationToken missing entirely on the two most expensive Detection routes** - Medium
   - Where: `src/KiriChess.Server/Moderation/Detection/EngineDetectionEndpoints.cs:29-70` (`GET
     /{userId}`) and `:77-107` (`GET /game/{gameId}`); `src/KiriChess.Server/Moderation/Detection/SandbaggingEndpoints.cs:60-118`
     (`GetForUserAsync`) and `:120-198` (`GetSweepAsync`). None of the four handlers declares a
     `CancellationToken` parameter, so none of their `FirstOrDefaultAsync`/`ToListAsync` calls receive one.
   - Impact: `EngineDetectionEndpoints.cs:18-22`'s own doc comment calls the 200-game cap "the only
     thing between a moderator's click and a multi-second query," and `SandbaggingEndpoints.cs:24-32`
     documents a 5,000-row-times-4 sweep budget for the same reason - these are the two routes in the
     whole section where an uncancellable query actually costs something, and they are also the two
     routes missing the token entirely rather than just not passing it through.
   - Fix: add `CancellationToken ct` to each handler's parameter list and thread it into every EF call,
     matching `ContentEndpoints.cs` and `RetentionEndpoints.cs` in the same folder tree.

3. **CancellationToken inconsistency inside ModerationEndpoints.cs and missing from ReportEndpoints.cs** - Low
   - Where: `src/KiriChess.Server/Moderation/ModerationEndpoints.cs:25` (`GET /reports`), `:54` (`POST
     /reports/{id}/resolve`), `:85` (`GET /users/{userId}`) take no `CancellationToken`, while the other
     four routes in the same file (`:133`, `:145`, `:180`, `:208`) do. `src/KiriChess.Server/Moderation/ReportEndpoints.cs:20`
     (`POST /api/reports`) and `:86` (`GET /api/reports/mine`) take none at all.
   - Trigger: none needed to observe the inconsistency; it is visible by reading the file. Practical
     effect is a client disconnect or shutdown not stopping an in-flight query on these five routes.
   - Fix: thread `CancellationToken ct` through the remaining five handlers, matching the sanctions
     routes already in the same file and `backend.md`'s "CancellationToken on async methods."

4. **StripeWebhookEndpoints.cs never threads a CancellationToken** - Low
   - Where: `src/KiriChess.Server/Account/StripeWebhookEndpoints.cs:53` (`FirstOrDefaultAsync`) and
     `:60` (`SaveChangesAsync`) run uncancelled; the handler does not accept `context.RequestAborted`
     either. Every other file in `Account/` (`AccountEndpoints.cs`, `AccountDeletionService.cs`,
     `AccountExportBuilder.cs`) threads `ct` through every EF call.
   - Fix: accept a `CancellationToken` (or read `context.RequestAborted`) and pass it to both EF calls.

5. **`ClientReferenceId` is trusted with no ownership proof** - Low (trigger: a `/donate` link starts
   passing `client_reference_id`, currently none do per L-2-4)
   - Where: `src/KiriChess.Server/Account/StripeWebhookEndpoints.cs:51` - `Guid.TryParse(session.ClientReferenceId, ...)`
     is the only check before granting `IsPatron`; nothing ties the id to the person who completed the
     Stripe Checkout session.
   - Impact: this is the IDOR shape `security.md` names ("user id always comes from `ClaimsPrincipal`...
     never from a client-supplied id") arriving through a webhook field instead of a request body. It is
     inert today because no donation link sets this field (L-2-4 already covers the export/erasure half
     of that same gap); flagged here only because the trust-boundary angle is distinct from L-2-4's and
     the fix needs to land before, not after, a link is wired up.
   - Fix: when a donation link is wired, derive `client_reference_id` from a short-lived server-minted
     token tied to the authenticated session that created the checkout, not a bare user guid the client
     controls.

##### Nits

1. `StripeWebhookEndpoints.cs` stands apart from every neighboring file's style: individually
   spelled-out `using System;`, `using System.IO;`, `using System.Threading.Tasks;` etc. (lines 1-14)
   despite `ImplicitUsings` being enabled project-wide and no other file in `Account/`, `Moderation/`
   or `Retention/` writing these out; `[FromServices]` attributes (lines 29-31) that no other
   minimal-API handler in this section uses; and a `void`-returning `MapStripeWebhookEndpoints` (line
   20) where every other `Map*Endpoints` in the section returns `IEndpointRouteBuilder` for chaining.
2. The `StreamReader` used to read the raw webhook body (`StripeWebhookEndpoints.cs:40`) is never
   disposed, unlike the deliberate resource-scoping elsewhere in the same folder (e.g. `await using var
   tx` in `AccountDeletionService.cs:57`).
3. The comment `// Optional: hides it from Swagger if you have it` (`StripeWebhookEndpoints.cs:24`)
   reads like an unreviewed placeholder next to comments elsewhere in this section that always name a
   specific, load-bearing reason.

#### Files inspected

Moderation/: ModerationOptions.cs, ModerationPolicies.cs, ModerationSeeder.cs, SanctionPolicy.cs,
ModerationDtos.cs, ReportDtos.cs, SanctionService.cs, ReportEndpoints.cs, ModerationEndpoints.cs,
Content/ContentDtos.cs, Content/ContentModerationOptions.cs, Content/ContentPolicy.cs,
Content/DisplayNameService.cs, Content/DisplayNameNormalizer.cs, Content/ContentEndpoints.cs,
Detection/DetectionDtos.cs, Detection/EngineAssistanceAnalyzer.cs, Detection/SandbaggingAnalyzer.cs,
Detection/EngineDetectionEndpoints.cs, Detection/SandbaggingEndpoints.cs.

Account/: StripeWebhookEndpoints.cs, AccountDtos.cs, AccountDeletionService.cs,
AccountExportBuilder.cs, AccountEndpoints.cs.

Retention/: RetentionSweeper.cs, DormantDtos.cs, DormantAccountPolicy.cs, RetentionOptions.cs,
RetentionSweep.cs, RetentionSchedule.cs, RetentionEndpoints.cs.

Supporting (read for verification only, not in scope): `src/KiriChess.Server/Data/KiriChessDbContext.cs`
(NormalizedDisplayName partial unique index, UserSanction FK behavior), `docs/decisions/RETENTION.md`,
`docs/audits/L.md`.

### Section INF-1: Infrastructure as files

#### Summary

No blockers. The Fly topology is unusually well documented for a one-person project: `fly.backend.toml`
and `fly.backend.staging.toml` diverge only where DEPLOYMENT.md and the tomls' own comments say they
should (`auto_stop_machines`, `auto_start_machines`, `min_machines_running`, and the `[metrics]` vs
`[[metrics]]` syntax `flyctl launch` rewrites), and `strategy = "immediate"` on the backend is a
deliberate consequence of Redis being unprovisioned (no safe way to run two machines with divergent
in-memory game state), not an oversight. `docker-compose.prod.yml` boots and is what the E2E job
actually exercises. The gaps found are all narrower: a Postgres major-version mismatch between the dev
and prod-shaped compose files, an inconsistent `permissions:` posture across workflows now that the repo
is public, one stale action pin, and a couple of small self-host/CI-hygiene nits. Findings that would
just restate the known, stated positions (Redis unprovisioned, offsite-backup/ops-probes never green,
Cloudflare Access still up) are intentionally omitted.

Counts: Blockers 0, Should fix 3, Nits 3.

#### Findings

##### Blockers

None found.

##### Should fix

**INF-1-1 — Dev compose runs Postgres 15, the prod-shaped compose runs Postgres 16**
- Severity: Medium (trigger: a schema/query change behaves differently on 16 and nobody notices until
  the E2E job or a self-host deploy, both of which use `docker-compose.prod.yml`)
- Where: `docker-compose.yml:3` (`image: postgres:15`) vs `docker-compose.prod.yml:3`
  (`image: postgres:16-alpine`)
- Impact: `docker-compose.yml` is the file a contributor runs for day-to-day local development; it never
  exercises the same Postgres major version as the file that actually ships (`docker-compose.prod.yml`,
  also the base for the E2E overlay and the documented self-host path in DEPLOYMENT.md § 6). A behavior
  difference between 15 and 16 (there have been minor planner and `jsonb` changes across major versions,
  and `PgnBuilder`/game-history queries lean on `jsonb`) would only surface in CI, not in the loop a
  contributor actually runs.
- Fix: Bump `docker-compose.yml:3` to `postgres:16-alpine` to match `docker-compose.prod.yml`.

**INF-1-2 — `permissions:` is inconsistent across workflows now that the repo is public**
- Severity: Medium (trigger: a compromised transitive dependency's install script runs inside a job
  that still has the repository's default `GITHUB_TOKEN` scope)
- Where: `.github/workflows/ci.yml` (no `permissions:` key anywhere in the file), `.github/workflows/deploy.yml`
  (same), `.github/workflows/restore-drill.yml` (same) — contrast
  `.github/workflows/secret-scan.yml:11-13` (`contents: read, pull-requests: read`),
  `.github/workflows/codeql.yml:22-25`, `.github/workflows/semgrep.yml:20-21`,
  `.github/workflows/offsite-backup.yml:26-27`, `.github/workflows/ops-probes.yml:24-25`, all of which
  explicitly scope the token.
- Impact: Five of eight workflows in this repo already follow least-privilege `permissions:` blocks; the
  three that don't are exactly the ones that run the most untrusted code per invocation (`npm ci`,
  `dotnet restore`, arbitrary test/build scripts) on every push and PR. Without an explicit block, the
  job inherits whatever the repository's default workflow permission setting is, which is broader than
  any of these jobs need (none of them push commits, open PRs, or write releases). This is a
  consistency/least-privilege gap the project's own pattern elsewhere already fixes.
- Fix: Add `permissions: contents: read` at the top of `ci.yml`, `deploy.yml`, and `restore-drill.yml`
  (the same minimal scope `secret-scan.yml` uses), matching the rest of the directory.

**INF-1-3 — `ci.yml`'s two main jobs, `codeql.yml`, and `semgrep.yml` have no `timeout-minutes` or `concurrency` group**
- Severity: Medium (trigger: a hung step or a burst of pushes to `dev`/`main` runs to GitHub's 360-minute
  per-job default, or runs duplicated in parallel, on a repo whose Actions minutes have already run out
  once this month)
- Where: `.github/workflows/ci.yml:9` (`backend` job) and `:64` (`frontend` job) have neither; contrast
  `ci.yml:125` (`e2e` job: `timeout-minutes: 25`) and the Neon-touching workflows, all of which set both
  (`offsite-backup.yml:22-24` + `:32`, `restore-drill.yml:32-34` + `:39`, `ops-probes.yml:20-22` + `:30`/`:54`).
  `.github/workflows/codeql.yml` and `.github/workflows/semgrep.yml` also have neither.
- Impact: `ci.yml`'s own `e2e` job and every Neon-touching workflow already treat "bound the runtime, and
  don't let two runs of the same workflow stack up" as a needed guard on a minutes-constrained repo. The
  backend/frontend CI jobs, CodeQL, and Semgrep are the ones that fire most often (every push and PR) and
  are the only ones left unbounded.
- Fix: Add `timeout-minutes: 20` (or similar) to `ci.yml`'s `backend` and `frontend` jobs, plus a
  `concurrency: group: ${{ github.workflow }}-${{ github.ref }}, cancel-in-progress: true` block to
  `ci.yml`, `codeql.yml`, and `semgrep.yml` so a superseded push doesn't keep running.

##### Nits

**INF-1-4 — `restore-drill.yml` pins `actions/setup-dotnet` to a different major version than every other workflow**
- Severity: Low (trigger: someone assumes all `setup-dotnet` pins in the repo track together and is
  surprised this one doesn't move with the rest)
- Where: `.github/workflows/restore-drill.yml:46` (`actions/setup-dotnet@67a3573c9a986a3f9c594539f4ab511d57bb3ce9 # v4`)
  vs `.github/workflows/ci.yml:18`, `.github/workflows/codeql.yml:41`, and
  `.github/workflows/offsite-backup.yml:39` (all `actions/setup-dotnet@a98b56852c35b8e3190ac28c8c2271da59106c68 # v6`)
- Impact: No functional difference observed (both install a .NET SDK), but it is stale relative to the
  rest of the repo's own pins and reads as an accident rather than a deliberate choice, since nothing in
  the file explains why the drill needs an older major version.
- Fix: Bump `restore-drill.yml:46` to the same v6 SHA used elsewhere, unless there's an undocumented
  reason to stay on v4.

**INF-1-5 — Self-host via `docker-compose.prod.yml` silently loses replay page metadata**
- Severity: Low (trigger: someone follows DEPLOYMENT.md § 6 verbatim to self-host, then shares a replay
  link)
- Where: `docker-compose.prod.yml:88-107` (frontend service: no `environment:` key at all) vs
  `docker-compose.e2e.yml:47-51`, the only compose file that sets `API_BASE_INTERNAL`; consumed at
  `ui/app/replay/[id]/layout.tsx:10-21` and `ui/app/replay/[id]/opengraph-image.tsx:21-22`.
- Impact: `getReplay`'s server-side metadata/OG-image fetch needs a backend URL reachable from inside the
  frontend container, not the browser-facing `NEXT_PUBLIC_API_BASE`. `docker-compose.prod.yml`, the file
  DEPLOYMENT.md § 6 tells a self-hoster to run, never sets `API_BASE_INTERNAL`, so that fetch fails and
  silently falls back to generic "Watch a KiriChess replay" metadata (the `catch` block is a deliberate,
  graceful fallback, not a crash, so nothing surfaces the gap to the operator).
- Fix: Add `environment: - API_BASE_INTERNAL=http://backend:8080` to the `frontend` service in
  `docker-compose.prod.yml`, the same compose-internal hostname the e2e overlay already uses.

**INF-1-6 — `docker-compose.load.yml`'s `deploy.resources.limits` silently no-ops under plain `docker compose up` on some setups**
- Severity: Low (trigger: someone runs the load overlay without first confirming their Compose/Docker
  combination honors `deploy.resources.limits` outside Swarm)
- Where: `docker-compose.load.yml:47-55` (`deploy: resources: limits:`), comment at `:48-50` claims
  "Compose V2 honours `deploy.resources.limits` for `docker compose up`"
- Impact: This is already caveated in-file and the README's own run-ladder script verifies the applied
  limit with `docker inspect` before trusting it (per the comment), so this is a documentation nit, not a
  functional gap: the comment states a general claim ("Compose V2 honours...") without noting it depends
  on the Docker Engine/Compose version pair, which is exactly why the verification step exists two lines
  away in the harness.
- Fix: None required; the harness already self-checks. Worth a one-line comment pointer to that
  `docker inspect` verification for a reader who only sees this file and not the loadtest scripts.

#### Already covered / verified, no defect

- `fly.backend.toml` vs `fly.backend.staging.toml`: every divergence (`auto_stop_machines`,
  `auto_start_machines`, `min_machines_running`, and the `[metrics]`/`[[metrics]]` syntax difference) is
  explained both in the tomls' own comments and in DEPLOYMENT.md § 2.1 as a deliberate cost/safety
  tradeoff or a known `flyctl launch` formatting side effect. Not re-filed.
- `fly.ui.toml` vs `fly.ui.staging.toml`: no unexplained drift; both scale to zero (the frontend is
  stateless in both environments), build args differ only in target host and Sentry DSN, both documented.
- `strategy = "immediate"` in `fly.backend.toml:30` (no health-check-gated rollout, no auto-rollback on a
  bad release) is a direct, load-bearing consequence of Redis being unprovisioned (a rolling or
  blue-green strategy would put two machines with divergent in-memory game state in the traffic path at
  once), not an independent gap. The manual rollback path is documented in DEPLOYMENT.md § 3/§ 3.1.
- `kill_timeout = 30` in both `fly.backend*.toml` against `LiveGamePersister`'s flush budget, and the
  deliberate absence of `kill_timeout` in both `fly.ui*.toml` (nothing to flush) are both explained inline
  and match DEPLOYMENT.md § 4.1's "shutdown budget" section.
- `docker-compose.prod.yml` does not publish the `db`/`redis` ports to the host (`docker-compose.prod.yml:11`,
  implicit for `redis`); `docker-compose.yml` (dev) does, deliberately, for local tooling access.
- Both Dockerfiles pin base images by digest (10c.3) and both `HEALTHCHECK` directives are liveness-only,
  matching the `[[http_service.checks]]` blocks in the Fly tomls.
- Action pins across the workflow set are SHA-pinned (not floating tags) with a consistent Dependabot
  comment, except for the one drift noted in INF-1-4.
- Secret handling in every workflow read (`deploy.yml`, `offsite-backup.yml`, `restore-drill.yml`,
  `ops-probes.yml`) passes secrets through `env:` to a script or CLI, never interpolates one into a `run:`
  string or a URL, and never echoes one.
- The migration gate (`release_command` in `fly.backend.toml`/`fly.backend.staging.toml`, `RunMigrationsOnStartup=false`
  everywhere it appears) is consistent across both Fly tomls, all four compose files (merged correctly
  across the e2e/load overlays, which only add keys rather than replacing the base `environment:` list),
  and `.env.example`.
- Redis being unprovisioned, the offsite-backup/ops-probes workflows never having had a green run, and
  Cloudflare Access still fronting the site are stated positions per ROADMAP.md/DEPLOYMENT.md and are not
  re-filed here.

#### Files inspected

- `docker-compose.yml`, `docker-compose.prod.yml`, `docker-compose.e2e.yml`, `docker-compose.load.yml`
- `fly.backend.toml`, `fly.backend.staging.toml`, `fly.ui.toml`, `fly.ui.staging.toml`, `fly.logshipper.toml`
- `Dockerfile.backend`, `Dockerfile.frontend`, `.env.example`
- `.github/workflows/ci.yml`, `.github/workflows/deploy.yml`, `.github/workflows/codeql.yml`,
  `.github/workflows/semgrep.yml`, `.github/workflows/secret-scan.yml`,
  `.github/workflows/restore-drill.yml`, `.github/workflows/offsite-backup.yml`,
  `.github/workflows/ops-probes.yml`
- `infra/betterstack/main.tf`, `infra/betterstack/outputs.tf`, `infra/betterstack/variables.tf`,
  `infra/betterstack/versions.tf`, `infra/betterstack/terraform.tfvars.example`, `infra/betterstack/README.md`
  (`.terraform/`, `*.tfstate*`, and `terraform.tfvars` confirmed gitignored via `.gitignore:70-74`, not
  tracked despite being present on disk)
- Supporting reads: `.claude/rules/project.md`, `.claude/rules/testing.md`, `docs/ops/DEPLOYMENT.md`,
  `docs/ops/MIGRATIONS.md`, `docs/audits/O.md`, `docs/audits/O.md`,
  `ui/lib/api.ts:454-466`, `ui/app/replay/[id]/layout.tsx`, `.gitignore:70-74`

### Section FE-1: Frontend lib, scripts and config

#### Summary

27 files in `ui/lib/` read in full (`api.generated.ts` and `rules.generated.ts` header-checked only,
per instructions), plus `ui/scripts/gen-api.mjs`, `gen-icons.mjs`, `gen-rules.mjs`; `ui/next.config.ts`,
`eslint.config.mjs`, `vitest.config.mts`, `vitest.setup.ts`, `tsconfig.json`, `package.json`; the four
Sentry/instrumentation files; `ui/i18n/request.ts`; `ui/messages/en.json`. Read against
`.claude/rules/project.md`, `frontend.md`, `testing.md`.

This is the most heavily and honestly documented part of the codebase seen so far: `types.ts` and
`api.ts` contain zero hand-written DTO mirrors (confirmed by grep: only `types.ts` imports from
`api.generated.ts`), `costPreview.ts`/`botClock.ts` are pinned by real golden-vector tests against the
C# engine's own output (`costVectors.test.ts` replays `config/cost-vectors.generated.json`, generated
by the engine itself), both `gen-*.mjs` scripts fail loudly (`process.exit(1)`) on `--check` drift, and
Sentry has `sendDefaultPii: false` in all three config files. No hand-written mirrors, no silent
drift-guard gaps, and no CSP defect were found.

The real issues are narrower: `bot.ts`'s Stockfish worker is a module-level singleton with no
termination and a single resolver that a second overlapping search silently overwrites (the task's
"worker lifecycle" concern, confirmed live); `a11y.ts` is the one feature-sized chunk of user-facing
text that never went through the otherwise-thorough next-intl migration; and a few coverage gaps in
`ui/lib/*.test.ts`.

**Counts: 0 Blockers, 3 Should-fix (Medium/Low), 3 Nits (Low).**

#### Findings

##### Blockers

None found.

##### Should fix

###### FE-1-1 — `bot.ts`'s Stockfish worker has no termination and a single global resolver a second search silently overwrites
- Severity: **Medium** (bot-only games, no persisted/multiplayer data at risk, but a real correctness
  and resource-leak bug matching the audit's own "worker lifecycle" ask)
- Where: `ui/lib/bot.ts:1-2` (module-level `engine`/`resolveBestMove` singletons), `:11-29`
  (`getEngine()` creates the worker once and never calls `.terminate()` anywhere in the file or
  elsewhere in `ui/` — confirmed by grep, no `terminate(` call exists in `ui/`), `:14-24`
  (`engine.onmessage` resolves whatever `resolveBestMove` currently points to), `:36`
  (`resolveBestMove = resolve;` overwrites any still-pending previous resolver with no `"stop"`
  command sent to the engine first). Consuming effect at `ui/app/game/[id]/page.tsx:511-552`: the
  cleanup (`:549-551`) only sets `signal.aborted = true`, which stops `handleMove` from firing for a
  stale result but does not stop the in-flight `getEngineMove` call or terminate the worker.
- Impact: if a second `getEngineMove` call starts before the first's `"bestmove"` arrives (the effect's
  own dependency array includes `legalUci`, a freshly-computed array each render, so this is reachable
  on a re-render, not just a rare double-invoke), the first call's promise never resolves (leaked), and
  when the *first* search's `bestmove` line eventually arrives, it resolves the *second* call's promise
  instead, since `resolveBestMove` was overwritten in between — the bot can play a move computed from a
  now-stale position. Separately, since the worker is never terminated, navigating away from an
  in-progress bot game mid-search leaves Stockfish still computing (and, per the race above, capable of
  misfiring into whatever the next bot game's promise is) for the lifetime of the tab.
- Fix: export a `stopEngine()` from `bot.ts` that posts `"stop"` and clears the singleton, guard
  `getEngineMove` so a new call rejects/ignores a still-pending resolver rather than silently replacing
  it (or send `"stop"` before every new `"position"`/`"go"`), and call the stop path from the game
  page's effect cleanup and on game end/unmount.

###### FE-1-2 — `a11y.ts`'s screen-reader announcer strings never went through the next-intl migration; every other user-facing string in the app did
- Severity: **Low** (no bug today under English-only, per project policy; a real inconsistency for the
  audience the feature exists to serve)
- Where: `ui/lib/a11y.ts:39-181` (`describeSquare`, `moveCore`, `describeMove`, `describeClocks`,
  `spokenTime` — every literal is hardcoded English: `"empty"`, `"capture"`, `"costs you"`,
  `"castles kingside"`, `"You are ahead by"`, etc., no `Translator` parameter, no lookup into
  `ui/messages/en.json`). Contrast with `ui/lib/tutorial.ts:14-18,34-37,64-67`, which explicitly
  carries translation *keys* and takes a `Translator` function precisely because it is a non-component
  module that "can't call `useTranslations` itself" — the same constraint `a11y.ts` is under, solved
  there but not here. `ui/messages/en.json` (1084 lines, Phase 9f.4 per `ui/i18n/request.ts:3-19`) has
  no announcer/a11y namespace at all despite covering nearly every other visible string down to
  `"Loading…"` and `"COPIED"`.
- Impact: `ui/i18n/request.ts:9-17` documents a real, staged plan for adding a second locale ("When the
  first non-English community arrives... add i18n/routing.ts with locales [\"en\", ...]"). The moment
  that happens, every screen-reader announcement (move descriptions, clock readouts, urgency
  thresholds) stays English while the rest of the UI translates — for the live game board specifically,
  which `docs/plans/a11y/NVDA-PASS.md` already treats as needing a real (non-axe) accessibility pass.
- Fix: give `a11y.ts`'s exported functions a `Translator` parameter the same way `tutorial.ts` does
  (keeping the module itself pure/node-testable), and add an "a11y" namespace to `messages/en.json`
  when the second locale is actually being built, not before.

###### FE-1-3 — Coverage gaps in `ui/lib/*.test.ts`
- Severity: **Low/Medium**
- Where:
  - `ui/lib/bot.ts` has zero test coverage, including the trivial pure fallback `getRandomMove`
    (`ui/lib/bot.ts:76-80`), the one function in this file that's easily testable.
  - `ui/lib/useDisplayNameCheck.ts:21-58` (debounce + sequence-number stale-response guard, exactly the
    kind of logic `challengeLink.ts`'s own header comment says gets pulled into `lib/` "so it can be
    pinned by" a test) is structurally untestable under the current Vitest split:
    `ui/vitest.config.mts:24` runs `lib/**/*.test.ts` under a `node` environment (no jsdom, no RTL),
    while the jsdom "components" project (`ui/vitest.config.mts:38`) only globs
    `components/**/*.test.tsx`. A hook living in `lib/` currently has no project that can mount it.
  - `ui/lib/presets.ts:36-67` (`toPreset()`, deriving `economy`/`multiplier`/`tag` from
    `rules.generated.ts`) has no direct test, unlike its sibling economy-mirror files
    `costPreview.ts`/`botClock.ts` (the two files `testing.md` names explicitly as needing coverage),
    even though it does comparable arithmetic (e.g. the same capture-multiplier-ratio logic
    `scripts/gen-rules.mjs:117-122`'s `multiplierLabel` computes) with nothing to catch drift.
- Fix: add `bot.test.ts` for `getRandomMove` at minimum; either add a `hooks`/`lib-dom` Vitest project
  with a jsdom environment so `useDisplayNameCheck.ts` can be tested, or relocate it under
  `components/` if that's the intended boundary; add a `presets.test.ts` asserting `toPreset()`'s
  `economy`/`multiplier`/`tag` derivation against a couple of known pools.

##### Nits

###### FE-1-4 — `costPreview.ts`'s `hasCheckChip` doesn't reflect whether the move actually delivers check, and nothing consumes it
- Severity: **Low**
- Where: `ui/lib/costPreview.ts:175-176` (`hasCheckChip = economy.checkCheckerGain > 0 ||
  economy.checkVictimLoss > 0` — a static property of the economy config, independent of `from`/`to`
  or whether the move gives check). The doc comment at `:104` says the function "Excludes check chip
  amount from `moverNet`... only whether one applies is reported via `hasCheckChip`," which reads as
  "this move," not "this economy." Confirmed unused: both real call sites
  (`ui/app/game/[id]/page.tsx:612-616`, `ui/app/learn/page.tsx:146-154`) ignore it and instead compute
  actual check status themselves via `chess.isCheck()`.
- Fix: either rename the field to something like `checkChipEconomyActive` to match what it actually
  measures, or drop it if it stays unconsumed; a future caller reading the name at face value would be
  misled.

###### FE-1-5 — `gen-icons.mjs` imports `sharp`, which is not a declared dependency of `ui/package.json`
- Severity: **Low**
- Where: `ui/scripts/gen-icons.mjs:1` (`import sharp from "sharp";`); `ui/package.json:17-46`
  (dependencies/devDependencies list, no `"sharp"` entry anywhere). It resolves today only because
  `sharp` is an `optionalDependency` of `next` itself (`ui/package-lock.json:9048-9058`), not a package
  KiriChess installs on purpose.
- Impact: an environment that skips optional deps, or where the native `sharp` binary isn't available
  for the platform, makes `node scripts/gen-icons.mjs` fail with "Cannot find module 'sharp'." Low
  stakes since this script isn't wired into `package.json`'s `scripts` or CI (icon regeneration is a
  manual, occasional step), but still a latent break waiting on an unrelated Next.js upgrade.
- Fix: add `sharp` to `ui/package.json`'s `devDependencies` directly.

###### FE-1-6 — `eslint-config-next` is a full minor version behind `next`
- Severity: **Low**
- Where: `ui/package.json:22` (`"next": "16.3.0"`) vs `:40` (`"eslint-config-next": "15.5.21"`).
- Fix: bump `eslint-config-next` to the `16.x` line matching `next` when next doing routine dependency
  maintenance, so lint rules track the framework version actually in use.

#### Files inspected

- `ui/lib/`: `a11y.ts`, `a11y.test.ts`, `analytics.ts`, `api.generated.ts` (header only),
  `api.ts`, `bot.ts`, `botClock.test.ts`, `botClock.ts`, `challengeLink.test.ts`, `challengeLink.ts`,
  `clockAnalysis.test.ts`, `clockAnalysis.ts`, `costPreview.test.ts`, `costPreview.ts`,
  `costVectors.test.ts`, `customConfig.ts`, `fen.ts`, `presets.ts`, `realtime.ts`, `replayCard.test.ts`,
  `replayCard.ts`, `rules.generated.ts` (header only), `site.ts`, `turnstile.ts`, `tutorial.ts`,
  `types.ts`, `useDisplayNameCheck.ts`
- `ui/scripts/`: `gen-api.mjs`, `gen-icons.mjs`, `gen-rules.mjs`
- `ui/next.config.ts`, `ui/eslint.config.mjs`, `ui/vitest.config.mts`, `ui/vitest.setup.ts`,
  `ui/tsconfig.json`, `ui/package.json`, `ui/package-lock.json` (spot-checked for `sharp`)
- `ui/instrumentation.ts`, `ui/instrumentation-client.ts`, `ui/sentry.edge.config.ts`,
  `ui/sentry.server.config.ts`
- `ui/i18n/request.ts`, `ui/messages/en.json`
- Spot-checked consumers (not full audits): `ui/app/game/[id]/page.tsx` (bot effect, cost preview
  usage), `ui/app/learn/page.tsx` (cost preview / tutorial usage)

### Section FE-2: Shared components

#### Summary

All 57 files under `ui/components/` (including `ui/components/learn/`) were read in full. The
board/clock/economy components (`Board.tsx`, `AccessibleBoard.tsx`, `Clock.tsx`, `ClockGraph.tsx`,
`TimeDeltaLayer.tsx`, `MoveList.tsx`, `GameControls.tsx`, `GameOverOverlay.tsx`,
`CapturedMaterialPanel.tsx`) are all **controlled** components driven entirely by props (`fen`,
`seconds`, `legalUci`, `history`, ...) with no internal timers and no cached copy of server state
that could go stale or negative on its own — `Clock.tsx`'s `formatTime` already clamps to `0:00`
(`Clock.tsx:17-22`, pinned by `Clock.test.tsx`). Reconciliation of an optimistic move against a
server disagreement, and the interval that actually ticks the clock down, both live in the parent
game page (`ui/app/game/[id]/page.tsx`), which is out of scope for this section (FE-3/FE-4) — there
is nothing to reconcile inside `Board.tsx`/`AccessibleBoard.tsx` themselves since they hold no
independent position state beyond drag/selection UI state.

No blockers were found. The real issues are: one hub connection that reconnects more than it
needs to (visible presence flicker), the three game-adjacent modals having weaker focus/dialog
semantics than the rest of the a11y effort would suggest, one dead-simple UI bug from a
triplicated clipboard-copy pattern, and a handful of duplicated helpers that belong in `ui/lib/`.

#### Findings

##### Blockers

None found.

##### Should fix

**1. `PresenceProvider.tsx:26-40` reconnects the presence hub on every access-token refresh, not just on login/logout.**
The effect's dependency array is `[token, hydrated]` (`PresenceProvider.tsx:40`), and `token` is a
new string on every silent refresh persisted by `AuthProvider.tsx:136-149`
(`setOnTokenRefreshed`), which can fire many times over a session. Each time, the whole
`useEffect` tears down and recreates the SignalR connection (`PresenceProvider.tsx:30-39`), so
`usePresence()` briefly goes back to `null` and `PresenceIndicator.tsx:14` (`if (!presence) return
null;`) blinks out. This defeats the purpose: `createPresenceConnection()`
(`ui/lib/realtime.ts:34-40`) already builds an `accessTokenFactory: () => getAuthToken() ?? ""`
that SignalR calls fresh on every (re)negotiate — the connection does not need to be rebuilt for
the identity to stay current. The doc comment's stated reason ("Reconnects when the token changes,
since the token carries the identity", `PresenceProvider.tsx:21`) is the thing to revisit: drop
`token` from the dependency array and keep only `hydrated`.
*Fix:* change the effect deps to `[hydrated]`; the `accessTokenFactory` already handles a rotated token per reconnect.

**2. The three game-adjacent modals have weaker dialog semantics than the rest of the a11y work implies.**
`GameOverOverlay.tsx:102-104` renders its overlay as a bare `<div>` with no `role="dialog"`, no
`aria-modal`, and no `aria-labelledby` at all (unlike `ReportDialog.tsx:63-67` and
`BotGameDialog.tsx:56-60`, which do have those three attributes). None of the three — including the
two that do have ARIA dialog roles — trap focus, handle `Escape`, or move initial focus onto the
dialog when it opens, so a keyboard user can `Tab` straight through the dialog into the now-hidden
page behind it. `GameOverOverlay` is exactly the state this project's own accessibility notes flag
as unreachable by `e2e/tests/accessibility.spec.ts` (that spec deliberately excludes `game/[id]`),
so a WCAG violation here has no automated check at all, axe or otherwise. (This is not the
NVDA-PASS gap called out in `docs/plans/a11y/NVDA-PASS.md` — that plan covers the live board itself,
not the end-of-game modal or the two report/bot dialogs.)
*Fix:* give `GameOverOverlay` the same `role="dialog" aria-modal="true" aria-labelledby=...` shape
as the other two, and add a shared `Escape`-to-close + focus-trap (or initial-focus-on-mount) to
all three rather than three independent implementations.

**3. `ApiTokensCard.tsx:118-126`'s "Copied" label never resets.**
```
async function handleCopy() {
  if (!revealed?.token) return;
  try {
    await navigator.clipboard.writeText(revealed.token);
    setCopied(true);
  } catch {
    setCopied(false);
  }
}
```
There is no timeout to flip `copied` back to `false`. The two sibling implementations of the same
"copy to clipboard, show confirmation" pattern — `TwoFactorCard.tsx:166-175`'s `copyText` and
`ChallengeFriendPanel.tsx:197-209`'s `handleCopy` — both `setTimeout(() => onDone(false), 2000)`.
Because this one was hand-rolled a third time instead of sharing the other two, the button
permanently reads "Copied" after the first click, until the user creates a brand new token
(`setCopied(false)` only happens in `handleCreate`, `ApiTokensCard.tsx:77`).
*Fix:* add the same 2-second reset (or, better, extract one `copyToClipboard(text, onDone)` helper
into `ui/lib/` and use it in all three places).

**4. `SessionsCard.tsx:35-45` and `TwoFactorCard.tsx:62-73` fetch unconditionally on mount, unlike every sibling profile card.**
`ApiTokensCard.tsx:46` (`if (!token) return;`), `NotificationPreferencesCard.tsx:36`, `PushNotificationToggle.tsx:49`, and `MatchHistoryCard.tsx:70` all guard their data-fetching effect on `token` before calling the API. `SessionsCard.tsx:45` (`useEffect(load, [])`) and `TwoFactorCard.tsx:73` (`useEffect(loadStatus, [])`) do not — `load()`/`loadStatus()` call `getSessions()`/`getTwoFactorStatus()` regardless of whether a token exists, and neither effect has a `cancelled` guard against a state update after unmount either (every other card in this directory that fetches on mount does). In practice this is likely masked by `RequireAuth` gating the whole `/profile` route, but that gating lives outside this section, and the two components don't defend themselves the way their neighbors do.
*Fix:* match the sibling pattern — check `if (!token) return;` inside the effect and add a `cancelled` flag, the same shape `ApiTokensCard` already uses.

**5. `DetectionReportCard.tsx`'s `run()` (163-184) has no defense against a stale response landing on the wrong dossier.**
Every other on-demand fetch in this directory either guards on `token`/prop identity or uses a `cancelled` flag; `run()` does neither — it's a plain `.then()/.catch()` closing over `fetchReports`, which the parent (`ui/app/moderation/[userId]/page.tsx:548,552`) rebuilds per-`userId`. If the moderation route is ever navigated between two `userId`s without a full remount of this component, a slow `fetchReports()` call started for player A can resolve after the moderator has moved on to player B and overwrite B's card with A's report. Next.js App Router most likely remounts the page on a dynamic-segment change, which is probably why this hasn't surfaced, but the component itself provides no guarantee.
*Fix:* add a `cancelled`/generation-counter guard the way `MatchHistoryCard.tsx:71,91-93` does.

**Test coverage — ranked by what the absence actually costs, not blanket coverage.**
Only `AccessibleBoard`, `Clock`, and `LiveAnnouncer` have `.test.tsx` files. Of the untested 54:
1. **`Board.tsx`** — the primary (non-a11y) way to play a move. Castling-by-clicking-the-rook
   (`Board.tsx:78-92`), promotion defaulting to queen on both the click path (`Board.tsx:98-103`)
   and the drag path (`Board.tsx:128-134`), and the legal-move gate before `onMove` fires
   (`Board.tsx:105-107`, `136-138`) are exactly the kind of branchy interaction logic that breaks
   silently in a refactor and has zero coverage today, unlike its accessible sibling.
2. **`AuthProvider.tsx`** — session persistence, `normalizeRoles`, and the refresh/adopt/logout
   state machine (`AuthProvider.tsx:107-267`) is core session-security logic with no test at all,
   despite `docs/decisions/AUTH.md`'s level of scrutiny on this exact area.
3. **`GameOverOverlay.tsx`** — the `mySide`-to-`ratingChange` selection (`GameOverOverlay.tsx:95-100`)
   and the `ServerRestart`/unrated/rated branching in `resultText` (`GameOverOverlay.tsx:85-89`)
   are the kind of small conditional logic that's easy to get backwards (e.g. showing White's
   delta to Black) and would be caught immediately by a handful of prop-variant render tests.

##### Nits

- **`ui/components/learn/TutorialClocks.tsx:17-22`** duplicates `Clock.tsx:17-22`'s `formatTime` and most of its JSX verbatim. The doc comment (`TutorialClocks.tsx:24-27`) explains this was deliberate at the time because `Clock.tsx` was "the concurrent Phase 6f effort and may be changing under us" — that phase has clearly landed since `Clock.tsx` now exists, is stable, and has its own test file. Worth re-checking whether the fork is still needed.
- **`formatWhen`** is defined identically in `SessionsCard.tsx:12-17` and `ApiTokensCard.tsx:18-23` (same `toLocaleString(undefined, { dateStyle: "medium", timeStyle: "short" })` call). Belongs in `ui/lib/`.
- **`Translator`** (the `(key, values?) => string` type alias) is defined identically in `DonationTiers.tsx:91-94` and `MatchHistoryCard.tsx:19-22`. Belongs in `ui/lib/types.ts` or a shared module rather than being hand-copied.
- **`PlayButton.tsx:9-19,42-47`** is formatted differently from every neighboring file in this directory — single-quoted style values (`'pointer'`, `'inline-flex'`), irregular prop-destructuring spacing — suggesting it was never run through the project's usual formatter, unlike the rest of `ui/components/`.
- **`OpenSeekList.tsx:66`** and **`RatedPoolsGrid.tsx:76`** call `conn.stop();` in their cleanup without `void`/`await`, while `PresenceProvider.tsx:38` uses `void conn.stop();` for the identical teardown. Harmless today (an unhandled rejection would only log to console), but inconsistent with the pattern used two files over.

#### Files inspected

All 57 files under `ui/components/` (including `ui/components/learn/`):

Card.tsx, PresenceProvider.tsx, ServiceWorkerRegistration.tsx, AuthProvider.tsx, LiveAnnouncer.tsx,
RequireAuth.tsx, PlayButton.tsx, learn/TutorialStepCard.tsx, DonationTiers.tsx,
AccessibleBoard.test.tsx, Clock.test.tsx, LiveAnnouncer.test.tsx, CounterBeacon.tsx,
SkeletonRows.tsx, Board.tsx, BrandMark.tsx, NavBar.tsx, AccessibleBoard.tsx, Avatar.tsx, Clock.tsx,
ClockGraph.tsx, EmptyState.tsx, Footer.tsx, GameOverOverlay.tsx, InstallAppButton.tsx,
MoveList.tsx, OpenSeekList.tsx, RatingBadge.tsx, ReportDialog.tsx, SanctionBanner.tsx,
TimeDeltaLayer.tsx, learn/TutorialClocks.tsx, learn/TutorialProgress.tsx, TwoFactorPrompt.tsx,
CapturedMaterialPanel.tsx, DataTable.tsx, DetectionReportCard.tsx, GameControls.tsx,
GapLadder.tsx, GapMeter.tsx, PoolBadge.tsx, BotGameDialog.tsx, RatedPoolsGrid.tsx,
SessionsCard.tsx, TwoFactorCard.tsx, DisplayNameCard.tsx, NotificationPreferencesCard.tsx,
PushNotificationToggle.tsx, SecurityCard.tsx, ApiTokensCard.tsx, AccountDataCard.tsx,
PoolRatingCard.tsx, PresenceIndicator.tsx, MatchHistoryCard.tsx, PresetTile.tsx,
ChallengeFriendPanel.tsx, BotPlayPanel.tsx.

Also read for context (not part of this section's scope, touched only to verify a claim above):
`ui/lib/realtime.ts` (`createPresenceConnection`'s `accessTokenFactory`), `ui/lib/types.ts` and
`ui/lib/api.generated.ts` (`MySanctionDto` shape), `ui/app/moderation/[userId]/page.tsx`
(`DetectionReportCard` call sites).

### Section FE-3: Auth, account and app-shell routes

#### Summary

Scope: `ui/app/layout.tsx`, `error.tsx`, `global-error.tsx`, `not-found.tsx`, `login/`, `forgot-password/`, `reset-password/page.tsx`, `verify-email/page.tsx`, `auth/callback/page.tsx`, `profile/page.tsx`, `unsubscribe/page.tsx`.

Core auth mechanics are solid: the OAuth callback (`auth/callback/page.tsx`) correctly handles all four server error slugs (`cancelled`, `unverified_email`, `link_refused`, `unavailable`), the exchange code is single-use server-side (`ExternalLoginCodeService.ConsumeAsync`) and atomically consumed, a successful exchange calls `router.replace` (not `push`) so the code drops out of history, and the `started` ref latch prevents a double-exchange from React StrictMode. `AuthProvider`/`api.ts` token handling matches the documented `localStorage` model in `docs/decisions/AUTH.md` with no extra mistake found. `forgot-password/page.tsx` correctly never reveals whether an email exists. All forms disable their submit button while in flight.

Two real inconsistencies were found: the root layout loads the Cloudflare Turnstile script on every route, which contradicts the "on one route" premise `docs/decisions/AUTH.md` §1.7 relies on for its third-party-script risk accounting; and the OAuth callback page reimplements a weaker, untested copy of the shared open-redirect guard (`ui/lib/challengeLink.ts`'s `safeNextPath`) that is missing the control-character strip the shared version's own comment says is load-bearing. A profile-page accessibility regression (broken skip-link target) was also found and is not covered by the existing a11y test suite.

Counts: 0 Blockers, 3 Should fix, 3 Nits.

#### Findings

##### Blockers

None found.

##### Should fix

1. **Turnstile script loads globally, not "on one route" as the security threat model assumes.** `ui/app/layout.tsx:65-70` renders `<Script src="https://challenges.cloudflare.com/turnstile/v0/api.js" ... />` unconditionally inside the root `<body>`, which wraps every route in the app, and it is not gated on `TURNSTILE_SITE_KEY` being configured (`ui/lib/turnstile.ts:1-4` shows the key can be empty). `docs/decisions/AUTH.md:179` (§1.7 trigger 2) states "Today the only external script is Turnstile's, on one route, from Cloudflare," and treats each additional third-party script as directly widening the blast radius of the accepted `localStorage` token exposure ("Each addition converts a supply-chain compromise into a session-database compromise"). The code does not match that premise: the script (and its supply-chain risk) is present on every page view, not just `/login`.
   Fix: move the `<Script>` tag out of the root layout into `ui/app/login/page.tsx` (or a wrapper client component), conditioned on `TURNSTILE_SITE_KEY` being set, so the code matches the "one route" claim `docs/decisions/AUTH.md` already relies on; alternatively, if global loading is intentional, update that doc's premise instead.

2. **The OAuth callback's own open-redirect guard is a weaker, untested duplicate of the shared one.** `ui/app/auth/callback/page.tsx:42-49` defines a local `safeReturnPath` whose own docstring (lines 33-41) says it mirrors the server's `ExternalAuthEndpoints.SafeReturnPath` and must not rely on the server "because the page is reachable directly." But `ui/lib/challengeLink.ts:6-32`'s `safeNextPath` (used by `login/page.tsx` for `?next=`) strips ASCII tab/CR/LF from the candidate *before* running the same checks, with a comment explaining exactly why: "browsers strip tab/CR/LF from a URL before parsing it," so a raw tab character between `/` and the rest of a path can survive a naive `.startsWith("//")` check as plain text and only become `//evil.example` once something later parses it as a URL. `auth/callback/page.tsx`'s local copy has no such strip step, so it is missing the exact protection its sibling function documents as necessary, and it is not covered by `ui/lib/challengeLink.test.ts` (which only tests `challengeLink.ts`'s own `safeNextPath`) or any other test found in the repo.
   Fix: delete the local `safeReturnPath` and import/use `safeNextPath` from `ui/lib/challengeLink.ts` instead, the same way `login/page.tsx` does, so there is one tested definition of "safe" instead of two that can drift.

3. **Skip-to-content link is broken on `/profile`.** `ui/components/NavBar.tsx:86-91` renders `<a href="#main-content">…Skip to content</a>` as the first element on every page that includes `<NavBar />`. `ui/app/profile/page.tsx:86` renders `<NavBar />` but its `<main>` element at `profile/page.tsx:88` (`<main className="flex-1 w-full max-w-7xl mx-auto px-8 pt-10 pb-24">`) has no `id="main-content"`, unlike `ui/app/error.tsx:29-32` and `ui/app/not-found.tsx:21-24`, which both pair `<NavBar />` with a `<main id="main-content">`. A keyboard or screen-reader user activating the skip link on the profile page gets no effect. This is not caught by `e2e/tests/accessibility.spec.ts`: `/profile` is not in its `ANONYMOUS_PAGES` list and is not the one signed-in page it separately scans (the custom-game builder).
   Fix: add `id="main-content"` to the `<main>` in `ui/app/profile/page.tsx:88`.

##### Nits

1. **Five routes in scope have no route-level metadata.** `login/layout.tsx` and `forgot-password/layout.tsx` both exist specifically, per their own comments, "because the page below is a client component" (mirroring `ui/app/learn/layout.tsx`). `reset-password/page.tsx`, `verify-email/page.tsx`, `auth/callback/page.tsx`, `unsubscribe/page.tsx` and `profile/page.tsx` are all `"use client"` pages with no sibling `layout.tsx`, so none of them export `metadata` and all fall back to the root layout's generic `"KiriChess"` title. Trigger: browser tab title / social-share preview for any of these five routes is generic rather than descriptive. Fix: add a thin `layout.tsx` per the established pattern, at least for `profile/page.tsx` as the most-visited of the five.

2. **`error.tsx` doesn't use next-intl while the near-identical `not-found.tsx` does.** `ui/app/error.tsx:36-61` hardcodes English copy ("Something went wrong", "Try again", "Back to home") inside the same NavBar+Card layout shape that `ui/app/not-found.tsx:8-43` uses with `getTranslations("notFound")` for every string. `global-error.tsx` is correctly untranslated (it replaces the root layout and has no `NextIntlClientProvider` to draw from), but `error.tsx` renders inside the normal chrome and could use `useTranslations` the same way. Trigger: any non-English visitor who hits a route-level render error sees English text while every other error-shaped page (404) is localized.

3. **`verify-email`/`unsubscribe` auto-run effects lack the double-invoke guard `auth/callback` documents as necessary.** `ui/app/verify-email/page.tsx:21-33` and `ui/app/unsubscribe/page.tsx:22-34` call `confirmEmail`/`unsubscribeFromNotifications` directly inside a `useEffect` with no ref latch, whereas `ui/app/auth/callback/page.tsx:57-60` explicitly adds a `started` ref specifically because "React StrictMode double-invokes effects in dev, and a naive effect would fire the exchange twice." `confirmEmail`/`unsubscribeFromNotifications` are idempotent server-side (verified: `AuthEndpoints.cs:318-333`'s `ConfirmEmailAsync` just re-succeeds on a token that already confirmed the account), so this is a harmless extra dev-mode network call rather than a functional bug, but it is an inconsistency with the pattern the neighboring auth-flow page already established for exactly this class of effect.

#### Files inspected

- `ui/app/layout.tsx`
- `ui/app/error.tsx`
- `ui/app/global-error.tsx`
- `ui/app/not-found.tsx`
- `ui/app/login/layout.tsx`
- `ui/app/login/page.tsx`
- `ui/app/forgot-password/layout.tsx`
- `ui/app/forgot-password/page.tsx`
- `ui/app/reset-password/page.tsx`
- `ui/app/verify-email/page.tsx`
- `ui/app/auth/callback/page.tsx`
- `ui/app/profile/page.tsx`
- `ui/app/unsubscribe/page.tsx`

Read for context (not primary scope, FE-2/backend-owned):
- `ui/components/AuthProvider.tsx`
- `ui/components/TwoFactorPrompt.tsx`
- `ui/components/NavBar.tsx`
- `ui/lib/api.ts`
- `ui/lib/challengeLink.ts`
- `ui/lib/turnstile.ts`
- `ui/lib/site.ts`
- `ui/instrumentation-client.ts`
- `docs/decisions/AUTH.md`
- `src/KiriChess.Server/Auth/ExternalAuthEndpoints.cs`
- `src/KiriChess.Server/Auth/ExternalLoginCodeService.cs`
- `src/KiriChess.Server/Auth/AuthEndpoints.cs` (login/confirm-email handlers only)
- `e2e/tests/auth.spec.ts`
- `e2e/tests/accessibility.spec.ts`

### Section FE-4: Game, social and content routes

#### Summary

Scope: `ui/app/game/[id]`, `ui/app/watch/*`, `ui/app/replay/[id]/*`, `ui/app/challenge/[token]/*`,
`ui/app/custom`, `ui/app/leaderboards/*`, `ui/app/moderation/*`, `ui/app/analytics`, and the
content/metadata routes (`ui/app/page.tsx`, `about`, `learn/*`, `rules`, `privacy`, `terms`,
`developers`, `donate`, `offline`, `manifest.ts`, `robots.ts`, `sitemap.ts`, `opengraph-image.tsx`).

The admin-gated pages (`moderation`, `analytics`) do the right thing: both carry an explicit
"`isAdmin` only hides page content; the server enforces this on every route regardless" comment and
render a clean `EmptyState` refusal for a non-admin rather than a broken page. The `custom` game
builder clamps every rule-variable input client-side (`clampInt` in `NumberField`'s `onChange`), so
the engine's own lack of upper bounds (already filed under ENG-1) cannot actually be reached through
the UI — not re-filed here. `watch/[id]` genuinely works with no auth token (`createGameConnection`
sends an empty access token, and `JoinAsSpectator` is the one hub method that accepts it), and
`replay/[id]` never opens a hub connection at all. No raw `fetch` against `/api/*` anywhere in this
scope; everything goes through `ui/lib/api.ts`. Empty/error states (empty watch lobby, empty
leaderboard pool, expired/unknown challenge token, non-existent replay id) are all handled with a
real `EmptyState`, not a blank screen.

The one real functional gap is on the page the task singled out for the closest look: the live game
page's own reconnect/rejoin path discards move history, captured material and the clock-history graph
on every reconnect or fresh load into a game already in progress, while its sibling spectator page
(`watch/[id]`) does the equivalent restore correctly from the same kind of hub snapshot. A second,
narrower gap is a metadata-fetch path that can never reach the compose-internal backend, unlike the
pattern its sibling (`replay/[id]`) already established for the same problem.

Counts: Blockers 0, Should fix 2, Nits 1.

#### Findings

##### Blockers

None found.

##### Should fix

**FE-4-1 — Reconnecting or reloading mid-game wipes the move list, captured-material panel and clock graph**
- Severity: High (trigger: any SignalR `onreconnected` transport blip, or simply loading/refreshing
  `/game/[id]` for a game that already has moves played)
- Where: `ui/app/game/[id]/page.tsx:390-413` (the `join` function) and `:415-418`
  (`conn.onreconnected(() => join()...)`)
- Impact: `join()` unconditionally calls `setMoves([])`, `setCapturedByWhite([])`,
  `setCapturedByBlack([])`, `setCaptureSecondsWhite(0)`, `setCaptureSecondsBlack(0)`, and resets
  `clockHistory` to a single point at the current time, regardless of `snap.state.moveCount`. This
  is because `GameSnapshotDto` (`JoinGame`'s return type, `src/KiriChess.Server/Games/RealtimeDtos.cs:7`,
  aliased as `GameSnapshot` in `ui/lib/types.ts:442`) only carries `{ state, legalMoves }` — there is
  no move log to restore from. Board position, clocks and legal moves stay correct (they live on
  `state.fen`/`state.whiteTime`/`state.blackTime`), but every reconnect or reload during a live game
  visibly blanks the move list, hides all previously captured pieces, and collapses the clock-swing
  graph to nothing, even though the game itself is fine. Contrast the sibling spectator page: its
  `SpectatorSnapshotDto` is documented as carrying "the full move log so a spectator arriving mid-game
  gets a complete move list and clock graph" (`ui/lib/types.ts:482-485`), and `watch/[id]/page.tsx`'s
  own `join()` (`:148-177`) correctly rebuilds `moves`/`clockHistory` from `snap.moves` on every call,
  including the reconnect path — so the fix pattern already exists one file over, just not applied to
  the page that needs it more.
- Fix: Either extend `GameSnapshotDto`/`JoinGame` to carry the move log the same way
  `SpectatorSnapshotDto`/`JoinAsSpectator` does, or have the player-side `join()` only clear
  `moves`/`clockHistory`/captured-material state on the very first join (when there is nothing to
  preserve) and otherwise leave existing local history in place across a reconnect.

**FE-4-2 — Challenge preview card can never use the compose-internal API base, unlike replay's**
- Severity: Medium (trigger: self-hosting via any docker-compose overlay and pasting a challenge link
  into a chat app, even with `API_BASE_INTERNAL` correctly set)
- Where: `ui/lib/api.ts:518-520` (`getChallenge(token)`, no `base` parameter) vs `:464-466`
  (`getReplay(id, base?: string)`); consumed at `ui/app/challenge/[token]/layout.tsx:16-19`
  (`generateMetadata` calls `getChallenge(token)` with nothing to override the default)
- Impact: `replay/[id]/layout.tsx:10-21` explicitly documents and works around this problem: its
  server-side `generateMetadata` passes `process.env.API_BASE_INTERNAL` because the browser-facing
  `NEXT_PUBLIC_API_BASE` "may not be reachable from inside the frontend container itself." The
  challenge layout's own comment states its whole reason for existing is the same thing replay's
  does — "this URL's entire job... is to be pasted into a chat window, and the preview card is what a
  friend sees before they ever load the page" (`ui/app/challenge/[token]/layout.tsx:6-10`) — but
  `getChallenge` has no `base` parameter to pass an override through at all, so even a deployment that
  sets `API_BASE_INTERNAL` correctly (e.g. `docker-compose.e2e.yml:51`) cannot fix this one at the
  config layer the way INF-1-5 fixes the equivalent replay-side gap; it needs a code change. The
  `catch` block silently falls back to the generic "Play a friend" card, so nothing surfaces the
  failure to an operator.
- Fix: Give `getChallenge` the same optional `base?: string` parameter `getReplay` has, and pass
  `process.env.API_BASE_INTERNAL` from `challenge/[token]/layout.tsx`'s `generateMetadata`, mirroring
  `replay/[id]/layout.tsx:21`.

##### Nits

**FE-4-3 — `developers/page.tsx` redefines `API_BASE` instead of importing it**
- Severity: Low (trigger: someone changes the fallback URL in `ui/lib/api.ts` and doesn't notice this
  second copy)
- Where: `ui/app/developers/page.tsx:6-7`
  (`const API_BASE = process.env.NEXT_PUBLIC_API_BASE || "http://localhost:5053";`) vs
  `ui/lib/api.ts:47-48` (`export const API_BASE = process.env.NEXT_PUBLIC_API_BASE ?? "http://localhost:5053";`)
- Impact: Purely cosmetic today (`||` and `??` behave identically here since the env var is either a
  non-empty string or unset), but it's a second, independent source of truth for the same fallback
  constant on a page whose entire content is "here is the real API base URL," which is exactly the
  kind of value most worth reading from one place.
- Fix: `import { API_BASE } from "@/lib/api";` and drop the local re-declaration.

#### Files inspected

- `ui/app/game/[id]/page.tsx`
- `ui/app/watch/layout.tsx`, `ui/app/watch/page.tsx`, `ui/app/watch/[id]/page.tsx`
- `ui/app/replay/[id]/page.tsx`, `ui/app/replay/[id]/layout.tsx`, `ui/app/replay/[id]/opengraph-image.tsx`
- `ui/app/challenge/[token]/page.tsx`, `ui/app/challenge/[token]/layout.tsx`
- `ui/app/custom/page.tsx`
- `ui/app/leaderboards/layout.tsx`, `ui/app/leaderboards/page.tsx`
- `ui/app/moderation/page.tsx`, `ui/app/moderation/[userId]/page.tsx`
- `ui/app/analytics/page.tsx`
- `ui/app/page.tsx`
- `ui/app/about/page.tsx`
- `ui/app/learn/page.tsx`, `ui/app/learn/layout.tsx`
- `ui/app/rules/page.tsx`
- `ui/app/privacy/page.tsx`
- `ui/app/terms/page.tsx`
- `ui/app/developers/page.tsx`
- `ui/app/donate/page.tsx`
- `ui/app/offline/page.tsx`
- `ui/app/manifest.ts`
- `ui/app/robots.ts`
- `ui/app/sitemap.ts`
- `ui/app/opengraph-image.tsx`

Supporting reads (out of primary scope, consulted for cross-referencing): `ui/lib/types.ts`,
`ui/lib/api.ts`, `ui/lib/api.generated.ts` (schema check only), `ui/lib/realtime.ts`,
`ui/lib/customConfig.ts`, `ui/lib/rules.generated.ts`, `ui/eslint.config.mjs`,
`src/KiriChess.Server/Games/GameActionExecutor.cs`, `src/KiriChess.Server/Games/RealtimeDtos.cs`,
`docker-compose.e2e.yml`, `docs/audits/C.md (section INF-1)` (to confirm FE-4-2 doesn't duplicate
INF-1-5, which covers replay's compose-config side of the same class of bug, not challenge's
code-level gap).

### Deep audit improvement plan (packet C-1)

Date: 2026-08-22
Packet: `docs/audits/INDEX.md` table 3, **C-1 sectioned code quality**
Sections: 11, all complete. Findings live in `docs/audits/C.md (C-1) sections/`.
No production code was changed.

#### Verdict

The codebase is in good health, and this audit's main value is not a list of bugs. It is a short list
of places where a rule the project already states about itself is not enforced by anything. Nothing
found loses data or hurts a stranger today, and no finding survived verification as Critical. The
three High findings are a ban that is not checked when Google sign-in mints a token, a player losing
their own move history on reconnect, and REST game endpoints that skip the ownership router.

Three things came back clean that would have been expensive to get wrong, and they are worth stating
as results rather than as silence:

- **Clock discipline holds.** No `IGameClock` violation anywhere in `Games/`, no wall-clock
  subtraction used as a duration, and no wall-clock read, randomness or I/O inside the engine.
- **The three-way rule mirror agrees.** `config/kirichess.rules.json`, the generated `docs/RULES.md`
  and `ui/lib/costPreview.ts` show no drift.
- **Authorization holds where it is applied.** `ModerationPolicies.Admin` gates every admin route with
  no gap, every attribution id comes from claims rather than from a request body, and `UserSanction`'s
  foreign keys are `Restrict`, so a sanction survives erasure of the account it targets.

The recurring theme across sections is **enforcement by convention rather than by construction**: a
rule is documented, applied correctly at four call sites, and missed at the fifth, with no test or
type that would notice. Batch 1 is the clearest instance. The ordering below puts the cheap structural
fixes that make a whole class of miss impossible ahead of the individual misses.

#### Severity counts

| Section | Critical | High | Medium | Low |
|---|---|---|---|---|
| BE-1 Auth | 0 (1 re-graded) | 1 | 2 | 2 |
| BE-2 Games and realtime | 0 | 1 | 2 | 3 |
| BE-3 Ratings and analytics | 0 | 0 | 3 | 4 |
| BE-4 Moderation, account, retention | 0 | 0 | 2 | 6 |
| BE-5 Data, infrastructure, Program | 0 | 0 | 3 | 4 |
| ENG-1 Engine | 0 | 0 | 3 | 5 |
| INF-1 Infrastructure files | 0 | 0 | 3 | 3 |
| FE-1 Frontend lib and config | 0 | 0 | 2 | 4 |
| FE-2 Components | 0 | 0 | 5 | 5 |
| FE-3 Auth and account routes | 0 | 0 | 3 | 3 |
| FE-4 Game and content routes | 0 | 1 | 1 | 1 |
| **Total** | **0** | **3** | **29** | **40** |

BE-1 filed its finding as Critical. It was verified at the source and re-graded to High, with the
reasoning recorded under Verifications in the C-1 run log below, rather than quietly changed.

---

#### Batch 1 — Sanction enforcement is applied by hand, and two endpoints were missed

Security. Do this first. Two passes over different folders independently found the same shape,
which is what makes this a design finding rather than two bugs.

`SanctionPolicy.BlockingSanctionAsync` is called at each site that needs it. There is no central gate,
so a new authenticated endpoint is unsanctioned by default and nothing fails when one is forgotten.
`SanctionPolicy.cs:23-27`'s own doc comment enumerates its enforcement points, which makes both gaps
below read as omissions rather than as decisions.

1. **`/api/auth/external/exchange` mints tokens without a ban check.** `Auth/ExternalAuthEndpoints.cs:101-128`
   has no `SanctionPolicy` dependency at all, while `Auth/AuthEndpoints.cs:172`, `:231` and `:281`
   (login, 2FA step, refresh) all check. A banned user who can complete the Google handshake gets a
   fresh 15-minute access token whenever they like. Play is still blocked by `PlayEligibilityPolicy`,
   and both refresh and the 2FA step do check, which is why this is High rather than Critical, but the
   endpoint skips the gate its own scope name describes.
   *Fix:* inject `SanctionPolicy`, call `BlockingSanctionAsync(user.Id, SanctionScope.Account, ct)`
   before issuing tokens, mirroring `AuthEndpoints.cs:172-174`, and add the matching
   `.Produces<ErrorResponse>(403)`.

2. **`POST /api/account/delete` is not gated on an active ban.** `Account/AccountDeletionService.cs:31-42`
   guards not-found, already-deleted and in-game, but not sanctions. A ban revokes refresh and API
   tokens (`Moderation/SanctionService.cs:78-79`) and not the current JWT, so inside that window a
   banned player can free their email and display name. The sanction row itself survives on the
   `Restrict` foreign keys, and signup is email-only with no identity check, so this does not open
   evasion that is not already open. What it costs is the moderator's correlation signal.
   *Fix:* add a `BlockingSanctionAsync` check beside the in-game guard, returning a new
   `AccountDeletionOutcome` that the endpoint maps to 409, the shape `GameInProgress` already uses.

3. **The structural fix, which is the point of the batch.** Both misses are invisible because nothing
   enumerates the enforcement points. Either add a test asserting that every token-minting path
   refuses a banned account (table-driven over the four endpoints), or move the check behind a shared
   filter so a new endpoint opts out deliberately rather than by omission.

Cross-reference: packet **S-1** owns security and authorization, **S-3** owns moderation. Neither has
run yet. Hand them this batch rather than rediscovering it.

---

#### Batch 2 — A player sees less of their own game than a spectator does

User-visible, and the likeliest thing here to be noticed by a stranger in the launch window.

1. **Reconnect wipes the move list, captured material and the clock graph.** `ui/app/game/[id]/page.tsx:392-401`
   resets all three on every `join()`, and `join()` is exactly what `onreconnected` calls at `:415-418`.
   A transport blip or a mid-game reload empties the move list and the clock-economy graph for the
   player, while `ui/app/watch/[id]/page.tsx:158-175` rebuilds all of it for a spectator watching the
   same game. Board and clocks stay correct, so this is presentation, but the clock graph is the
   feature that makes the variant legible.
   *Root cause, which changes the fix:* a DTO asymmetry, not a missed client pattern.
   `Games/RealtimeDtos.cs:7` defines `GameSnapshotDto` as state plus legal moves; `:80-85` gives
   `SpectatorSnapshotDto` a full `Moves` list. The data already exists server-side.
   *Fix:* add the move log to `GameSnapshotDto` and rebuild client state from it on rejoin. Per
   `.claude/rules/realtime.md` that means declaring it in `HubContract.cs` first and regenerating
   OpenAPI, so it is a real change and not a one-liner. A smaller interim fix is an HTTP refetch of the
   game's moves on rejoin.

2. **A dropped connection on the accepter's side suppresses the challenger's notice.**
   `Games/ChallengeEndpoints.cs:200-206` passes the accepting request's own `CancellationToken` to both
   the SignalR broadcast and the notification email. If the accepter's connection drops after the game
   is committed, the person who created the challenge link is never told it was accepted, through any
   channel, and `NotifyChallengeAcceptedAsync` swallows the cancellation silently.
   *Fix:* pass `CancellationToken.None` (or a short independent token) to the broadcast and
   notification once the game itself is committed. One request's connection state should not suppress a
   message meant for someone else.

---

#### Batch 3 — Dormant today, wrong on the first day of Phase 8.4

Every item here is invisible on one Fly machine with Redis unprovisioned, and becomes a live defect the
moment a second instance exists. Redis being unprovisioned is a standing position and not a finding;
shipping these unfixed into the moment it changes is the risk. Fix them before 8.4, not during it.

1. **REST move, resign and get bypass the ownership router.** `Games/GameEndpoints.cs:87-165` and
   `Games/PublicApiEndpoints.cs:297-355` call `GameRegistry.Get(id)` directly and 404 on a miss. Every
   hub action goes through `IGameActionExecutor`, which resolves ownership via `GameCommandRouter`
   (`GameActionExecutor.cs:113-141`). `.claude/rules/realtime.md` states the rule these two skip. The
   affected surface is the bot API and the REST fallback, so the visible symptom is a bot getting a 404
   for a game that is very much still running. `TwoInstanceGameTests.cs:167-175` exercises only the hub
   path.
   *Fix:* route both through `IGameActionExecutor`, or document the two REST surfaces as
   single-instance-only until they are migrated.

2. **`MatchNoShowWatcher` asks a process-local question.** `Games/MatchNoShowWatcher.cs:57-58` uses the
   synchronous `IsConnected`, while `GameConnectionTracker.cs:251` and `GameRecoveryService.cs:142` were
   both moved to the cluster-aware `IsConnectedAsync` for exactly this reason. A player connected to a
   different instance reads as absent and has their game aborted as a no-show after joining. There is
   no `MatchNoShowWatcherTests.cs`.
   *Fix:* switch both calls to `IsConnectedAsync`; the method is already async.

3. **The Redis multiplexer is never disposed.** `Infrastructure/Redis/RedisRegistration.cs:36-37` hands
   the container a pre-built instance, and the built-in provider only disposes singletons it
   constructs, so `RedisConnection.Dispose`/`DisposeAsync` are dead code on the shutdown path. Fires on
   any graceful shutdown once Redis is configured, which is also when `fly deploy` matters most.
   *Fix:* register via a factory so the container owns the instance.

4. **The leaderboard cache stops answering after an hour and never recovers.** `Ratings/LeaderboardWarmup.cs:47-48`
   documents an hourly re-warm; `ExecuteAsync` at `:41-83` runs once per process with no timer. When the
   `{p}lb:built` marker's one-hour TTL lapses, `LeaderboardCache.IsWarmAsync` returns false for the rest
   of the process's life and every read falls through to Postgres. It degrades safely, which is why it
   is Medium, but the Phase 8.4 feature has never worked as documented.
   *Fix:* either loop on a `PeriodicTimer` matching the TTL, or drop the claim from the comment and rely
   on write-through plus restart. The code and the comment need to agree either way.

---

#### Batch 4 — Rating integrity has no backstop below the in-memory guard

`Ratings/RatingService.cs:32-66` writes rating changes and two `RatingHistory` rows straight from the
passed-in `Game`, with no check that the game was already rated. The only thing preventing a double
apply is the caller's in-memory `_sessions.TryRemove` in `GameRegistry.PersistIfOverAsync`, which
guards concurrent callers on one live session and does nothing for a process restart that re-persists
an already-rated game. `KiriChessDbContext.cs:143-150` indexes `RatingHistory` on
`(UserId, Pool, CreatedAt)`, that index is not unique, and there is no constraint on `(GameId, UserId)`.

A rating is the one number a player accumulates over months, and a silent double-count is not visible
after the fact.

*Fix:* a unique index on `RatingHistory(GameId, UserId)`, so a duplicate insert throws where the caller
already logs instead of silently double-counting. That is a migration, so per `.claude/rules/backend.md`
generate it and do not run `database update` without asking.

---

#### Batch 5 — Engine config validation does not hold the guarantee engine.md states

`.claude/rules/engine.md` promises that an invalid config fails loudly at game creation. Today the
custom-game endpoint validates separately and gets there first, so none of this is reachable through
the API. The guarantee is the engine's to keep, and the duplicated enforcement is what makes these
worth closing.

1. **No upper bound on any rule variable.** `Config/KiriChessConfig.cs:94-126` checks non-negativity and
   never a ceiling, and the economy math is plain unchecked `int` (`KiriChessEngine.cs:208`, `:228`,
   `:236`, `:245-246`). Large values wrap and sign-flip the clock delta. Custom games are self and bot
   only, so the blast radius is the creator's own data.
   *Fix:* a per-field ceiling in `Validate()`, in the style of the existing checks.
2. **`MoveCostMode` is never checked as a defined enum value**, so an out-of-range value falls into
   `MovementToll`'s `_ => 0` default (`KiriChessEngine.cs:282-290`) instead of throwing.
   *Fix:* `Enum.IsDefined` in `Validate()`.
3. **`Presets` is a public static mutable dictionary of shared mutable configs** (`KiriChessConfig.cs:345`).
   All four call sites clone correctly today. Nothing enforces it.
   *Fix:* expose as `IReadOnlyDictionary`, or return a clone per lookup.

Per `.claude/rules/engine.md`, a changed rule variable needs a cost-vector fixture entry and not only a
unit test. Cross-reference: packet **C-8** owns "custom rule injection cannot break clocks or crash the
engine" and should take items 1 and 2 as input rather than rediscovering them.

---

#### Batch 6 — Frontend correctness

1. **The Stockfish worker is a leaked singleton with one global resolver.** `ui/lib/bot.ts:1-2`, `:11-29`,
   `:36`. It is never terminated, so it keeps running after the game ends and after navigation, and a
   second overlapping search overwrites the single resolver, so a stale `bestmove` can resolve the wrong
   game's promise. `bot.ts` has no tests.
   *Fix:* scope the worker to the game, terminate it on unmount, and key each search so a late reply for
   a superseded position is discarded.
2. **Presence reconnects on every token refresh.** `ui/components/PresenceProvider.tsx:40` depends on
   `[token, hydrated]`, and `ui/lib/realtime.ts:37`'s `accessTokenFactory` already re-reads the token per
   connect, so the teardown is unnecessary and makes the presence count blink out.
   *Fix:* depend on `[hydrated]` alone.
3. **Two stale-response gaps.** `ui/components/DetectionReportCard.tsx:163-184` has no cancellation guard,
   so a slow response can land on a different moderator dossier. `SessionsCard.tsx:45` and
   `TwoFactorCard.tsx:73` fetch unconditionally on mount while every sibling profile card guards on
   `token` first.
   *Fix:* the `cancelled` flag pattern `MatchHistoryCard.tsx:71,91-93` already uses.
4. **`getChallenge` cannot use the internal API base.** `ui/lib/api.ts:518-520` has no `base` override,
   unlike `getReplay` at `:464-466`, so a self-hosted challenge link silently falls back to a generic
   preview card.

---

#### Batch 7 — Accessibility in the places axe cannot reach

`e2e/tests/accessibility.spec.ts` deliberately excludes the live board, and `docs/plans/a11y/NVDA-PASS.md`
is written but not yet run (ROADMAP #5). Both items sit inside that blind spot, and neither is the NVDA
pass itself.

1. **`GameOverOverlay.tsx:102-104` is a bare `div`**: no `role="dialog"`, no `aria-modal`, no
   `aria-labelledby`, unlike `ReportDialog.tsx:63-67` and `BotGameDialog.tsx:56-60`. None of the three
   modals traps focus, moves initial focus, or handles `Escape`, so a keyboard user tabs straight through
   into the hidden page behind. This is the end-of-every-game state.
2. **The skip-to-content link is a no-op on `/profile`.** `NavBar.tsx:86-91` targets `#main-content`;
   `ui/app/profile/page.tsx:88`'s `main` has no id, unlike `error.tsx` and `not-found.tsx`.

Cross-reference: packet **P-5** owns accessibility engineering. Item 2 is a one-attribute fix worth
taking now.

---

#### Batch 8 — Where a document and the code disagree

In each case the fix is to change the code back to what the document says, not to amend the document.

1. **Turnstile loads on every route.** `ui/app/layout.tsx:64-70` puts the script in the root layout.
   `docs/decisions/AUTH.md:178-180` states as fact that the only external script is Turnstile's, "on one
   route", and that premise is load-bearing for its localStorage-token risk accounting: it is §1.7's
   trigger 2, the one that converts a supply-chain compromise into a session-database compromise. No new
   third party was added, so the trigger has not fired in the sense that reopens the cookie decision, but
   the script now executes on every authenticated route rather than on one anonymous one.
   *Fix:* scope the `<Script>` to the routes that need it, gated on the site key being configured.
2. **Two definitions of a safe redirect path.** `ui/app/auth/callback/page.tsx:42-49` is a local, untested
   duplicate of `ui/lib/challengeLink.ts:24-32`'s `safeNextPath`, missing the control-character strip the
   shared version documents as necessary.
   *Fix:* import the shared one, as `login/page.tsx` already does.
3. **A destructive migration against the stated policy.** `Data/Migrations/20260819235137_DropChallengeRated.cs`
   is a bare `DropColumn`; `docs/ops/MIGRATIONS.md` §4 requires expand and contract across two releases.
   No retroactive schema fix is needed since the column held nothing, but §2's cost table is now six
   migrations stale and does not mention the only destructive one among them.
4. **`/api/auth/register`'s kill-switch 503 is undeclared.** `Auth/AuthEndpoints.cs:37-39` returns it; the
   `.Produces` chain at `:137-141` does not declare it, unlike the same pattern at
   `PublicApiEndpoints.cs:252` and `SeekEndpoints.cs:34`. Client codegen depends on this.
5. **Two options are read eagerly and cannot be overridden in tests.** `Program.cs:219` and `:234` bake
   `BreachedPasswordCheckOptions` and `TurnstileOptions` into singletons before `Build()`, the shape that
   already caught `EmailOptions` and `VapidOptions`. Neither registration is conditional on the value, so
   nothing forces the eager read.
   *Fix:* `services.Configure<T>` plus `IOptions<T>`, as `RetentionOptions` does a few lines away.

Cross-reference: packet **C-9** owns drift and should take items 1, 3 and 4 as input.

---

#### Batch 9 — CI and compose hygiene

1. **Dev and prod run different Postgres majors.** `docker-compose.yml:3` is `postgres:15`;
   `docker-compose.prod.yml:3` is `postgres:16-alpine`, and the prod-shaped file is what E2E and
   self-hosters actually run. A contributor's loop never exercises the shipped version.
2. **Three workflows have no `permissions:` block.** `ci.yml`, `deploy.yml` and `restore-drill.yml`, while
   five others already scope `GITHUB_TOKEN`. These three run the most untrusted code per invocation. Note
   that the section write-up justified this by calling the repo public; it is private (`gh repo view`,
   2026-08-22), so the argument is least privilege and consistency, not exposure.
3. **No `timeout-minutes` or `concurrency` on the jobs that fire most often.** `ci.yml:9` and `:64`, plus
   `codeql.yml` and `semgrep.yml`, while `ci.yml:125`'s e2e job and every Neon-touching workflow set both.
   This is a real cost risk on a repo whose Actions minutes have already run out once, which is currently
   what blocks ROADMAP 10c.1 and 10c.5.

Cross-reference: packet **I-3** owns CI/CD.

---

#### Batch 10 — CancellationToken, as one sweep

Not urgent individually, worth one pass rather than ten. `.claude/rules/backend.md` states the
convention, and the misses cluster in read paths:

- `Moderation/Detection/EngineDetectionEndpoints.cs:29-70`, `:77-107` and
  `Moderation/Detection/SandbaggingEndpoints.cs:60-118`, `:120-198`: no token at all, on the two routes
  whose own doc comments describe them as multi-second queries.
- `Ratings/RatingEndpoints.cs:15-107` (four handlers) and `Analytics/AnalyticsEndpoints.cs:42-63`,
  `:67-117`, while `CounterEndpoints.cs` in the same folder threads it correctly.
- `Moderation/ModerationEndpoints.cs:25`, `:54`, `:85` and `Moderation/ReportEndpoints.cs:20`, `:86`.
- `Account/StripeWebhookEndpoints.cs:53`, `:60`.

---

#### Batch 11 — Test gaps that actually cost something

Ranked, not blanket coverage.

1. `ui/components/Board.tsx`: the primary way a move is made, with untested castling-by-rook-click
   (`:78-92`), promotion defaulting (`:98-103`, `:128-134`) and the legal-move gate (`:105-107`,
   `:136-138`). Its accessible sibling is tested; it is not.
2. `ui/components/AuthProvider.tsx:107-267`: the refresh, adopt and logout state machine, with no test at
   all, in the area `docs/decisions/AUTH.md` scrutinises most.
3. `tests/KiriChess.Server.Tests/ExternalLoginTests.cs`: no ban branch and no 2FA branch, which is where
   batch 1 lands.
4. No `MatchNoShowWatcherTests.cs` at all, which is where batch 3 item 2 lands.
5. `ui/lib/bot.ts`: no tests.
6. Glicko-2's bracket-widening branch (`Glicko2.cs:62-67`), the common established-player case, is
   unexercised while the worked example covers only the other branch.

Cross-reference: packet **C-10** owns the ranked missing-test list and should merge this in.

---

#### Already covered elsewhere, not re-reported

- localStorage token placement, CSP limits and the moderator 2FA gap: `docs/decisions/AUTH.md`. Batch 8
  item 1 is a factual drift against that document, not a re-argument of its decision.
- Export, erasure and retention rights analysis: `docs/audits/L.md`.
- INDEX.md's O-3 row calling the repo public: already caught by `docs/audits/O.md:83` and
  `docs/audits/O.md:14`.
- Redis unprovisioned, tournaments parked, Cloudflare Access on until launch, and the offsite-backup and
  ops-probes workflows never having had a green run: standing positions in ROADMAP.md and DEPLOYMENT.md.
- Fly staging versus production toml drift and the `immediate` deploy strategy: checked by INF-1 and found
  justified and documented, deliberately not filed.

#### Action list

In order: batches 1, 2, 3, 5, 6, 8, 10. Batch 4 needs a migration generated but not
applied. Batches 7 and 11 belong to packets P-5 and C-10 respectively.

Your decisions:

1. Whether batch 3 is done now or booked against Phase 8.4. Doing it now is cheaper than doing it while
   chasing a live multi-instance bug.
2. Whether optional phase-3 guardrail files are wanted. `.claude/rules/` already carries seven
   rule files, and every finding in batches 1, 3 and 8 is a miss against a rule that is already written
   down, which argues for enforcement in tests rather than for an eighth document.
3. Batch 9 item 3 interacts with the Actions-minutes constraint currently blocking 10c.1 and 10c.5.

### Deep audit progress

Packet: **C-1 sectioned code quality** (docs/audits/INDEX.md, table 3).
Deliverables: the section reports, the improvement plan, and this file.

No production code was changed.

#### How each section was read

Each section was read-only: no edits to production code, migrations, or dependencies. A finding
needed a `file:line` citation. Severity was Critical (hurts a stranger or loses data), High, Medium,
or Low. Fixes were kept to one to three sentences, with no exploit code. Standing positions in
`docs/audits/INDEX.md` were out of scope to reverse (free forever, no correspondence, individual
controller, games and ratings kept, Redis unprovisioned, tournaments parked). Findings already in
`docs/audits/L.md` and `docs/audits/O.md` were cross-referenced, not repeated.

#### Section checklist

| Section | Scope | Status |
|---|---|---|
| BE-1 | `src/KiriChess.Server/Auth/` (21 files), JWT, 2FA, external login | done: 1C(→H on verify) / 0H / 2M / 2L |
| BE-2 | `src/KiriChess.Server/Games/` (51 files), hubs, Distributed, engine integration | done: 0C / 1H / 3M / 3L |
| BE-5 | `src/KiriChess.Server/Data/`, `Infrastructure/` (48), `Program.cs` | done: 0C / 0H / 3M / 4L |
| ENG-1 | `src/KiriChess.Engine/` (17 files), cost vectors, config | done: 0C / 0H / 3M / 5L |
| BE-3 | `src/KiriChess.Server/Ratings/`, `Analytics/` (17 files) | done: 0C / 0H / 3M / 4L |
| BE-4 | `src/KiriChess.Server/Moderation/`, `Account/`, `Retention/` (32 files) | done: 0C / 0H / 2M / 6L |
| INF-1 | `docker-compose*.yml`, `fly.*.toml`, `.github/workflows/`, `infra/` | done: 0C / 0H / 3M / 3L |
| FE-1 | `ui/lib/` (27 files, no `ui/hooks/` exists), `ui/scripts/`, config and instrumentation | done: 0C / 0H / 2M / 4L |
| FE-2 | `ui/components/` (57 files), all of it | done: 0C / 0H / 5M / 5L |
| FE-3 | `ui/app/` auth, account and shell routes | done: 0C / 0H / 3M / 3L |
| FE-4 | `ui/app/` game, watch, replay, challenge, custom, leaderboards, moderation, analytics, content | done: 0C / 1H / 1M / 1L |

#### Phase status

- [x] Phase 0 context loaded (rules/project.md, frontend.md, backend.md, ROADMAP.md open half)
- [x] Phase 1 section inspection (11 of 11 sections, all files written)
- [x] Phase 2 synthesis (improvement plan, 11 ordered batches)
- [ ] Phase 3 guardrails (optional; not started, see Open questions)
- [x] Packet report `docs/audits/C.md`

**Run complete.** Totals: 0 Critical, 3 High, 29 Medium, 40 Low.

#### Run log

- 2026-08-21: batch 1 (BE-1, BE-2, BE-5, ENG-1) completed.
- 2026-08-21: batch 2 (BE-3, BE-4, INF-1, FE-1) was interrupted. BE-3's section was already written.
  BE-4, INF-1 and FE-1 were not.
- 2026-08-22: BE-4, INF-1 and FE-1 were finished from the reading already done.
- 2026-08-22: batch 3 (FE-2, FE-3, FE-4) completed. Phase 1 done.
- 2026-08-22: the improvement plan and this report were written. No production code changed.

#### Verifications

- BE-1's Critical (external OAuth exchange skips the sanction check) is confirmed as a real gap at
  the token-minting boundary, but re-graded **High**: every play path runs through
  `PlayEligibilityPolicy`, which does check the account ban, so a banned user gets a session and
  still cannot play. `/login/2fa` and refresh both check the ban as well. See `IMPROVEMENT_PLAN.md`.
- BE-4's account-deletion finding is confirmed and stays **Medium**, with the reasoning written out
  rather than left implicit. A ban revokes refresh and API tokens (`SanctionService.cs:78-79`) but not
  the 15-minute access token (`JwtOptions.cs:10`), `/api/account/delete` does not check sanctions, and
  deletion nulls `Email`/`NormalizedEmail` (`AccountDeletionService.cs:83-84`), so inside that window a
  banned user can free their address and re-register clean. It does not defeat the
  sanctions-survive-deletion position as badly as it first looks: the `UserSanction` row itself
  survives on Restrict FKs, and signup is email-only with no identity check, so evasion by registering
  a fresh address is already open by design at this size. The value of the fix is that the mod keeps
  the email as a correlation signal, not that it closes evasion.
- **Systemic theme, not a single bug.** BE-1 and BE-4 independently found missing sanction checks in
  different endpoints. Sanction enforcement is applied per-endpoint by hand, so a new authenticated
  route is unprotected by default. That pattern is the finding worth carrying into the plan.
- INF-1 describes the repo as "now public". It is not: `gh repo view` returns `isPrivate: true` on
  2026-08-22, and ROADMAP #12 keeps it private past launch day. The `permissions:` and
  `timeout-minutes` findings still stand on least-privilege and cost grounds, but their stated trigger
  is wrong, and the Actions-minutes exhaustion they cite is a consequence of the repo being private,
  not public. `docs/audits/O.md:83` and `O-4.md:14` already caught the same wrong premise in
  INDEX.md's own O-3 row, so this is cross-referenced, not re-reported.
- FE-4's reconnect finding is confirmed and stays **High**, and it is the most user-visible defect in
  the run. `game/[id]/page.tsx:392-401` resets moves, captured material and clock history on every
  `join()`, and `join()` is what `onreconnected` calls, so a transport blip or a mid-game reload wipes
  the move list and the clock-economy graph for a player. `watch/[id]/page.tsx:158-175` rebuilds all of
  it from `snap.moves`. The cause is a DTO asymmetry, not a missed client pattern:
  `Games/RealtimeDtos.cs:7` defines `GameSnapshotDto` as state plus legal moves only, while
  `RealtimeDtos.cs:80-85` gives `SpectatorSnapshotDto` a full `Moves` list. So the fix is server-side
  first (add the move log to `GameSnapshotDto`, which per `.claude/rules/realtime.md` means the
  `HubContract.cs` declaration and an OpenAPI regeneration), or an HTTP refetch on rejoin. A player
  currently sees less of their own game after a blip than a spectator does.

#### Open questions

- Phase 3 guardrail files are optional and not started. The owner decides before any
  `.claude/rules/*-guardrails.md` is written. `.claude/rules/` already carries seven rule files.
## C-2 — General code review
Date: 2026-08-21
Packet: docs/audits/INDEX.md

### Verdict

This codebase does not have a cleanliness problem. Comment density is unusually high and unusually
honest, the doc comments explain *why* rather than restating the code, dead code is rare, `async
void` and blocking `.Result` appear nowhere, and analytics and rating code that could easily have
been N+1 is deliberately written as flat queries plus an in-memory join, with the reasoning written
down. Reviewing against the rubric's "cleanness" axis mostly produced nothing worth reporting.

The problems are all in the same place, and they share one shape: **an invariant that is stated in a
doc comment and not enforced anywhere.** `GameActionExecutor` opens by declaring itself "the single
code path every player action goes through, the reason two identical bugs can never exist for one
rule", and two HTTP endpoints apply moves without it. `SpectatorSnapshotDto` explains at length why a
spectator must never receive the engine's legal-move list, and the very next broadcast sends it to
them. `GameSession` is documented as the authoritative live clock and has no synchronisation at all
while five different threads reach into it. Eight of ten background services wrap their loop body in
a catch; the two that do not are the flag-fall safety net and the presence broadcaster, and under the
default host behaviour either one takes the whole process down on a transient Neon or Redis fault.

The reason these survived is structural and worth naming on its own: **the test suite drives games
through the HTTP move endpoint and the hub tests drive them through the hub, and nothing crosses the
two.** Seven test files POST to `/api/games/{id}/moves` as a convenient way to advance a game. That
is precisely the path that does not broadcast and does not route, so every test that uses it is also
a test that cannot notice.

### Findings

#### C-2-1 — Two background services can stop the whole host on a transient dependency fault
- Severity: High
- Where: `src/KiriChess.Server/Games/FlagFallMonitor.cs:26-48`,
  `src/KiriChess.Server/Games/PresenceBroadcaster.cs:60-70`,
  `src/KiriChess.Server/Games/GameRegistry.cs:126`
- Impact: Neither `ExecuteAsync` contains a single `try`. `FlagFallMonitor`'s loop body calls
  `PersistIfOverAsync`, which does an unguarded `await db.SaveChangesAsync()`;
  `PresenceBroadcaster`'s calls `LiveGameIndex.PlayersInGameAsync` and
  `PresenceTracker.OnlineUsersAsync`, both Redis round trips.
  `HostOptions.BackgroundServiceExceptionBehavior` is not configured anywhere in `Program.cs`, so it
  is .NET's default of `StopHost`: one Neon connection blip during a flag-fall persist, or one Redis
  timeout during a presence tick, stops the application. Every live game on that instance dies with
  it. This is not hypothetical for this stack, Neon suspend and Redis being optional-at-runtime are
  both documented realities here. Note also that `PersistIfOverAsync` does `_sessions.TryRemove`
  *before* the save (`GameRegistry.cs:100`), so a failed persist has already evicted the in-memory
  game with no retry path. The other eight background services in the repository
  (`EmailDispatchService`, `RetentionSweeper`, `OrphanedGameSweeper`, `LatencyProbe`,
  `InstanceHeartbeat`, `LeaderboardWarmup`, `GameCommandRouter`, `LiveGamePersister`) all guard
  their loop body already, so this is two outliers against an established house pattern, not a
  missing convention.
- Fix: Wrap each loop body in `try { … } catch (Exception ex) when (ex is not
  OperationCanceledException) { _logger.LogError(…); }` so one bad tick is skipped rather than
  fatal, matching `RetentionSweeper`. A sweep that logs and retries next second is strictly better
  than a sweep that ends the process.

#### C-2-2 — Three parallel implementations of "apply a move"; two of them silently skip the broadcast and the router
- Severity: High
- Where: `src/KiriChess.Server/Games/GameActionExecutor.cs:36-51` (the stated invariant),
  `src/KiriChess.Server/Games/GameActionExecutor.cs:279-306` (the correct path),
  `src/KiriChess.Server/Games/GameEndpoints.cs:106-165`,
  `src/KiriChess.Server/Games/PublicApiEndpoints.cs:297-355`
- Impact: `PublicApiEndpoints.cs:297-335` is a near-verbatim copy of `GameEndpoints.cs:106-146`, and
  both differ from `SubmitMoveLocalAsync` in two ways that matter:
  1. **No hub broadcast.** `SubmitMoveLocalAsync` ends with
     `_hub.Clients.Group(gameId).SendAsync("MovePlayed", …)`. Neither HTTP handler sends anything. A
     bot playing a human over `/api/v1/games/{id}/moves` (the documented play surface, `/developers`
     advertises exactly this) makes a move the human's SignalR connection is never told about. The
     human's board sits frozen until their clock runs out. Same for `/resign`, which never sends
     `GameEnded`.
  2. **No ownership routing.** Both call `registry.Get(id)` and return `404` when it misses.
     `.claude/rules/realtime.md` is explicit that "any new game action needs to go through the
     router, not assume it's running on the owning instance". Once a second Fly machine exists, a
     player whose game is owned elsewhere gets "game not found" for a game that is live.

  This is also the single largest DRY violation in the server: parse, turn check, apply, persist,
  enqueue snapshot and metrics are written out three times, and `GameHistoryQuery` right next door
  exists specifically so `/api/games/mine` and `/api/v1/games/mine` "cannot drift". The move path
  got no such treatment.
- Fix: Make both HTTP handlers call `IGameActionExecutor.SubmitMoveAsync` / `ResignAsync` with
  `connectionId: string.Empty` (the `AbandonAsync` path already establishes that convention at
  `GameActionExecutor.cs:191-193`), and delete the duplicated bodies. If HTTP play is meant to be a
  second-class surface that genuinely does not broadcast, that has to be stated in `HubContract.cs`
  or `/developers` and pinned by a test, because right now nothing says so.

#### C-2-3 — `GameSession` is unsynchronised shared mutable state on five concurrent paths
- Severity: High
- Where: `src/KiriChess.Server/Games/GameSession.cs:23` (`List<MoveRecord> _moves`),
  `src/KiriChess.Server/Games/GameSession.cs:65` (handed out live as `IReadOnlyList`),
  `src/KiriChess.Server/Games/GameSession.cs:207-249`,
  `src/KiriChess.Server/Games/GameSession.cs:325-335`,
  `src/KiriChess.Server/Games/GameSession.cs:147-162`,
  `src/KiriChess.Server/Games/LiveGamePersister.cs:119-131`,
  `src/KiriChess.Server/Games/GameRegistry.cs:75-79`
- Impact: There is no lock, no `SemaphoreSlim` and no immutable collection anywhere in
  `src/KiriChess.Server/Games/` outside the `ConcurrentDictionary`s. Meanwhile `ApplyMove` mutates
  `Position`, `Status`, `Winner`, `LastMoveAtUtc`, `_lastMoveTimestamp` and appends to `_moves`;
  `CheckFlagFall` mutates `Winner`/`Status`/`Termination`/`EndedAt` and is called from
  `GameRegistry.Get` on *every read* plus the 1-second sweep; `ReportLatency` mutates the RTT
  estimates from `LatencyProbe`; and `ToState`/`ToSnapshot`/`ToEntity`/`Moves` enumerate `_moves`
  from spectator joins, the flag-fall sweep and the write-behind persister. The concrete reachable
  failure is `LiveGamePersister.WriteBatchAsync:128`, `session.Moves.ToList()`, which enumerates the
  live `List<T>` while a hub thread may be inside `_moves.Add`. That line sits **outside** the
  method's own `try` (which starts at :134), so the resulting `InvalidOperationException: Collection
  was modified` escapes `ExecuteAsync`, is not the `OperationCanceledException` the `catch` at :92
  handles, and lands in C-2-1's host-stop. SignalR's default
  `MaximumParallelInvocationsPerClient = 1` serialises one client's own invocations, which is why
  this has not bitten yet; it does nothing for a second tab, the HTTP path of C-2-2, the sweep, the
  probe, or the persister. There are no concurrency tests over `GameSession` in the suite.
- Fix: Give `GameSession` one private `object _gate` and take it in `ApplyMove`, `CheckFlagFall`,
  `Resign`/`OfferDraw`/`RespondDraw`/`Abandon`/`Abort`, `ReportLatency`, and every method that reads
  `_moves` (returning `_moves.ToArray()` under the lock rather than the live list from the `Moves`
  property). All of it is microseconds of in-memory work with no I/O inside, so a plain monitor is
  the right tool. Then add a test that hammers one session from several tasks, which is the thing
  that would have caught this.

#### C-2-4 — A rate-limited or 5xx refresh is treated as a dead session and logs the player out
- Severity: Medium
- Where: `ui/lib/api.ts:117-120`, `ui/components/AuthProvider.tsx:153-156`,
  `src/KiriChess.Server/Infrastructure/RateLimiting.cs:102`,
  `src/KiriChess.Server/Infrastructure/RateLimiting.cs:205-214`
- Impact: `ensureFreshToken` treats any `!res.ok` from `/api/auth/refresh` as refresh-token death and
  calls `onRefreshFailed()`, which is `clearSession(); router.push("/login")`. But
  `/api/auth/refresh` sits in the `RateLimitPolicies.Auth` group, which is 30 requests per minute
  **partitioned by client IP** and shared with register, login, forgot-password and
  resend-confirmation. Behind CGNAT, a school, a café or any shared household NAT, an unrelated
  visitor's login attempts can consume that budget; a 429 then boots a signed-in player to the login
  page. A 502 from the edge or a 500 from a restarting API does the same. Mid-game this is not a
  cosmetic annoyance, the clock keeps running while the player is looking at a login form. Note the
  network-error path immediately below (`catch { return false; }`) already gets this right by not
  calling `onRefreshFailed`.
- Fix: Only treat `401` and `403` as refresh-token death. For any other non-OK status return `false`
  without calling `onRefreshFailed`, exactly as the `catch` branch does, so the caller surfaces a
  retryable error and the session survives.

#### C-2-5 — A second disconnect orphans the abandon timer, and the opponent's "disconnected" banner never clears
- Severity: Medium
- Where: `src/KiriChess.Server/Games/GameConnectionTracker.cs:229-247`,
  `src/KiriChess.Server/Games/GameConnectionTracker.cs:115-119`,
  `src/KiriChess.Server/Games/GameConnectionTracker.cs:188-197`
- Impact: `_pending` is keyed by `(gameId, userId)`, not by connection, and `RunGraceAsync` arms it
  with a bare indexer assignment: `_pending[(gameId, userId)] = cts` (:238). A player with two tabs
  open, or one tab on a flaky connection that drops twice inside the 30-second grace window, gets
  two `RunGraceAsync` calls; the second silently replaces the first CTS without cancelling or
  disposing it. The orphaned first timer keeps running, and when its delay elapses it executes
  `_pending.TryRemove((gameId, userId), out _)` (:247), removing the **newer** timer's entry. When
  the player then reconnects, `HandleReconnectAsync` calls `CancelPendingAbandon`, finds nothing,
  and returns at :117 without broadcasting `OpponentReconnected`. The opponent's client set
  `presenceNote` on `OpponentDisconnected` and only clears it on `OpponentReconnected`
  (`ui/app/game/[id]/page.tsx:370-378`), so they spend the rest of the game being told their
  opponent is disconnected and has 30 seconds to return. The forfeit itself is saved by the
  `IsConnectedAsync` re-check at :251, so this is a stuck UI rather than a wrongly lost game.
  Separately, the CTS at :237 is never disposed on the timeout path.
- Fix: Arm with an atomic `AddOrUpdate` that cancels and disposes any CTS it displaces, and have
  `RunGraceAsync` remove only its own entry (the `TryRemove(KeyValuePair<,>)` overload, or a
  compare-and-remove on the CTS reference) so an expiring old timer cannot evict a live one. Dispose
  the CTS in a `finally`.

#### C-2-6 — `JoinGame` joins the SignalR group before it authorises the caller
- Severity: Medium
- Where: `src/KiriChess.Server/Games/GameHub.cs:82-87`,
  `src/KiriChess.Server/Games/GameActionExecutor.cs:272-277`
- Impact: `await Groups.AddToGroupAsync(Context.ConnectionId, gameId)` runs first, and only then does
  `JoinGameAsync` reach `JoinGameLocalAsync`, which throws `HubException("Not a player in this
  game.")` for a non-participant. Nothing removes the connection from the group on that path. Any
  authenticated caller who invokes `JoinGame` with an arbitrary game id therefore ends up subscribed
  to that game's `MovePlayed`, `GameEnded`, `DrawOffered`, `OpponentDisconnected` and
  `RematchAccepted` pushes for the life of the connection. The information is largely public (anyone
  can spectate any live game), so this is not primarily a confidentiality finding; what it actually
  defeats is the two controls that sit on the legitimate door: `JoinAsSpectator` checks the
  `Spectating` kill switch (`GameHub.cs:265-266`) and registers with `SpectatorTracker`, and this
  path does neither. It is an uncounted spectator that the kill switch cannot turn off, which is
  exactly the situation 10c.4 exists to prevent. The method's own comment justifies the ordering by
  saying only "the game logic" needs routing, which is true and does not address the group.
- Fix: Move `Groups.AddToGroupAsync` to after `JoinGameAsync` returns, alongside the
  `_connections.Track` call that is already deliberately ordered that way for the same reason
  (:84-86). The comment about group membership belonging to the connection and spanning instances
  stays true either way.

#### C-2-7 — The game-history query drags two jsonb columns per row to build a DTO of ten scalars
- Severity: Medium
- Where: `src/KiriChess.Server/Games/GameHistoryQuery.cs:20-26`,
  `src/KiriChess.Server/Data/KiriChessDbContext.cs:88-92`,
  `src/KiriChess.Server/Data/Entities/Game.cs:36-41`
- Impact: `ListMineAsync` materialises whole `Game` entities (`ToListAsync` on `db.Games` with no
  projection) and only then projects in memory at :47-67. `Game` carries `Moves` as a `jsonb` column
  holding the complete move log, including per-ply `ThinkMs`, `LagMs` and `ReceivedAtUtc`, plus
  `ConfigSnapshot` as a second jsonb blob. The resulting DTO uses neither: it needs `Id`, the two
  player ids, `Result`, `Termination`, `RatingPool`, `IsRated`, `PlyCount`, `CreatedAt` and
  `EndedAt`. `take` is clamped to 100, so one page of `/api/games/mine` can pull a hundred complete
  move logs across the Neon link to render a list that shows none of them. The irony is that
  `PlyCount` exists, with a doc comment saying so, precisely so consumers do not have to deserialise
  the move list, and this is the consumer. The `ORDER BY COALESCE(EndedAt, CreatedAt)` compounds it:
  no index supports that expression, so Postgres sorts the matched rows (with their jsonb payloads)
  before applying the limit. This is the profile page and the public/bot API's history route, so it
  is on a first-load path.
- Fix: Add a `.Select(g => new { … })` projection before `ToListAsync` naming only the ten columns
  the DTO uses. That removes the jsonb transfer entirely and lets the sort work on narrow tuples. If
  the ordering stays hot, a persisted "finished at" column indexed alongside each player id is the
  follow-up, but the projection alone is the large win and is a one-line change.

#### C-2-8 — The double-check multiplier is untested and its predicate is the most expensive thing in the move path
- Severity: Medium
- Where: `src/KiriChess.Engine/KiriChessEngine.cs:241-250`,
  `src/KiriChess.Engine/KiriChessEngine.cs:253-276`,
  `tests/KiriChess.Engine.Tests/Rule4CheckChipTests.cs`,
  `src/KiriChess.Engine/Tooling/CostVectorGenerator.cs:212-220`
- Impact: Two problems, one cause. **Coverage**: `doubleCheckChipMultiplier` ships as `2` in
  `config/kirichess.rules.json:48`, so it is a live economy lever on the default rules, and nothing
  exercises it. `Rule4CheckChipTests.cs` covers single check on and off in two tests and stops
  there; the only other repository references to the setting are config round-tripping
  (`ConfigLoadingTests`, `RulesDocumentTests`, `PgnExportTests`). The cost-vector fixture cannot help
  either: `CostVectorGenerator` builds `moverNet` from
  `CaptureGain`/`MovementToll`/`CastleCost`/`PromotionCost` only (:212-215) and `opponentLoss` from
  `CaptureLoss` only (:217-218), so the check chip is outside every one of the 154 vectors by
  construction. That exclusion is deliberate and documented on the client side
  (`ui/lib/costPreview.ts:100-104`), which is fine as a scope decision, but it means
  `.claude/rules/engine.md`'s "a rule variable needs a fixture entry" is not satisfiable for this one
  and the C# gap is all there is. In particular, whether a checker that is pinned against its own
  king still counts toward the attacker total depends entirely on whether
  `ChessBoard.GeneratePositions` returns legal or pseudo-legal moves, and no test pins the answer.
  **Cost**: `IsDoubleCheck` walks all 64 squares and calls `board.GeneratePositions` for every
  friendly piece, up to sixteen full per-piece generations, on every checking move, in the hottest
  code path in the system.
- Fix: Add engine tests for a genuine double check (a discovered check plus a direct check), for a
  single check that must not be doubled, and for the pinned-checker case, which is what decides the
  `GeneratePositions` question one way or the other. Then short-circuit: return early when
  `config.DoubleCheckChipMultiplier == 1` or when both `CheckCheckerGain` and `CheckVictimLoss` are
  zero, since the scan cannot change any amount in those cases, and consider counting attackers of
  the king square directly instead of generating every friendly piece's moves.

#### C-2-9 — Spectators receive the legal-move list the spectator DTO deliberately withholds
- Severity: Low
- Where: `src/KiriChess.Server/Games/RealtimeDtos.cs:76-78` (the stated intent),
  `src/KiriChess.Server/Games/RealtimeDtos.cs:10-18` (`MovePlayedDto.LegalMoves`),
  `src/KiriChess.Server/Games/GameActionExecutor.cs:294-300`,
  `src/KiriChess.Server/Games/GameHub.cs:276`
- Impact: `SpectatorSnapshotDto` carries a three-line comment explaining that it omits legal moves
  because "shipping the engine's move-generator output to an unauthenticated caller is free surface
  with no consumer". `JoinAsSpectator` then adds the spectator's connection to the game group
  (`GameHub.cs:276`, the same group the players are in), and the next `MovePlayed` broadcast goes to
  `Clients.Group(gameId)` carrying `LegalMoves` in full. So the join withholds it and every
  subsequent ply hands it over. The same broadcast also carries `RatingChange`, both players'
  before/after Glicko values, on the game-ending ply; those are public on `/leaderboards`, so that
  half is harmless. This is a stated design decision that the code does not implement, which is
  worth fixing or worth deleting the comment, but not worth leaving as is.
- Fix: Either send the spectator group a reduced payload (a second `SendAsync` to a
  `{gameId}:spectators` group, with the players in their own group), or drop the claim from
  `SpectatorSnapshotDto`'s comment and accept that legal moves are public for a live game. The first
  is more work; the second is honest and costs nothing. Pick one, because the current state reads as
  an enforced control and is not.

#### C-2-10 — Persisted and exported clock deltas are unclamped while the clocks beside them are clamped
- Severity: Low
- Where: `src/KiriChess.Engine/KiriChessEngine.cs:175-187`,
  `src/KiriChess.Server/Games/GameSession.cs:234-245`,
  `src/KiriChess.Server/Games/PgnBuilder.cs:216`, `docs/game/PGN.md:136-139`
- Impact: `Apply` computes `rawWhite`/`rawBlack`, stores `Math.Max(0, …)` into the new position
  (:185-186), and returns `whiteDelta`/`blackDelta` unclamped. `GameSession.ApplyMove` persists both
  onto the same `MoveRecord`: `WhiteTimeAfter` clamped, `WhiteDelta` not. When a capture or check
  chip drives a side below zero, the record says something like "black -18" beside a clock that went
  from 5 to 0. `PgnBuilder` emits both into `[%kc w,b,dw,db]`, and `docs/game/PGN.md:138` describes
  `dw` as "the economy delta applied to White by this ply", which in that case it is not. A
  KiriChess-aware tool reconstructing clocks from the deltas gets a different answer than the clocks
  in the same comment. Replay (`GameReplayBuilder.cs:66-67`) and the client clock graph inherit the
  same discrepancy.
- Fix: Cheapest correct option is documentation: change `PGN.md`'s wording to say `dw`/`db` are the
  *nominal* economy amount, which may exceed what a clock at zero could absorb, and say the clocks
  are authoritative. If reconciliation is wanted instead, clamp the delta to the actual applied
  change in `Apply` and regenerate the cost vectors. Do not do both. This overlaps **C-4** (clock
  economy) and **C-8** (PGN fidelity); flagged here because it is a data-shape inconsistency visible
  from the code alone.

#### C-2-11 — The game page re-derives "which side am I" six times, and one copy is wrong
- Severity: Low
- Where: `ui/app/game/[id]/page.tsx:307-311`, `ui/app/game/[id]/page.tsx:336-339`,
  `ui/app/game/[id]/page.tsx:350-351`, `ui/app/game/[id]/page.tsx:479-483`,
  `ui/app/game/[id]/page.tsx:657-661`, `ui/app/game/[id]/page.tsx:799`,
  `ui/app/game/[id]/page.tsx:134`, `ui/app/game/[id]/page.tsx:655`
- Impact: The expression `isBotMode ? playerSide : state.whitePlayerId === userId ? "White" :
  "Black"` is written out five times, and a sixth copy at :350-351 (`actualPlayerSide`) **omits the
  bot-mode branch** the other five have; it happens to be compensated by an `|| (isBotMode &&
  p.state.winner === playerSide)` two lines down, so the win/loss sound is right today by
  coincidence rather than by construction. Separately, `drawOfferFrom` is typed `Side | null`, is
  filled from `DrawOfferedDto.BySide`, and is then only ever compared against `null` (:655). The
  comment there argues the value is unnecessary because "own offers echo via server only to the
  opponent", which is true per connection and not per player: the server excludes the offering
  *connection* (`GameActionExecutor.cs:322`), so an offerer with a second tab open on the same game
  sees an actionable Accept/Decline for their own offer, and clicking it produces "Cannot answer your
  own draw offer". The payload already carries the field that would make the check correct.
- Fix: Hoist one `const mySide = useMemo(…)` (or a small `sideOf(state, userId, isBotMode,
  playerSide)` helper in `ui/lib/`) and use it in all six places. Then make `incomingDraw` compare
  `drawOfferFrom !== mySide` instead of `!== null`, which uses the field the DTO is already sending.

#### C-2-12 — Dead client wrappers for the HTTP move path
- Severity: Low
- Where: `ui/lib/api.ts:417-425`
- Impact: `getLegalMoves` and `submitMove` are exported and have zero callers anywhere in `ui/` (the
  game page moves over the hub, `ui/app/game/[id]/page.tsx:502`). They are the only two exports in
  that 1,095-line module with no consumer. Minor on its own, but they are the client half of C-2-2:
  they make the HTTP move endpoint look like a supported first-party path when nothing in the product
  uses it, which is part of why its missing broadcast has gone unnoticed.
- Fix: Delete both, or add a comment saying they exist for the documented bot surface. Deleting is
  better; `/api/v1` is the published bot API and `ui/lib/api.ts` is not how a bot calls it.

#### C-2-13 — `Console.WriteLine` on the per-game path
- Severity: Low
- Where: `src/KiriChess.Server/Games/GameSession.cs:93-94`
- Impact: The one unstructured stdout write in the entire server, in the `GameSession` constructor, so
  it fires once per game created. It has no log level, no category, no structured fields, and no
  correlation with the request, so it cannot be filtered, sampled or searched alongside everything
  else in Better Stack. Every other diagnostic in this codebase goes through `ILogger` with
  structured placeholders. Under load it is also a synchronous console write per game start.
- Fix: `GameSession` has no logger and should not grow one (it is a pure domain object constructed
  directly, not through DI). Move the line to `GameRegistry.CreateAsync`, which already has the
  session and can take an `ILogger<GameRegistry>`, as `_logger.LogDebug("Game {GameId} started …",
  …)`, or delete it, since `KiriChessMetrics.GameStarted` on the next line already records the event
  with its pool and rated flag.

#### C-2-14 — Engine test files invert the rule numbers the published rules use
- Severity: Low
- Where: `tests/KiriChess.Engine.Tests/Rule3RepetitionGuardTests.cs`,
  `tests/KiriChess.Engine.Tests/Rule4CheckChipTests.cs`, `docs/RULES.md:55`, `docs/RULES.md:71`
- Impact: `docs/RULES.md` (generated from `config/kirichess.rules.json`, so this is the published
  numbering) has Rule 3 as the check chip and Rule 4 as the repetition guard.
  `.claude/rules/engine.md` lists them in the same order. The test files are named the other way
  round. Anyone acting on "Rule 3 changed, check its tests" opens the repetition tests. Trivial, but
  this repository gates on generated-file drift in five separate test classes, so a hand-written name
  contradicting the generated document is worth the two-minute fix.
- Fix: Rename the two files and their classes to match `docs/RULES.md`.

### Already covered

- `docs/audits/deep/`, `docs/audits/readiness/` and `docs/audits/arch-speed.md` do not exist yet, so
  nothing here could be a re-report. **C-1** (sectioned deep audit) and **I-1** (arch/speed) have not
  been run; per the INDEX's own warning, if **C-1** runs later, C-2-2, C-2-3 and C-2-7 are the
  findings most likely to be rediscovered by it and the reports should be merged rather than stacked.
- C-2-10 sits on **C-4**'s (clock economy) and **C-8**'s (PGN fidelity) ground and is reported here
  only as a data-shape inconsistency; the rule question belongs to them.
- C-2-8's engine-rules half belongs to **C-3**, and the "which tests are missing, ranked" question is
  **C-10**'s packet. Reported here because the predicate and its cost are one finding.
- C-2-6 and C-2-9 touch authorisation but are not security findings; **S-1** owns the
  `HubAuthOverlayMiddleware` / `ApiToken*` / public-`/api/v1` review, and neither of these re-derives
  anything from `docs/decisions/AUTH.md`.
- C-2-2's multi-instance half is **C-5**'s territory (`Games/Distributed/`); it is reported here
  because the duplication and the routing gap are the same defect.
- The `localStorage` token decision behind C-2-4 is settled in `docs/decisions/AUTH.md` §1.7 and is
  not reopened; C-2-4 is about status-code handling in the refresh path only.

### Action list

Ordered by what a stranger's live game loses if it is not done.

1. Lock `GameSession` and move `session.Moves.ToList()` inside `WriteBatchAsync`'s try
   (C-2-3), then add the multi-task test. First because the persister race is a live trigger for
   item 2.
2. Guard the two background-service loops (C-2-1). Two `try`/`catch` blocks against an
   existing house pattern, and it is the difference between a Neon blip and a process restart that
   drops every live game.
3. Route the two HTTP move/resign handlers through `IGameActionExecutor` and delete the
   duplicated bodies (C-2-2). Add one test that makes a move over HTTP and asserts a hub-connected
   opponent receives `MovePlayed`, which is the test the suite structurally cannot currently have.
4. Fix the refresh-failure status handling (C-2-4). Three lines, and it removes a mid-game
   logout on shared IPs.
5. Make the abandon-timer arm atomic (C-2-5) and move the group join after authorisation
   (C-2-6).
6. Project the history query (C-2-7). One `.Select`.
7. Add double-check tests, then the short-circuit (C-2-8).
8. **Owner decision, then.** C-2-9: enforce the spectator legal-move omission or delete the
   claim. This is a product call about what a spectator is entitled to see, not a code call.
9. **Owner decision, then.** C-2-10: document the delta semantics or clamp them. Documenting is
   the smaller change and does not touch the engine.
10. The tidying: C-2-11, C-2-12, C-2-13, C-2-14. None of them affect a game in progress.

No standing position is touched by any of the above.

---

## C-3 — Engine and chess rules
Date: 2026-08-22
Packet: docs/audits/INDEX.md

### Verdict

Move generation and the four rules are in good shape, and the config-is-source-of-truth machinery
genuinely works: perft is walked through the engine's own public API and cross-validated against
`python-chess`, double-check detection survives the cases most implementations get wrong, every
number on `/rules` and in `docs/RULES.md` recomputes from `config/kirichess.rules.json`, and
`gen:rules:check` is green. The defects cluster in one place, and it is not move legality. It is
**what happens when a game ends two ways on the same move**. The engine gives checkmate priority
over flag-fall but gives flag-fall priority over stalemate, threefold repetition and the fifty-move
rule. That is the opposite of what the manual and `/rules` promise players, and the opposite of what
this repo's own generative invariant asserts. A second, milder cluster: the repetition key is
derived from FEN text, so one FIDE position splits into two whenever an en-passant flag is set,
again contradicting a claim the rules page makes in so many words.

Nothing here is a move-legality bug. Everything here is the engine and the rulebook disagreeing.

### Findings

#### C-3-1 — Flag-fall outranks stalemate, threefold and the fifty-move rule, contradicting the published rules
- Severity: **High**
- Where: `src/KiriChess.Engine/KiriChessEngine.cs:311-337` (`ResolveStatus` ordering); claims at
  `docs/RULES.md:92-95` and `ui/messages/en.json:401,408,410`; the repo's own contrary assertion at
  `tests/KiriChess.Engine.Tests/Generative/EconomyInvariantTests.cs:247-251`
- Impact: `ResolveStatus` checks checkmate (line 312), then flag-fall (line 316), and only then
  stalemate (line 328), insufficient material, fifty-move (line 330) and threefold (line 334).
  Checkmate therefore beats the flag, exactly as `docs/RULES.md:92` promises, but stalemate,
  threefold and fifty-move do not. If the mover's own clock reaches zero on the very move that
  produces one of those draws, the mover **loses on time** instead of drawing.

  Measured against the shipped engine: from `4k3/7p/8/8/8/8/7P/4K1N1 w - - 0 1` the knight-shuffle
  sequence `Nf3 Kd8 Ng1 Ke8 Nf3 Kd8 Ng1 Ke8` reaches the third occurrence on the last ply; with the
  mover thinking past their flag on that ply the engine returns `FlagFall`, winner White, not
  `DrawByRepetition`. Stalemate behaves the same way whenever the stalemated side still holds mating
  material. When it does not, `FlagVsInsufficientMaterialIsDraw` masks the bug by returning the right
  outcome under the wrong name, `DrawByInsufficientMaterial`.

  This is not a theoretical ordering nit. `EconomyInvariantTests.cs:249` already declares the correct
  rule in as many words ("key reached occurrence count 3 under ThreefoldRepetitionIsDraw, but Status
  is {…}, expected DrawByRepetition"), so the property is written down and the engine violates it.
  The suite does not catch it because the counterexample needs a threefold and a flag on the same
  ply. A 25x soak run (`KIRICHESS_ENGINE_SOAK=1`, all six pools) passed without sampling it. More
  random self-play will not find this; only a targeted test will.

  It matters most in the pools where it is most likely. Zeitnot starts at 60s, low-clock repetition
  endings are the normal shape of that pool, and every affected pool is rated.
- Fix: In `ResolveStatus`, move the `board.IsEndGame` switch (stalemate, insufficient material,
  fifty-move) and the threefold check **above** the flag-fall branch, so every immediate
  game-ending condition is treated the way checkmate already is. Then add the three precedence cases
  to `GameEndTests` as fixed positions, because the generative suite structurally cannot be relied on
  to find them. If the current ordering is instead a deliberate "time is the win condition" position,
  then `docs/RULES.md:92-95`, `ui/messages/en.json:408,410` and the invariant at
  `EconomyInvariantTests.cs:249` all have to say so; today all three say the opposite.

#### C-3-2 — The repetition key splits one FIDE position into two on the en-passant field
- Severity: **Low** (trigger: any position reached both immediately after a double pawn push and
  again later by a reversible manoeuvre)
- Where: `src/KiriChess.Engine/KiriChessEngine.cs:371-376` (`PositionKey`); claims at
  `docs/RULES.md:77` and `ui/messages/en.json:387`
- Impact: `PositionKey` takes FEN fields 1-4, and Gera.Chess writes the en-passant target square
  after *every* double pawn push, regardless of whether any enemy pawn could actually capture there.
  FIDE defines repetition by whether the en-passant capture is genuinely *available*. Measured: after
  `1.e4 Nf6 2.Nf3 Ng8 3.Ng1` the engine holds two keys,
  `rnbqkbnr/pppppppp/8/8/4P3/8/PPPP1PPP/RNBQKBNR b KQkq e3` and the same placement ending `-`, each
  counted once, for what the official definition calls one position occurring twice.

  Two consequences, both in the unsafe direction. Rule 4 fails to nullify the first recurrence, so
  the economy pays again where the guard says it should not; and the threefold counter is split, so
  `docs/RULES.md:79` and `ui/messages/en.json:410`'s "drawn the instant a position occurs for the
  third time" can arrive a cycle late. The blast radius is bounded, which is why this is Low rather
  than higher: a double push is irreversible, so only positions adjacent to one are affected, and the
  following cycle nullifies correctly (verified).
- Fix: Normalise the en-passant field out of the key unless a legal en-passant capture actually
  exists in that position (probe the generated move list for a pawn capture onto the ep square), and
  keep it when one does. Otherwise state the FEN-literal definition in `docs/RULES.md:77` and
  `ui/messages/en.json:387` instead of claiming the official one.

#### C-3-3 — `Apply` throws the chess library's own exception type for a move `IsLegal` accepted
- Severity: **Medium** (latent, contained today by two catch clauses and by `StartFen` never varying)
- Where: `src/KiriChess.Engine/KiriChessEngine.cs:136` (`board.Move(chessMove)`); contract claim at
  `src/KiriChess.Engine/KiriChessEngine.cs:76-81`
- Impact: `FromFen` happily loads a position the underlying library already considers finished (K+N
  vs K, K+B vs K, K vs K, or a halfmove clock at 100), because `AutoRules` auto-detects insufficient
  material and the fifty-move rule at load time. In such a position `LegalMoves` returns a full move
  list and `IsLegal` returns `true`, but `Apply` reaches `board.Move` and the library throws
  `Chess.ChessGameEndedException`. Measured on all four position shapes above.

  That is exactly the leak `IsLegal`'s remarks say were closed: lines 76-81 explain that the library
  "throws rather than returning false" and that answering from the generated set "cannot throw". True
  of `IsLegal`, but `Apply` still can, and it throws a type callers must reference the chess package
  to name. It is not reachable in production today: `Game.StartFen` is only ever written as
  `KiriChessEngine.StartFen` (`GameSession.cs:375`), and both replay paths catch `Chess.ChessException`,
  which `ChessGameEndedException` derives from. It becomes reachable the moment anything admits a
  caller-supplied FEN (puzzles, analysis, a custom start position), which is precisely what `FromFen`'s
  own summary advertises it for: "useful for tests and puzzles".
- Fix: Have `Apply` detect the already-finished board before `board.Move` and surface it through the
  engine's own vocabulary (an `ArgumentException`, or a `GameStatus` returned without mutating), so
  no library exception type crosses the engine boundary. Cover it with a FromFen-into-dead-position
  test alongside the existing `GameEndTests`.

#### C-3-4 — `FromDictionary` mutates the config it is handed, and the shared presets are reachable as that argument
- Severity: **Medium** (latent, no current caller passes a preset)
- Where: `src/KiriChess.Engine/Config/KiriChessConfig.cs:149` and `:164`; guard gap at
  `tests/KiriChess.Engine.Tests/RulesDocumentTests.cs:127-138`
- Impact: `var cfg = baseConfig ?? CreateFlagfall();` takes the caller's object by reference and
  `cfg.ApplyKey(...)` writes straight into it, including into its `PieceValues` and
  `MoveCostPerPiece` dictionaries. `KiriChessConfig.Presets` is a process-wide static of shared master
  copies. Measured: `FromDictionary(new(){["CLOCK_START_SECONDS"]="7", ["PIECE_TIME_VALUES"]="9,9,9,9,9"},
  KiriChessConfig.Presets["flagfall"])` leaves the global Flagfall preset at 7 seconds with a pawn
  worth 9, and returns that same object. Every subsequent Flagfall game in the process would be
  created under the corrupted rules.

  The null-baseConfig path is safe (`CreateFlagfall()` clones) and so is `PRESET=` (line 155 clones),
  so nothing is broken today. What makes this worth writing down is that the existing guard,
  `Presets_AreClonedBeforeUse_SoTheSharedCopyIsNeverMutated`, claims the property in its name but only
  exercises `CreateFlagfall()`; it never touches the `baseConfig` overload. And the obvious future
  caller is already designed: `ToKeyValues` and `ToConfigString` document `%kcfg` as the PGN
  round-trip vocabulary (`KiriChessConfig.cs:171-179`), and an importer reading a pool's config back
  would naturally reach for `FromDictionary(kcfg, Presets[pool])`.

  This extends, and partly corrects, deep-audit **ENG-1 #2** and **C-1-5**, which both flag
  `Presets` as a public static mutable dictionary and propose exposing it as
  `IReadOnlyDictionary<string, KiriChessConfig>`. That fix does not close this path: a read-only
  dictionary still hands out the same shared `KiriChessConfig` reference, and `FromDictionary`
  writes straight through it without any cast or indexer mutation. ENG-1 also concludes there is no
  live bug because "all four current call sites correctly call `.Clone()` first"; that is true of
  the call sites it enumerated, but it is the `baseConfig` parameter, not the indexer, that carries
  the hazard.
- Fix: Clone at the top of `FromDictionary` (`var cfg = (baseConfig ?? CreateFlagfall()).Clone();`)
  so the method is pure with respect to its argument, and extend the existing test to cover the
  `baseConfig` overload so the guard matches its own name. Do this in addition to ENG-1 #2's
  read-only accessor, not instead of it.

#### C-3-5 — Both replay paths justify a needed catch with a call the engine no longer makes
- Severity: **Low**
- Where: `src/KiriChess.Server/Games/GameReplayBuilder.cs:48-55`,
  `src/KiriChess.Server/Games/PgnBuilder.cs:63-71`
- Impact: Both comments explain that the `Chess.ChessException` arm is needed because
  "KiriChessEngine.Apply's `board.IsValidMove(chessMove)` check throws it". `Apply` has not called
  `IsValidMove` since it moved to `ResolveAgainst` against the generated move set; that path now
  yields `ArgumentException`, already covered by the same catch. The catch is still needed, but for
  the different reason in C-3-3: `board.Move` can throw `ChessGameEndedException`. Someone verifying
  the comment greps for `IsValidMove`, finds nothing, and deletes the arm, which reintroduces a 500
  on `/replay/[id]` and on PGN download, both public pages, permanently for the affected game.
- Fix: Rewrite both comments to name `board.Move`'s `ChessGameEndedException` as the reason. If
  C-3-3 is fixed at the engine boundary, say so and keep the arm as belt and braces.

#### C-3-6 — `/learn`'s module header describes a legality model the page no longer uses
- Severity: **Low**
- Where: `ui/lib/tutorial.ts:3-12` against `ui/app/learn/page.tsx:23,48,60,153-154`
- Impact: The header states "there is no client-side legal-move generator (the server's /legal-moves
  is the only legality authority), so every step carries its own static `fen`, the exact set of moves
  the player may play from it, and the true `fenAfter` of each move". All three clauses are now
  false: the page imports `chess.js` and constructs `new Chess(step.fen)` as its legality authority,
  and `TutorialStep` carries neither a move list nor `fenAfter`. The header also says the page adds
  `extraMoverDelta` and `extraOpponentDelta` for the check chip and repetition nullification; neither
  field exists anywhere in `ui/`, because the page computes the chip inline from
  `POOLS[step.preset].config`. Using `chess.js` in a no-backend sandbox is fine. The problem is that
  this comment is the only place the tutorial's legality model is written down, and it describes a
  superseded one. Worth noting while touching it: the inline chip calculation applies
  `checkCheckerGain * economyScale` with no `doubleCheckChipMultiplier`, so a tutorial step that ever
  contained a double check would display the wrong number.
- Fix: Rewrite the header to describe the `chess.js` sandbox and the inline chip computation, and
  drop the two field names that do not exist.

#### C-3-7 — `clockAnalysis.ts` states a toll range the config does not produce
- Severity: **Low**
- Where: `ui/lib/clockAnalysis.ts:6`
- Impact: The doc comment justifying `swingThreshold` says "an ordinary quiet move costs 1-7s in toll
  in every pool". The Second Edition table in `config/kirichess.rules.json` is pawn 1, knight 2,
  bishop 2, rook 3, queen 4, king 1, castling 3, a range of 1-4s. The code itself
  (`Math.max(4, round(startTimeSeconds * 0.05))`) is unaffected, but the sentence is the stated
  justification for the constant and it is quoting the wrong edition. This is the one prose
  rule-number claim in `ui/` that is not generated from the config.
- Fix: Correct to 1-4s, or derive the range from `DEFAULT_RULES.moveCostPerPiece` so it cannot go
  stale again.

#### C-3-8 — `GameSession.Restore` replays from the standard start, ignoring the stored `StartFen`
- Severity: **Low** (trigger: the first custom start position)
- Where: `src/KiriChess.Server/Games/GameSession.cs:402` against `GameReplayBuilder.cs:28` and
  `PgnBuilder.cs:49`
- Impact: Both replay builders honour `entity.StartFen`, each with an explicit
  blank-falls-back-to-standard-start comment. `Restore` calls `engine.NewGame(entity.ConfigSnapshot)`
  unconditionally. Harmless today because `StartFen` is only ever written as `KiriChessEngine.StartFen`
  (`GameSession.cs:375`), but the three paths that reconstruct a game from its move log no longer
  agree, and this is the one whose output resumes as a **live rated game** rather than a read-only
  view. Crash recovery of a non-standard-start game would silently replay from the wrong root.
- Fix: Use the same `string.IsNullOrWhiteSpace(entity.StartFen) ? KiriChessEngine.StartFen : entity.StartFen`
  expression the two builders use, so all three reconstruction paths share one rule.

### Verified clean

Checked deliberately, found correct. Recorded so the next pass does not re-derive them:

- **Move generation.** `PerftTests` walks the standard six reference positions through the engine's
  own `LegalMoves` and `Apply`, not the underlying board, and position 6 is cross-validated against
  `python-chess` node for node. Castling, en passant and promotion legality are covered by that.
- **Double-check detection.** `IsDoubleCheck` (`KiriChessEngine.cs:253-276`) fires correctly for a
  discovered double check, for one where the discovered attacker is **pinned to its own king**, and
  for one where a **pawn** is one of the two checkers. Those are the three shapes an attack count
  written against a move generator usually gets wrong. Checkmate correctly pays no chip.
  This is the evidence **C-2-8** and deep-audit **ENG-1 #8** both note is missing: the multiplier
  has no numeric cost-vector assertion and, per C-2-8, no test at all. It is worth recording that
  the predicate is behaviourally correct, so whoever acts on C-2-8 is writing a regression test
  around known-good behaviour rather than hunting a suspected bug. The correctness question and the
  cost question C-2-8 raises are independent.
- **Promotion pricing.** The promoting move pays the pawn toll (-1s under every rated pool), which is
  what `docs/RULES.md:83` and the rules page promise; the promotion fee survives only as the custom
  dial, and under-promotion is priced off the promoted piece.
- **En passant pricing.** Priced as a pawn capture, covered by both a unit test and a cost vector.
- **Config as source of truth.** `gen:rules:check` is green; no rule number is hardcoded anywhere in
  `ui/lib`, `ui/app` or `ui/components` outside the generated files (C-3-7 is prose, not a value);
  `RulesDocumentTests` pins embedded-vs-repo equality, preset derivation and full variable coverage.
- **Generated doc arithmetic.** Every derived table in `docs/RULES.md` recomputes correctly from
  `docModel`: the ~81s toll figure, all six thinking budgets, all eight conversion bills, and every
  pool's economy and multiplier label.
- **Rule 4 survives a crash.** `GameSession.Restore` carries `RepetitionCounts` forward
  (`GameSession.cs:415-417`), so the repetition guard is not reset by a restart.

### Already covered

- The **victim-drain seam**, where a capture or check loss drives the non-moving side's clock to 0
  while the result stays `Ongoing` and the overshoot is forgiven by the clamp, is deliberate
  engine/server layering. It is documented at `EconomyInvariantTests.cs:22-30` and pinned by
  invariant 8. Not re-reported here; the clock-side questions belong to **C-4**.
- **Think-time granularity** (`thinkTimeSeconds` is an `int`) is **C-4**'s "think-time measurement".
- **Rule 3 and Rule 4 are swapped in the engine test file names.** Already reported twice, as
  **C-2-14** and deep-audit **ENG-1 #7**. Confirmed independently here, with one addition for
  whoever fixes it: `src/KiriChess.Engine/KiriChessEngine.cs:193` carries the same inversion in a
  comment ("Rules 1, 2, 4 + promotion/castling costs"; `ApplyEconomy` implements 1, 2 and 3), so the
  rename should sweep that line too. Not counted as a finding here.
- **Unbounded rule variables and `Presets` being publicly mutable** are **ENG-1 #1/#2/#3** and
  **C-1-5**. C-3-4 above is the one path through that surface those reports do not cover, and it is
  written as an extension of them rather than a new claim.
- **`HasMatingMaterial` treating two same-coloured bishops as sufficient** is **ENG-1 #5**. It sits
  in the same `ResolveStatus` flag branch as C-3-1 but is a separate defect; fixing C-3-1's ordering
  neither fixes nor worsens it.
- `docs/audits/readiness/` and `docs/audits/arch-speed.md` did not exist at the time of this pass.
  `docs/audits/C.md (C-1) `, `C.md` and `C.md` were written by a parallel session while this packet
  was in progress and are cross-referenced above.
- The missing tests named in the fixes above (flag-versus-draw precedence, `FromFen` into a dead
  position, the `baseConfig` overload, en-passant repetition keys) belong in **C-10**'s ranked list
  if that packet runs later.

### Action list

1. - C-3-1: reorder `ResolveStatus` so stalemate, insufficient material, fifty-move and
   threefold resolve before flag-fall, and add the three precedence cases to `GameEndTests`. This is
   the only finding that changes a rated result, and the repo's own invariant already says the
   current behaviour is wrong.
2. **Owner decision, then** - C-3-1 alternative: if flag-beats-draw is the intended design,
   say so in `docs/RULES.md:92-95`, `ui/messages/en.json:408,410` and `EconomyInvariantTests.cs:249`
   instead. This is a rules question, not a code edit.
3. - C-3-3: stop `Chess.ChessGameEndedException` at the engine boundary, then C-3-5:
   correct the two replay comments that misname why their catch exists.
4. - C-3-4: clone in `FromDictionary`, and widen
   `Presets_AreClonedBeforeUse_SoTheSharedCopyIsNeverMutated` to cover the overload it names. Best
   done in the same change as ENG-1 #2's read-only accessor, since that fix alone does not close
   this path.
5. - C-3-2: either normalise the en-passant field out of `PositionKey` when no capture is
   available, or correct the "official chess definition" claim in `docs/RULES.md:77` and
   `ui/messages/en.json:387`. Prefer the former; the claim is the more valuable half.
6. - C-3-6, C-3-7, C-3-8: stale-comment cleanup. Fold `KiriChessEngine.cs:193` into
   whichever change closes C-2-14 / ENG-1 #7, so the rule-number inversion is corrected in one pass
   rather than two.

No standing position is touched by any of the above.

---

## C-4 — Clock economy
Date: 2026-08-22
Packet: docs/audits/INDEX.md

### Verdict

The economy arithmetic itself is sound, and the clock-source discipline is the best-defended part of
this codebase: `IGameClock` is honoured everywhere, `MonotonicClockTests` proves immunity to a
wall-clock step in both directions, `LatencyCompensationTests` covers eleven cases including the one
that matters most (compensation reaching flag-fall), and `EconomyInvariantTests` pins ten properties
across six pools of random self-play. Captures, tolls, chips and promotion all price exactly as
`config/kirichess.rules.json` says they do.

The defects are not in the arithmetic. They sit at the two seams where the clock stops being the
engine's business and becomes the server's, and both leak in the same direction: the server making a
decision the engine already knows how to make correctly.

The first is decisive. `flagVsInsufficientMaterialIsDraw` is a shipped rule in all six rated pools,
promised in three player-facing places, and implemented in exactly one of the two code paths that can
end a game on time. The path that implements it almost never fires; the path that fires for every
ordinary time-out does not consult material at all, and the result it invents goes straight into
Glicko-2. The second is quieter but continuous: sub-second think time is discarded rather than
carried, so every player is measurably undercharged on every move, in a variant whose entire premise
is that the clock is the win condition. The repo already found this hole from the bot side and worked
around it there; the human side was never revisited.

### Findings

#### C-4-1 — The insufficient-material draw is skipped on the flag path that actually fires
- Severity: **High**
- Where: `src/KiriChess.Server/Games/GameSession.cs:325-335` (`CheckFlagFall`, no material check)
  against `src/KiriChess.Engine/KiriChessEngine.cs:319-321` (the engine's correct branch); reached
  from `src/KiriChess.Server/Games/GameRegistry.cs:78` and
  `src/KiriChess.Server/Games/FlagFallMonitor.cs:35`. Claims at `docs/RULES.md:93`,
  `ui/messages/en.json:403`, `:406`, `:428`, and the custom builder's own hint at
  `ui/messages/en.json:224`.
- Impact: There are two ways a game can end on time, and they disagree.

  The **engine** path (`ResolveStatus`) applies the rule: if the mover's own clock crosses zero on a
  move they submitted, and the opponent cannot mate, the result is `DrawByInsufficientMaterial`.
  Measured on the shipped Flagfall preset: from `4k3/8/8/8/8/8/8/3QK3 w - - 0 1` with White on 3s and
  thinking 10s, `Apply` returns `DrawByInsufficientMaterial`, winner none; give Black a queen
  (`3qk3/...`) and the same call returns `FlagFall`, winner Black. The rule works.

  The **server** path (`CheckFlagFall`) does not. Its three guards are `IsOver`, `FlagFallLoses` and
  `LiveRemaining > 0`; it then sets `Winner = Position.SideToMove.Opposite()` unconditionally.
  `HasMatingMaterial` is never called from anywhere but `KiriChessEngine.cs:319`, confirmed by grep
  across `src/`.

  The two paths are not equally likely. They are close to mutually exclusive, and the wrong one wins.
  `GameRegistry.Get` runs `CheckFlagFall()` on **every read** (`GameRegistry.cs:78`), and every action
  path, hub and HTTP alike, goes through `Get` before reaching `ApplyMove`
  (`GameActionExecutor.cs:102,115,130,145,160,181,199`, `GameEndpoints.cs:89,98,114,154`). A player who
  runs out of time while thinking is therefore flagged by `CheckFlagFall` the moment anything reads
  the session, and by `FlagFallMonitor` within one second regardless; their late move then hits
  `IsOver` and throws. It never reaches `Apply`. **The engine's branch is unreachable for ordinary
  think-time flags.** It fires only in the narrow case where a legal, in-time move's own economy
  (toll, castle fee, promotion cost) carries the mover below zero.

  The shipped behaviour is thus the inverse of the published rule: a bare king normally *does* win on
  time, and only fails to when the loser happened to flag on the toll of a move they actually played.
  `PersistIfOverAsync` (`GameRegistry.cs:97-127`) takes `session.Winner` verbatim and hands it to
  `RatingService.ApplyAsync` in the same transaction, so the invented win is also a permanent Glicko-2
  change in a rated pool, not just a wrong scoreboard.

  This survived because the only behavioural test of the rule is an engine test
  (`tests/KiriChess.Engine.Tests/GameEndTests.cs:43-49`). Nothing exercises `CheckFlagFall` against
  material at all, and the generative suite structurally cannot see it: `EconomyInvariantTests` drives
  `KiriChessEngine.Apply` directly and never constructs a `GameSession`.

  The copy makes this worse rather than better, because it is specific. `en.json:428` coaches the
  losing side to rely on the rule by name ("The insufficient-material draw rule is your opponent's
  best shield"), and `en.json:224` states it flatly as "A bare king never wins on time."
- Fix: Move the decision out of the server. Expose the existing `HasMatingMaterial` /
  `FlagVsInsufficientMaterialIsDraw` logic as a small public engine call taking the current FEN and the
  flagging side, and have `CheckFlagFall` set `Status`/`Winner`/`Termination` from its answer, so both
  paths resolve a flag through one piece of code. Then add a server-level test: K+Q versus a lone king,
  flag by idling, assert a draw. Note the ordering interaction with **C-3-1**: if that finding is acted
  on, both flag paths should be reordered together rather than one each.

#### C-4-2 — Sub-second think time is discarded every ply instead of carried forward
- Severity: **Medium**
- Where: `src/KiriChess.Server/Games/GameSession.cs:199` (`ElapsedSeconds` floors), `:213` (charged at
  move time), `:229` (anchor reset discards the remainder); the same hole described from the bot side
  at `ui/lib/botClock.ts:4-8`
- Impact: `ElapsedSeconds()` floors the charged interval to whole seconds, and `ApplyMove` then resets
  `_lastMoveTimestamp` to a fresh reading. The sub-second remainder is not carried into the next ply,
  it is thrown away. Every move is undercharged by up to one full second, and the shortfall accumulates
  linearly with move count.

  Measured with `FakeGameClock` on a 60s config, twelve plies at exactly 1.9s of real thinking each:
  both sides spent **11.4 real seconds** and both were charged **6**. That is a 47% discount, and it is
  not an artifact of the interval chosen, it is `0.9s × 6 moves`. Over a 40-move game the ceiling is
  roughly 40 seconds of unbilled thinking; in Zeitnot (`startTimeSeconds: 60`) that is two thirds of
  the starting clock, and Zeitnot is rated.

  Two reasons this is worth more than a rounding note. First, the repo already found this hole from the
  other end: `ui/lib/botClock.ts:4-8` exists *because* "Stockfish replies in a few hundred
  milliseconds, which floors to zero seconds charged, every move, so before this module the bot's clock
  only ever moved via tolls and capture economy, and a human literally could not flag it." The bot got a
  compensating mechanism; the human side of the identical hole was never revisited, and
  `botClock.ts:135-137` reasons the human case away as "intentional and mirrors a human blitzing out an
  obvious recapture", which is true for a genuinely fast move but not for the 1.9s-charged-as-1s case
  above.

  Second, it is deterministically exploitable rather than merely sloppy: a caller that times its
  submissions just under an integer boundary pays about half of what it uses, repeatably. A human cannot
  do that reliably; a script driving the public API (**S-4**) or a hub client can, and it leaves no
  trace a moderator would recognise, because `MoveRecord.ThinkMs` records the honest millisecond figure
  while the clock was charged the floored one. The evidence is present, but nothing compares the two.

  It also quietly invalidates the published thinking-budget tables (`docs/RULES.md:147`, the `duration`
  blocks in `config/kirichess.rules.json`): those are computed from tolls plus a modelled think budget,
  against a clock that is not charging what it says it charges.
- Fix: Carry the remainder rather than discarding it. Keep a per-side `TimeSpan` carry field (per side,
  not one shared field, because the players alternate and the remainder belongs to whoever spent it),
  add it to `ChargedElapsed()`, and after charging `n` whole seconds retain `charged - n` for that
  side's next turn. Clocks stay whole seconds end to end, so no engine, DTO or UI change is needed.
  Then assert the property directly: twelve plies at 1.9s must charge 11 or 12 seconds, not 6.

#### C-4-3 — The board's cost preview cannot see the base increment, because the economy DTO omits it
- Severity: **Low** (trigger: any custom game with a non-zero increment)
- Where: `src/KiriChess.Server/Games/GameDtos.cs` (`EconomyDto`, sixteen fields, no `BaseIncrement`)
  against `src/KiriChess.Engine/KiriChessEngine.cs:172` (the increment is credited on every move);
  consumed at `ui/app/game/[id]/page.tsx:613-616` and `ui/lib/costPreview.ts:97-101`; the field is
  offered to players at `ui/app/custom/page.tsx:446-451`
- Impact: `Apply` credits `config.BaseIncrement` to the mover on every non-nullified move. `EconomyDto`
  does not carry the field, so `state.economy` cannot express it and `computeCostPreview` has no way to
  include it. Its doc comment lists the exclusion, but as a deliberate one alongside check-chip
  magnitude and repetition nullification, which are genuinely position-dependent. The increment is not:
  it is a constant the preview could show exactly.

  Invisible in every rated pool, because `baseIncrement` is 0 in the defaults and no pool overrides it.
  It becomes visible the moment someone uses the builder control at `custom/page.tsx:446`, after which
  every hover preview on the board understates the mover's net by the increment, on every move, in the
  one game mode whose entire purpose is letting a player see what a rule change does to the economy.
- Fix: Add `BaseIncrement` to `EconomyDto` and `ToEconomyDto`, export OpenAPI, regenerate the client,
  and add `moverNet += economy.baseIncrement` to `computeCostPreview`. Update the doc comment's
  exclusion list so it names only the two genuinely position-dependent exclusions.

#### C-4-4 — The client clock counts down off `Date.now()`, the exact clock `realtime.md` forbids for intervals
- Severity: **Low**
- Where: `ui/app/game/[id]/page.tsx:215` (sync anchor), `:461` (250ms ticker), `:619-627`
  (`displayTimes`); the rule it mirrors is `.claude/rules/realtime.md`'s "wall clock for instants,
  monotonic for intervals" and `src/KiriChess.Server/Games/IGameClock.cs:11-19`
- Impact: `displayTimes()` derives the running clock as `(now - sync.time) / 1000`, where both readings
  are `Date.now()`, a steppable wall clock used to measure an interval. This is precisely the mistake
  `IGameClock` exists to prevent, applied on the other side of the wire. A browser NTP correction, a
  laptop resuming from sleep, or a user changing the system clock mid-turn makes the displayed clock
  jump or run backwards; a backward step can even show more time than the player has.

  Server-authoritative, so no game result is affected, and every state push resyncs the anchor
  (`:213-219`). Hence Low. But it is not purely cosmetic despite the comment at `:459` saying so: the
  same value drives the assertive screen-reader urgency announcements at `:472-495`, the one push the
  a11y design lets interrupt, and a clock that jumps is a clock that announces a threshold the player
  never crossed.
- Fix: Anchor and tick on `performance.now()`, which is monotonic in every browser this targets, and
  keep `Date.now()` only where an absolute instant is wanted. One-line change in `resetTicker` and in
  the ticker; `displayTimes` needs no other edit.

#### C-4-5 — `StartTimeSeconds` has a floor of 1 in the browser and a floor of 0 on the server
- Severity: **Low**
- Where: `ui/app/custom/page.tsx:91` (`startTimeSeconds < 1` rejected) against
  `src/KiriChess.Engine/Config/KiriChessConfig.cs:115` (`< 0` rejected)
- Impact: The builder refuses a start time under one second; `KiriChessConfig.Validate()` accepts zero,
  so a direct POST to the create-game endpoint with `startTimeSeconds: 0` is accepted. Measured: such a
  config passes `Validate()`, and the resulting `GameSession` reports `LiveRemaining(White) == 0` with
  `CheckFlagFall()` returning `true`, `Status = FlagFall`, `Winner = Black`, before a single move is
  played. The game is persisted as a completed loss for a player who never had a turn.

  Harmless in reach, since custom games are self/bot-only (ENG-1) and unrated, so this produces junk in
  the creator's own history and nothing else. Worth recording because of the shape: the only guard on a
  rule variable's floor lives in the browser, while `.claude/rules/engine.md:24` states the engine's own
  guarantee is that an invalid config "fails loudly at game creation".
- Fix: Change `KiriChessConfig.cs:115` to `StartTimeSeconds < 1`, matching the client. This belongs in
  the same pass as ENG-1 #1's ceilings and ENG-1 #3's `Enum.IsDefined` check, all three being the same
  missing-bound family in the same method.

#### C-4-6 — The board shows "time!" up to ~1.5s before the server will actually flag
- Severity: **Low**
- Where: `ui/app/game/[id]/page.tsx:619-627` and `ui/components/Clock.tsx:25,45-53` against
  `src/KiriChess.Server/Games/GameSession.cs:133-139,199` and
  `src/KiriChess.Server/Games/FlagFallMonitor.cs:29`
- Impact: The two sides compute the flag moment differently, and the client is always the earlier one.
  The client subtracts the **full** elapsed interval; the server subtracts elapsed **minus** the lag
  credit (up to `MaxCompensationMs`, default 500) and then **floors**, and detection is finalised by a
  1Hz sweep. So the client renders `0:00` and the red `flagged` state (`Clock.tsx:25` keys off
  `seconds <= 0`) while the server still considers the player live, for up to roughly a second and a
  half.

  The direction is the safe one, since the client never shows time the server has already taken, and the
  discrepancy would be invisible in classical chess where nothing can happen at zero. It is not
  invisible here: this is a variant where a capture wins time back, so a player told they have flagged
  may still have a legal, winning, clock-restoring capture available and no reason to believe it. That
  is the specific reason to spend a fix on it rather than accept the rounding.
- Fix: Render the flag state from the authoritative signal rather than the local countdown. Treat the
  clock as flagged only when `state.status` says so, and let the countdown floor at `0:00` without
  claiming the flag. Alternatively surface the server's compensation credit in the state payload so the
  client can subtract the same quantity.

### Verified clean

Checked deliberately, found correct. Recorded so the next pass does not re-derive them:

- **Clock-source discipline.** No wall-clock interval subtraction anywhere in the charging path.
  `ChargedElapsed` is genuinely the single funnel its doc comment claims, reached by move-charging,
  `LiveRemaining`, and both flag-fall paths. `MonotonicClockTests` covers step forward, step backward,
  telemetry, flag immunity, and re-anchoring on restore.
- **Lag compensation.** Capped, EMA-smoothed, outlier-clamped (`MaxRttSampleMs`), recorded per ply in
  `MoveRecord.LagMs`, and correctly credited on the flag path rather than only on move-charging. Eleven
  tests, including `Compensation_reaches_flag_fall` and `Compensation_does_not_change_the_economy_deltas`.
- **The victim-drain seam cannot be escaped by a fast capture.** A side driven to 0 by a capture or
  check chip is on move at 0 and might, in principle, play a capture that wins time back before the 1Hz
  sweep notices. It cannot: `GameRegistry.Get:78` flags lazily on every read, and every hub and HTTP
  action path goes through `Get` before `ApplyMove`. Checked all sixteen call sites.
- **Resignation is not displayed as a flag-fall.** `GameSession.Resign` reuses `GameStatus.FlagFall` as
  the decisive bucket (`GameSession.cs:255`), but the overlay renders from `termination`, not `status`
  (`ui/components/GameOverOverlay.tsx:22,92-93,106`), so the player is told "Resignation".
- **Distributed clock reads are honest about being approximations.**
  `Games/Distributed/LiveGameStateDto.cs:9-15` states in so many words that its figures are base
  remaining, wall-clock-approximated, display-only, and never chargeable, and the record's field
  comments repeat it. The code matches the comment.
- **The client economy mirror matches the engine** on captures, en passant, castling detection (an
  e-file king moving two files is only ever castling in standard chess), movement toll in all five
  `MoveCostMode` values, and promotion pricing off the promoted piece. Its documented exclusions are
  honest except for the increment (C-4-3).
- **`Restore` cannot invent a flag.** Replay charges `thinkTime: 0`, so a replayed clock is always at
  least the real one; a flag during replay implies a flag in the real game. Downtime is correctly not
  charged, and both anchors restart together.

### Already covered

- **Unclamped persisted deltas beside clamped clocks** is **C-2-10**, which already names C-4 as an
  overlap. Confirmed independently, not re-reported. The fix choice there (document `dw`/`db` as
  nominal, *or* clamp, not both) is still the right framing.
- **No upper bound on any rule variable, and int32 overflow in the economy multiply chain**, is
  **ENG-1 #1**. C-4-5 above is the *lower*-bound half of the same method and is written as an extension,
  not a new claim.
- **`Enum.IsDefined` missing on `MoveCostMode`** is **ENG-1 #3**; **`Presets` mutability** is
  **ENG-1 #2** plus **C-3-4**.
- **`HasMatingMaterial` counting two same-coloured bishops as sufficient** is **ENG-1 #5**. That is the
  *content* of the material test; C-4-1 is that the test is not consulted at all on the live path.
  Fixing either alone leaves the other open, and C-4-1 is the larger of the two.
- **Flag-fall outranking stalemate, threefold and the fifty-move rule** is **C-3-1**. C-4-1 sits in the
  same `ResolveStatus` branch and the same `CheckFlagFall` method; whoever fixes one should fix both in
  one pass, because both are "the flag decision is made in the wrong place or the wrong order".
- **`FlagFallMonitor` having no `try`/`catch`** is **C-2-1**. Its other structural property, that the
  sweep awaits `PersistIfOverAsync` serially inside the `foreach` so flag-detection latency degrades
  with the number of games ending in the same second, is instrumented already (`FlagFallMonitor.cs:47`,
  `FlagFallSweepCompleted`) and belongs to **I-6**'s capacity ladder rather than here. Noted, not filed.
- **`clockAnalysis.ts` quoting a stale toll range** is **C-3-7**.
- **Think-time granularity** was deferred to this packet by **C-3**'s "already covered" list; it is
  C-4-2 above.
- The tests named in the fixes above (server-level flag-versus-insufficient-material, sub-second carry,
  increment in the preview) belong in **C-10**'s ranked list if that packet runs later.

### Action list

1. C-4-1: route `CheckFlagFall` through the engine's flag resolution so the
   insufficient-material draw applies on both paths, and add the server-level test that would have
   caught it. This is the only finding here that changes a rated result and writes a wrong Glicko-2
   change to the database. Do it in the same pass as **C-3-1**, which touches the same two methods.
2. C-4-2: carry the per-side sub-second remainder in `GameSession` instead of discarding it
   at `:229`, and assert the property with `FakeGameClock`. No engine, DTO or UI change needed.
3. C-4-3: add `BaseIncrement` to `EconomyDto`, regenerate OpenAPI and the client, and
   include it in `computeCostPreview`.
4. C-4-6: render the flagged state from `state.status` rather than from the local countdown
   reaching zero.
5. C-4-4: move the client countdown to `performance.now()`.
6. C-4-5: raise the server's `StartTimeSeconds` floor to 1, folded into whichever change
   closes ENG-1 #1 and #3, since all three are missing bounds in `KiriChessConfig.Validate()`.

No standing position is touched by any of the above.

---

## C-5 — Realtime and live state
Date: 2026-08-31
Packet: docs/audits/INDEX.md

### Verdict

The distributed layer is the best-argued code in the repository: every lease decision in
`Games/Distributed/` is written down with the failure it is choosing between, the asymmetries
(`RenewAsync` failing open on a Redis outage, `TryClaimAsync` failing closed) are deliberate and
correct, and the clock rule from `realtime.md` holds without exception — `ChargedElapsed`
(`GameSession.cs:185-190`) is the single monotonic path that move-charging and *both* flag-fall
paths go through, and the only wall-clock reads near a game are on the read-model and are labelled
display-only. That is the good news, and it is most of the file count.

The theme of what is wrong is the opposite of the theme of what is right. The distributed layer
reasons carefully about *two machines* and does not reason at all about *two threads on one
machine*: `GameSession` is a mutable object with four independent writers (hub, REST, public bot
API, flag-fall sweep) and one background reader, and there is no lock anywhere in `Games/`. That is
live today, on the single Fly machine, with Redis off — it is not a Phase 8.4 problem. The one place
it is guaranteed to bite is `LiveGamePersister`, which reads a move list a move path may be
appending to, on a line that sits outside its own `try`, in a `BackgroundService` whose default
exception behaviour stops the host. The second theme is smaller and is about the seams the design
argues about but does not close: a graceful shutdown deletes the instance's heartbeat and leaves its
game leases behind, so the "owner is provably dead ⇒ take over" branch that
`ResolveOwnerOrTakeoverAsync` was written for is exactly the branch that cannot execute after a
deploy. Nothing here needs a new subsystem; eight of the ten findings are a few lines each.

### Findings

#### C-5-1 — The snapshot writer reads a live move list outside its `try`, in a service whose unhandled exception stops the host
- Severity: Critical
- Where: `src/KiriChess.Server/Games/LiveGamePersister.cs:123-131`
  (`pending.Add((id, session.Moves.ToList()))` at `:128`, before the `try` that begins at `:134`), racing
  `src/KiriChess.Server/Games/GameSession.cs:234` (`_moves.Add(...)`); escape path
  `src/KiriChess.Server/Games/LiveGamePersister.cs:72-96` (the `await foreach` catches only
  `OperationCanceledException`)
- Impact: `session.Moves` is the `List<MoveRecord>` itself. `ToList()` on a `List<T>` takes the
  `ICollection<T>.CopyTo` path — read `Count`, allocate, copy — and an `Add` landing between those
  two steps throws `ArgumentException`, or copies a torn list. Both outcomes are routine, not
  adversarial: at the stated ~250-game ceiling the persister drains continuously while moves are
  being applied. A torn copy writes a truncated move list to the `Moves` column, which is the
  durability record restart recovery replays. The throw is worse: it is not caught anywhere between
  `WriteBatchAsync` and `ExecuteAsync`, `BackgroundServiceExceptionBehavior` is not configured in
  `src/KiriChess.Server/Program.cs`, so .NET's default `StopHost` applies and one unlucky
  interleaving takes the whole server down, ending every live game on the machine. There is no
  `Parallel`/`Task.WhenAll` concurrency test over a single `GameSession` anywhere in
  `tests/KiriChess.Server.Tests/`, so nothing would catch this before production.
- Fix: take the move-list snapshot under the same serialization that guards `ApplyMove` (C-5-2), and
  move the `_registry.Get` / `ToList()` loop inside the existing `try` so a failure falls into the
  per-game retry that is already written rather than escaping the service. Do not "fix" this by
  setting `BackgroundServiceExceptionBehavior.Ignore` — that converts a crash into a silently dead
  persister, which is the durability failure 6c exists to prevent.

#### C-5-2 — `GameSession` has no concurrency control, and four independent paths mutate one instance
- Severity: High
- Where: `src/KiriChess.Server/Games/GameSession.cs:207-249` (`ApplyMove`) and `:325-335`
  (`CheckFlagFall`), reached concurrently from `src/KiriChess.Server/Games/GameActionExecutor.cs:279-306`
  (hub + forwarded commands), `src/KiriChess.Server/Games/GameEndpoints.cs:107-141` (REST),
  `src/KiriChess.Server/Games/PublicApiEndpoints.cs:315-347` (public bot API),
  `src/KiriChess.Server/Games/FlagFallMonitor.cs:36` and
  `src/KiriChess.Server/Games/GameRegistry.cs:78` (`Get` flag-checks on every read)
- Impact: SignalR serializes invocations within one connection and nothing else. Two connections —
  a player's second tab, or the hub and `POST /api/v1/games/{id}/moves` used together, which is
  exactly what the bot API invites — can both pass the `IsOver` / `PlayerOnTurn` / `IsLegal` reads
  at `:209-211` before either writes at `:227-245`. The result is two `MovePlayed` broadcasts
  describing different positions, a duplicated ply number (`_moves.Count + 1` read twice), and the
  same think-time charged twice off one anchor. Separately, `FlagFallMonitor` calls `CheckFlagFall`
  on every session every second, so `Finish()` setting `Status`/`EndedAt` can interleave with
  `ApplyMove` overwriting `Status` back to the move's result — a player wins on time and then does
  not. Nothing in `Games/` takes a lock; the only `lock` in the whole namespace is `MatchQueue`.
- Fix: give `GameSession` a private lock (or a `SemaphoreSlim(1,1)` if an async boundary is needed)
  and hold it across `ApplyMove`, `Resign`, `OfferDraw`, `RespondDraw`, `Abandon`, `Abort`,
  `CheckFlagFall`, and the `Moves`/`ToEntity` reads. Per-session, not global, so it costs nothing at
  the ceiling: contention only ever exists between two writers to the *same* game, which is the case
  that must not proceed in parallel.

#### C-5-3 — A graceful shutdown deregisters the instance but never releases its game-ownership leases
- Severity: High (trigger: `Redis:Configuration` set — i.e. the first `fly deploy` after Phase 8.4)
- Where: `src/KiriChess.Server/Infrastructure/Redis/InstanceHeartbeat.cs:65-69`
  (`StopAsync` → `DeregisterAsync`, which deletes the heartbeat key immediately and touches no
  lease); `src/KiriChess.Server/Infrastructure/Redis/InstanceRegistry.cs:131-140`;
  `src/KiriChess.Server/Games/Distributed/GameOwnership.cs:63-78` (`TryClaimAsync` is `SET NX`);
  `src/KiriChess.Server/Games/Distributed/GameOwnershipHeartbeat.cs` (renews, never releases);
  consumed at `src/KiriChess.Server/Games/GameActionExecutor.cs:406-418` and
  `src/KiriChess.Server/Games/Distributed/OrphanedGameSweeper.cs:70-71`
- Impact: the heartbeat key is deleted at once, the `{p}game:owner:{gameId}` keys are left to their
  own TTL (`InstanceTtlSeconds`, default 30 — `RedisOptions.cs:36`). That asymmetry is unique to a
  *graceful* stop: on a crash both keys lapse together and takeover works as designed. After a
  deploy, `ResolveOwnerOrTakeoverAsync` reaches `!IsLiveAsync(owner)` — the branch written for
  "provably dead, safe to migrate" — calls `TryTakeoverAsync`, whose `SET NX` fails because the dead
  owner's key is still there, retries once, and throws `HubException("Game not found.")`
  (`GameActionExecutor.cs:418`). `OrphanedGameSweeper` skips the same games for the same reason
  (`owner is not null`). So for up to 30 seconds after every deploy, every player reconnecting to a
  live game is told it does not exist, and nothing is recovering it in the background either.
  `fly.backend.toml`'s `strategy = "immediate"` puts the new machine in the path during exactly that
  window.
- Fix: release the leases on the way out — an `IHostedService.StopAsync` (or a `StopAsync` override
  on the type that already owns the hook) that calls `GameOwnership.ReleaseAsync` for each
  `GameRegistry.ActiveSessions` entry, registered so it stops before `InstanceHeartbeat`.
  Alternatively, make the dead-owner branch a compare-and-swap on the owner key (replace *this*
  named dead owner) rather than `SET NX`; the lease TTL stops being the only proof, but
  `IsLiveAsync` already supplied the proof the branch was written around.

#### C-5-4 — Hub group membership is keyed by the raw client string, broadcasts by the canonical GUID
- Severity: Medium
- Where: `src/KiriChess.Server/Games/GameHub.cs:82` (`Groups.AddToGroupAsync(Context.ConnectionId, gameId)`),
  `:276` (same for spectators), `:123`, `:185`; against
  `src/KiriChess.Server/Games/GameActionExecutor.cs:300`, `:314`, `:334`, `:350` and
  `src/KiriChess.Server/Games/FlagFallMonitor.cs:42`, all of which broadcast to
  `session.Id.ToString()`
- Impact: `ParseGameId` (`GameHub.cs:298-299`) validates with `Guid.TryParse`, which accepts `N`,
  `B`, `P` and `X` formats and any casing — but the *unparsed* string is what names the group. A
  caller joining with `guid.ToString("N")`, braces, or uppercase lands in a group nothing ever sends
  to: `JoinGame` returns a correct snapshot, so the client believes it is subscribed, and then
  receives no `MovePlayed`, no `GameEnded`, no `OpponentDisconnected` while its clock runs down. The
  first-party UI is safe because its route params are canonical, so this is invisible in the app and
  lands squarely on the third-party bot authors `/developers` and `S-4` are trying to attract; their
  symptom is "the server froze", every game, until they flag.
- Fix: use the parsed value for the group name everywhere in `GameHub` — `id.ToString()` rather than
  the `gameId` parameter, in `JoinGame`, `JoinAsSpectator`, `OfferRematch`, `AcceptRematch`,
  `WatchChallenge`/`UnwatchChallenge` (the challenge token has the same shape of problem).

#### C-5-5 — `JoinGame` joins the SignalR group before checking the caller is a player, and never leaves on rejection
- Severity: Medium
- Where: `src/KiriChess.Server/Games/GameHub.cs:82-87`, against the participant check in
  `src/KiriChess.Server/Games/GameActionExecutor.cs:272-277`
- Impact: the group join is unconditional and happens first; when `JoinGameAsync` throws "Not a
  player in this game", nothing removes the connection. Any authenticated caller can therefore
  subscribe to any live game's full event stream without going through `JoinAsSpectator` — which
  means without the `KillSwitch.Spectating` check (`GameHub.cs:265-266`) and without
  `SpectatorTracker`. The confidentiality cost is near zero, since spectating is anonymous by design
  (8.1), but two real things break: 10c.4's "disable spectating" lever does not actually stop anyone
  watching, and the spectator count players see undercounts. The comment at `:79-81` justifies the
  unconditional join on the grounds that group membership is local; that argues for it being
  unrouted, not for it being unauthorized.
- Fix: swap the two statements — `await _actions.JoinGameAsync(...)` first, then
  `AddToGroupAsync`. The ordering buys nothing today and the snapshot call is what already decides
  whether this connection belongs in the room.

#### C-5-6 — The connection budget's "players' reserve" is a reserve for authenticated callers, not for players
- Severity: Medium (trigger: the launch-day crowd 10a.2 was written for, once most watchers are
  signed in)
- Where: `src/KiriChess.Server/Infrastructure/HubConnectionBudget.cs:22-30` (the derivation),
  `:115-143` (`TryReserve`), `:193-198` (`IsAuthenticated`), applied at `:221-241`
- Impact: the stated arithmetic is "600 anonymous leaves 400 sockets only authenticated callers can
  occupy, and 400 is 100 concurrent games". The partition is decided at connect time by
  `IsAuthenticated`, before any hub method runs, so a signed-in visitor watching `/watch/[id]` — an
  ordinary and likely-common case on a launch thread — spends the players' reserve. The reserve
  holds against logged-out onlookers only; it does nothing against the same crowd once they have
  accounts, which is the direction 8.7 is trying to push them. The failure the doc comment names
  ("players refused while onlookers hold the capacity") is reachable with the budget working exactly
  as written.
- Fix: smallest honest change is to say so in the doc comment, since the reserve still does real
  work. If a stronger guarantee is wanted, count a socket as a player's only once `JoinGame` has
  succeeded on it (the hub already knows), and cap authenticated-but-not-in-a-game sockets
  separately.

#### C-5-7 — Anonymous `JoinAsSpectator` reaches ownership takeover, so an unauthenticated caller drives a Redis write and a Postgres query per arbitrary game id
- Severity: Medium (trigger: `Redis:Configuration` set; dormant today, since
  `ResolveOwnerOrTakeoverAsync` returns immediately at `GameActionExecutor.cs:380-383` when Redis is off)
- Where: `src/KiriChess.Server/Games/GameHub.cs:259-281` →
  `src/KiriChess.Server/Games/GameActionExecutor.cs:197-209` → `:378-419` → `:426-443`
- Impact: for a game id no instance holds — including a random GUID — the anonymous path does
  `GET {p}game:owner:{id}`, then `SET NX` (which *succeeds*, creating a key), then a
  `db.Games.FirstOrDefaultAsync(...)` against Neon, then `ReleaseAsync`. That is one Postgres query
  and three Redis ops bought with one cheap hub call from an unauthenticated connection, limited
  only by `HubDefaultBurst` per connection (`HubRateLimitFilter.cs:118-123`) and the 600-socket
  anonymous ceiling. Neon is the resource the whole capacity story is bounded by (9c.7), so this is
  amplification pointed at the narrowest part.
- Fix: the spectator path does not need to *claim* anything. Resolve the owner and forward
  (`GetOwnerAsync` + `ForwardAsync`) without `TryTakeoverAsync` for `JoinAsSpectator`; a game with no
  live owner is one a spectator can be told is unavailable. If takeover on the spectator path is
  wanted, gate it on the id being present in `{p}games:live` first, which is a Redis read rather than
  a database query.

#### C-5-8 — The shutdown flush that 10a.3 needs a number for emits no log line
- Severity: Low (trigger: measuring 10a.3, or any deploy that loses moves)
- Where: `src/KiriChess.Server/Games/LiveGamePersister.cs:98-117`
- Impact: `StopAsync` has no `ILogger` call at all — no session count, no elapsed, no completion. It
  records `_metrics.SnapshotBatchWritten` per batch (`:151`), but metrics stop being scraped when the
  process exits, so that is not evidence either. If the host's shutdown token fires mid-flush,
  `OperationCanceledException` passes the catch filter at `:153` (`ex is not OperationCanceledException`),
  escapes `StopAsync`, and the remaining sessions in the loop are never written and never named.
  ROADMAP 10a.3 already identifies the partial-flush risk and already set `kill_timeout = 30`; what
  is new here is that the flush leaves no record even when it *succeeds*, so the measurement 10a.3
  is waiting on has nothing to read.
- Fix: log session count and elapsed at flush start and end, and wrap the loop in a
  `catch (OperationCanceledException)` that logs how many sessions were still unflushed. Three lines,
  and they are what turns 10a.3 from a load-test exercise into a number visible on any deploy.

#### C-5-9 — `RunGraceAsync` overwrites its pending-abandon entry without cancelling the previous one
- Severity: Low (trigger: a player disconnecting twice inside one 30s grace window — a flapping
  mobile connection)
- Where: `src/KiriChess.Server/Games/GameConnectionTracker.cs:249-250`
  (`_pending[(gameId, userId)] = cts;`), against `CancelPendingAbandon` at `:194-203`
- Impact: the replaced `CancellationTokenSource` is neither cancelled nor disposed, and its
  `Task.Delay` keeps running. A later reconnect cancels only the newest, so the orphan wakes at the
  end of its own window and calls `AbandonAsync`. The forfeit itself is still correct — the
  `IsConnectedAsync` guard at `:263` catches it — so this is a leaked timer and CTS per flap, not a
  wrong result. Two `OpponentDisconnected` broadcasts also go out for one player.
- Fix: call `CancelPendingAbandon(gameId, userId)` immediately before storing the new CTS, so the
  existing remove-once semantics dispose the old one.

#### C-5-10 — The per-IP hub connection cap is a per-process counter
- Severity: Low (trigger: a second instance)
- Where: `src/KiriChess.Server/Infrastructure/HubConnectionBudget.cs:80-82` (`_perIp` is an in-memory
  `Dictionary`), `:115-143`, `:168-178`
- Impact: the cap's stated purpose is that "a script from one machine can no longer take the site's
  whole budget". With N instances behind Fly's proxy, one address gets N × 20 sockets, and a client
  that reconnects until it lands on a fresh instance gets there deliberately. The anonymous ceiling
  has the same shape but is harmless, because `hard_limit` is per machine too and the reserve
  arithmetic is per machine by construction; the per-IP cap is the one whose whole point is
  cross-machine.
- Fix: note the per-process scope in the doc comment now, and when multi-instance lands, move the
  per-IP counter onto the `RedisRateLimiter` that already exists rather than adding a second
  distributed mechanism.

### Already covered

- **Clock correctness across `Games/`** — verified independently and clean; `C.md (section BE-2)`
  reaches the same conclusion. `ChargedElapsed` is the single monotonic path for move-charging and
  both flag-fall paths; the wall-clock reads at `LiveGameIndex.cs:66`, `LiveGameStateDto.cs:9-15`
  and `GameEndpoints.cs:367` are read-model/display and are documented as such. No finding.
- **REST and public-API move/resign bypass `GameCommandRouter`** — `C.md (C-1 improvement plan)` batch 3
  item 1, `C.md (section BE-2)` finding 1. (C-5-2 above concerns a *different* property of the
  same endpoints — that they mutate the session without serialization — and stands independently of
  whether they are ever routed.)
- **`MatchNoShowWatcher` uses process-local `IsConnected`** — `IMPROVEMENT_PLAN.md` batch 3 item 2.
- **Redis `ConnectionMultiplexer` never disposed on shutdown** — `C.md (section BE-5)` finding 3.
- **`RunGraceAsync` has no top-level catch** — `C.md (section BE-2)` finding 4. (C-5-9 is a
  distinct defect in the same method.)
- **`GameSession` logs via `Console.WriteLine`** — `C.md (section BE-2)` finding 5.
- **`LatencyProbe` hardcodes `SystemGameClock.Instance`** — `C.md (section BE-2)` finding 6.
- **Flag-fall sweep serialises persist + rating + broadcast in one tick** — `I.md (I-1)` AS-5.
- **`LiveGameIndex` per-move set re-population and whole-set reads** — `I.md (I-1)` AS-6.
- **`ActiveSessions` / `PlayersInGame` allocate on hot paths** — `I.md (I-1)` AS-8.
- **The shutdown flush has a stated but unmeasured budget** — ROADMAP 10a.3. C-5-8 adds only that
  it is also unobservable.
- **Redis unprovisioned / single machine** — standing position; every trigger-based finding above is
  named against it rather than arguing with it.

### Action list

In order:

1. **C-5-1 + C-5-2 together** — add the per-session lock, then move the persister's move-list read
   inside its `try`. These are one change; doing C-5-1 alone leaves the torn-copy half of the race.
   Add a `Parallel.ForEachAsync` test over one `GameSession` (hub path + REST path + flag-fall) —
   `tests/KiriChess.Server.Tests/` has no concurrency test over a live session at all today.
2. **C-5-4** — use the parsed GUID for group names throughout `GameHub`. Two-line change; pin it with
   a test that joins with `ToString("N")` and asserts a `MovePlayed` arrives.
3. **C-5-5** — swap the two statements in `JoinGame`.
4. **C-5-3** — release ownership leases on shutdown. Wants a `TwoInstanceGameTests` case: stop the
   owner gracefully, assert the sibling can take the game over immediately rather than after
   `InstanceTtlSeconds`.
5. **C-5-8** — three log lines in `LiveGamePersister.StopAsync`. Do this *before* the 9e.8 window, so
   the 10a.3 measurement has something to read.
6. **C-5-9** — one line in `RunGraceAsync`.
7. **C-5-7** — drop `TryTakeoverAsync` from the spectator resolution path.
8. **C-5-6 and C-5-10** — doc-comment corrections now; the enforcement halves belong with whatever
   change provisions Redis, not before it.

Owner:

- Whether C-5-6's reserve should become a real players-only cap is a product judgement about who gets
  refused on launch day, not a code decision. The current behaviour is defensible; the doc comment
  claiming more than it delivers is not.
- 10a.3's measurement still needs the 9e.8 window (ROADMAP Todo #10/#11). C-5-8 makes that window
  cheaper to read but does not replace it.

---

## C-6 — Matchmaking and pairing
Date: 2026-08-31
Packet: docs/audits/INDEX.md

### Verdict

The parts of this subsystem that were designed as security machinery are genuinely good. The
challenge token is 128 bits from the CSPRNG, URL-safe, uniquely indexed, single-use through one
guarded `ExecuteUpdateAsync`, TTL-bounded at an hour, revocable by the challenger, and capped at
three open per account. Self-pairing is blocked twice (in the in-memory list and in the Lua script).
The pairing script really is atomic, so a double-pairing cannot be produced by two instances racing.
Rated-versus-unrated is correct at every seat: the queue is always rated in an official pool,
challenges are always unrated, customs are always unrated, rematches inherit, bot-pool games are
rated in a parallel `bot:` namespace that human queues cannot name, and `RatingService.ApplyAsync`
refuses a solo or aborted game outright. Pool isolation inside the queue holds: a browser JWT cannot
name a `bot:` key and an API token is always remapped onto one.

The weakness is that all of that discipline stops at the queue's edge. `MatchmakingService` checks
the entrant and nobody else, so the eligibility verdict on the player who is actually sitting in the
pool is however old their enqueue was, and the three other paths that create a game (challenge
accept, rematch accept, `POST /api/games`) never remove that player from the queue. In the same
direction, the one consequence the no-show path claims to have does not exist, so queueing and
vanishing is free; `OfferRematch` is bounded by neither the age of the finished game nor by how many
times it has already been sent, while firing a push on every call; and the bot/human partition that
the queue enforces so carefully has no counterpart on the challenge link, which the public API and
the first-party API share verbatim. None of this loses data or touches a rating. All of it costs a
stranger time in a lobby that, by Phase 11's own framing, does not have time to spare.

### Findings

#### C-6-1 — Nothing re-checks the waiting player, and no game-creating path leaves the queue
- Severity: High
- Where: `src/KiriChess.Server/Games/MatchmakingService.cs:89-97` (only `userId`, the entrant, is put
  through `EnsureNotAlreadyInGameAsync` / `EnsureEligibleForRatedPlayAsync`; the opponent returned by
  `TryPairAsync` at `:97` goes straight to `CreateAsync` at `:122`).
  `src/KiriChess.Server/Games/GameHub.cs:212-216` and `:287-296` are the only two callers of
  `MatchmakingService.Leave` in the whole server (`LeaveQueue` and `OnDisconnectedAsync`). The three
  other paths that create a live game do not call it:
  `src/KiriChess.Server/Games/ChallengeEndpoints.cs:188`,
  `src/KiriChess.Server/Games/GameHub.cs:176-177` (rematch accept), and
  `src/KiriChess.Server/Games/GameEndpoints.cs:79`.
- Impact: a player queued on one hub connection who enters a game through any of those three paths
  keeps their queue entry, because none of them is a hub disconnect. The next stranger to join that
  pool is paired against someone already in a live game, which is exactly the state
  `PlayEligibilityPolicy.EnsureNotAlreadyInGameAsync` exists to prevent, and the stranger's game is
  dead on arrival: it sits `Ongoing` until `MatchNoShowWatcher` aborts it 45 seconds later. Two
  browser tabs are enough to reproduce it, no tooling. The same gap covers a slower case: an
  `AccountBan`, a `RatedPlayBan` or an abandonment cooldown that lands while a player is waiting is
  never seen, because the verdict was taken at enqueue time and never again.
- Fix: re-run `EnsureNotAlreadyInGameAsync` (and, for the queue, `EnsureEligibleForRatedPlayAsync`)
  against the opponent `TryPairAsync` returns, before `CreateAsync`, and drop the pairing rather than
  create the game if it fails. Separately, have every path that creates a two-player game call
  `MatchmakingService.Leave` for both participants, so entering a game and standing in a queue cannot
  be simultaneous states.

#### C-6-2 — A no-show costs nothing, and the code says otherwise
- Severity: High
- Where: `src/KiriChess.Server/Games/GameSession.cs:307-317` states "The deterrent for repeat
  no-shows is the matchmaking cooldown, not the result."
  `src/KiriChess.Server/Games/AbandonmentPolicy.cs:48-51` filters on
  `g.Termination == TerminationReason.Abandoned` and explicitly excludes `NoShow`.
  `src/KiriChess.Server/Games/MatchNoShowWatcher.cs:61-68` deliberately does not record which player
  was missing, so nothing downstream could attribute one even if it wanted to.
- Impact: the named deterrent is disclaimed by both of the files that would have to implement it. A
  player can join a pool, get matched, never call `JoinGame`, and repeat as fast as the hub queue
  bucket allows (10 burst, 1 per second, `Infrastructure/RateLimiting.cs:110-111`), burning 45
  seconds of a real opponent per cycle at zero cost. It is also the cheap route around the
  abandonment cooldown that does work: disconnecting after joining produces `Abandoned` and counts,
  never joining at all produces `NoShow` and does not. On a lobby whose scarce resource is the other
  player, this is queue poisoning with no brake on it.
- Fix: record the missing player id on the aborted game (a `TerminationReason.NoShow` game already
  has no winner, so a nullable "at fault" column is the smallest addition) and count it in
  `AbandonmentPolicy`'s existing 24-hour window alongside `Abandoned`. If that migration is not
  wanted now, correct the `AbortNoShow` comment so the gap is visible rather than described as
  handled.

#### C-6-3 — `OfferRematch` has no age bound, no repeat guard, and fans out a push per call
- Severity: High
- Where: `src/KiriChess.Server/Games/RematchService.cs:52-80` accepts any finished game the caller
  played, with no recency limit, and re-offering while unaccepted overwrites and returns rather than
  refusing (`:70-75`). `src/KiriChess.Server/Games/GameHub.cs:128` arms a fresh
  `RematchNotificationService` timer on every call, and `:134` fires
  `PushNotificationService.NotifyRematchOffered` immediately and unconditionally
  (`Infrastructure/Push/PushNotificationService.cs:38-39`).
  `src/KiriChess.Server/Games/RematchNotificationService.cs:51-56` arms one independent timer per
  call, and its 90-second re-check (`:73`) passes for every one of them because the offer is still
  open. `OfferRematch` falls in the hub *default* bucket, 30 burst and 3 per second
  (`Infrastructure/RateLimiting.cs:112-113`).
- Impact: anyone who has ever played you can push-notify you at three per second indefinitely, and
  a burst of N offers on one old game produces N identical "your opponent wants a rematch" emails 90
  seconds later. Multiplied by every past game between the pair, and unbounded in time, this is a
  targeted-harassment channel wearing a game feature's clothes; it also burns the Resend free tier
  and the sending-domain reputation that every future user's confirmation mail depends on. The
  category preference only silences the mail half, never the push (already recorded as M-5-3).
- Fix: bound `OfferAsync` to a game that finished recently (the existing challenge TTL is a
  reasonable value) and refuse a re-offer that is not materially newer than the standing one; make
  the notification fan-out one-shot per `(gameId, opponentId)` rather than per hub call, so repeat
  offers cannot multiply notices; and route the push through
  `NotificationPreferenceService.IsSubscribedAsync` the way the mail already is.

#### C-6-4 — The bot/human partition is a queue rule only; a challenge link crosses it undisclosed
- Severity: Medium
- Where: `src/KiriChess.Server/Ratings/RatingConstants.cs:23-35` builds a parallel `bot:` namespace
  precisely so "Human queues never use this namespace", and
  `src/KiriChess.Server/Games/MatchmakingService.cs:67-83` enforces it in both directions. Nothing
  equivalent exists on the challenge path: `ChallengeEndpoints.AcceptChallengeAsync`
  (`src/KiriChess.Server/Games/ChallengeEndpoints.cs:135-205`) is called verbatim by both the
  first-party route (`:82-91`) and the API-token route
  (`src/KiriChess.Server/Games/PublicApiEndpoints.cs:427-436`), and
  `src/KiriChess.Server/Data/Entities/Challenge.cs:22-59` carries no record of which surface minted
  the row.
- Impact: a bot operator can mint a challenge link through `POST /api/v1/challenges`, post it
  anywhere, and play humans who accept it; symmetrically a bot can accept a human's link. Ratings are
  safe (challenges are always unrated, `ChallengeEndpoints.cs:188`), so the cost is honesty rather
  than Glicko: `ChallengeDto` shows a display name and a pool, and nothing on the accept screen tells
  the person that the account on the other side is an API token. That is the same misrepresentation
  the `bot:` pools were built to avoid, arriving through the one door they do not cover.
- Fix: stamp the challenge row with the surface that created it and surface a boolean on
  `ChallengeDto`, so the accept screen can label a bot invitation as one. Overlaps **S-2** (API bots
  in human pools); flagged here because the mechanism is the pairing path, not detection.

#### C-6-5 — `POST /api/games` marks a solo game rated in an official pool, with no eligibility gate
- Severity: Medium
- Where: `src/KiriChess.Server/Games/GameEndpoints.cs:32-86`. The handler checks only
  `SanctionScope.Account` (`:40-43`); it never calls `PlayEligibilityPolicy`, so neither
  `EnsureNotAlreadyInGameAsync` nor `EnsureEligibleForRatedPlayAsync` runs. Line `:79` writes
  `rated && pool != null` onto a row whose two seats are the same account (`:77-78`).
- Impact: any user can open unlimited concurrent solo games in an official pool, each persisted with
  `IsRated = true` and `RatingPool = steal1`, while under an abandonment cooldown or a
  `RatedPlayBan`. No rating moves, but only because `Ratings/RatingService.cs:38` refuses
  `WhitePlayerId == BlackPlayerId` downstream, so the invariant is held by an accident of a later
  layer rather than by the endpoint. Meanwhile the rows themselves are counted by
  `_metrics.GameStarted(session.RatingPool, session.IsRated)` (`Games/GameRegistry.cs:56`) and by the
  pool-balance query the `(RatingPool, EndedAt)` index exists for
  (`Data/KiriChessDbContext.cs:113-114`), so a script can inflate a pool's rated-game statistics at
  will.
- Fix: call `PlayEligibilityPolicy` here as the queue and the challenge accept path both do, and do
  not write `IsRated = true` on a game whose two player ids are equal; let the row say what it is.

#### C-6-6 — The queue-depth gauge reads the local dictionary, so `queue_stuck` goes dark the day Redis is on
- Severity: Medium (trigger: Redis enabled / a second instance, the standing "Redis unprovisioned"
  position keeps it dormant today)
- Where: `src/KiriChess.Server/Infrastructure/GameMetricsObserver.cs:70-72` observes
  `MatchmakingService.QueueDepths`, which is `_queue.Depths()`
  (`src/KiriChess.Server/Games/MatchmakingService.cs:140`), the synchronous in-memory version.
  `MatchQueue.DepthsAsync` (`src/KiriChess.Server/Games/MatchQueue.cs:333-354`) is the Redis-aware
  version written for exactly this gauge and has no caller anywhere in the application. In the Redis
  path nothing is ever added to `_pools`, so `Depths()` returns an empty dictionary forever.
- Impact: `kirichess.matchmaking.queue.depth` stops emitting entirely once Redis is enabled, and
  Better Stack's `queue_stuck` alert (`infra/betterstack/main.tf:219-291`) is written against that
  series with `on_missing_data = "dont_fire"`. Worse, `infra/betterstack/README.md:57-61` already
  normalises "no data" on this chart as the expected reading for an idle lobby, so a permanently
  dead series is indistinguishable from a quiet evening and nobody will look. The one alert whose job
  is "nobody is being paired" would be the one that cannot fire.
- Fix: make the gauge asynchronous or cache a `DepthsAsync` result on a short interval, so the
  exported series reflects the Redis ZCARDs when Redis is the store. Cross-ref I-5-2, which asks for
  a freshness alert on a series that is always a real zero; that fix and this one are complementary,
  not substitutes.

#### C-6-7 — A Redis blip makes the seek list say "nobody is waiting" instead of falling back
- Severity: Low (trigger: a Redis outage or timeout)
- Where: `src/KiriChess.Server/Games/MatchQueue.cs:435-439` returns `Array.Empty<OpenSeek>()` from
  the outage catch. Every sibling in the same file degrades to the local answer instead:
  `TryPairAsync` at `:260-264`, `RemoveAsync` at `:320-323` (which already ran `Remove` locally), and
  `DepthsAsync` at `:349-353` all fall back to the in-memory view, and the disabled-Redis branch of
  this very method does so too (`:382`).
- Impact: during exactly the window in which `TryPairAsync` has been falling back and filling the
  local dictionary with real waiting players, `/api/seeks` reports an empty lobby. Phase 9e.4 exists
  to convert near-misses into games; this makes the outage present itself to a first-time visitor as
  "nobody is home", which is the single message most likely to lose them.
- Fix: `return OpenSeeks(maxAge, pools);` in that catch, matching `DepthsAsync` two methods above.

#### C-6-8 — Challenge accept indexes the preset dictionary and 500s on a retired pool key
- Severity: Low (trigger: a pool key removed from `config/kirichess.rules.json` while challenge rows
  naming it are still inside their one-hour TTL)
- Where: `src/KiriChess.Server/Games/ChallengeEndpoints.cs:187`,
  `config = KiriChessConfig.Presets[challenge.Pool].Clone()`. The pool was validated as official at
  create time (`:249`), never re-validated at accept time. `KiriChessConfig.PoolLabel`
  (`src/KiriChess.Engine/Config/KiriChessConfig.cs:353-371`) documents the opposite rule for exactly
  this situation: "An archived game whose pool has since been retired still renders, falling back to
  the key rather than throwing."
- Impact: an unhandled `KeyNotFoundException` out of the accept handler, so the friend clicking the
  link gets a 500 rather than a "that challenge is no longer valid". Narrow window, but it is the
  one place in the pairing path that indexes where the codebase's own stated convention is to fall
  back.
- Fix: `TryGetValue` and return the existing 409 "That challenge is no longer open" (or a 400 naming
  the retired pool) instead of indexing.

#### C-6-9 — `DELETE /api/v1/challenges/{token}` is the one public-API route with no rate limit at all
- Severity: Low (trigger: a bot looping the revoke call)
- Where: `src/KiriChess.Server/Games/PublicApiEndpoints.cs:444-453` carries no
  `.RequireRateLimiting(...)`, unlike every other route in the file (`:309`, `:348`, `:369`, `:408`,
  `:423`, `:437`, and the `read` group's own `:35`). `/api/v1` is explicitly exempted from the global
  IP backstop at `src/KiriChess.Server/Infrastructure/RateLimiting.cs:377-379`, so nothing else
  catches it.
- Impact: an authenticated token holder has one unmetered route that performs a database UPDATE per
  call. Small in absolute terms and self-inflicted rather than aimed at a stranger, but it is the
  single hole in an otherwise complete per-token limiting story, and it is the kind of thing that is
  only ever noticed while someone is already reading this file.
- Fix: add `.RequireRateLimiting(RateLimitPolicies.ApiPlayCreate)` (or `ApiRead`) to that route.

### Already covered

- **`MatchNoShowWatcher` uses the process-local `IsConnected`, so a cross-instance player is aborted
  as a no-show after joining** — `docs/audits/C.md (section BE-2)` finding 2,
  `docs/audits/C.md (C-1 improvement plan)` batch 3 item 2, and echoed in `I.md (I-1)`,
  `C.md:66`, `C.md:229`, `I.md:116`. Not re-reported; it is the sharpest thing in this packet's
  code and it already has an owner.
- **Challenge accept binds its post-creation broadcast and email to the accepting request's own
  cancellation token** — `docs/audits/C.md (section BE-2)` finding 3.
- **No `MatchNoShowWatcherTests.cs` exists** — `docs/audits/C.md` C-10-4, ranked 4th in its list.
- **Web push ignores the per-category notification preferences, so `RematchOffered` push is
  ungated** — `docs/audits/M.md` M-5-3. C-6-3 above is the volume half, not the consent half.
- **`RematchOffers` and `Challenges` survive account erasure, are never swept, and appear in no
  retention document** — `docs/audits/L.md` L-1-8/L-1-9 and `docs/audits/L.md` L-2-2.
- **`KillSwitch.Pairing` gates only `GameHub.JoinQueue`; challenge accept, rematch accept,
  `POST /api/games` and `POST /api/v1/games` keep creating games with the switch thrown** —
  `docs/audits/S.md:19,33-39`. C-6-1 touches the same four call sites for a different reason.
- **Nothing notices when `/metrics` stops producing data** — `docs/audits/I.md` I-5-2. C-6-6 is a
  different failure (the series is computed from the wrong source), and the two fixes compose.
- **`QueueJoin` counters are recorded for `bot:` pool joins too** — `docs/audits/M.md:77`.
- **No patron field is read by `MatchQueue` or `PairingNotificationService`** — `docs/audits/funding.md`
  confirmed the pay-to-win line holds in pairing. Re-confirmed here; nothing has changed.
- **HTTP move/resign endpoints bypass `GameCommandRouter`** — `docs/audits/C.md (section BE-2)`
  finding 1. Adjacent to C-6-1's list of game-creating paths but a separate defect.

### Action list

1. C-6-1 — re-check the opponent's eligibility before `CreateAsync` in
   `MatchmakingService.TryJoinAsync`, and call `MatchmakingService.Leave` from every path that puts a
   player into a game. Smallest change with the largest effect in this packet; a two-tab test proves
   it.
2. C-6-3 — bound `RematchService.OfferAsync` by game age, refuse a duplicate standing
   offer, make the notification fan-out one-shot per pair, and gate the push on the category
   preference (closes M-5-3's rematch half in the same edit).
3. **Owner** C-6-2 decide whether a no-show should count. If yes, it needs a
   column and a migration, so it is a deliberate act; if no, fix the `AbortNoShow` comment in the same
   commit so the next reader is not told a deterrent exists.
4. C-6-5 — put `POST /api/games` behind `PlayEligibilityPolicy` and stop writing
   `IsRated = true` on a same-account game.
5. C-6-6 — feed the queue-depth gauge from `DepthsAsync`. Do this before Redis is
   provisioned, not after, or the alert's first day live is also its last.
6. C-6-7, C-6-8, C-6-9 — three one-line fixes; batch them.
7. **[Owner decision] C-6-4** — whether a human accepting a link should be told the challenger is an
   API token. Cheap to implement, but it is a product position about what the bot API is for, and it
   belongs with **S-2** rather than being decided inside a pairing fix.

---

## C-7 — Ratings

Date: 2026-08-31
Packet: docs/audits/INDEX.md

### Verdict

The Glicko-2 arithmetic is correct and the boundaries around it are drawn deliberately: the solver at
`Ratings/Glicko2.cs:52-92` is a faithful transcription of Glickman's steps 5 through 9, the constants
(1500 / 350 / 0.06, tau 0.5) are the published defaults, and the four refusals at
`Ratings/RatingService.cs:35-39` mean an unrated, custom, solo or aborted game genuinely cannot move a
rating. Bot play is a real partition, not a flag: a separate `bot:` Glicko namespace
(`RatingConstants.cs:23-35`), a separate queue key, chosen from the caller's credential rather than
from a client argument (`GameHub.cs:201`), and excluded from human leaderboards and from both
detectors. The problems are not in the maths, they are in the layer around it. One is a system-level
omission: nothing in this codebase ever inflates rating deviation for inactivity, which is the half of
Glicko-2 that lives between rating periods rather than inside one, and three separate pieces of code
are written as though it happens. The rest cluster in the Phase 8.4 leaderboard cache, where the
"trust this cache" marker is written before the data it certifies and the bot pools it gates are never
built at all, so the cache's carefully argued Postgres fallback does not fire in the two cases that
would need it most. The deep audit already flagged that the warm-up never repeats; these are different
bugs in the same file, and the file's own doc comments are the most reliable guide to what it was
meant to do and does not.

### Findings

#### C-7-1 — Rating deviation never grows, so an inactive rating stays certain forever

- Severity: Medium
- Where: `src/KiriChess.Server/Ratings/Glicko2.cs:30-35`; `src/KiriChess.Server/Ratings/RatingService.cs:88-95`; `src/KiriChess.Server/Ratings/LeaderboardCache.cs:46-49`; `src/KiriChess.Server/Ratings/RatingConstants.cs:12-13`
- Impact: Glicko-2 has two halves: the per-period update, and the RD inflation an idle player accrues
  between periods. Only the first is implemented. The zero-results branch that performs the inflation
  (`Glicko2.cs:30-35`) is labelled "unused in per-game flow" and is in fact unreachable in production;
  the only route into `Update` is `UpdateSingle` (`Glicko2.cs:96-97`) from `RatingCalculator.cs:19-20`,
  and `RatingService.ApplyTo` (`RatingService.cs:88-95`) is the only writer of `Rating.RatingDeviation`
  outside the dev seeder. RD therefore moves only when a game finishes. Three consequences. A player
  who settles near RD 48 and stops playing is treated as a certain rating indefinitely, so on returning
  a year later they take and give near-full points against a pool that has moved on without them.
  `ProvisionalRdThreshold` (`RatingConstants.cs:13`) is the sole gate on the "established" leaderboard,
  and since RD never rises, whoever tops a pool holds that spot permanently without playing again,
  which on a site this size is the leaderboard a visitor sees. And `LeaderboardCache.cs:46-49` justifies
  its ZREM branch in as many words with "RD *rises* with inactivity in Glicko-2, so a player can leave
  the established set without ever playing a game that would otherwise touch their row": that branch is
  dead as written, and the comment is the clearest evidence the omission was not intended.
- Fix: add a small periodic job that applies the existing zero-results branch to `Rating` rows untouched
  for a rating period, capped at `DefaultRd`, writing through to the cache the same way game completion
  does. If that is more than is wanted before launch, the honest minimum is to add a recency or
  `GamesPlayed` condition to the "established" filter and correct the `LeaderboardCache` comment, so the
  code and its stated reasoning agree.

#### C-7-2 — The leaderboard warm marker is written before the data it certifies

- Severity: Medium
- Where: `src/KiriChess.Server/Ratings/LeaderboardWarmup.cs:49-50` (marker), `:63-67` (the work it certifies), `:77-82` (the catch); `src/KiriChess.Server/Ratings/LeaderboardCache.cs:75-82`
- Impact: `{p}lb:built` is doing two jobs, the boot lock and the "this cache can be trusted" marker, and
  it is set first. If the Postgres scan at `LeaderboardWarmup.cs:63-65` then fails (Neon scale-to-zero at
  boot is the named case in DEPLOYMENT.md) or `RebuildAsync` fails partway through the pool loop, the
  general catch at `:77-82` logs and returns with the marker still set for its full hour. `IsWarmAsync`
  answers true, `TopAsync` reads an unbuilt or half-built ZSET, and returns an empty list. Empty is not
  the fallback signal: `RatingEndpoints.cs:54-55` falls through to Postgres only on null, so an empty
  cache answer is served to the reader as a real ranking. `/leaderboards` then shows an empty or partial
  board on a site that has players, for up to an hour after a boot, which is exactly the outcome
  `LeaderboardCache.cs:75-77` says the warm check exists to prevent.
- Fix: separate the two jobs. Keep a short-TTL lock key for the boot race, and write the `lb:built`
  marker only after every pool has rebuilt successfully; delete it in the catch so any failure leaves
  reads falling back to Postgres.

#### C-7-3 — Bot pools are never warmed, but the shared marker tells readers their cache is trustworthy

- Severity: Medium
- Where: `src/KiriChess.Server/Ratings/LeaderboardWarmup.cs:61`; `src/KiriChess.Server/Ratings/RatingConstants.cs:30-35`; `src/KiriChess.Server/Games/PublicApiEndpoints.cs:209,217-228`; `src/KiriChess.Server/Ratings/LeaderboardCache.cs:143-148`
- Impact: the warm-up iterates `RatingConstants.OfficialPools`, but the set of pools that actually
  receive rated games is `MatchmakingPools` (`RatingConstants.cs:34-35`), which adds the `bot:` namespace.
  So `lb:bot:{preset}` is only ever populated by write-through (`RatingService.cs:59-60` into
  `GameRegistry.cs:134-136`) and never rebuilt, while the warmth check that gates reading it
  (`LeaderboardCache.cs:143-148`) is a single global key. On a fresh Redis the warm-up sets `lb:built`
  while every `lb:bot:*` key is absent, so `GET /api/v1/leaderboards/bot/{pool}`
  (`PublicApiEndpoints.cs:217-228`) takes the cache-hit branch, receives an empty id list, and returns
  `[]`, while the Postgres branch immediately below it (`:230-252`) holds the real bot ranking. Bot
  developers get an empty or partial board after each deploy, and it fails silently because an empty
  leaderboard looks like a plausible answer rather than a fault. This is the sharpest instance of the
  cache-versus-Postgres inconsistency this packet asks about.
- Fix: warm `RatingConstants.MatchmakingPools` rather than `OfficialPools`, and make the warmth check
  per-pool (a marker key per pool, or check that the pool's own key exists) so a pool that was never
  rebuilt reads as cold and falls back, instead of reading as empty.

#### C-7-4 — A non-outage Redis error escapes the cache and fails game completion after the rating is committed

- Severity: Medium
- Where: `src/KiriChess.Server/Ratings/LeaderboardCache.cs:55,83,159`; `src/KiriChess.Server/Games/GameRegistry.cs:130-136`; `src/KiriChess.Server/Games/GameActionExecutor.cs:291-300`
- Impact: `GameRegistry.cs:130-133` states that "a cache write failure is logged and swallowed inside
  `UpsertAsync` itself", but the exception filter behind that claim (`LeaderboardCache.cs:159`) admits
  only `RedisConnectionException` and `RedisTimeoutException`. A `RedisServerException`, the
  OOM / READONLY / MISCONF family a managed Redis returns under memory pressure or during a failover,
  and Upstash is the planned provider, propagates out of `UpsertAsync`, out of `PersistIfOverAsync`, and
  out of `SubmitMoveLocalAsync` before the `MovePlayed` broadcast at `GameActionExecutor.cs:300`. The
  players see their final move fail even though Postgres has already committed the result and both
  ratings, and `_distribution.OnGameEndedAsync` (`GameRegistry.cs:143`) is skipped, leaving the
  ownership lease dangling. The same narrow filter on the read path (`LeaderboardCache.cs:83`) turns a
  server-side Redis error into a 500 on the public leaderboard, and the `Guid.Parse` at `:81` is covered
  by no filter at all.
- Fix: catch every exception in `UpsertAsync` and in `TopAsync` (returning null from the latter). This is
  a derived cache whose failure is by design never fatal, so the narrow filter buys nothing here; keep
  it only where the two outage types are actually distinguished.

#### C-7-5 — Rated self-play games carry an official pool and are counted by the balance dashboard

- Severity: Medium
- Where: `src/KiriChess.Server/Games/GameEndpoints.cs:49-53,77-79`; `src/KiriChess.Server/Analytics/AnalyticsEndpoints.cs:48-57`; contrast `src/KiriChess.Server/Moderation/Detection/EngineDetectionEndpoints.cs:47`
- Impact: `POST /api/games` refuses any opponent id other than the caller's own (`GameEndpoints.cs:49-53`),
  so `black = req.OpponentId ?? me.Value` at `:78` always resolves to the caller. Every game this
  endpoint creates is self-play, yet with a preset and `rated: true` it is persisted with `IsRated = true`
  and an official `RatingPool` (`:79`). Ratings themselves are safe: `RatingService.cs:38` refuses to rate
  a game whose two seats are the same account. But `/api/analytics/balance` filters on `IsRated` and
  buckets by `RatingPool` with no self-play exclusion (`AnalyticsEndpoints.cs:48-57`), so any
  authenticated caller can post fully-controlled games into a pool's win rate, draw rate and game-length
  statistics, at nothing more than the `GameCreate` rate limit. That dashboard is what the owner reads
  when tuning `config/kirichess.rules.json`, which makes it a lever on the variant's rules rather than a
  cosmetic count. The neighbouring detector already applies the missing filter explicitly
  (`EngineDetectionEndpoints.cs:47`, `g.WhitePlayerId != g.BlackPlayerId`), so the codebase knows the
  exclusion is needed and one query was left out.
- Fix: add `g.WhitePlayerId != g.BlackPlayerId` to the balance query, matching
  `EngineDetectionEndpoints.cs:47`. Better still, stop writing `IsRated` and an official `RatingPool` onto
  a game the rating system will refuse, so a row's flags describe how it was actually treated.

#### C-7-6 — Two games finishing for one player race on an unguarded Rating row

- Severity: Low. Trigger: `EnsureNotAlreadyInGameAsync` is a read-then-create with no lock, so this needs
  two near-simultaneous game completions for one account; an API-token bot is the likeliest caller to get
  there.
- Where: `src/KiriChess.Server/Data/Entities/Rating.cs:4-20`; `src/KiriChess.Server/Data/KiriChessDbContext.cs:152-158`; `src/KiriChess.Server/Ratings/RatingService.cs:68-84`; `src/KiriChess.Server/Games/PlayEligibilityPolicy.cs:41-48`
- Impact: `Rating` carries no concurrency token, and `ApplyAsync` is a read, compute and write spread
  across two separate DbContext scopes. If one account does finish two rated games at once, the second
  transaction computes from a value the first has already superseded and overwrites it: one game's rating
  change disappears silently, and both `RatingHistory` rows record the same `ValueBefore`, so even the
  audit trail agrees with the wrong answer. Separately, `GetOrCreateAsync` (`RatingService.cs:68-84`)
  inserts a new `Rating` with no conflict handling, so two concurrent first games in one pool collide on
  the unique index at `KiriChessDbContext.cs:155`; the resulting `DbUpdateException` escapes
  `PersistIfOverAsync` after the session has already been removed from `_sessions`
  (`GameRegistry.cs:100`), leaving a game row stuck Ongoing with no live session behind it.
  `PlayEligibilityPolicy.EnsureNotAlreadyInGameAsync` makes this rare but cannot make it impossible.
- Fix: give `Rating` a concurrency token (an `xmin` property marked `.IsRowVersion()`) so a lost update
  throws rather than landing quietly, and retry once in `ApplyAsync` on a concurrency or unique-violation
  failure. This pairs naturally with the unique index on `RatingHistory(GameId, UserId)` the deep audit
  already asked for.

#### C-7-7 — A confirmed sandbagger's rating has no remediation path

- Severity: Low. Trigger: the first sandbagging case the detector actually confirms.
- Where: `src/KiriChess.Server/Moderation/Detection/SandbaggingEndpoints.cs:203-219`; `src/KiriChess.Server/Moderation/ModerationEndpoints.cs:98-101`; `src/KiriChess.Server/Games/PlayEligibilityPolicy.cs:66-77`
- Impact: the detector and the moderator dossier both read ratings, and nothing in `Moderation/` writes
  one. The only lever is a `RatedPlayBan`, which stops future play and does nothing about the rating the
  manipulation produced or the points it moved to the accounts that beat the tanked one. When the ban
  lapses the account returns with its manipulated rating and, because of C-7-1, a low and therefore
  "certain" RD, which is precisely the state that maximises what it can take from honest opponents on the
  way back up. With one moderator, the ban is what will actually be used, so the gap is the realistic
  outcome rather than a corner case.
- Fix: no new detector. Add one admin-only action that resets a named account's `Rating` row in a pool to
  the Glicko-2 defaults and writes a `RatingHistory` row recording the reset, so a confirmed case has a
  proportionate and auditable remedy. Do not try to unwind opponents' ratings; if TERMS §5 does not
  already say that a rating is not restored, that wording belongs to L-11.

#### C-7-8 — The public per-user ratings endpoint exposes bot pools the rest of the surface hides

- Severity: Low. Trigger: any account that has played through an API token.
- Where: `src/KiriChess.Server/Ratings/RatingEndpoints.cs:28-32`; contrast `:106-110` and `src/KiriChess.Server/Games/PublicApiEndpoints.cs:196-198`
- Impact: `GET /api/ratings/users/{userId}` is anonymous and returns every `Rating` row for that user with
  no pool filter, `bot:{preset}` included, while the history endpoint for the same pool returns 404
  (`RatingEndpoints.cs:110`), the leaderboard rejects it (`:47`), and the bot API deliberately filters the
  other way (`PublicApiEndpoints.cs:196-198`). Nothing secret is exposed, so this is a consistency wart
  rather than a leak, but a client rendering that list gets a pool it cannot chart and the three
  endpoints disagree about whether bot pools are part of the public human surface.
- Fix: filter `/api/ratings/users/{userId}` to `RatingConstants.OfficialPools`, matching the leaderboard
  and history endpoints. The bot ratings already have their own route.

### Already covered

- **Warm-up runs once, so the cache goes cold after an hour and never recovers.** Deep audit
  `docs/audits/C.md (section BE-3)` and `docs/audits/C.md (C-1 improvement plan)`. C-7-2 and
  C-7-3 are different bugs in the same file (marker ordering, pool coverage) and stand on their own.
- **No idempotency guard on `ApplyAsync` and no unique index on `RatingHistory(GameId, UserId)`.** Deep
  audit `BE-3.md:47-63`, `IMPROVEMENT_PLAN.md:162-174`. C-7-6 is the different-games race on the `Rating`
  row, not the same-game double-apply.
- **`RatingEndpoints` handlers take no `CancellationToken`.** Deep audit `BE-3.md:65-76`.
- **No iteration cap on the volatility solver; the bracket-widening branch and the self-play guard are
  untested.** Deep audit `BE-3.md:88-111`, and `IMPROVEMENT_PLAN.md:324`.
- **Glicko-2 constants and the per-update algorithm.** Re-verified independently against Glickman's paper
  (steps 2 through 9, `Glicko2.cs:26-92`, including the Illinois solver at `:69-79`): faithful, with the
  published 1500 / 350 / 0.06 and tau 0.5. No finding. C-7-1 is about the between-periods half, which the
  deep audit's "matches Glickman's published algorithm" verdict does not cover.
- **Unrated games never touching rating.** Verified sound and not re-reported: `RatingService.cs:35-39`
  guards all four cases, `GameEndpoints.cs:60-66` forces custom configs unrated, and
  `ChallengeEndpoints.cs:188` and `PublicApiEndpoints.cs:306` both pass `rated: false`. Only the
  flag-versus-treatment mismatch in C-7-5 is a finding.
- **Erasure and anonymisation versus kept ratings.** Standing position ("Games and ratings kept") and
  L-2 / `docs/decisions/RETENTION.md`. Not reopened.
- **Ownership-lease windows around `PersistIfOverAsync`; pool isolation and the pairing band.** C-5 and
  C-6 respectively. C-7-4 touches the lease only as a consequence of the cache write, not as a claim
  about ownership.

### Action list

In this order:

1. C-7-4. Widen the two catches in `LeaderboardCache`. Smallest change, and it is the only finding that
   can currently break a live game rather than a page.
2. C-7-2 and C-7-3 together, in `LeaderboardWarmup` plus the warmth check. Same file, same feature, and
   the deep audit's warm-up fix should land in the same commit so the three are reasoned about once.
3. C-7-5. Add the self-play filter to the balance query, and decide whether the row should carry
   `IsRated` at all.
4. C-7-8. One-line pool filter.
5. C-7-6. Concurrency token plus a migration, alongside the deep audit's `RatingHistory(GameId, UserId)`
   unique index, since both are schema work on the same two tables.
6. C-7-1, the part that is mechanical: correct the `LeaderboardCache.cs:46-49` comment so it stops
   asserting behaviour that does not exist.

Your decision:

7. C-7-1, the part that is a design choice: whether RD inflation for inactive players ships before
   launch, or whether the "established" leaderboard gets a recency condition instead. This changes what a
   rating means, so it is the owner's call, not a bug fix.
8. C-7-7. Whether a moderator gets a rating-reset action at all. It is a new admin write path on the one
   number players accumulate, and doing nothing is a defensible answer at this size, as long as it is a
   chosen answer.

---

## C-8 — PGN, replay, spectate, custom
Date: 2026-08-31
Packet: docs/audits/INDEX.md

### Verdict

The PGN format is the best-specified artefact in this repository and the code matches it: the header
order, the escaping, the `%kcfg` key order, the FEN-derived move numbering and the 80-column wrap are
all pinned by `PgnExportTests`, and the `ToKeyValues`/`ApplyKey` pair genuinely round-trips all twenty
keys. Display names cannot inject into a tag pair, because both the register path and the OAuth
derivation reduce a name to `[A-Za-z0-9 _.-]` before it is ever stored. The spectator surface is
similarly deliberate: `SpectatorMoveDto` withholds the 6e.2 anti-cheat telemetry, `JoinAsSpectator`
never touches the disconnect-forfeit tracker, and the connection budget bounds anonymous watchers at
20 per IP and 600 in total. Nothing here leaks a live player's private information.

The soft edge is everything *around* the record rather than the record itself. Three themes. First,
**a user-authored ruleset now reaches a second human**: the custom builder ships a "challenge a
friend" button that carries the full config, which invalidates the "self/bot-only, so it only
corrupts the creator's own data" premise on which ENG-1 downgraded the missing config bounds to
Medium. Second, **the custom ruleset is never shown to anybody except its author** — the accept page
prints the word "custom" and nothing else, and the replay's `EconomyDto` omits five of the twenty
keys the PGN carries, so the one game shape a reader most needs explained is the one nothing
explains. Third, **reconstruction fails quietly**: both builders deliberately truncate a corrupt move
log rather than 500, which is right, but they emit no log line, no metric and no field on the
response, so the file still claims a result its movetext never reaches and the maintainer never finds
out a row rotted.

Total: **0 Critical, 1 High, 3 Medium, 6 Low.**

### Findings

#### C-8-1 — A user-supplied ruleset reaches a stranger through challenges, which retires the premise behind the missing config bounds
- Severity: **High**
- Where: `ui/app/[locale]/custom/page.tsx:510` (`createChallenge("custom", "Random", config)`),
  `src/KiriChess.Server/Games/ChallengeEndpoints.cs:239` (`GameEndpoints.BuildCustomConfig(req.Custom)`),
  `src/KiriChess.Server/Games/ChallengeEndpoints.cs:184-188` (accept builds the game from
  `challenge.CustomConfig.Clone()`), against `src/KiriChess.Engine/Config/KiriChessConfig.cs:115-125`
- Impact: `ENG-1 #1` rated the unbounded, overflow-prone economy multiply chain **Medium** on the
  stated grounds that "custom games are self/bot-only today, so this affects only the creator's own
  data, not a stranger's". Phase 9d.8's challenge path makes that false: `POST /api/challenges` with
  a `Custom` body stores the config on the row and `AcceptChallengeAsync` hands it to
  `registry.CreateAsync` for **two different players**. The browser is the only thing enforcing sane
  numbers (`custom/page.tsx` caps at 7200/100/50/20/10 per control); `Validate()` enforces
  non-negativity and nothing else, so a hand-rolled request produces a game whose first capture
  sign-flips through int32, or whose clock starts at zero — for someone who is not the author. Rated
  play is untouched (challenges are always unrated), so this costs a stranger a wasted game and their
  trust in the clock, not a rating. The bounds fix itself belongs to ENG-1 #1 and C-4-5; what is new
  here is that the reachability premise those two were rated against no longer holds.
- Fix: add per-field ceilings and a `StartTimeSeconds >= 1` floor to `KiriChessConfig.Validate()` so
  the engine, not the browser, is the boundary — which fixes both audits' findings at the same line —
  and re-rate ENG-1 #1 as stranger-facing rather than self-only.

#### C-8-2 — A custom challenge discloses no rules to the person accepting it, and the replay cannot state them afterwards
- Severity: **Medium**
- Where: `src/KiriChess.Server/Games/ChallengeDtos.cs:25-29` (`ChallengeDto` carries `Pool` and no
  config), `ui/app/[locale]/challenge/[token]/page.tsx:153` (`preset` is null for `"custom"`, so the
  `PoolBadge` at `:190` is skipped) and `:201` (renders the literal `t("customGame")`);
  `src/KiriChess.Server/Games/GameDtos.cs:59-74` (`EconomyDto`)
- Impact: the challenge flow exists precisely so the second player consents (9d.8, and
  `GameDtos.cs:45-53`'s own note that `OpponentId` was closed because it "lets any authenticated
  caller create a game in a stranger's name"). For a custom challenge that consent is to a person,
  never to the rules: the accept page shows one word. After the game, `ReplayResponse.Economy` omits
  `BaseIncrement`, `RepeatedPositionNullifiesTime`, `ThreefoldRepetitionIsDraw`, `FlagFallLoses` and
  `FlagVsInsufficientMaterialIsDraw`, so the replay of a custom game cannot say under which end
  conditions it was played — while the PGN of the identical game carries all twenty keys. The one
  game shape that has no published rules page is the one the API describes least.
- Fix: add the economy to `ChallengeDto` and render a rule summary on the accept page for
  `pool === "custom"`, and add the five missing fields (plus `BaseIncrement`, which C-4-3 already
  wants for the cost preview) to `EconomyDto` in the same pass.

#### C-8-3 — A corrupt move log truncates the replay and the PGN silently: no log, no metric, no signal on the wire
- Severity: **Medium**
- Where: `src/KiriChess.Server/Games/GameReplayBuilder.cs:45-59` and
  `src/KiriChess.Server/Games/PgnBuilder.cs:63-73` — both `catch { break; }` with no logger
- Impact: the decision to serve a prefix rather than 500 is right and well argued in both comments.
  What is missing is that nobody is ever told. The row's `Result` and `Termination` are still written
  from the entity, so a truncated export produces a file asserting `[Result "1-0"]` with
  `[KiriChessTermination "Checkmate"]` over a movetext that stops mid-game — a record that reads as
  authoritative and is not. `ReplayResponse` has no `truncated` field either, so the replay page draws
  a full result overlay on a partial game. Both classes are static with no `ILogger`, and there is no
  counter, so a jsonb row that stops replaying is invisible to the maintainer forever; the only
  coverage is `PgnExportTests.A_corrupt_move_log_truncates_instead_of_throwing`, which asserts the
  truncation and not the silence.
- Fix: pass an `ILogger` (or return a truncation flag the endpoint logs) and increment a counter on
  the `break`, and add a `TruncatedAtPly` to `ReplayResponse` so the client can say "this record is
  incomplete" instead of rendering a result it cannot show.

#### C-8-4 — Replays and PGNs are immutable but are re-derived per request with no cache headers
- Severity: **Medium** (trigger: one shared replay link reaching an audience, which is AD.3's plan)
- Where: `src/KiriChess.Server/Games/ReplayEndpoints.cs:29-114` (both routes) and
  `src/KiriChess.Server/Games/PublicApiEndpoints.cs:146-190`; the only control is
  `RateLimitPolicies.Replay`, 60/min per IP (`src/KiriChess.Server/Infrastructure/RateLimiting.cs:114`).
  No `Cache-Control`, `ETag` or output cache anywhere in `src/KiriChess.Server/`
- Impact: a finished game's replay is a pure function of an immutable row, and both routes filter
  `Result != Ongoing` precisely to guarantee that. Yet every request re-runs the engine over the whole
  move log — `ChessBoard.LoadFromFen` plus a full move generation per ply — plus two or three
  database round-trips to Neon, and the OG-image route (`replay/[id]/opengraph-image.tsx`) makes a
  crawler hit it too. The 60/min IP partition is the right shape against one abuser and does nothing
  against a link on Reddit, which is the exact traffic shape the launch plan is aiming for. This is
  the cheapest fix on the list: the response is already provably stable.
- Fix: set `Cache-Control: public, max-age=86400, immutable` and a weak `ETag` on both replay and PGN
  responses for a non-`Ongoing` game, so Cloudflare absorbs a shared link instead of Fly and Neon.

#### C-8-5 — `GET /api/games/{id}` and `/legal-moves` have no participant check, while their `/api/v1` twins do
- Severity: **Low** (trigger: any authenticated account, or any read-scoped API token, enumerating
  live game ids)
- Where: `src/KiriChess.Server/Games/GameEndpoints.cs:88-105` (both handlers call `registry.Get(id)`
  and return), against `src/KiriChess.Server/Games/PublicApiEndpoints.cs:101` and `:130`
  (`if (!IsParticipant(...)) return Results.NotFound()`), and against
  `src/KiriChess.Server/Games/RealtimeDtos.cs:77-79`
- Impact: the bot API is strictly stricter than the first-party API for the same data. The state
  itself is not secret — spectating is anonymous by design and `GET /api/games/live` publishes the
  FEN — so this is not a disclosure of anything a stranger could not already watch. What it does
  contradict is the one explicit decision on this surface: `SpectatorSnapshotDto` withholds legal
  moves because "shipping the engine's move-generator output to an unauthenticated caller is free
  surface with no consumer", and `/api/games/{id}/legal-moves` hands exactly that to any
  authenticated caller for any live game they are not in. Two endpoints doing the same job with two
  different answers is also how the next reader learns the wrong rule.
- Fix: apply the same `IsParticipant` guard `PublicApiEndpoints` already has to both first-party
  routes; the helper exists and needs no new logic.

#### C-8-6 — `engine.FromFen` sits outside the corrupt-row guard both builders otherwise apply
- Severity: **Low** (trigger: any row whose `ConfigSnapshot` or `StartFen` fails to round-trip — a
  hand-seeded row, a jsonb shape change, or a future rule variable added to `Validate()`)
- Where: `src/KiriChess.Server/Games/GameReplayBuilder.cs:33` and
  `src/KiriChess.Server/Games/PgnBuilder.cs:50`
- Impact: both files go out of their way to survive a corrupt move log because "an exception here
  would be permanent for that game" — and then call `engine.FromFen(config, startFen)` one line
  earlier with no guard at all. `FromFen` calls `config.Validate()` (which throws
  `InvalidOperationException` for a missing `MoveCostPerPiece[King]`, exactly the shape an older jsonb
  row would deserialize into after a config change) and then `ChessBoard.LoadFromFen`, which throws on
  a malformed FEN. Either becomes an unhandled 500 on an anonymous public route, permanently, for the
  one class of failure the rest of the method was written to survive.
- Fix: wrap the `FromFen` call in the same `catch` and return a minimal response (headers plus an
  empty movetext, which `An_empty_move_list_still_produces_a_valid_file` already proves is valid), or
  404 the game deliberately, rather than letting it 500.

#### C-8-7 — Spectators receive `MovePlayedDto.LegalMoves`, defeating the snapshot's stated omission
- Severity: **Low**
- Where: `src/KiriChess.Server/Games/GameActionExecutor.cs:300`
  (`Clients.Group(session.Id.ToString()).SendAsync("MovePlayed", payload)`),
  `src/KiriChess.Server/Games/RealtimeDtos.cs:17` (`LegalMoves` on the payload) and `:77-79`
- Impact: `GameHub.JoinAsSpectator` adds the anonymous connection to the same SignalR group as the
  players (`GameHub.cs:276`), so the very next `MovePlayed` push hands that connection the legal-move
  list the join snapshot deliberately refused to send. The information content is nil — the list is
  derivable from the FEN in the same payload — but the design decision recorded in `RealtimeDtos.cs`
  is not actually in force, and a reader who trusts that comment will draw a wrong boundary next time.
- Fix: either send a spectator-shaped `MovePlayed` without `LegalMoves` to a separate spectator group,
  or amend the `SpectatorSnapshotDto` comment to say the omission is cosmetic — the two must agree.

#### C-8-8 — The PGN's `%clk` picks the mover from FEN alternation, not from the persisted `MoveRecord.Mover`
- Severity: **Low**
- Where: `src/KiriChess.Server/Games/PgnBuilder.cs:77` (`Ply` captures `record.Mover`) and `:220`
  (`var moverTimeAfter = isBlackToMove ? ply.BlackTimeAfter : ply.WhiteTimeAfter`)
- Impact: `Ply.Mover` is stored and then never read — the movetext derives whose clock to print from
  `isBlackToMove`, which is seeded from the FEN and toggled per ply. For a healthy game the two always
  agree. For the exact class of row the truncation guard above exists for, they need not, and the
  failure mode is a `%clk` naming the wrong player's clock rather than a truncation that stops. It is
  also a dead field the compiler cannot flag, so the safer of the two sources is present and unused.
- Fix: read `ply.Mover` for the `%clk` selection and keep the FEN-derived alternation only for the
  move number, which is what it is actually for.

#### C-8-9 — The Elo lookup assumes one `RatingHistory` row per (game, user) with no index enforcing it
- Severity: **Low** (trigger: any second history row for the same game and user — a rating
  recomputation, a retried persist, or a future correction path)
- Where: `src/KiriChess.Server/Games/ReplayEndpoints.cs:86-90` and
  `src/KiriChess.Server/Games/PublicApiEndpoints.cs:172-176`
  (`ToDictionaryAsync(h => h.UserId, h => h.ValueBefore, ct)`), against
  `src/KiriChess.Server/Data/KiriChessDbContext.cs:160-168`, which declares only a non-unique
  `(UserId, Pool, CreatedAt)` index
- Impact: a duplicate key throws `ArgumentException` inside the handler, which is a 500 on an
  anonymous public download rather than a missing `[WhiteElo]` tag. The neighbouring `db.Users`
  dictionary is safe because its key is the primary key; this one rests on an invariant no constraint
  holds. No path produces a duplicate today (`RatingService.ApplyAsync` skips solo and unrated games),
  so this is a latent assumption, not a live bug.
- Fix: either add a unique index on `(GameId, UserId)` to `RatingHistory`, or make the projection
  duplicate-tolerant with a `GroupBy`/`ToLookup`; the index is the better of the two, since the
  duplicate would be a real data problem worth failing on at write time.

#### C-8-10 — A replay can never reproduce the `FlagFall` event the players actually saw
- Severity: **Low**
- Where: `src/KiriChess.Server/Games/GameReplayBuilder.cs:41-43` and `:72` (events kept from a
  `thinkTimeSeconds: 0` re-apply), against `src/KiriChess.Engine/KiriChessEngine.cs:316-318`
  (`FlagFall` is emitted only when `moverRawClock <= 0`)
- Impact: the class comment says the replay's clocks are discarded but "the position and the itemised
  events are kept". For every event but one that is true. A flag that fell because the mover's *think
  time* ran the clock out cannot be re-derived at zero think time, so the final ply's `Events` array
  omits the `FlagFall` a viewer would expect to see next to a game whose `Termination` is `Flagfall`.
  The reverse also holds: a flag driven by the economy alone does reproduce, so the event is present
  sometimes and absent sometimes for the same termination, which is worse than consistently absent.
- Fix: state the exclusion in `GameReplayBuilder`'s doc comment the way `SpectatorMoveDto` states
  its own, or synthesise the `FlagFall` event on the last ply from `entity.Termination` so the
  breakdown agrees with the result banner above it.

### Already covered

- **No upper bound on any rule variable, and int32 overflow in the economy multiply chain** is
  **ENG-1 #1**. C-8-1 does not re-report the defect; it reports that the reachability premise ENG-1
  rated it against ("self/bot-only") is no longer true.
- **`StartTimeSeconds` floors at 1 in the browser and 0 on the server** is **C-4-5**. Same line,
  same fix, and C-8-1 is the reason it now matters to somebody other than the author.
- **`EconomyDto` omitting `BaseIncrement`, so the cost preview cannot see it**, is **C-4-3**.
  C-8-2 asks for the same field plus the four end-condition booleans, for a different reader.
- **`MoveList` rendering a ply index as a chess move number** is **C-11-3**; it affects the replay
  and watch pages equally and is not re-reported here.
- **Unclamped persisted deltas beside clamped clocks**, which is why a replay's `%kc` deltas and its
  `TimeAfter` figures cannot be reconciled by subtraction, is **C-2-10** with **C-4** as an overlap.
- **HTTP move/resign bypassing `GameCommandRouter`**, which touches the same
  `GameEndpoints.cs:88-165` block as C-8-5, is **BE-2 #1**. The two fixes are compatible and belong
  in one pass.
- **`Enum.IsDefined` missing on `MoveCostMode` in `Validate()`** is **ENG-1 #3**; `BuildCustomConfig`
  parses the enum defensively before `Validate()` runs, so the challenge path inherits that guard.
- **Spectator-plus-engine as a fair-play channel** — the spectator feed carries the live FEN with no
  broadcast delay — is **S-2**'s packet, not a defect in this code.
- **The hub connection budget being per-instance and in-memory** is capacity work (**I-6**/**C-5**),
  not a spectator-correctness defect; it bounds anonymous watchers correctly on one machine today.
- Verified clean, worth recording so the next audit does not re-derive them: display names cannot
  inject a PGN tag pair (register goes through `ContentPolicy.CheckDisplayName`'s ASCII charset,
  OAuth through `DisplayNameService.Sanitize`'s `[A-Za-z0-9 _.-]` filter, so `EscapeTagValue` is
  belt-and-braces); `%kcfg` round-trips all twenty keys through `ApplyKey` with no gaps;
  `GameSession.CheckFlagFall` honours `Config.FlagFallLoses`, so a custom game cannot be flagged
  against its own rules; a custom config cannot produce a genuinely unwinnable game, because the
  fifty-move rule is auto-detected by the board and survives every combination of the four booleans;
  and `Frontend__BaseUrl` is a required deployed secret, so `[Site]` is never the `"?"` fallback in
  production.

### Action list

1. C-8-1: add per-field ceilings and a `StartTimeSeconds >= 1` floor to
   `KiriChessConfig.Validate()`. One method, closes ENG-1 #1 and C-4-5 at the same time, and is the
   only finding here that lets one player hand another a broken clock.
2. C-8-4: `Cache-Control` and `ETag` on the four replay/PGN routes. Smallest change on
   the list and the one that matters most if a launch link travels.
3. C-8-3: log and count the truncation in both builders, and add a truncation field to
   `ReplayResponse`. Without this, the failure mode C-8-6 also describes stays invisible.
4. C-8-6: extend the existing `catch` upward over `engine.FromFen` in both builders.
   Same two files as item 3, do them together.
5. C-8-2: put the economy on `ChallengeDto` and render it on the accept page; add the
   five missing fields to `EconomyDto` alongside C-4-3's `BaseIncrement`.
6. C-8-5: add the `IsParticipant` guard to `GameEndpoints`' `{id}` and `/legal-moves`.
   Best done in the same pass as **BE-2 #1**, which rewrites the same handlers.
7. C-8-9: unique index on `RatingHistory (GameId, UserId)`, with the migration reviewed
   against **C-12**'s schema pass rather than added in isolation.
8. C-8-7, C-8-8, C-8-10: three comment-or-one-line corrections. Batch them; each is a
   case of a stated intent and the code disagreeing, and each costs more to rediscover than to fix.
9. **Owner** — decide whether the custom builder's "challenge a friend" button should exist at all
   before item 1 lands. Removing it restores ENG-1's original premise for free; keeping it is
   defensible, and item 1 is then not optional.

---

## C-9 — Drift

Date: 2026-08-29
Packet: docs/audits/INDEX.md

### Verdict

The generated-artifact drift guards in this repo are genuinely thorough: every generated file
(`docs/api/openapi.json`, `docs/api/openapi-public.json`, `ui/lib/api.generated.ts`,
`ui/lib/rules.generated.ts`, `docs/RULES.md`, `config/cost-vectors.generated.json`) has a freshness
test, most run in CI as a hard gate, and the engine's own copy of the rule config is an
`<EmbeddedResource>` pointing at the one file rather than a duplicate. `HubContract.cs`,
`KiriChessMetrics`, the Better Stack Terraform, the CI workflow set, the legal documents and the
pool labels each have a dedicated contract test.

The drift the gates do **not** cover is all at the seams where the guards were never pointed:
hand-written code that consumes a generated contract without itself being generated
(`ui/lib/api.ts`'s request layer), documents that mirror a spec by hand with no equality check
(`.cursor/rules/*.mdc`, `.env.example`, the two `fly.backend*.toml`), and a guard scoped to one
message namespace when the risk spans several (`rulesCopy.test.ts` vs the `learn` catalogue). None
of these is a live drift today — every pair I checked is currently in sync — so each finding is
trigger-based: the next edit that touches one side and forgets the other.

### Findings

#### C-9-1 — `ui/lib/api.ts` request paths and methods are never checked against the OpenAPI document

- Severity: Medium
- Where: `ui/lib/api.ts:178` (`request<T>()`), `:195`, `:205`, plus ~70 literal `/api/...` path
  strings throughout the file; contrast `ui/lib/api.generated.ts` (generated, gated by
  `npm run gen:api:check`).
- Impact: `gen:api:check` only proves `api.generated.ts` was regenerated from `openapi.json`, and
  `api.generated.ts` is imported by `api.ts` **for types only** — every actual HTTP call is a
  hand-written `fetch(\`${base}${path}\`)` with a string literal path and a string method. So a
  backend change that renames a route, changes its verb, or renames a query parameter while keeping
  the response DTO shape identical passes `OpenApiContractTests`, passes `gen:api:check`, passes
  `tsc --noEmit` (the DTO type is unchanged), and only fails at runtime as a 404/400 — caught solely
  if an E2E spec happens to exercise that exact call. This is the single largest hole in the drift
  net: the wire contract is generated and gated on the server side and on the type side, but the
  client's URL/verb/query surface is neither.
- Fix: Add a Vitest test that extracts every `/api/...` literal and its method from `api.ts` and
  asserts each `(path, method)` pair resolves to an operation in `docs/api/openapi.json` (paths
  normalised for `${id}` → `{id}`). Cheaper than generating the request layer and closes the gap
  that matters. A follow-up option is to move `api.ts` onto a generated client (e.g.
  `openapi-fetch`) so the paths come from the document too.

#### C-9-2 — No gate keeps `.claude/rules/*.md` and `.cursor/rules/*.mdc` in sync

- Severity: Low (trigger: any edit to a rules file that forgets its twin)
- Where: `.claude/rules/{backend,engine,frontend,project,realtime,security,styling,testing}.md` vs
  `.cursor/rules/*.mdc`; the instruction that is not enforced is `ui/lib/styling.test.ts:16-17`
  ("update `.claude/rules/styling.md` and its `.cursor/rules/styling.mdc` mirror in the same
  commit") and `.claude/rules/styling.md`'s own closing paragraph.
- Impact: Both trees are checked into git and are standing instructions for two different
  tools. All eight pairs are currently in sync (bodies identical after normalising line
  endings and the deliberate `[foo.md](foo.md)` → `` `foo.mdc` `` cross-reference rewrite). Nothing
  keeps them that way: `styling.test.ts` references the mirror rule in a comment but does not test
  it, and the other seven pairs carry no same-commit instruction at all. A reader of `.claude/rules/`
  and a reader of `.cursor/rules/` would then be working from different conventions — exactly the class of hand-mirrored-document
  drift the other contract tests exist to prevent.
- Fix: Extend `styling.test.ts` (or a new small test) to read all eight `.md`/`.mdc` pairs, strip
  frontmatter and the `> Mirrors …` line, apply the known `.md`→`.mdc` link substitution, normalise
  CRLF, and assert body equality.

#### C-9-3 — `rulesCopy.test.ts` guards only the `rules` message namespace, not `learn`

- Severity: Low (trigger: a `learn.*` string that hardcodes a priced quantity instead of an ICU
  placeholder)
- Where: `ui/lib/rulesCopy.test.ts:24-33` (`rulesCopy()` reads only `catalogue.rules`);
  `ui/messages/en.json` `learn.*` (e.g. `stepCapturesStealBody`, `capturesBurnCapturedPayoff`).
- Impact: `/learn` teaches the same clock economy as `/rules` — capture presets, move tolls, check
  chip, net clock effect — and its strings live in `messages/*.json` under `learn`. The test that
  stops a retunable number from being frozen into prose ("a priced quantity has to arrive as an ICU
  placeholder") iterates the `rules` namespace only. The `learn` strings currently thread values as
  placeholders (`{steal1}`, `{net}`, `{loss}`), so there is no live drift, but a future edit that
  writes "capturing gives you 10 seconds" into a `learn` string would survive both a
  `config/kirichess.rules.json` retune and this guard.
- Fix: Iterate every namespace that can quote an engine number (`learn` at minimum), or generalise
  the test to scan the whole catalogue and rely on the placeholder-blanking already in place.

#### C-9-4 — Stripe is the only external integration with no `appsettings.json` section and no `.env.example` entry

- Severity: Low (trigger: a contributor or fork bringing up the donations path)
- Where: `src/KiriChess.Server/Account/StripeWebhookEndpoints.cs:31`
  (`config["Stripe:WebhookSecret"]`, read inline, no options record); `src/KiriChess.Server/appsettings.json`
  (has stub sections for `Turnstile`, `Vapid`, `Email`, `Sentry`, `Redis`, `Authentication:Google`
  — none for `Stripe`); `.env.example` (no `Stripe__*` line).
- Impact: Every other fail-closed integration follows one pattern — an empty stub in
  `appsettings.json` plus, usually, a commented `.env.example` line explaining "empty = disabled".
  Stripe breaks it: the only place `Stripe:WebhookSecret` is written down is
  `docs/ops/PATRON-BADGE-GOLIVE.md`. A contributor grepping `appsettings.json` or `.env.example` to
  learn the config surface will not find the donations webhook at all, and the endpoint's
  "not configured" path only logs an error server-side.
- Fix: Add `"Stripe": { "WebhookSecret": "" }` to `appsettings.json` and a commented
  `# Stripe__WebhookSecret=` block to `.env.example` with the same "empty = webhook rejected"
  wording used for `Email__ApiKey` and `Vapid__*`.

#### C-9-5 — `.env.example` omits `Turnstile__SecretKey` and `Authentication__Google__*`

- Severity: Low (trigger: a fork wanting bot protection or Google sign-in on)
- Where: `.env.example` (no `Turnstile__*`, no `Authentication__Google__*`); both are read in
  `src/KiriChess.Server/Program.cs` (`TurnstileOptions.From`, `ExternalAuthOptions.From`) and both
  fail closed when empty.
- Impact: Distinct from O-5-3 (which asked for a "minimum for a real instance" grouping of keys
  that *are* listed): these two keys are read by code, follow the documented "empty = disabled"
  convention, and simply are not in the file. `docs/ops/PROVIDER-ACCOUNTS.md` notes the Google keys
  are the "only provider whose keys are genuinely absent" from `fly secrets` — that gap is mirrored
  in `.env.example` with no comment saying it is deliberate.
- Fix: One commented line each, matching the `Sentry__Dsn` / `Vapid__*` style already in the file.

#### C-9-6 — Staging and production `fly.backend*.toml` parity is unguarded; the visible symptom is a `[metrics]` vs `[[metrics]]` split

- Severity: Low (trigger: a change to one fly config that should have been mirrored to the other)
- Where: `fly.backend.toml:93` (`[metrics]`, single table) vs `fly.backend.staging.toml:51`
  (`[[metrics]]`, array of tables); more generally all four `fly.*.toml` files.
- Impact: Both `[metrics]` forms are accepted by Fly, so this is cosmetic — but it is the tell that
  the two backend configs are hand-maintained in parallel with nothing checking that their only
  differences are the intended ones (documented in-file: app name, `ASPNETCORE_ENVIRONMENT`,
  `min_machines_running`, `auto_stop_machines`, `auto_start_machines`). `CiWorkflowDriftTests`
  guards that workflow files exist and are SHA-pinned; there is no equivalent asserting
  staging/prod infra config only diverges on an allowlist. A prod-only tuning change (a new
  `[env]` var, a concurrency bump, a health-check tweak) can silently skip staging, which is the
  environment meant to take the first hit.
- Fix: A test that parses the four `fly.*.toml` files and asserts `fly.backend.toml` vs
  `fly.backend.staging.toml` (and the two UI configs) differ only on an allowlisted key set, each
  allowlist entry carrying a one-line reason — the `CiWorkflowDriftTests` idiom.

### Already covered

- **OpenAPI, first-party and public**: `OpenApiContractTests.Committed_document_is_up_to_date`,
  `Committed_public_document_is_up_to_date`, plus coverage/enum/required guards. Both `openapi.json`
  and `openapi-public.json` are gated; `gen:api:check` gates the TS types on top. Not re-reported.
- **Hub wire surface**: `HubContractTests` (call sites + `ui/` `conn.on`/`invoke`) and
  `OpenApiContractTests` via `HubPayloadDocumentTransformer`. Not re-reported.
- **Rule config → `docs/RULES.md` + `ui/lib/rules.generated.ts`**: `npm run gen:rules:check`, CI-gated
  (`.github/workflows/ci.yml:93`). Engine embeds the same file via `<EmbeddedResource>`
  (`KiriChess.Engine.csproj:20`), so no engine-side copy to drift.
- **Client economy mirror (`ui/lib/costPreview.ts`)**: `ui/lib/costVectors.test.ts` +
  `CostVectorConformanceTests.cs` against `config/cost-vectors.generated.json`. Covered — see also
  deep-audit ENG-1 and `.claude/rules/engine.md`.
- **Legal docs vs code**: `LegalDocumentDriftTests` (packet L-1, done). Stripe's absence from
  ROPA/RETENTION is `$1-7` / L-1-5 / L-3-2, not re-reported here.
- **Metrics names / Better Stack**: `MetricsContractTests`, `BetterStackAlertingDriftTests`.
- **CI workflow set**: `CiWorkflowDriftTests` (existence, triggers, SHA-pinning).
- **Styling spec mechanical half**: `ui/lib/styling.test.ts` (readiness A11Y / STYLING-CONFORMANCE.md
  cover the judgement half).
- **`.env.example` "which values are mandatory" grouping**: O-5-3 (Low). C-9-4/C-9-5 are the
  distinct "keys read by code but entirely absent" angle.

### Action list

1. C-9-1: add a Vitest test asserting every `(path, method)` in `ui/lib/api.ts` exists in
   `docs/api/openapi.json`. Highest value — closes the one Medium.
2. C-9-2: extend `ui/lib/styling.test.ts` to assert all eight `.claude/rules` ↔
   `.cursor/rules` pairs are body-identical after the known normalisations.
3. C-9-3: widen `rulesCopy.test.ts` to the `learn` namespace (and any other namespace
   quoting engine numbers).
4. C-9-4 / C-9-5: add `Stripe`, `Turnstile__SecretKey`, `Authentication__Google__*` to
   `appsettings.json` stubs and/or `.env.example` with fail-closed comments.
5. C-9-6: add a `fly.*.toml` staging/prod parity test with a reasoned allowlist; fold the
   `[metrics]`/`[[metrics]]` normalisation into it.
6. No standing-position reversals. No production code behaviour changes — all six are test/config/doc
   additions.

---

## C-10 — Tests

Date: 2026-08-29
Packet: docs/audits/INDEX.md, table 3

### Verdict

Coverage is broad and, in the places that would be expensive to get wrong, deliberate: `IGameClock`
discipline, the three-way rule mirror, distributed live-state, API-token scheme isolation, rate-limit
partitioning, counters, web push, and the legal/OpenAPI/metrics drift guards all have real suites,
most CI-gated. The readiness **TEST** domain has not been run (`docs/audits/readiness/` does not
exist yet), so this packet stands alone; merge it in when that domain runs.

The gaps cluster in one shape: **logic that is reached only through a React component render or an
HTTP endpoint, where the underlying service is tested directly but the wiring around it is not.**
`ExternalLoginService` is tested; the `/external/exchange` endpoint that calls it is not. `Glicko2`
is tested; one of its two volatility branches is not. `Board`'s style helper is tested; the move
handlers next to it are not. `AuthProvider`'s whole refresh/adopt/logout machine has no test at all.
None of these is a live failure today, but each is a place where a regression lands silently: green
unit tests, green `tsc`, and a defect only a human clicking through would see.

Four items below (2, 6, 7, and the correctness half of 5) are test gaps behind findings the deep
audit already filed. They are ranked here as *missing tests*, per this packet's job; the fixes stay
owned by C-1/C-4/C-8.

### Findings

#### C-10-1 — `/api/auth/external/exchange` has no banned-account and no 2FA test
- Severity: High
- Where: `tests/KiriChess.Server.Tests/ExternalLoginTests.cs` (drives `ExternalLoginService` /
  `ExternalLoginCodeService` directly, never the token-minting endpoint);
  `src/KiriChess.Server/Auth/ExternalAuthEndpoints.cs:101-128` (`/external/exchange`).
- Impact: Every other token-minting path is tested against a ban — `AuthLifecycleTests`,
  `TwoFactorTests`, refresh in `TokenServiceTests`. The Google exchange endpoint is not, and it is
  exactly the endpoint the deep audit found skips `SanctionPolicy.BlockingSanctionAsync` entirely
  (IMPROVEMENT_PLAN batch 1 finding 1, graded High). There is also no test that an account with 2FA
  enabled is handed a challenge rather than a full session on exchange. A regression that widens
  either gap passes the whole suite.
- Fix: Add two `ExternalLoginTests` cases exercising the exchange endpoint end to end: one where the
  target account carries an active `SanctionScope.Account` ban and the call must not mint tokens, one
  where the account has 2FA enabled and the call must return the challenge shape. Table-drive the ban
  assertion over all four token-minting endpoints while adding it (batch 1 item 3).

#### C-10-2 — `AuthProvider.tsx` refresh / adopt / logout state machine is entirely untested
- Severity: High
- Where: `ui/components/AuthProvider.tsx:122-200` (hydrate, avatar backfill, `setOnTokenRefreshed`
  re-persist, `setOnRefreshFailed` → `clearSession` + redirect), `:216-289` (`login` 2FA fork,
  `adoptSession` / `completeExternalLogin`, best-effort `logout` vs awaited `signOutEverywhere`),
  `:109-113` (`normalizeRoles` corruption guard). No `AuthProvider.test.tsx` exists.
- Impact: This is the client half of the session model `docs/decisions/AUTH.md` scrutinises most —
  token rotation on silent refresh, dead-refresh-token teardown, the "old pair is dead the moment the
  server responds" contract on `adoptSession`. A bug that fails to re-persist a rotated refresh token
  (reload reuses a dead token → forced logout) or that lets `signOutEverywhere` clear locally before
  the server call resolves would not be caught by anything. `AuthProvider` is a provider, so E2E only
  sees it indirectly.
- Fix: Add a Vitest + Testing Library suite mocking `@/lib/api`: assert `setOnTokenRefreshed`
  re-writes `localStorage`, `setOnRefreshFailed` clears and pushes `/login`, `login` returns the
  `twoFactor` object without persisting when the API returns a challenge, `signOutEverywhere` does
  not clear the session when `apiLogoutAll` rejects, and `normalizeRoles` survives a corrupted blob.

#### C-10-3 — `Board.tsx` move handlers are untested; only the style helper is covered
- Severity: High
- Where: `ui/components/Board.tsx:115-163` (`handleSquareClick`: castling-by-rook-click at
  `:127-141`, promotion default-to-queen at `:147-152`, legal-move gate at `:154`), `:169-191`
  (`handlePieceDrop`: promotion piece from the dropped glyph, legal gate at `:185`).
  `ui/components/Board.test.tsx` tests only the exported pure function `squareStylesForSelection`.
- Impact: `Board` is the primary way a human makes a move. Its accessible sibling
  (`AccessibleBoard.test.tsx`) is tested; `Board` is not. `critical-path.spec.ts` drags one ordinary
  move, so plain moves have some E2E cover, but castling by clicking king-then-rook, promotion
  defaulting to a queen, and the silent rejection of an illegal target are all unexercised at any
  layer. A refactor of the click handler could break castling and ship green.
- Fix: Either export the move-resolution logic (input: `fen`, `legalUci`, `from`, `to`, dropped
  piece → `uci | null`) and unit-test it directly, or add a Testing Library render test that fires
  `onSquareClick` / `onPieceDrop` and asserts the `onMove` argument for: king→rook kingside and
  queenside, a pawn reaching the last rank, and an illegal target producing no call.

#### C-10-4 — No `MatchNoShowWatcherTests.cs`
- Severity: Medium
- Where: `src/KiriChess.Server/Games/MatchNoShowWatcher.cs` (no test file); the sibling
  `GameConnectionTracker.RunGraceAsync` is covered by `AbandonmentPolicyTests` and the distributed
  suites.
- Impact: The watcher aborts a matched game as a no-show when a player never calls `JoinGame`.
  Nothing tests that it does *not* fire when both players did join, and nothing tests the
  cluster-aware path — `:57-58` calls the synchronous `IsConnected`, while `GameConnectionTracker`
  and `GameRecoveryService` were both moved to `IsConnectedAsync` precisely so a player on another
  instance does not read as absent (IMPROVEMENT_PLAN batch 3 item 2). A wrong answer here aborts a
  live game out from under a present player.
- Fix: Add `MatchNoShowWatcherTests` with `Matchmaking:JoinDeadlineSeconds` set to a sub-second
  value: assert no abort when both connections are registered, an abort when one never joins, and
  (mirroring `TwoInstanceGameTests`) no abort when the "missing" player is connected to a second
  instance. The last case fails until the `IsConnectedAsync` switch lands.

#### C-10-5 — No server-level test of the flag-vs-insufficient-material draw
- Severity: High (cross-reference: C-4-1 owns the fix)
- Where: only `tests/KiriChess.Engine.Tests/GameEndTests.cs:43-49` exercises the rule;
  `src/KiriChess.Server/Games/GameSession.cs` `CheckFlagFall` and `FlagFallMonitor` never consult
  material. `EconomyInvariantTests` (`tests/KiriChess.Engine.Tests/Generative/`) structurally cannot
  reach the server flag path.
- Impact: `flagVsInsufficientMaterialIsDraw` ships in all six rated pools and the result the live
  path invents on an ordinary time-out goes straight into Glicko-2. This is the single
  highest-value missing *server* test in the repo.
- Fix: Per C-4-1 — once both flag paths resolve through one code path, add a server-level test: K+Q
  versus a lone king, the K+Q side flags, assert the game ends drawn rather than lost. Do not add
  the test against the current split implementation.

#### C-10-6 — Glicko-2's volatility "expected result" branch is unexercised
- Severity: Medium
- Where: `src/KiriChess.Server/Ratings/Glicko2.cs:62-67` (the `else` / bracket-widening branch of
  step 5); `tests/KiriChess.Server.Tests/Glicko2Tests.cs` — all three cases feed large upsets, which
  take the `if d2 > phi2 + v` branch at `:58-61`.
- Impact: The `else` branch is the common case: an established player whose results roughly match
  their rating. The worked-example test proves the paper's numbers for a surprising result set; the
  everyday path through the volatility solver has no assertion on it. A sign or bound error there
  would pass every current test. Cross-reference: IMPROVEMENT_PLAN batch 11 item 6.
- Fix: Add a `Glicko2Tests` case with a low-RD player (e.g. 1500 / 50 / 0.06) drawing a same-rated
  opponent, assert `d2 <= phi2 + v` holds for the inputs, and pin the resulting volatility to a
  tolerance so the widening loop stays covered.

#### C-10-7 — `KiriChessConfig.Validate()` has no upper-bound or enum-membership test
- Severity: Medium (cross-reference: C-8 owns "custom rule injection cannot break clocks")
- Where: `tests/KiriChess.Engine.Tests/ConfigLoadingTests.cs` covers the non-negativity throws at
  `src/KiriChess.Engine/Config/KiriChessConfig.cs:101-125`; nothing covers a ceiling (the economy
  math at `KiriChessEngine.cs:208`, `:228`, `:236` is unchecked `int` and wraps on a large value) or
  `MoveCostMode` reaching `Validate()` as an out-of-range cast (the text parser at `:238`/`:310`
  guards its own path, `Validate()` does not).
- Impact: A custom config with a huge `CaptureMult` or piece value sign-flips the clock delta rather
  than being refused at `NewGame`, contradicting the guarantee in `.claude/rules/engine.md`. Custom
  games are self/bot only, so blast radius is the creator's own data, but the engine is supposed to
  fail loud.
- Fix: Per `.claude/rules/engine.md` a changed rule variable needs a cost-vector fixture entry, not
  only a unit test. Add a `ConfigLoadingTests` case per new ceiling once `Validate()` grows one
  (batch 5 items 1-2), plus one `CostVectorFixture` entry showing a near-ceiling config still prices
  sanely.

#### C-10-8 — `ui/lib/bot.ts` has no tests
- Severity: Medium
- Where: `ui/lib/bot.ts` — `getEngineMove` Elo→Skill-Level mapping (`:52-58`, "keep in lockstep with
  `simulator/KiriChess.Simulator/BotSkill.cs`"), Elo→depth ladder (`:60-68`), sub-800 random-blend
  ratio (`:39-45`), `getRandomMove` (`:77-81`).
- Impact: The lockstep comment is the tell: this mapping and the simulator's C# port
  (`BotClockTests.cs` pins the clock port, not this one) can drift with nothing to notice, and bot
  strength is what a new player's first games are against. `getRandomMove` returning `""` on an
  empty list is an untested edge. Separately, the leaked-singleton / single global resolver bug
  (IMPROVEMENT_PLAN batch 6 item 1) needs a test to pin whatever fix lands.
- Fix: Extract the pure Elo→(skillLevel, depth, randomChance) mapping into a testable function and
  assert boundary values (400, 799, 800, 3000) against `BotSkill.cs`'s expected numbers; test
  `getRandomMove` on an empty and a single-element list.

#### C-10-9 — The `safeNextPath` copy in the auth callback is untested
- Severity: Low (trigger: an open-redirect regression in the OAuth return path)
- Where: `ui/app/auth/callback/page.tsx:42-49` — a local reimplementation of
  `ui/lib/challengeLink.ts`'s `safeNextPath`, missing the control-character strip; the shared
  version is covered by `ui/lib/challengeLink.test.ts`, the copy by nothing. Cross-reference:
  IMPROVEMENT_PLAN batch 8 item 2.
- Impact: The callback runs on a URL an attacker can shape. The tested shared helper rejects
  `//evil.com`, `\t`-smuggled paths and absolute URLs; the untested copy is close but not identical,
  and only the copy runs on the OAuth return.
- Fix: Best is to delete the copy and import the shared helper (as `login/page.tsx` already does),
  which folds it under the existing test. If the copy must stay, give it its own test with the same
  fixture list as `challengeLink.test.ts`.

#### C-10-10 — Three profile cards fetch on mount with no stale-response guard and no test
- Severity: Low (trigger: a slow response landing after the user navigated or switched context)
- Where: `ui/components/DetectionReportCard.tsx:163-184` (no `cancelled` guard — a slow response can
  render onto a different moderator dossier), `ui/components/SessionsCard.tsx:45` and
  `ui/components/TwoFactorCard.tsx:73` (fetch unconditionally on mount, unlike sibling cards that
  guard on `token`). `MatchHistoryCard.test.tsx` exists and pins the `cancelled` pattern
  (`MatchHistoryCard.tsx:71,91-93`); these three have neither the guard nor a test. Cross-reference:
  IMPROVEMENT_PLAN batch 6 item 3.
- Impact: `DetectionReportCard` is the sharpest: a moderator opening dossier B before dossier A's
  request resolves can see A's detection data under B's heading. The other two are cosmetic flicker.
- Fix: Apply the `cancelled` flag pattern from `MatchHistoryCard`, then add a render test that
  resolves a stale promise after unmount / prop change and asserts nothing is written.

### Already covered

- **Clock economy**: `MonotonicClockTests`, `LatencyCompensationTests` (11 cases),
  `EconomyInvariantTests` (10 properties x 6 pools), engine `Rule1`–`Rule4` suites,
  `CostVectorConformanceTests` + `ui/lib/costVectors.test.ts`. The one real gap is C-10-5 (server
  flag path), owned by C-4-1.
- **Public / bot API and API tokens**: `PublicApiTests` — 14 cases: issue/list/revoke, ten-token
  cap, scheme isolation (`/api/v1` vs JWT), scope enforcement, `logout-all` does not touch API
  tokens, ban+erasure revoke them, per-token-id rate bucket, hub `JoinQueue` in the bot pool. Not
  thin. Not re-reported.
- **Hub auth overlay**: exercised through `PublicApiTests`
  (`Api_token_JoinQueue_pairs...`/`jwt_cannot_join_bot_keys`) and `HubContractTests` /
  `HubConnectionBudgetTests`. Not thin.
- **Counters**: `CounterEndpointTests` — 11 cases: atomic increment, allowlist, source
  normalisation, `direct` collapse, no-auth beacon, admin-only traffic route, no identifiers in the
  response. Not thin.
- **Web push**: `PushNotificationTests` (9), `NotificationPreferenceTests` (4) — subscribe round
  trip, gone-subscription pruning, challenge/rematch push fan-out, forged unsubscribe token. Not
  thin.
- **Drift guards**: `OpenApiContractTests`, `HubContractTests`, `MetricsContractTests`,
  `BetterStackAlertingDriftTests`, `CiWorkflowDriftTests`, `LegalDocumentDriftTests`,
  `PoolLabelDriftTests`, `gen:rules:check`, `gen:api:check` — see packet C-9 for what these do
  **not** cover.
- **External login policy**: `ExternalLoginTests` covers linking, the unverified-email
  account-takeover regression, and the fail-closed defaults thoroughly — the gap is only the
  endpoint-level ban/2FA branches (C-10-1).

### Action list

Ranked by cost of the miss:

1. **C-10-1** — endpoint-level ban + 2FA tests on `/api/auth/external/exchange`; table-drive the ban
   assertion over the four token-minting paths.
2. **C-10-2** — `AuthProvider.test.tsx` for the refresh/adopt/logout machine.
3. **C-10-3** — `Board.tsx` move-handler tests (export the resolver, or render-test the handlers).
4. **C-10-4** — `MatchNoShowWatcherTests.cs`, including the two-instance case that fails until batch
   3 item 2 lands.
5. **C-10-8** — extract and test the `bot.ts` Elo mapping against `BotSkill.cs`.
6. **C-10-6** — Glicko-2 `else`-branch (expected-result) volatility case.
7. **C-10-9 / C-10-10** — small frontend guard tests, best done alongside the batch 6/8 fixes.

Owned elsewhere, sequence behind the fix:

- **C-10-5** waits on C-4-1 (unify the two flag paths first).
- **C-10-7** waits on C-8 / batch 5 (add the `Validate()` ceiling first), then needs a cost-vector
  fixture entry, not just a unit test.

No production code changed in this packet. No standing-position reversals.

---

## C-11 — Frontend correctness

Date: 2026-08-29
Packet: docs/audits/INDEX.md, table 3

### Verdict

The live-game client is in good shape on the things that are hardest to get right: one hub connection
for the life of the page, `withAutomaticReconnect` plus an explicit `onreconnected` resync, a
`stopWhenSettled` helper that defuses the StrictMode start/stop race, a single-flight token refresh
in `api.ts` that stops two concurrent 401s from looking like refresh-token reuse, and a StrictMode
latch on the single-use OAuth exchange code. The accessible board is a genuine parallel input path,
not a bolt-on. Nothing here loses data or hurts a stranger.

The theme across the findings is **the client never uses the player's own colour.** The page computes
`mySide` and then only feeds it to screen-reader announcements: the visual board is hard-wired to
White's orientation, the two clocks are hard-wired White-on-bottom, and piece draggability is gated
on side-to-move alone. A player assigned Black plays the whole game upside down relative to every
other chess site. That is C-11-1 and it is the one worth fixing before #16.

Two findings are the residue of fixes that half-landed: the reconnect path (deep-audit FE-4-1) got
its interim client-side guard but not the server-side move-log it actually needs, so a reconnect now
silently *diverges* instead of visibly blanking; and `api.generated.ts` is wired up for types only,
so "generated client vs hand wrappers" resolves to "there is no generated client" (drift already
filed as C-9-1).

### Findings

#### C-11-1 — The board and both clocks never flip for the Black player
- Severity: High (trigger: being assigned Black, which is roughly half of all matchmade and
  challenge games)
- Where: `ui/components/Board.tsx:219` (`boardOrientation="white"`, hard-coded, no prop);
  `ui/app/[locale]/game/[id]/page.tsx:686-702` (the `<aside>` renders `<Clock side="Black">` above
  `<GapMeter>` above `<Clock side="White">` unconditionally); `ui/components/AccessibleBoard.tsx:99`
  takes `you` but uses it only for the `aria-label`/announcements, never to reorder cells
- Impact: The page already knows the local player's colour: `mySide` is computed at
  `game/[id]/page.tsx:637-641` and passed to `AccessibleBoard` as `you`. It is never passed to
  `<Board>`, and the two `<Clock>` elements are in a fixed source order. A player who is Black sees
  the board from White's side (their own back rank at the top, pawns moving *up* the screen) and
  their own clock pinned to the top of the sidebar while the opponent's sits at the bottom. Every
  competing site (Lichess, Chess.com) orients the board to the viewer and puts "you" at the bottom;
  a new player assigned Black will read this as the site being broken rather than as a choice. Board
  logic stays correct throughout (moves, legal-move hints, last-move tint all work), so this is
  purely presentation, but it is the presentation of the core product on half of all games.
- Fix: thread `mySide` into `<Board>` as `boardOrientation={mySide === "Black" ? "black" : "white"}`
  (`react-chessboard` supports `"black"` natively, and `squareStylesForSelection` is
  orientation-independent so nothing else changes), and swap the two `<Clock>` blocks so the local
  player's clock is the lower one. `watch/[id]` and `replay/[id]` should keep White-on-bottom (a
  spectator has no side) or gain an explicit flip control — out of scope here.

#### C-11-2 — Reconnecting mid-game no longer blanks the move list, but never resynchronises it either
- Severity: Medium (trigger: any ply landing on the server during the window a player's transport is
  down and `onreconnected` has not yet fired)
- Where: `ui/app/[locale]/game/[id]/page.tsx:350-381` (`join()` with the `firstJoin` guard added in
  `be9d9a1`), `:383-386` (`conn.onreconnected(() => join()...)`)
- Impact: The interim fix from deep-audit FE-4-1 / IMPROVEMENT_PLAN batch 2.1 shipped: `join()` now
  only clears `moves` / `clockHistory` / captured-material on the first join of a mount, so a clean
  reconnect no longer wipes the sidebar. But `JoinGame` returns `GameSnapshot`
  (`GameSnapshotDto` = `{ state, legalMoves }`, `ui/lib/types.ts`), which carries no move log, so the
  reconnect path has nothing to *rebuild* from. Any `MovePlayed` broadcast that fired while the
  socket was down is gone: the move list is now missing those plies, `clockHistory` has a gap, the
  captured-material panel is missing any piece taken in that window, and every subsequently appended
  entry's `moveNumber` (`:229`, `prev.length + 1`) is off by the number of missed plies. The board,
  clocks and legal moves are correct (they come from `snap.state`), so the divergence is silent.
  `watch/[id]/page.tsx:173` does this correctly because `SpectatorSnapshotDto` carries a full
  `Moves` list. The player page currently shows *less* reliable history than a spectator does.
- Fix: the server-side half of FE-4-1 — add the move log to `GameSnapshotDto` / `JoinGame`
  (per `.claude/rules/realtime.md` that means the `HubContract.cs` declaration first, then an OpenAPI
  regen) and rebuild `moves` / `clockHistory` from it on every `join()`, the way the watch page
  already does. A smaller interim step: on `onreconnected` only, HTTP-refetch the game's moves.

#### C-11-3 — The move list numbers every ply instead of pairing moves
- Severity: Medium (trigger: any game past the second ply; visible to every player and every replay
  viewer)
- Where: `ui/components/MoveList.tsx:53-54` renders `{move.moveNumber}{move.side === "White" ? "." : "…"}`;
  `move.moveNumber` is a 1-based **ply** index, set at `game/[id]/page.tsx:229` (`prev.length + 1`)
  and `replay/[id]/page.tsx:144` (`p.ply`)
- Impact: White's second move renders as `3.`, White's eighth as `15.`, and Black's continuations as
  `2…`, `4…`, `6…`. The `.` / `…` split is borrowed straight from SAN, where it means "White's move
  N" vs "Black's move N", so the notation actively tells a chess reader these are move numbers when
  they are ply numbers. It is consistent between the live game and the replay, so it is a deliberate
  shape rather than a regression, but it is wrong by the convention it imitates and a chess audience
  reads it as broken.
- Fix: display `Math.ceil(move.moveNumber / 2)` as the number while keeping `moveNumber` /
  `selectedPly` as the ply for row selection and scroll-into-view. Optionally group two plies per
  row (`1. e4 e5`) the way both reference sites do; the per-line form is fine if the number is right.

#### C-11-4 — There is no generated API client, only generated types; every path, method and status in `api.ts` is hand-maintained
- Severity: Low (trigger: a server route rename or response-DTO reshape that the hand wrapper is not
  updated for — it compiles green)
- Where: `ui/lib/api.generated.ts` is imported only by `ui/lib/types.ts:13` (`components`, for type
  aliases); `ui/lib/api.ts:178` `request<T>()` builds ~70 literal `/api/...` strings and returns
  `(await res.json()) as T` with no runtime validation
- Impact: `.claude/rules/frontend.md` describes `api.ts` as "hand-written wrappers" over the
  generated client, but there is no generated *client* — `openapi-typescript` emits types, and
  nothing consumes the `paths` object. So a path typo, a wrong HTTP verb, or a server DTO that drops
  a field is invisible to `tsc`: the `as T` cast asserts a shape that was never checked. This is the
  same exposure C-9-1 filed from the drift angle (`api.ts` request layer never checked against the
  OpenAPI document); recorded here only because C-11's brief names "generated client vs hand
  wrappers" and the answer is that the split does not exist. No fix beyond C-9-1's.

#### C-11-5 — `isDraggablePiece` lets the off-turn player pick up the mover's pieces in a human game
- Severity: Low (trigger: in a PvP game, the player whose turn it is *not* dragging a piece; the
  server rejects the resulting `SubmitMove` and surfaces an error banner)
- Where: `ui/components/Board.tsx:193-200` (`isDraggablePiece` checks `sideToMove` and piece colour
  only), `:98-102` (`legalFromSquares` is derived from `legalUci`, which is always the side-to-move's
  moves)
- Impact: In bot mode the page already blanks `legalUci` when it is not the player's turn
  (`game/[id]/page.tsx:707-709`, `:722-724`), so the bot's pieces are inert. In a human game there is
  no such guard: both players see the side-to-move's pieces as draggable, so the waiting player can
  drag the opponent's piece and fire a `SubmitMove` that the server refuses ("not your turn" / "not a
  participant of this game"), briefly flashing `error`. Harmless — server-authoritative — but it lets
  a player interact with pieces that are not theirs.
- Fix: the same `mySide` thread as C-11-1 — pass the local colour to `<Board>` and require
  `piece colour === mySide` in `isDraggablePiece`, and gate `legalFromSquares` / `legalUci` on
  `sideToMove === mySide` for human games (mirroring the bot-mode blanking already there).

#### C-11-6 — Underpromotion is impossible except by dragging; click and keyboard promote to a queen only
- Severity: Low (trigger: wanting a knight/rook/bishop promotion via click-to-move or the accessible
  board)
- Where: `ui/components/Board.tsx:147-152` (`handleSquareClick` hard-codes `${dragFrom}${square}q`),
  `ui/components/AccessibleBoard.tsx:134-138` (`buildUci` hard-codes `q`, with a comment scoping
  under-promotion to "V2")
- Impact: `react-chessboard`'s built-in promotion dialog fires on a drag-drop of a promoting pawn and
  routes the chosen piece back through `handlePieceDrop` (`Board.tsx:169-191`), so drag-to-promote,
  including under-promotion, works. The click path and the entire keyboard/accessible path have no
  dialog and always send `...q`. A keyboard-only or screen-reader user therefore cannot under-promote
  at all. Rare in practice, and the drag path covers the common case, so Low — but the accessible
  path being strictly less capable than the mouse path is the kind of gap P-5 / the NVDA pass should
  weigh.
- Fix: add a small piece picker to the click path (or reuse `react-chessboard`'s
  `showPromotionDialog`), and give `AccessibleBoard` a promotion prompt after a pawn reaches the last
  rank. Cross-reference: packet **P-5** owns the accessible-input parity question.

#### C-11-7 — Own moves have no optimistic feedback; a laggy link makes the board feel frozen
- Severity: Low (trigger: playing on a high-latency or congested connection)
- Where: `ui/app/[locale]/game/[id]/page.tsx:466-477` (`handleMove` invokes `SubmitMove` and waits;
  the comment states state updates arrive via the `MovePlayed` broadcast "including our own move")
- Impact: A drag-drop gets `react-chessboard`'s local animation, but a click move produces nothing
  visible until the server round-trips the `MovePlayed` broadcast back. On a slow link the piece sits
  still for a few hundred milliseconds after the click, which reads as an unregistered input in a
  game where the clock is the win condition. Think-time is charged from the server's own monotonic
  clock (`.claude/rules/realtime.md`), so this is not unfair — only sluggish. Deliberate today per
  the code comment.
- Fix: optionally render the mover's piece on the target square immediately (client-side FEN apply)
  and reconcile when `MovePlayed` lands; or at minimum disable the moved-from square and show a
  pending affordance so the click reads as accepted.

#### C-11-8 — Clock never shows sub-second time
- Severity: Low (trigger: a clock under ~10 seconds in a time scramble)
- Where: `ui/components/Clock.tsx:17-22` (`formatTime` floors to whole seconds, always `M:SS`)
- Impact: In a variant whose entire pitch is "the clock is the battlefield", the clock displays
  `0:03` for the whole of the last three seconds with no tenths, where both reference sites switch to
  `0:03.4` under 10-20s. The urgency is exactly when the reading matters most. The accessible layer
  does better here (`crossedThreshold` announcements at `game/[id]/page.tsx:440-464`).
- Fix: show tenths below a threshold (e.g. `< 20s`): `formatTime` already has the seconds value, and
  the 250ms cosmetic tick at `:428-431` is frequent enough to animate one decimal.

### Already covered

- **Reconnect wiping move history / clock graph** — deep-audit FE-4-1 and IMPROVEMENT_PLAN batch 2.1.
  C-11-2 above is the status after the interim client fix landed: the visible blank is gone, a silent
  divergence remains until the server-side `GameSnapshotDto` change ships.
- **`api.ts` request layer never checked against OpenAPI** — C-9-1. C-11-4 is the same gap from the
  "is there a generated client" angle.
- **`safeReturnPath` in `auth/callback/page.tsx` is a local duplicate of `challengeLink.ts`'s
  `safeNextPath`** — IMPROVEMENT_PLAN batch 8 item 2. The callback copy (`:42-49`) now also rejects
  any `:` and any `\`, so it is stricter than when that finding was filed, but it is still a second
  copy of the rule and still skips the control-character strip; the batch-8 fix (import the shared
  one) stands.
- **`ui/lib/bot.ts` leaked Stockfish worker / single global resolver** — IMPROVEMENT_PLAN batch 6
  item 1. Not re-inspected here.
- **Presence reconnect on every token refresh; `DetectionReportCard` / `SessionsCard` /
  `TwoFactorCard` stale-response gaps; `getChallenge` has no `base` override** — IMPROVEMENT_PLAN
  batch 6 items 2-4 and deep-audit FE-4-2.
- **Board / AuthProvider / bot.ts test gaps** — IMPROVEMENT_PLAN batch 11 and packet C-10
  (`C-10-2`, `C-10-5`). C-11-1, C-11-3 and C-11-5 would each be caught by a prop-variant render test
  of `Board` / `MoveList`.
- **Board raw `rgba()` / `borderRadius` / off-palette ring** — STYLING-CONFORMANCE.md cross-cutting
  section. Not a correctness issue.

### Action list

Ordered, all :

1. **C-11-1** — thread `mySide` into `<Board boardOrientation>` and reorder the two `<Clock>`s.
   Highest visible value and self-contained; do before #16. Add a `Board` render test per C-10.
2. **C-11-3** — `Math.ceil(ply / 2)` for the displayed move number in `MoveList`. One-line, add a
   `MoveList` render test.
3. **C-11-5** — fold the local colour into `isDraggablePiece` / `legalUci` gating for human games
   (rides on the same `mySide` prop as C-11-1).
4. **C-11-2** — needs the server-side `GameSnapshotDto` move-log change (owned jointly with FE-4-1 /
   `.claude/rules/realtime.md`'s `HubContract.cs` + OpenAPI flow). Interim: HTTP move refetch on
   `onreconnected`.
5. **C-11-6** — click/keyboard under-promotion picker. Coordinate with packet **P-5**.
6. **C-11-7**, **C-11-8** — polish; owner's call whether either is worth doing before launch.

No standing-position reversals. C-11-4 adds nothing to do beyond C-9-1.

---

## C-12 — Schema and migrations
Date: 2026-08-31
Packet: docs/audits/INDEX.md

### Verdict

The schema itself is in good shape: the entity model is deliberate, every index in it has a written
reason, the tombstone-instead-of-delete design is coherent, and the migration gate
(`RunMigrationsOnStartup=false` plus `release_command`) is correctly configured in every place it
appears. The problem is not the schema, it is that **the two documents an operator reads during an
incident have fallen behind the migration chain in a way that inverts their advice.** MIGRATIONS.md
and DEPLOYMENT.md both promise that rolling the application back against a head schema is safe
because "EF ignores columns it does not know about". That is true of added columns and false of
dropped ones, and two migrations have since dropped columns, the newer of them from `AspNetUsers`.
Rolling back past it does not degrade a feature, it 500s every read of the users table. Underneath
that, three smaller things: the daily retention sweep has no index on any of its five predicates, one
column's uniqueness is assumed by the revoke path and not enforced by the schema, and one index on
the hottest table is never usefully used. The anonymisation-leftover half of this packet is already
well covered by L-1, L-2 and $1 and is not re-derived here.

### Findings

#### C-12-1 — The documented rollback boundary is two migrations stale, and the current one breaks login
- Severity: **High**
- Where: `src/KiriChess.Server/Data/Migrations/20260825204345_ReplaceAvatarKeyWithUpload.cs:14-16`
  (drops `AspNetUsers.AvatarKey`), `src/KiriChess.Server/Data/Migrations/20260819235137_DropChallengeRated.cs:13-15`
  (drops `Challenges.Rated`), against `docs/ops/MIGRATIONS.md:98-112` and `docs/ops/DEPLOYMENT.md:184-199`
- Impact: Play A is the default recovery and the only one `strategy = "immediate"` leaves available
  without a Neon restore. Both documents state its precondition as "EF ignores columns that are not
  in its model, so a `kirichess-api` at version N−1 runs happily against a schema at version N", and
  both then name the exceptions: MIGRATIONS.md:98 names exactly one (the 8a.4 unique index) and calls
  it "One exception, and it is the reason this play needs a check rather than a reflex";
  DEPLOYMENT.md:196 names two (8a.4 and 8.5). Neither names a dropped column, and the reasoning does
  not extend to one: EF Core emits an explicit column list on every read, so an image built before
  `20260825204345` issues `SELECT ... "AvatarKey" ... FROM "AspNetUsers"` against a head schema and
  takes a `42703 column does not exist` on **every** query touching the users table, which is login,
  registration, token refresh, profile and moderation. That is a total outage produced by the
  recovery procedure, discovered mid-incident, with the runbook saying it was safe. The Challenges
  case (2026-08-19) is milder because it is one feature, but it moves the floor too. The repo has
  already met this failure shape once from a stale migrate image, so it is not hypothetical.
  Compounding it, MIGRATIONS.md:72-76 admits its own cost table is four migrations behind; it is now
  twelve, and the two destructive ones are among the missing rows.
- Fix: State an explicit rollback floor at the top of MIGRATIONS.md §3 Play A and in DEPLOYMENT.md's
  "two things to check" paragraph, worded as a version rather than a story: no `kirichess-api` image
  older than `20260825204345_ReplaceAvatarKeyWithUpload` may be deployed against a head schema, and
  the floor moves forward on every `DropColumn`. Add the twelve unpriced migrations to §2's table in
  the same pass, and add a line to §4 rule 1 saying a `DropColumn` sets the floor, so the next one is
  recorded when it is written rather than when it is needed.

#### C-12-2 — Every retention sweep predicate is a sequential scan; no swept table is indexed on its cutoff column
- Severity: **Medium**
- Where: `src/KiriChess.Server/Retention/RetentionSweep.cs:60,62,64,66,68-70` against
  `src/KiriChess.Server/Data/KiriChessDbContext.cs:170-176` (RefreshToken), `:180-189` (ApiToken),
  `:191-196` (ExternalLoginCode), `:209-214` (TwoFactorChallenge), `:120-142` (Challenge)
- Impact: The sweep deletes on `ExpiresAt < cutoff` for four tables and on
  `(ExpiresAt < cutoff || RevokedAt < cutoff)` for `ApiTokens`. No table carries an index on
  `ExpiresAt`, and `RevokedAt` appears only as the trailing column of `(UserId, RevokedAt)`, which
  cannot serve a predicate that does not constrain `UserId`. The full index inventory in
  `Data/Migrations/KiriChessDbContextModelSnapshot.cs:69-72,265-270,353-356,555-558,666-669` confirms
  it. So all five daily DELETEs are full table scans. `RefreshTokens` is the table this matters on:
  rotation writes a row per refresh, so it grows per-session-per-15-minutes and is kept for
  `ExpiresAt + 30d`, making it the largest table in the database well before `Games` is. RETENTION.md
  §5 limit 1 anticipates the wrong half of this: it names the unbounded DELETE and proposes "a
  `Take`-bounded loop over the primary key", which does not help, because the cost is in finding the
  rows, not in deleting them, and a PK-ordered loop still scans. `docs/audits/I.md (I-1)`
  reviewed this schema's index coverage approvingly and did not reach the sweep. Costs nothing today
  at zero deployed users; the trigger is simply the site working.
- Fix: One additive migration adding `IX_RefreshTokens_ExpiresAt` and the same on
  `ExternalLoginCodes`, `TwoFactorChallenges` and `Challenges`; for `ApiTokens` two single-column
  indexes let Postgres BitmapOr the two arms. Index-only migrations are the one genuinely free
  reversal in this chain (MIGRATIONS.md §2 says so of `AddGameResultIndex`), so this does not move the
  Play A floor. Generate it, do not apply it, per `.claude/rules/backend.md`.

#### C-12-3 — `StripeSubscriptionId` uniqueness is assumed by the revoke path and not enforced by the schema
- Severity: **Medium**
- Where: `src/KiriChess.Server/Data/KiriChessDbContext.cs:86-87` (non-unique, unfiltered index) against
  `src/KiriChess.Server/Account/PatronService.cs:110`
- Impact: The comment at `:86` states the invariant precisely, "the revoke path is a lookup by
  subscription id and nothing else", and `PatronService.cs:110` implements it as
  `FirstOrDefaultAsync(u => u.StripeSubscriptionId == subscriptionId)`, a single-row assumption. The
  index behind it is neither unique nor filtered. If two accounts ever carry the same subscription id
  (a support-driven manual fix, a re-linked customer, a webhook replay against a changed account),
  `customer.subscription.deleted` revokes whichever row Postgres returns first and leaves the other
  showing a paid badge indefinitely, with nothing logged, because zero-versus-two is indistinguishable
  from one at this call site. Separately the index is unfiltered, so it holds an entry for every
  non-patron account, which is nearly the whole table, to serve a lookup that only ever matches
  patrons. $2 covers the patron columns as a pay-to-win question and $5 covers badge duration; neither
  reaches the index.
- Fix: `.IsUnique().HasFilter("\"StripeSubscriptionId\" IS NOT NULL")` at `KiriChessDbContext.cs:87`,
  which enforces the one-account-per-subscription invariant the revoke path already assumes and
  shrinks the index to the rows it is for. Additive and index-only, so it does not move the Play A
  floor. Note the partial-index shape is already the house pattern (`:69-71`).

#### C-12-4 — The migration gate is configuration, not privilege: the serving process holds DDL rights
- Severity: **Low** (trigger: a second machine, or any path that can influence configuration in the serving process)
- Where: `src/KiriChess.Server/Program.cs:788` (`RunMigrationsOnStartup` default), `fly.backend.toml:35`
  (pinned `"false"`), `:26-27` (`release_command`), with `docs/audits/I.md:35` recording that the app
  connects as `neondb_owner`
- Impact: The gate itself is sound and worth saying so: `Program.cs:788` defaults to
  `app.Environment.IsDevelopment()`, so production is false even if the toml line were lost, and
  INF-1 confirmed the setting is consistent across both Fly tomls, all four compose files and
  `.env.example`. What it is not is a boundary. The `--migrate` release machine and the serving
  container run the same binary against the same connection string as the same role, so the only
  thing standing between a long-lived serving process and `DROP TABLE` is a boolean it reads from its
  own configuration. Everything MIGRATIONS.md argues about `Down()` being unreachable from the
  production image (§1, "no SDK, no project source") is about the EF tooling, not about the
  privileges the connection actually holds.
- Fix: Either a second Neon role without DDL for the serving app, keeping `neondb_owner` for the
  `release_command` only (one `GRANT`, one extra Fly secret, and Neon makes the role trivial), or a
  sentence in MIGRATIONS.md §1 stating that the gate is deliberately a configuration gate at this
  size. The second is a legitimate answer for a one-machine hobby host; the current state is neither,
  because nothing records that the question was asked.

#### C-12-5 — JWT validation runs before the `--migrate` branch, so a key problem surfaces as a failed migration
- Severity: **Low** (trigger: a deploy that rotates or clears `Jwt__Key`, or standing up a new environment)
- Where: `src/KiriChess.Server/Program.cs:686` (`StartupValidation.ValidateJwt`) versus `:691`
  (`if (args.Contains("--migrate"))`), noted in `docker-compose.e2e.yml:11` and nowhere else
- Impact: The release machine builds the full application and validates the JWT signing key before it
  reaches the migration branch, so a deploy whose JWT secret is missing or malformed aborts at
  `release_command` with a JWT error. The `release_command` is documented, at `fly.backend.toml:24-25`
  and in MIGRATIONS.md §1, as the thing that fails when *the migration* fails, so the first reading of
  an aborted deploy will be "the schema change is bad" when the schema was never touched. The
  behaviour is understood in the compose files, which anchor a shared env block specifically to carry
  a `Jwt__Key` into the migrate service, but that knowledge lives only there.
- Fix: One line in MIGRATIONS.md §1 recording that `--migrate` runs the full startup validation first,
  so a `release_command` failure is not necessarily a migration failure. Moving the branch above
  `ValidateJwt` would also work and is arguably more honest, but it is a production-code change and
  the coupling is currently load-bearing for the compose paths.

#### C-12-6 — `IX_AspNetUsers_DeletedAt` is never usefully used
- Severity: **Low**
- Where: `src/KiriChess.Server/Data/KiriChessDbContext.cs:79`, against every reader:
  `src/KiriChess.Server/Account/AvatarService.cs:62`,
  `src/KiriChess.Server/Account/PublicProfileEndpoints.cs:38`,
  `src/KiriChess.Server/Retention/RetentionEndpoints.cs:52`
- Impact: Three queries filter on `DeletedAt`. Two of them combine it with `u.Id == userId`, a primary
  key equality that resolves to one row before the predicate matters. The third, the dormant-account
  scan, filters `DeletedAt == null` across the whole table, which matches essentially every row and is
  precisely the shape Postgres will not use an index for. So the index earns nothing on reads while
  costing a write on every insert and update to the busiest table in the schema. The comment above it
  at `:75-78` explains the erasure design rather than the index, which is probably how it survived
  review.
- Fix: Drop it in the next migration that touches `AspNetUsers`, or keep it and record why (a future
  "list erased accounts" admin view would justify it, filtered `WHERE "DeletedAt" IS NOT NULL`, which
  is the selective direction). Dropping an index is the one reversal MIGRATIONS.md §2 prices at
  **None**, so unlike the column drops in C-12-1 this does not move the Play A floor.

#### C-12-7 — RETENTION.md §1 claims to be exhaustive; two tables appear in no row of it
- Severity: **Low**
- Where: `docs/decisions/RETENTION.md:10-28` ("The whole retention schedule... everything else is a
  stated decision, not an oversight") against `src/KiriChess.Server/Data/KiriChessDbContext.cs:35`
  (`KillSwitches`) and `:46` (`DataProtectionKeys`)
- Impact: The §1 table covers thirteen categories and the context maps more. The other absentees are
  already filed elsewhere and are not re-reported here (see Already covered), which leaves these two
  as the unclaimed ones. `DataProtectionKeys` is the one that deserves a line rather than silence: ASP.NET
  Core never prunes expired keys from the ring, so the table grows without bound (slowly, one key per
  90 days) and MIGRATIONS.md:80 already prices it as irrecoverable and site-affecting if lost. A
  reader auditing retention against the schema finds a table holding cryptographic material with no
  stated position, which reads as an oversight even though keeping it is obviously correct.
  `KillSwitches` is four fixed rows and needs one clause, not a rule.
- Fix: Two rows in RETENTION.md §1: `DataProtectionKeys`, "indefinite, key ring must outlive the
  payloads it protects, never swept", and `KillSwitches`, "fixed row set, no growth". No code change.

### Already covered

- **Web push subscriptions survive erasure** — `L-2-1` (High), with the disclosure half at `L-1-4`. The
  unreachable-cascade mechanism is stated there; not re-derived.
- **Rematch offers survive erasure and are never swept** — `L-2-2`, disclosure at `L-1-8`.
- **Donation references are never swept, and single-use is not schema-enforced** — `$1-6` and `$1-5`.
- **`DropChallengeRated` violates MIGRATIONS.md §4's expand/contract rule, and §2's cost table is
  stale** — `C.md (section BE-5)` and `C-1-x`. C-12-1 above extends this: there is a second
  destructive migration, it is newer, it is on `AspNetUsers`, and the runbook consequence is the new
  part. BE-5's "the only destructive one among them" is no longer accurate.
- **Missing `Game(WhitePlayerId, EndedAt)` / `Game(BlackPlayerId, EndedAt)` index** — `I.md (I-1)`
  (AS-1) and `I.md:76`.
- **Missing unique index on `RatingHistory(GameId, UserId)`** — `C-1-4`, `C.md (section BE-3)`.
- **No test asserts the EF model has no pending changes against the snapshot** —
  `C.md (section BE-5)`. Worth pairing with C-12-1: such a test would not have caught it either,
  since the model and the snapshot agree; the drift is between the chain and the runbook.
- **`strategy = "immediate"` has no health-gated rollout and no auto-rollback** — `I.md:153-155`
  judged this a deliberate consequence of Redis being unprovisioned, not a gap, and `INF-1` and `C-5`
  cover the in-flight-game half. C-12-1 does not reopen it; it observes that `immediate` makes the
  manual Play A the only fast recovery, which is why Play A's floor being wrong matters.
- **Schema/migration invariants deferred here from CI/CD** — `I.md:167-168` explicitly parked them
  for this packet.
- **Sanctions surviving deletion, games and ratings kept indefinitely** — standing positions in
  `INDEX.md`; not reopened.

### Action list

1. C-12-1: add the rollback floor to MIGRATIONS.md §3 and DEPLOYMENT.md's "two
   things to check", add a `DropColumn` clause to §4 rule 1, and backfill §2's cost table with the
   twelve missing migrations. Highest value in this packet and touches no code.
2. C-12-2: one additive migration adding the five retention-sweep
   indexes. Per `.claude/rules/backend.md`, generate but do not run `database update`.
3. C-12-3: make `StripeSubscriptionId` unique and filtered on
   `IS NOT NULL`. Small, but it is the one place the schema contradicts what a payment code path
   assumes. Batch with item 2 as a single migration if convenient.
4. **Owner** C-12-4: a non-DDL Neon role for the serving app, or a stated
   position in MIGRATIONS.md §1 that the gate is configuration at this size. Either answer closes it;
   silence does not.
5. C-12-5 and C-12-7: one line in MIGRATIONS.md §1 about `ValidateJwt`
   preceding `--migrate`, two rows in RETENTION.md §1 for `DataProtectionKeys` and `KillSwitches`.
6. C-12-6: drop `IX_AspNetUsers_DeletedAt`, or record the read that justifies keeping it.
   Lowest urgency; fold into whichever migration lands next.

