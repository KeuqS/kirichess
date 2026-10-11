# S — Security, trust, fair play

Merged packet reports for category **S**. Individual packets were `docs/audits/S-N.md`; they now live as sections below. Index: [INDEX.md](INDEX.md).

---

## S-1 — Security and authorization
Date: 2026-08-31
Packet: docs/audits/INDEX.md

### Verdict

The authorization matrix itself is in good shape and I found no IDOR: every route group in
`Program.cs:726-754` carries an explicit posture, every service that touches a user's own data scopes
its query to `GetUserId()` rather than a client-supplied id (`RefreshTokenService.cs:105-112`,
`ApiTokenService.cs:105-112`, `PushSubscriptionService.cs:50-57`, `ChallengeService.cs:96-103` all
follow the same no-oracle shape), and the 9f.5 scheme isolation between the session JWT and the
`kc_` API token is real, deliberate, and covered by tests (`PublicApiTests.cs:133-176`). The theme of
this packet is not missing authorization; it is **what a credential can still do after it has
already been stolen, and how long it lives**. `docs/decisions/AUTH.md` accepts token theft as a full
takeover and then names two mitigations that do not exist in code: the recovery action the doc
implies (a password reset) does not revoke API tokens, and the irreversible actions a stolen
15-minute access token can reach — erase the account, enable 2FA against its owner — ask for no
password at all, so a 15-minute compromise converts into permanent, unrecoverable loss on a service
with one maintainer and no support desk. The second theme is smaller and cheaper: three anonymous
surfaces (`/openapi/v1.json`, `/health/ready`, and the whole of `/api/v1` for one route) sit in the
global rate limiter's exemption list and do real per-request work, which is the one place
`I.md (I-1)` §4.1's "every mutating HTTP surface has a named policy" is not quite true. Nothing
here is a Critical: no stranger's data is reachable by an unauthenticated caller, and the moderation
surface holds.

Of `AUTH.md` §1.7's four triggers, one has arguably fired (trigger 3, see S-1-10) and one is already
recorded as partially fired by the deep audit (trigger 2, Turnstile in the root layout). Triggers 1
and 4 have not: `Moderation:AdminEmails` is still a single-entry list with no second admin
configured anywhere in the repo, and staging is still on `*.fly.dev`. I did not re-derive the
localStorage, CSP or moderator-2FA decisions.

### Findings

#### S-1-1 — A password reset revokes sessions but not API tokens
- Severity: High
- Where: `src/KiriChess.Server/Auth/AuthEndpoints.cs:389`; the method that would fix it already
  exists, unused from here, at `src/KiriChess.Server/Auth/ApiTokenService.cs:114-121`;
  `docs/decisions/AUTH.md:209-216`
- Impact: `/api/auth/reset-password` calls `refreshTokens.RevokeAllForUserAsync` and nothing else, so
  a personal access token minted by an attacker (the exact scenario `AUTH.md` §2 names — "a payload
  that can call `POST /api/account/api-tokens` while the stolen access token is live can issue a key
  that survives the 15-minute JWT and the victim's own logout") also survives the one action a
  compromised user is told to take. `AUTH.md` §2 lists what must **not** revoke API tokens
  (signing out everywhere, toggling 2FA) and what must (an `AccountBan`, erasure); a password reset
  appears in neither list, so this is a gap rather than a decision. `SanctionService.cs:78-79`
  already shows the two-line pattern.
- Fix: inject `ApiTokenService` into the `/reset-password` handler and call
  `RevokeAllForUserAsync(user.Id, ct)` beside the refresh-token revoke, then add the sentence to
  `AUTH.md` §2's list so the next reader sees a decision instead of an omission.

#### S-1-2 — Irreversible and lockout-inducing account actions require no re-authentication
- Severity: High
- Where: `src/KiriChess.Server/Account/AccountEndpoints.cs:49-58` (erasure),
  `src/KiriChess.Server/Auth/TwoFactorEndpoints.cs:69-99` (enable) and `:107-134` (disable),
  `src/KiriChess.Server/Account/AccountEndpoints.cs:145-166` (mint a PAT)
- Impact: every one of these accepts a bearer access token and nothing more. `/api/account/delete`'s
  only additional gate is the literal string `"DELETE MY ACCOUNT"`, which its own comment calls "a
  deliberate speed bump" — that is a speed bump for a human, not a control against a script holding a
  token, and the action is explicitly un-undoable (`AccountDeletionService.cs:80-107` scrubs the row
  and drops every credential). `/api/account/2fa/enable` is worse in one respect: a stolen token can
  enrol the attacker's own authenticator and turn 2FA on, after which the legitimate owner cannot get
  back in even by resetting their password, because `/api/auth/login` will demand a TOTP code only
  the attacker has (`AuthEndpoints.cs:183-188`). A 15-minute credential therefore buys permanent
  lockout or permanent data loss. Not Critical only because it presupposes the takeover `AUTH.md` §1
  already accepts, and because the erasure path refuses while a game is in progress.
- Fix: require the account password (`UserManager.CheckPasswordAsync`) on erasure, 2FA enable and
  2FA disable, skipping the check only for an account with no `PasswordHash` (Google-only sign-in),
  which is the same shape Identity's own `ChangePassword` uses. Doing the same on
  `POST /api/account/api-tokens` also closes the PAT-minting hole `AUTH.md` §2 names and currently
  accepts.

#### S-1-3 — A never-expiring API token is documented to travel in a URL query string
- Severity: High
- Where: `src/KiriChess.Server/Auth/ApiTokenAuthenticationHandler.cs:63-68`,
  `src/KiriChess.Server/Program.cs:306-311`, and the instruction to bot authors at
  `ui/messages/en.json:1104` ("Pass `access_token=kc_…` on negotiate") plus its five translations
- Impact: this codebase already states the rule it is breaking. `AccountEndpoints.cs:91-93` sends a
  raw refresh token as a header specifically "so it never lands in a proxy access log the way a query
  parameter would — the raw refresh token is bearer-equivalent material." A `kc_` PAT is
  bearer-equivalent material with a longer life than a refresh token (see S-1-4: no expiry at all),
  and `/developers` tells third parties to put it in a URL, where Fly's proxy log, Cloudflare's
  request log and Sentry's captured request URL (`Program.cs:96` sets `SendDefaultPii = false`,
  which does not strip query strings) will all record it. A 15-minute JWT in the same place is a
  bounded exposure; a permanent token is not.
- Fix: no server change is needed — `ApiTokenAuthenticationHandler.ReadRawToken` already accepts
  `Authorization: Bearer kc_…` on `/hubs/game`, and a non-browser bot can set that header on the
  WebSocket handshake. Change `/developers` to document the header as the way to authenticate a bot
  to the hub, keep the query path for the first-party JWT client that genuinely cannot set headers,
  and say plainly that a `kc_` token in a URL will be logged.

#### S-1-4 — API tokens can never expire; the column exists and is never written
- Severity: Medium
- Where: `src/KiriChess.Server/Auth/ApiTokenService.cs:47-58` (the `ApiToken` initialiser sets no
  `ExpiresAt`), `src/KiriChess.Server/Data/Entities/ApiToken.cs:27` (the nullable column),
  `src/KiriChess.Server/Account/AccountDtos.cs:123` (`CreateApiTokenRequest` has no expiry field),
  and `ApiTokenService.cs:40,68,87` which all read the column that nothing sets
- Impact: every PAT is immortal until a human revokes it. The whole expiry apparatus — entity
  column, the `ExpiresAt > now` filters, the DTO fields at `AccountDtos.cs:110,115,120`, and
  `Retention:ApiTokenGraceDays` — is dead code that reads as a working control on the profile page,
  which shows an "Expires" column that is always empty. A key pasted into a public bot repo works
  forever. This is the multiplier on S-1-1 and S-1-3.
- Fix: add an optional `ExpiresInDays` to `CreateApiTokenRequest`, default it to something finite
  (90 days is the usual choice and matches a bot author's tolerance), and set
  `ExpiresAt = now.AddDays(...)` in `IssueAsync`. The read paths already handle it.

#### S-1-5 — `/openapi/v1.json` is anonymous, regenerated per request, and exempt from the limiter
- Severity: Medium
- Where: `src/KiriChess.Server/Program.cs:758-762`, exempted at
  `src/KiriChess.Server/Infrastructure/RateLimiting.cs:377-379`, work done at
  `src/KiriChess.Server/Infrastructure/OpenApi/OpenApiExport.cs:73-80`
- Impact: each GET calls `IOpenApiDocumentProvider.GetOpenApiDocumentAsync()`, which walks the whole
  route table and runs four transformers, then serialises the result into a `StringWriter` — 51 KB
  of JSON (`docs/api/openapi-public.json`) built from scratch, per request, with no cache and with
  the global per-IP backstop deliberately switched off for the `/openapi` path. On one shared-CPU Fly
  machine that is an unauthenticated request-amplification lever aimed at the same process that runs
  every live game's clock.
- Fix: build the string once into a `static Lazy<Task<string>>` (the document cannot change without
  a redeploy) and serve that, or drop `/openapi` from the exemption list at `RateLimiting.cs:378`.
  The first is better: the exemption exists so a scraper is never throttled.

#### S-1-6 — `/health/ready` opens a database connection on every anonymous, unlimited request
- Severity: Medium
- Where: `src/KiriChess.Server/Infrastructure/HealthEndpoints.cs:23-31`, exempted at
  `src/KiriChess.Server/Infrastructure/RateLimiting.cs:377`
- Impact: `db.Database.CanConnectAsync(ct)` per call, `AllowAnonymous()`, and the `/health` prefix is
  the first thing the global limiter exempts. Against Neon — serverless, a bounded connection
  allowance, cold starts on an idle branch — a sustained hammering of this one path can consume the
  connection budget that live games need, and the endpoint answers with the Redis status as a bonus.
  The exemption is right in principle (a probe must never be throttled), but it currently applies to
  the whole world rather than to the probe.
- Fix: keep `/health` (liveness, touches nothing) exempt and anonymous; give `/health/ready` a cheap
  in-process cache of its own result for a few seconds so a burst costs one round trip, which is also
  what makes the Fly/Better Stack probes cheaper. Do not add auth — an orchestrator probe cannot
  carry a credential.

#### S-1-7 — `DELETE /api/v1/challenges/{token}` is the one `/api/v1` route with no rate limit at all
- Severity: Medium
- Where: `src/KiriChess.Server/Games/PublicApiEndpoints.cs:444-453`; the `play` group carries no
  group-level policy (`:37-38`); `/api/v1` is exempt from the global backstop at
  `src/KiriChess.Server/Infrastructure/RateLimiting.cs:378`
- Impact: every other route on this group names a policy explicitly (`:309`, `:348`, `:369`, `:408`,
  `:423`, `:437`); this one does not, and because the global limiter skips `/api/v1` entirely there
  is no backstop underneath it. Any holder of a `play` token gets an unbounded stream of
  `ChallengeService.CancelAsync` UPDATE statements against Postgres. This is also the single
  exception to `I.md (I-1)` §4.1's claim that rate limiting "covers every mutating HTTP surface
  with a named policy".
- Fix: add `.RequireRateLimiting(RateLimitPolicies.ApiPlayCreate)` to the DELETE. Better, move the
  policy onto the `play` and `read` group builders (`:33-38`) so a future `/api/v1` route cannot be
  added unlimited by omission — the same "unsanctioned by default" argument the deep audit's Batch 7
  makes about `RequireAuthorization`.

#### S-1-8 — A web push endpoint URL is stored and requested with no validation
- Severity: Medium (trigger: `Vapid:PublicKey`/`PrivateKey` configured in production; today
  `appsettings.json:52-56` ships them empty and `IPushSender` is never registered)
- Where: `src/KiriChess.Server/Infrastructure/Push/PushEndpoints.cs:28-31` (the only check is
  non-empty), `src/KiriChess.Server/Infrastructure/Push/PushSubscriptionService.cs:24-43`,
  `src/KiriChess.Server/Infrastructure/Push/IPushSender.cs:50-55`
- Impact: any authenticated account can register an arbitrary string as a push endpoint, and
  `WebPushSender` will later issue an HTTP POST to it from inside the Fly machine whenever a rematch
  is offered. That is a server-side request forgery primitive pointed at Fly's private 6PN network
  and at anything else the container can reach. It is blind (the body is discarded), but
  `SendAsync` distinguishes 404/410 from everything else at `:58-61`, which is a one-bit response
  oracle. Real push services are a short, well-known list, so nothing legitimate is lost by
  restricting it.
- Fix: validate in `PushEndpoints` before storing — require `https`, reject anything that resolves to
  a private or link-local address, and allowlist the push-service hosts actually in use
  (`*.googleapis.com`, `*.push.services.mozilla.com`, `*.notify.windows.com`). Reject with a 400
  rather than storing and failing later.

#### S-1-9 — `GameHub.JoinGame` joins the SignalR group before checking the caller is a player
- Severity: Medium
- Where: `src/KiriChess.Server/Games/GameHub.cs:82-86`
- Impact: `Groups.AddToGroupAsync` runs unconditionally, then `_actions.JoinGameAsync` throws
  `"Not a player in this game."` — and the connection stays in the group, receiving every
  `MovePlayed`, `GameEnded`, `DrawOffered`, `RematchOffered` and `RematchAccepted` broadcast for that
  game. The content is not itself a secret (spectating is anonymous by design, 8.1), but this is a
  path into a live game's room that never touches `KillSwitch.Spectating` (`GameHub.cs:265`), never
  registers with `SpectatorTracker`, and is therefore invisible to the spectator count and to the
  anonymous-connection reserve that 10a.2's budget is sized against. The file's own comment two lines
  below shows the intended discipline: `_connections.Track` is deliberately called only after
  `JoinGameAsync` accepted.
- Fix: move `Groups.AddToGroupAsync` below the `await _actions.JoinGameAsync(...)` line, beside
  `_connections.Track`, so a rejected join leaves no group membership behind. Add a test asserting a
  non-participant who calls `JoinGame` receives no subsequent `MovePlayed`.

#### S-1-10 — `AUTH.md` §1.7 trigger 3 has arguably fired: report details are rendered user content
- Severity: Low (trigger: it is the trigger — a reader of §1.7 today would conclude no user content
  beyond a display name is rendered, and that has not been true since 6e.2)
- Where: `src/KiriChess.Server/Moderation/ReportEndpoints.cs:35-38` accepts up to 1000 characters of
  free text; `ui/app/[locale]/moderation/page.tsx:232-238` renders it; `docs/decisions/AUTH.md:181-183`
  lists "rendered user content beyond a display name" as a trigger and cites only chat, profile text
  and annotations, all of which are still unbuilt
- Impact: no XSS today — React escapes it and I found no `dangerouslySetInnerHTML` anywhere in `ui/`.
  But the text is attacker-chosen, and the page it renders on is the one page only the `Admin`
  account opens, whose stolen session §1.5 accepts on the narrow basis that there is exactly one
  moderator. The trigger list is the mechanism that is supposed to notice this, and it does not
  mention the one user-content surface that already shipped.
- Fix: amend `AUTH.md` §1.7 trigger 3 to record report `Details` as already-rendered user content,
  say why it does not reopen the cookie decision (React escaping, staff-only route, no HTML sink),
  and state what would: any HTML rendering of it, or exposing it outside the moderation page.

#### S-1-11 — `AUTH.md` §1.3's CSP directive list no longer matches what ships
- Severity: Low
- Where: `docs/decisions/AUTH.md:66-69` versus `ui/next.config.ts:93-118`
- Impact: the document says "`img-src` is `'self' data:`"; the shipped policy is
  `img-src 'self' data: blob: <api origins>` (`next.config.ts:103`), and the document mentions
  neither `worker-src` (`:102`) nor `frame-src` (`:109-111`). None of the additions is wrong — `blob:`
  is the avatar cropper, `worker-src` is Stockfish, `frame-src` is Turnstile's iframe — but §1.3 is
  the argument for *how much* the CSP buys, so its accuracy is the whole point of the section.
- Fix: restate §1.3's directive list from `buildCsp()` and note that `img-src` is now the widest of
  them. Nothing in the exfiltration analysis changes: `blob:` is same-origin, and neither addition is
  an outbound channel.

#### S-1-12 — The `/metrics` scrape token is compared with `==`
- Severity: Low
- Where: `src/KiriChess.Server/Infrastructure/MetricsEndpoint.cs:68`
- Impact: `authorizationHeader == $"Bearer {options.ScrapeToken}"` is an ordinal string comparison
  that returns early on the first differing byte. This is the only shared-secret comparison in the
  server (everything else — refresh tokens, API tokens, external login codes, 2FA tickets,
  unsubscribe tokens — is a hash lookup or an HMAC), so it is the only place the argument applies.
  Remote timing against a network-jittered path is not a practical attack; the fix is one line and
  removes the exception from an otherwise uniform posture.
- Fix: `CryptographicOperations.FixedTimeEquals` over the UTF-8 bytes of the two strings.

#### S-1-13 — `/api/auth/reset-password` distinguishes a known email from an unknown one
- Severity: Low
- Where: `src/KiriChess.Server/Auth/AuthEndpoints.cs:372-387`
- Impact: an unknown address returns a validation problem keyed `"Email"`; a known address with a
  bogus token falls through to Identity and returns one keyed `"InvalidToken"`, and a known address
  with a breached new password returns one keyed `"NewPassword"` before the token is ever checked.
  Three distinguishable answers make this an account-existence oracle at 30 requests per minute per
  IP, which is the one thing `/forgot-password` and `/resend-confirmation` two handlers above go out
  of their way to avoid (`:347-349`, `:364-366`).
- Fix: return the same `["Email"] = ["Invalid email or token."]` shape for an unknown email *and* an
  invalid token, and move the breached-password check to after `ResetPasswordAsync` succeeds so it
  cannot answer for an address the caller has no token for.

#### S-1-14 — A push subscription row is re-keyed to whoever last presents the endpoint
- Severity: Low
- Where: `src/KiriChess.Server/Infrastructure/Push/PushSubscriptionService.cs:24-33`
- Impact: `SubscribeAsync` upserts on `Endpoint` alone and reassigns `existing.UserId = userId`. The
  behaviour is deliberate and documented (a shared browser re-subscribing under a second account),
  but the security consequence is not stated: an attacker who obtains a victim's endpoint URL — which
  `L-2` correctly calls a capability URL — can move the row to their own account, silently stopping
  the victim's notifications and making the victim's browser show notifications generated by the
  attacker's games. Low because the endpoint is high-entropy and never exposed by any read endpoint.
- Fix: no code change is required, but the doc comment should say that re-keying is a deliberate
  accepted trade and that possession of the endpoint is therefore possession of the row. If it is
  ever cheap to do so, requiring the new `p256dh`/`auth` pair to differ from the stored one would
  catch a replay of a copied row.

#### S-1-15 — The HTTP move and resign paths bypass `GameCommandRouter`
- Severity: Low (trigger: `Redis:Configuration` set, i.e. the 8.4 multi-instance mode; the standing
  position is that Redis is unprovisioned today)
- Where: `src/KiriChess.Server/Games/GameEndpoints.cs:115,155` and
  `src/KiriChess.Server/Games/PublicApiEndpoints.cs:323,361`, all four calling `registry.Get(id)`
  directly, against `.claude/rules/realtime.md`'s "any new game action needs to go through the
  router"
- Impact: not a divergence risk — `GameRecoveryService.cs:92-96` claims ownership before restoring,
  so a non-owning instance genuinely has no local session and returns 404 rather than applying a move
  to a stale copy. But it means every HTTP move and resign, including the whole documented bot API,
  silently fails for any game the receiving instance does not own, and a bot's clock keeps running
  while it retries. The hub path already routes correctly (`GameActionExecutor.cs:113-126`).
- Fix: route these four through `IGameActionExecutor` the way `GameHub` does, or, if the HTTP move
  path is considered first-party-only legacy, state that in `PublicApiEndpoints`' header and have the
  bot docs point at the hub for moves.

### Already covered

- **`/api/auth/external/exchange` mints tokens with no ban check** —
  `docs/audits/C.md (C-1 improvement plan)`. Confirmed still present at
  `Auth/ExternalAuthEndpoints.cs:101-128`; the three other minting paths do check
  (`AuthEndpoints.cs:172`, `:231`, `:281`). Not re-reported; it belongs to that batch.
- **A ban revokes refresh and API tokens but not the live access token** —
  `docs/audits/C.md (C-1 improvement plan)`. Correct as written
  (`Moderation/SanctionService.cs:78-79`).
- **Turnstile's script loads on every route, not one** — `docs/audits/C.md (C-1 improvement plan)`,
  which correctly reads it as `AUTH.md` §1.7 trigger 2 not-quite-fired. Still unfixed; the file moved
  to `ui/app/[locale]/layout.tsx:90-95`.
- **Web push subscriptions survive account erasure** — `docs/audits/L.md:30-50`. The erasure
  transaction at `Account/AccountDeletionService.cs:62-78` still omits `WebPushSubscriptions`.
- **Stripe webhook: three-argument `ConstructEvent` and event coverage** — `docs/audits/funding.md:21-66`.
  Signature verification and idempotency both hold today: replay is blocked by
  `DonationReference.ConsumedAt` (`Account/PatronService.cs:41`), and amount and tier come from
  Stripe's payload, never the client.
- **Rate-limiting posture overall** — `docs/audits/I.md (I-1)`. S-1-5, S-1-6 and S-1-7
  are the three exemption-list gaps that summary's "every mutating HTTP surface" wording does not
  cover.
- **Missing tests for API tokens, the hub auth overlay, the Stripe webhook** — `docs/audits/C.md`.
  There is no `ApiTokenTests.cs` and no `StripeWebhookTests.cs`; scheme isolation *is* covered
  (`PublicApiTests.cs:133-176`), as is the Data Protection key ring (`DataProtectionKeyRingTests.cs`).
- **localStorage token placement, the CSP's limits, the moderator-2FA claim ride** —
  `docs/decisions/AUTH.md` §§1.1–1.7. Not re-derived; S-1-10 and S-1-11 are amendments to that
  document, not reversals of its decisions.

### Action list

1. S-1-1: revoke API tokens on `/api/auth/reset-password`, and add the line to
   `AUTH.md` §2's must-revoke list. Two lines plus a test.
2. S-1-2: require the account password on `/api/account/delete`,
   `/api/account/2fa/enable` and `/api/account/2fa/disable` (skip when `PasswordHash` is null).
   Consider the same on `POST /api/account/api-tokens`, which closes `AUTH.md` §2's named hole.
3. S-1-7: put `RequireRateLimiting` on the `/api/v1` group builders so no route can be
   added unlimited by omission; the DELETE is the current instance.
4. S-1-5 and S-1-6: cache `/openapi/v1.json`'s serialised document, and give
   `/health/ready` a few-seconds result cache. Both are small and both remove an anonymous
   amplifier from the exemption list.
5. S-1-4: give API tokens a finite default expiry and an `ExpiresInDays` request field,
   so the profile page's "Expires" column stops being decorative.
6. **Owner** S-1-3: rewrite the `/developers` hub-auth paragraph (six locale files) to
   document `Authorization: Bearer kc_…` as the way a bot authenticates to `/hubs/game`, and say
   that a token in a URL will be logged. Write it; you decide whether to keep the
   query path working for `kc_` at all.
7. S-1-9: move the `Groups.AddToGroupAsync` in `GameHub.JoinGame` below the participant
   check, with a test.
8. S-1-8: validate push endpoints before storing them — do this before web push is
   enabled in production, not after.
9. S-1-10 and S-1-11: amend `AUTH.md` §1.3 (restate the CSP from `buildCsp()`) and §1.7
   trigger 3 (record report `Details`). Documentation only; no decision is being reversed.
10. S-1-13 and S-1-12: flatten the `/reset-password` failure shapes; swap the `/metrics`
    token comparison to `FixedTimeEquals`.
11. S-1-15: fold into **C-5**, which owns realtime and the router rule, rather than
    fixing it standalone here.

---

## S-2 — Fair play
Date: 2026-08-31
Packet: docs/audits/INDEX.md

### Verdict

The fair-play system's ethics are in better shape than its arithmetic. Every structural decision here
is the right one for a one-person mod team: both detectors are advisory-only and nothing in the
codebase reads a `DetectionReportDto` and acts on it; `EngineAssistanceAnalyzer` refuses to speak
below 200 plies across 5 games and attaches a non-removable false-positive sentence to every
Watch/Elevated verdict; `SandbaggingAnalyzer` returns `Insufficient` rather than a low score and says
in its own source that "we cannot say" is not "they are clean"; sanctions are attributable, liftable,
and never issued by a query. That restraint is the correct answer to this packet's central question,
and it should not be traded away.

The defect is inside the maths, in the one signal that was written specifically for this variant. The
clock-pressure signal correlates a player's think time against a clock that the same think time has
already been subtracted from, so a fixed negative bias is baked into the correlation, and its size
grows with exactly the heavy-tailed thinking that signal 1 treats as the mark of a human. The unit
tests do not catch it because the fixture builds a clock the engine cannot produce. Around that sit
three honest coverage gaps that no detector reaches: nothing looks at a *pair* of accounts (the one
alt-abuse shape that needs no IP address to see), nothing records a disconnect that always returns
inside the grace window, and the human/bot pool boundary is keyed on which credential the client
chose to authenticate with. The engine-assistance detector is the sole backstop behind all three,
which is why fixing its arithmetic outranks extending its reach — and why the sweep this report
recommends must come second, not first. Sweeping with a signal biased toward "suspicious" does not
find cheaters; it manufactures suspects for a volunteer to adjudicate.

### Findings

#### S-2-1 — the clock-pressure signal regresses think time against a clock think time was deducted from
- Severity: High
- Where: `src/KiriChess.Server/Moderation/Detection/EngineAssistanceAnalyzer.cs:242-269` (covariance
  loop `:258-262`, `r` at `:263`, contribution at `:264`, weight `0.30` at `:69`). The x-axis is
  `MoveTimingFact.MoverTimeAfterSeconds`, documented at `:15-16` as "the mover's own remaining clock
  AFTER this move", projected at
  `src/KiriChess.Server/Moderation/Detection/EngineDetectionEndpoints.cs:135`. The accounting is at
  `src/KiriChess.Server/Games/GameSession.cs:215` (`_engine.Apply(Position, move, Config, think)`)
  and `:229-231`, which writes the post-Apply clock into `MoveRecord.WhiteTimeAfter`.
- Impact: for ply *i*, `TimeAfter = TimeBefore − think + economyDelta`. The signal correlates `think`
  against a variable containing `−think`, so the covariance carries a `−Var(think)` term that is pure
  arithmetic, not behaviour. The induced bias on `r` is approximately `−sd(think)/sd(clock)`, and the
  detector reads negative `r` as engine assistance at the joint-largest weight (0.30 of a 1.0 score,
  where 0.40 is Watch). It is worst for the most human player in the pool: a player who takes a few
  genuinely long thinks raises `sd(think)`, which is what signal 1 (`:202-213`, also weight 0.30)
  scores as *innocent*, while the same variance drives signal 3 toward the full accusatory
  contribution. The two signals therefore read the same behaviour in opposite directions, and on a
  180-second default clock (`config/kirichess.rules.json:31`) a handful of 30-second thinks is enough
  for the artifact alone to reach the `r <= −0.20` full-weight threshold. Aggravating, on the same
  axis: the economy moves this clock for reasons unrelated to pressure — a queen capture pays the
  captor `captureMult × value` = 25 seconds (`config/kirichess.rules.json:41` and `:39`), roughly 14%
  of the starting clock, plus per-piece tolls and check chips (`:56`) — and `MoveRecord.WhiteDelta` /
  `BlackDelta` (`src/KiriChess.Server/Data/Entities/MoveRecord.cs:28-29`), which record exactly that
  confounder, are not projected into `MoveTimingFact` at all. Advisory-only status is what keeps this
  off Critical: no ban follows automatically. But this is the report a moderator reads before deciding.
- Fix: use the mover's *previous* ply's `TimeAfter` — their clock entering the decision, already
  present in the same `SubjectPlies` list — as the pressure axis instead of the clock after their own
  think was charged. That removes the accounting identity and still measures the thing the signal
  claims to measure, with no new column and no new PII. Re-derive the `0.25` / `−0.20` cutoffs
  afterwards, since they were guessed against the biased quantity.

#### S-2-2 — the analyzer's test fixture builds a clock the engine cannot produce
- Severity: Medium
- Where: `tests/KiriChess.Server.Tests/EngineAssistanceAnalyzerTests.cs:35-51`, especially `:42`
  (`remaining = Math.Max(20, remaining - 5)`), `:44` (`baseThink = remaining * 12 * paceScale`) and
  `:48` (`if (j % 9 == 4) thinkMs += 9000`). Same construction in the metronomic fixture at `:62`.
- Impact: the fixture decrements the clock by a flat 5 seconds per ply regardless of `thinkMs`, then
  derives `thinkMs` from the clock — the causal arrow is the reverse of the game's, the ply's own
  think time is never subtracted from `remaining`, and no economy delta is ever applied. The 9-second
  long think at `:48` costs the fixture player nothing on their clock, which is the single behaviour
  S-2-1 is about. So `HumanLikeGame` is not a game this engine can generate, and every test that
  asserts a human scores `Unremarkable` is asserting it against data constructed to satisfy the
  analyzer's assumption rather than the variant's rules. This is why S-2-1 has been green since 8.5.
- Fix: build the fixture clock forward through the engine's own accounting — subtract the ply's think
  and apply a delta — so a long think visibly costs the fixture player time, and add one case with a
  large mid-game capture gain. Better still, generate a fixture from a real self-play game through
  `simulator/`, the way the engine's cost vectors already keep three consumers honest.

#### S-2-3 — the most serious accusation has no discovery path; the least serious one does
- Severity: Medium
- Where: `src/KiriChess.Server/Moderation/Detection/SandbaggingEndpoints.cs:51` maps a sweep
  (`GET /api/moderation/detect/sandbagging`, two-phase and capped at `MaxSweepPlayers` `:32`), surfaced
  as `SweepTab` in `ui/app/[locale]/moderation/page.tsx:715-720`.
  `src/KiriChess.Server/Moderation/Detection/EngineDetectionEndpoints.cs:25-27` maps only
  `/{userId}` and `/game/{gameId}` — per named account, per named game, no sweep.
- Impact: engine assistance can only ever be computed about someone a player already noticed and
  bothered to report. The UI states the argument against that arrangement in its own words, for the
  other detector: the report-driven views "by construction only find the abusers a player happened to
  notice and bother reporting; this is the one route that goes looking". The site has that route for
  rating manipulation and not for the thing TERMS §5 lists first. Practically, engine assistance in a
  small pool is also the *least* likely to be reported, because a stranger losing to a stronger
  player has no baseline to find it odd. (L-11-1 noted the pull-only shape as evidence for a TERMS
  overclaim; the asymmetry between the two detectors and its remedy are this packet's.)
- Fix: give the engine detector the sweep shape `SandbaggingEndpoints` already proves out — candidates
  by ply count over a window, capped, behind an explicit button, never a cron. **Sequence it after
  S-2-1.** On a one-person mod team a ranked list of "Elevated" strangers generated by a biased signal
  is worse than no list, because it converts arithmetic into a queue of accusations somebody feels
  obliged to work through.
- **Resolution (2026-09-08, B20a).** Owner declined the sweep. Named-user and named-game lookup stay
  the discovery path. TERMS §5 now states that engine-assistance analysis is run against a named
  account or a named game, typically after a report, and that there is no pool-wide engine scan.
  Revisit only if a confirmed engine case exists and named lookup is the bottleneck.

#### S-2-4 — nothing looks at the pair, which is the alt shape that needs no IP address
- Severity: Medium
- Where: `src/KiriChess.Server/Moderation/Detection/SandbaggingAnalyzer.cs:60-63` scores one `userId`
  in isolation; `EngineAssistanceAnalyzer.cs:104-107` likewise. No detector, query or endpoint takes
  two accounts. Separately confirmed: no IP address, user-agent or device identifier is persisted
  anywhere — `RemoteIpAddress` is read only for transient rate-limit partitioning
  (`src/KiriChess.Server/Infrastructure/RateLimiting.cs:450`) and hub connection budgets
  (`src/KiriChess.Server/Infrastructure/HubConnectionBudget.cs:254`), and neither stores it.
- Impact: two accounts run by one person feeding rating to each other is the multi-account abuse that
  actually pays on a rated ladder, and it is invisible here. Each account looks unremarkable alone:
  the feeder is a bad player, the fed account is a good one, and `SandbaggingAnalyzer`'s whole premise
  is *inconsistency within one account* (`:118-137`), which a dedicated feeder does not have. The
  useful point is that this particular shape needs no identity data at all — repeated-opponent
  concentration and net rating flow between a pair are both computable from the `Games` and
  `RatingHistory` tables the site already keeps, so the standing privacy position costs nothing here.
  The genuinely unclosable half (is this the same human?) should be written down as unclosable rather
  than left looking like an oversight.
- Fix: one query in the existing sweep shape — for each recently active account, the share of its
  rated games played against its single most frequent opponent, and the net rating transferred across
  that pair — surfaced as an advisory signal with the same caveat contract. Do not add IP or device
  logging to improve it; state in the moderation surface that same-person linkage is not detectable
  by design.

#### S-2-5 — the human/bot pool boundary is keyed on which credential the client chose
- Severity: Medium
- Where: `src/KiriChess.Server/Games/GameHub.cs:199-201` — `apiToken` is read from the
  `ApiTokenClaims.TokenUse` claim and passed as `botPool:` into `TryJoinAsync`. The `bot:` namespace
  itself is `src/KiriChess.Server/Ratings/RatingConstants.cs:24`, and both detectors correctly exclude
  it (`EngineDetectionEndpoints.cs:52`, `SandbaggingEndpoints.cs:219`).
- Impact: the segregation is real for a developer who follows `/developers` and mints an API token,
  and absent for one who does not. A script that authenticates with the account's own password and
  drives the ordinary hub is indistinguishable from a browser at the pool-join boundary, and lands in
  the human pool against a human rating. That is not a defect anyone can fully close — no site can —
  but it is worth stating plainly, because it means the bot pool is a courtesy to honest bot authors
  rather than a control, and the only thing actually standing behind it is the engine-assistance
  detector: the one with the biased signal (S-2-1) and no discovery path (S-2-3). (L-11-3 covers the
  TERMS carve-out and the challenge-path hole; this is the queue-join mechanism itself.)
- Fix: no new friction on honest bots. Record the finding as the reason S-2-1 and S-2-3 are the
  priority work, and note in `/developers` that playing the human pool from a script is the prohibited
  case regardless of which credential was used.

#### S-2-6 — a disconnect that always returns inside the grace window is invisible everywhere
- Severity: Medium
- Where: `src/KiriChess.Server/Games/GameConnectionTracker.cs:23` (`GraceSeconds = 30`), `:70-81`
  (`Track` cancels any armed timer on every reconnect) and `:194` (`CancelPendingAbandon`), with no
  cap on how many times that cycle may repeat. `src/KiriChess.Server/Games/AbandonmentPolicy.cs:51-53`
  counts only games that actually *terminated* `Abandoned` with the subject losing. No disconnect
  count is persisted on any entity — `MoveRecord` (`Data/Entities/MoveRecord.cs:22-33`) and `Game`
  carry none, and `SpectatorTracker`/`GameConnectionTracker` state is in-memory.
- Impact: dropping and returning inside 30 seconds never terminates a game as `Abandoned`, so it never
  reaches the abandonment cooldown, never reaches the sandbagging termination-skew signal
  (`SandbaggingAnalyzer.cs:231-242`, which reads `TerminationReason` on finished games), and leaves no
  row a moderator could look at when a report arrives. Meanwhile the opponent is the one on the clock
  and being charged, and is shown an `OpponentDisconnected` countdown
  (`GameConnectionTracker.cs:247`) that invites them to wait rather than move. On a variant where the
  clock is the win condition that is a direct competitive gain, and it is the conduct TERMS §5 bullet
  3 names in as many words. The `AbandonmentPolicy` design note is right that a disconnect is usually
  innocent — which is the argument for *recording* it rather than for punishing it.
- Fix: persist a per-game, per-player disconnect count (one integer on the `Game` row, incremented
  where the grace timer is armed) so the pattern is visible in the moderator dossier. No automatic
  consequence, no new cooldown: making it reviewable is the whole ask, and it is the cheapest way to
  turn an untestable complaint into a fact.

#### S-2-7 — the live feed is anonymous, undelayed, and leaves no record of who watched
- Severity: Low (trigger: the first credible relay report, or the first event with a prize, an
  invited field, or titled players — i.e. the first time 9f.1 tournaments unpark)
- Where: `src/KiriChess.Server/Games/GameHub.cs:259` — `JoinAsSpectator` is the one anonymous-reachable
  hub method, deliberately so and well argued at `:15-17`. It adds the caller to the game's SignalR
  group, which receives move pushes as they happen; `SpectatorTracker` holds a count in memory only.
- Impact: an assisting confederate watching a live game needs no account, is subject to no sanction,
  and leaves nothing behind. The assisted player's own timing is then the sole trace — which routes
  the entire vector through the signal S-2-1 biases. Two things make this genuinely low today: the
  anonymous spectator is the point of the feature for a brand-new variant, and an off-the-shelf engine
  does not understand the clock economy (captures paying the captor, per-piece tolls, check chips), so
  relayed advice is materially worse here than it would be at standard chess. That is a real defence,
  and it is nowhere written down.
- Fix: nothing to build at this size. Write the position down — spectating is open by design, secrecy
  is not the defence, and the timing detector is — so it reads as a decision. If the trigger fires, a
  delayed spectator feed fits the existing `KillSwitch` shape (`KillSwitch.Spectating`, already checked
  at `GameHub.cs:265`) without new infrastructure.

### Already covered

- **TERMS §5 overclaims the detectors, omits abuse and stalling, and has no bot carve-out** — L-11-1,
  L-11-2, L-11-3, L-11-7. Not re-reported; S-2-5 and S-2-6 give L-11 the mechanism behind two of them.
- **The abandonment cooldown is an automatic restriction no document names** — L-11-4.
- **Detector thresholds are unvalidated guesses** — stated by the code itself
  (`EngineAssistanceAnalyzer.cs:30-32`, `SandbaggingAnalyzer.cs:33-35`) and correct for a site with no
  confirmed cheater. Not a finding, and S-2-1 is a different claim: an arithmetic artifact, not an
  untuned cutoff.
- **Art. 22 / no solely-automated sanction** — settled at `docs/ROADMAP.md:383`. Every finding here
  keeps the advisory-only contract intact.
- **Ban evasion by a fresh email address** — TERMS §6 and `docs/ROADMAP.md:455`. S-2-4 is a different
  shape (two live accounts farming each other), not re-registration.
- **Appeal path and the moderator dossier** — **S-3**. **API token scopes, hashing and revocation** —
  **S-4**. **Rating constants, provisional ratings and the sandbagging interaction** — **C-7**.
  **Report intake authorization and rate limiting** — **S-1**.

### Action list

Ordered so that nothing widens the detector's reach before its arithmetic is trustworthy.

1. Move the clock-pressure axis to the mover's previous-ply `TimeAfter`. Smallest
   change with the largest effect on whether any of this is safe to act on.
2. Rebuild the fixture clock through the engine's accounting,
   including one capture-gain case, so item 1 is actually pinned.
3. **Owner — decision.** Re-derive the `0.25` / `−0.20` cutoffs and the `0.40` / `0.70` verdict bands
   against the corrected quantity, or state that they stay guesses until a confirmed case exists. Both
   are defensible; leaving the old numbers on a new quantity is not.
4. Persist a per-game disconnect count. Advisory only, no new cooldown.
5. **Owner — declined 2026-09-08 (B20a).** Do not add the engine-assistance sweep. Named-user and
   named-game lookup stay the discovery path. TERMS §5 records that there is no pool-wide engine
   scan. Revisit only if a confirmed engine case exists and named lookup is the bottleneck.
6. Pair-concentration and net-rating-flow signal from existing tables. No IP or
   device logging.
7. **Owner — writing, not code.** Record two positions where a reader will find them: same-person
   account linkage is not detectable by design (S-2-4), and the spectator feed is open by design with
   the timing detector as the defence (S-2-7). Fold the S-2-5 sentence into `/developers` alongside
   whatever L-11-3 resolves.

---

## S-3 — Moderation
Date: 2026-08-31
Packet: docs/audits/INDEX.md

### Verdict

The moderation machinery itself is careful, and in two places it is better than it needs to be: the
display-name identity normalizer folds Cyrillic, Greek, leetspeak and separators into one key and is
backed by a real unique index (`DisplayNameNormalizer.cs:43-107`, `KiriChessDbContext.cs:69-71`),
which closes impersonation more thoroughly than most sites this size bother with; and that index was
made *partial* with a written argument for why a per-user suffix would have been a trap
(`KiriChessDbContext.cs:62-68`). Sanctions are attributable to a person, never issued by a query,
never deleted, and a moderator cannot sanction themselves or another moderator
(`ModerationEndpoints.cs:150-156`). None of that is where the problems are.

The problems are all at the **boundaries** — where moderation meets some other subsystem that was
built by a different phase and does not know moderation exists. Account erasure frees the banned
account's email *and* its display name, and registration never asks whether the address it is handing
out belonged to a banned account, so the exact laundering path TERMS §6 says is prevented is open
(S-3-1). The Admin role is granted on an email-address match with no confirmation check, on a
deployment where confirmed email is deliberately off (S-3-2). And the sanctioned player's side of the
system is thin in a way the code does not admit: three separate places call the sanction row "the
appeal path", but the row is the moderator's record of their own action, and the ban message names no
channel at all (S-3-3). One more sits inside moderation proper: the single triage signal in the
report queue counts reports rather than reporters, and turns a player red at four, which one
determined account can produce in an afternoon (S-3-4).

On the packet's explicit question — **is the absence of an appeal path acceptable at this size?**
Yes, as a *process*. A one-person volunteer service does not owe a tribunal, a form, or an SLA, and
building one would be theatre. What is not acceptable is that the ban message is a dead end while a
contact address already exists in the footer, and that the codebase describes an audit record as
though it were a remedy. That is a two-line fix and a three-file comment correction, not a programme.

### Findings

#### S-3-1 — Erasure launders a ban: the retained sanction row is not connected to anything

- Severity: High
- Where: `src/KiriChess.Server/Account/AccountDeletionService.cs:82-84` (nulls `Email` and
  `NormalizedEmail`, clears `EmailConfirmed`) and `:95` (resets `DisplayName`), combined with the
  partial unique index at `src/KiriChess.Server/Data/KiriChessDbContext.cs:69-71`
  (`HasFilter("\"DeletedAt\" IS NULL")`, so the name leaves the index too);
  `src/KiriChess.Server/Auth/AuthEndpoints.cs:27-135` — the register handler checks the kill switch,
  consent, Turnstile, the display name and the breached-password list, and never once looks at
  `UserSanctions`; `src/KiriChess.Server/Account/AccountEndpoints.cs:49-50` — erasure carries
  `RequireAuthorization()` and no `SanctionPolicy` check. The claim being contradicted is
  `docs/legal/TERMS.md:58-62`.
- Impact: TERMS §6 tells every player, in bold, that "**Sanctions survive account deletion**: because
  deletion anonymises rather than deletes your account row, a sanction record is retained against the
  anonymised row **precisely so that deleting your account and registering again cannot be used to
  launder a ban**." The first half is true — the row is retained, `Restrict` foreign keys guarantee
  it. The second half is not implemented anywhere. The retained row points at a tombstone `Guid` and
  is read only by `SanctionPolicy.BlockingSanctionAsync(userId, …)`, which is keyed on the *new*
  account's id, so it can never match. Concretely: a banned player still holds a live access token
  for up to `Jwt:ExpiryMinutes` after `SanctionService.cs:78-79` revokes their refresh tokens, and
  `POST /api/account/delete` accepts it; that call releases their email address and their display
  name back into the pool; they then re-register with **the same address and the same handle** and
  are back in the rated pool with a clean record, which is not the "fresh email address" case S-2 and
  L-11 book as accepted but the specific case §6 promises is closed. The standing position in
  `INDEX.md` marks this ✅; retention is ✅, prevention is unbuilt. Not Critical because it takes a
  deliberate sequence and the volume of banned accounts today is zero — but this is the one control
  standing between a sanctioned harasser and the strangers they were sanctioned for.
- Fix: when an active `AccountBan` exists at erasure time, write a keyed HMAC of the account's
  `NormalizedEmail` onto the tombstone row before nulling the column, and have `/api/auth/register`
  refuse an address whose HMAC matches a tombstone with an unlifted, unexpired `AccountBan`. That
  keeps erasure genuinely irreversible for the player's data, keeps the pseudonymous marker under the
  same Art. 17(3)(e) basis `RETENTION.md:24` already uses for `Reports`, and makes the §6 sentence
  true. If that is more than the owner wants to build, the honest alternative is to change §6 to say
  the sanction record is retained for the moderator's history and that re-registration is not
  prevented — but do not leave the document claiming a control that does not exist.

#### S-3-2 — The Admin role is granted on an unverified email address

- Severity: High (trigger: any `Moderation:AdminEmails` entry that has no registered account yet —
  which `ModerationSeeder.cs:36` logs as a normal, expected state, and which is the state of every
  restore into an empty database and of every second moderator added to config before that person
  signs up)
- Where: `src/KiriChess.Server/Moderation/ModerationSeeder.cs:31-41` — `FindByEmailAsync(email)` then
  `AddToRoleAsync`, with no `EmailConfirmed` check anywhere in between; enabled by
  `src/KiriChess.Server/Auth/AuthEndpoints.cs:166`, where `Auth:RequireConfirmedEmail` defaults to
  `false` and `docs/ops/DEPLOYMENT.md:600` records that it "stays `false`" in production; the second
  factor at `src/KiriChess.Server/Program.cs:403-405` does not help, because the `mfa` claim comes
  from the holder enrolling their own authenticator.
- Impact: registration is open, issues tokens immediately (`AuthEndpoints.cs:135`), and never proves
  the registrant controls the address. So whoever registers an address listed in `AdminEmails` first
  gets the `Admin` role on the next boot, whether or not they own that mailbox — after which they
  self-enrol TOTP, satisfy `ModerationPolicies.Admin`, and hold the whole moderation surface: every
  account's email address (`ModerationEndpoints.cs:124`), the report queue, sanction issuance, and
  forced renames. The seeder's own comment treats "the configured admin may not have registered yet"
  as benign, and it is exactly the window. The two structural mitigations elsewhere are strong
  (`SeedAsync` never removes a role, so a typo cannot de-admin the owner; `AccountDeletionService.cs:78`
  drops `UserRoles` on erasure) — this is the one direction that is not guarded.
- Fix: add `if (!user.EmailConfirmed) { log and continue; }` before `AddToRoleAsync` in
  `ModerationSeeder`. One line, no behaviour change for the owner (who can confirm their own address),
  and it makes the "the configured admin may not have registered yet" window safe to leave open.

#### S-3-3 — The ban message is a dead end, and the code calls its audit record an appeal path

- Severity: Medium
- Where: `src/KiriChess.Server/Auth/AuthEndpoints.cs:451-457` — `BanMessage` renders the reason and
  the expiry and nothing else; `ui/components/SanctionBanner.tsx:47` is mounted only at
  `ui/app/[locale]/profile/page.tsx:138`, which an `AccountBan`ned player cannot reach because they
  cannot sign in. The phrase to correct is at
  `src/KiriChess.Server/Data/Entities/UserSanction.cs:19` ("the row history *is* the appeal path 6e.3
  said did not exist yet"), repeated at `src/KiriChess.Server/Moderation/SanctionService.cs:88` and
  rendered to the moderator at `ui/app/[locale]/moderation/[userId]/page.tsx:300` and `:459`.
- Impact: two separate things, and only one of them is a defect. The absence of an appeals *process*
  is fine at this size and should stay absent. But `Reason` is written to be read by the player
  (`UserSanction.cs:33` says so explicitly), and the moment they read it the conversation stops:
  `contact@kirichess.com` is published in the footer (`ui/components/Footer.tsx:27`) and the ban
  message does not mention it, so a wrongly-banned player's only route is to guess. The second half
  is a wording problem with teeth: three places assert an appeal path exists, and what they describe
  is the moderator's own immutable log of their own action. A future maintainer reading
  `UserSanction.cs:19` would reasonably conclude the player-facing side was built. It was not.
- Fix: append one sentence to `BanMessage` — that a player who believes this is a mistake can write to
  `contact@kirichess.com` — and reword the three comments to say the row history is the *audit record*
  of a sanction, not an appeal path. Optionally state in TERMS §6 that appeals are handled by email on
  a best-effort basis by one person, which is honest and commits to nothing.

#### S-3-4 — The queue's repeat-offender signal counts reports, not reporters

- Severity: Medium
- Where: `src/KiriChess.Server/Moderation/ModerationEndpoints.cs:234-238` — `GroupBy(r =>
  r.ReportedUserId).Select(g => g.Count())`; surfaced as
  `src/KiriChess.Server/Moderation/ModerationDtos.cs:34` (`OpenReportsAgainstReportedUser`, commented
  "repeat-offender signal, straight in the queue row") and rendered with escalating colour at
  `ui/app/[locale]/moderation/page.tsx:84-88` (gold at 2, `--loss` red at 4) and `:272-280`. The
  supply side is `src/KiriChess.Server/Moderation/ReportEndpoints.cs:55-59`, where duplicate
  suppression keys on `(ReporterId, ReportedUserId, GameId, Open)` — so the same reporter files a
  fresh row per game — against a budget of 20 per hour per account
  (`src/KiriChess.Server/Infrastructure/RateLimiting.cs:106`).
- Impact: this is the only triage signal in the queue, and it is the cheapest number in the system to
  inflate. One account that has played four games against a target can raise it to red on its own,
  inside the hourly budget, without ever repeating a `(target, game)` pair. Two alt accounts do it
  with no games at all, and S-2-4 establishes that same-person account linkage is undetectable here
  *by design* — a position I am not reopening, but it means nothing downstream will catch the
  inflation either. The result is that a brigade can aim the moderator's attention, which on a
  one-person team is the scarcest resource there is; and because the dossier's own counts
  (`ModerationEndpoints.cs:114-115`) are a second click away, the queue row is what actually decides
  what gets looked at first.
- Fix: count distinct reporters — `g.Select(r => r.ReporterId).Distinct().Count()` — which makes the
  number mean "how many different people complained" and costs nothing. Worth pairing with a second
  column, or a discount, for reporters whose prior reports were `Dismissed`, since that is the signal
  that separates a brigade from a genuine pattern.

#### S-3-5 — Nothing tells the moderator a report exists

- Severity: Medium
- Where: `src/KiriChess.Server/Moderation/ReportEndpoints.cs:63-74` — the report is inserted and the
  handler returns; no email, no push, no metric. Confirmed absent elsewhere: `KiriChessMetrics` emits
  no report or sanction instrument, and `infra/betterstack/main.tf:299` says in as many words that the
  report table "is otherwise read with psql". Discovery is the nav link at
  `ui/components/NavBar.tsx:40`, visible only once the admin is signed in and looking.
- Impact: the entire report pipeline is pull-only. A player who reports harassment gets a `201` and a
  `Status: Open` on `/api/reports/mine`, and nothing at all happens until the owner independently
  decides to open `/moderation`. For a hobby service that is a reasonable *service level* — but it is
  currently not even a mechanism: there is no signal that would tell the owner a queue exists to
  drain, so the latency on the first serious report is bounded only by how often he happens to log in
  as admin. This is also the operational half of what L-5 checks (a tiny EU host owes a working way to
  report abuse); intake works, and the loop is open at the other end.
- Fix: send one low-volume email to the configured admin address on report creation, reusing
  `IEmailSender` and the existing templates — or, if that is too chatty, a daily digest of the open
  count from the same place. Either is a handful of lines and turns the queue from pull-only into
  something that asks for attention once.

#### S-3-6 — Moderator *read* actions are audited only to the log stream

- Severity: Medium
- Where: `src/KiriChess.Server/Moderation/ModerationEndpoints.cs:93-96` — the 9d.3 line that logs
  "who looked" at a dossier; `src/KiriChess.Server/Moderation/Content/ContentEndpoints.cs:195-197` —
  the forced-rename log line. Both are `ILogger` calls. There is no moderation audit table anywhere in
  the schema (no `ModerationAuditLog`, no equivalent), while the *write* actions are durable by
  contrast: `UserSanction.IssuedByUserId`/`LiftedByUserId` (`UserSanction.cs:39,47`) and
  `Report.ReviewedByUserId` (`ModerationEndpoints.cs:71`).
- Impact: the comment at `:93-96` says this line is what answers "who looked", for a route that
  returns a stranger's email address, report history and sanction history. It answers that question
  only for as long as the log sink retains it, which is a Better Stack plan setting, not a property of
  this system — and the dossier read, the report-queue read, and every detector run against a named
  account are precisely the moderator actions that leave no other trace. The asymmetry is the finding:
  irreversible actions are recorded forever in Postgres, and surveillance-shaped actions are recorded
  in a stream that rolls off. With one moderator this is low-stakes; the moment there is a second, it
  is the only thing that could answer a question about the first.
- Fix: cheapest honest version is to amend the comment at `:93-96` to name the retention window the
  log line actually has, so the next reader does not over-trust it. If the owner wants the guarantee
  rather than the caveat, one small append-only table holding (moderator id, action, subject id,
  timestamp) written from these two sites plus the two detector endpoints is enough, and it costs one
  insert on routes that are already rare.

#### S-3-7 — `SanctionPolicy` fails open with no signal beyond a warning line

- Severity: Low (trigger: any sustained Postgres fault — a Neon cold start, suspend or failover, all
  of which `I-4` and `I-9` treat as routine on this plan)
- Where: `src/KiriChess.Server/Moderation/SanctionPolicy.cs:67-73`.
- Impact: every `BlockingSanctionAsync` call site — login (`AuthEndpoints.cs:172`), 2FA login (`:231`),
  refresh (`:281`), game creation (`GameEndpoints.cs:40`), queue join
  (`PlayEligibilityPolicy.cs:56,71`) — treats an exception as "no sanction", so a database wobble
  silently lifts every ban on the service for its duration. The trade is right and well argued (a
  database hiccup must not lock every player out), and I am not proposing to reverse it. What is
  missing is that the failure is invisible: `LogWarning` with no metric, no counter in
  `KiriChessMetrics`, and no Better Stack rule keyed on it, so nobody learns that bans were off for
  ten minutes.
- Fix: raise the level to `LogError` and give it a distinctive message, so the existing log-based
  alerting can key on one string. A counter would be better but is not needed to close this.

#### S-3-8 — A `Warning` may never be seen by the player it is issued to

- Severity: Low (trigger: the first `Warning` or `RatedPlayBan` actually issued)
- Where: `ui/components/SanctionBanner.tsx:47`, mounted only at
  `ui/app/[locale]/profile/page.tsx:138`; the data comes from `/api/moderation/my-sanctions`
  (`ModerationEndpoints.cs:208-223`).
- Impact: `SanctionType.Warning` is documented as "Recorded and shown to the player. Changes nothing
  about what they can do" (`UserSanction.cs:9`) — being shown to the player is the *entire* function of
  a warning, since it changes nothing else. It is shown on one page a player has no particular reason
  to visit. A `RatedPlayBan` is slightly better off, because the player will collide with it at the
  queue (`PlayEligibilityPolicy.cs:71`), but the explanation still lives on `/profile`. So the
  cheapest, most proportionate sanction in the vocabulary is the one most likely to go unread, which
  pushes a moderator toward a heavier one to be sure it lands.
- Fix: render `SanctionBanner` on the lobby as well as `/profile`, or check `my-sanctions` once per
  session and surface an active one wherever the player next lands. No new endpoint is needed.

### Already covered

- **A ban revokes refresh and API tokens but not the live access token** —
  `docs/audits/C.md (C-1 improvement plan)`. Not re-reported; S-3-1 uses that window as the means of
  reaching the erasure route, and is a claim about what happens *after* the token expires.
- **`/api/auth/external/exchange` mints tokens with no ban check** —
  `docs/audits/C.md (C-1 improvement plan)`, re-confirmed by `docs/audits/S.md:271-274`.
- **Erasure and 2FA-enable require no re-authentication** — `docs/audits/S.md:52-71` (S-1-2).
  Requiring the password on `/api/account/delete` would also narrow S-3-1's approach, but the two
  findings are independent: S-3-1 stands even for a player who knows their own password.
- **Report `Details` is rendered user content and an `AUTH.md` §1.7 trigger** —
  `docs/audits/S.md:187-201` (S-1-10). The maintainer-harassment angle on the same field is `B-2`.
- **Ban evasion by a *fresh* email address** — booked at `docs/legal/TERMS.md:60-62` and
  `docs/ROADMAP.md:454-456`, and treated as accepted by `docs/audits/L.md:140-141` and
  `docs/audits/S.md:193-194`. S-3-1 is the *same-address* case, which those documents explicitly
  claim is prevented.
- **TERMS §5 does not prohibit abuse, while the site solicits and sanctions it** —
  `docs/audits/L.md:45-59` (L-11-2). This is the contractual ground under most non-cheating
  sanctions and remains the cheapest fix in the moderation area.
- **The abandonment cooldown is a fourth adverse action §6 does not name** —
  `docs/audits/L.md:82-98` (L-11-4).
- **Detector arithmetic, thresholds, false-positive cost, and the missing engine-assistance sweep** —
  `docs/audits/S.md` in full. `Moderation/Detection/` is that packet's; I read it only to confirm no
  detector writes a `UserSanction` (it does not — `UserSanction.cs:20-22` holds). The sweep half was
  declined 2026-09-08 (B20a / S-2-3): engine assistance stays named lookup after a report.
- **Same-person account linkage is undetectable by design** — `docs/audits/S.md:101-122` (S-2-4).
  S-3-4 depends on that being true and does not reopen it.
- **Art. 22 / no solely-automated sanction** — settled at `docs/ROADMAP.md:383`. Every finding here
  keeps that contract intact.
- **Sanctions-survive-deletion vs Art. 17, and API tokens in export/erasure** — `docs/audits/L.md`.
  S-3-1 is the enforcement question, not the lawfulness one, and its proposed fix is deliberately
  shaped to stay inside L-2's conclusion.
- **Display-name policy as implemented matches TERMS §4** — verified at `docs/audits/L.md:120-123`.
  I found no impersonation gap to add: `DisplayNameNormalizer.cs:43-107` and the unique index hold.

### Action list

1. **Owner** S-3-1. Decide between building the tombstone email marker and
   amending TERMS §6 to stop claiming the control. Everything else in this report is smaller than this
   one choice, and the standing position in `INDEX.md` should be re-marked from ✅ to ~ until it is
   made.
2. S-3-2. One `EmailConfirmed` check in `ModerationSeeder.SeedAsync`. Smallest fix in the
   report, and it is the only finding here that hands over the whole moderation surface.
3. S-3-4. Count distinct reporters in `ToReportDtosAsync`. One LINQ change plus a test that
   two reports from one account do not turn the row red.
4. S-3-3. Add the contact sentence to `BanMessage`; reword "appeal path" to "audit record"
   in the three places that say it. Pair the wording change with whatever L-11-2 does to §5, since both
   are TERMS-adjacent and share a version bump.
5. S-3-5. One admin notification (or daily digest) on report creation, reusing
   `IEmailSender`.
6. S-3-8. Mount `SanctionBanner` outside `/profile`. Do this before the first `Warning` is
   issued, not after.
7. S-3-7. `LogWarning` → `LogError` with a distinctive message in `SanctionPolicy`.
8. **Owner** S-3-6. Either name the log-retention window in
   the `ModerationEndpoints.cs:93-96` comment, or add the small append-only audit table. The first is
   honest and free; the second only earns its keep if a second moderator ever exists.

---

## S-4 — Public / bot API
Date: 2026-08-31
Packet: docs/audits/INDEX.md

### Verdict

The core of 9f.5 is right, and unusually deliberately right. Scope enforcement is real and layered
(`Program.cs:408-429`): a `read` token cannot reach any `play` route, a `kc_` token cannot satisfy an
unversioned `RequireAuthorization()` because `ApiToken` is never the default scheme, and the token
principal is stamped without `role` or `mfa` even for the admin account
(`ApiTokenAuthenticationHandler.cs:36-48`). Secrets are 256 CSPRNG bits stored only as SHA-256
(`ApiTokenService.cs:151-158`) with a unique index (`KiriChessDbContext.cs:185`), never returned after
issue, and never exported. Every hot claim is covered by a real integration test
(`PublicApiTests.cs:81-346`). Public and internal OpenAPI are genuinely separated at the path level
(`Program.cs:839-847`). Rate limiting is partitioned by token id exactly as the packet asks
(`RateLimiting.cs:459-463`).

The theme of what is left is **the two halves of this surface do not enforce the same things**.
`/api/v1` re-checks the token in the database on every single request, so a revocation bites
instantly there; `/hubs/game` checks it once, at connect, and never again, so the same revocation, and
an `AccountBan` that `SanctionService.cs:73-80` explicitly says must "bite now", does not reach the
socket where rated bot-pool play actually happens. The same asymmetry runs through the limits: HTTP is
bounded per token id, the hub is bounded per connection id, and nothing anywhere is bounded per
account, so ten tokens buy ten times the documented ceiling on a surface that `RateLimiting.cs:378`
also exempts from the global backstop. Nothing here is a Critical: no stranger's data is reachable,
and there is no scope escalation or IDOR on `/api/v1`.

Most of what a first pass would flag on this packet is already filed. S-1 owns the no-expiry column,
the password-reset gap, the token-in-a-URL instruction, and the unlimited DELETE; S-5 owns the absent
`/api/v1` kill switch; M-6 owns the `/developers` and public-document usability gaps; arch-speed owns
the `/account/playing` 200-game scan. See "Already covered" for the one-liners.

### Findings

#### S-4-1 — Revoking a token, or banning the account, does not close its live hub connection
- Severity: High
- Where: `src/KiriChess.Server/Program.cs:420-429` (`ApiTokenPolicies.HubPlay`),
  `src/KiriChess.Server/Games/GameHub.cs:74-237` (every player method carries only that policy),
  `src/KiriChess.Server/Moderation/SanctionService.cs:73-80`,
  `src/KiriChess.Server/Auth/ApiTokenService.cs:77-98`; test gap at
  `tests/KiriChess.Server.Tests/PublicApiTests.cs:268-270`
- Impact: SignalR authorizes a hub method against the `ClaimsPrincipal` captured when the connection
  was established, not against a freshly authenticated one. `HubPlay` therefore re-runs on every
  invocation but only ever re-reads claims that were frozen at connect time, and
  `ApiTokenService.FindValidAsync` (the only thing that consults `RevokedAt`, `ExpiresAt` and
  `User.DeletedAt`) is never called again for the life of that socket. `AddAuthenticationSchemes` on
  the policy does not change this: it is honoured by the HTTP authorization middleware, not by the hub
  dispatcher. So a bot whose token the owner has just revoked, or whose account a moderator has just
  banned, keeps calling `JoinQueue`, `JoinGame`, `SubmitMove`, `Resign` and `AcceptRematch` for as long
  as it holds the socket open, and a bot is precisely the client built to hold one open for days. This
  is the half of the surface where the damage lands: `/api/v1` play is always unrated
  (`PublicApiEndpoints.cs:278-279`), while hub `JoinQueue` is the rated bot-pool path
  (`GameHub.cs:199-208`), so the connection that survives revocation is the one that moves ratings.
  `SanctionService.cs:73-80` revokes API tokens specifically so the ban is immediate; that intent
  holds on HTTP and silently does not on the hub. High rather than Critical because the next reconnect
  fails closed and the blast radius is the banned account's own games, not a stranger's data.
- Fix: keep a revocation signal the hub can see without a per-invocation database round trip. The
  cheapest shape that fits this codebase: have `SanctionService` and `ApiTokenService.RevokeByIdAsync`
  /`RevokeAllForUserAsync` publish the affected user id, and add a hub filter (or reuse
  `GameConnectionTracker`) that aborts connections whose `token_id` or subject matches. A
  low-tech alternative that closes most of it is a periodic re-validation on any connection carrying
  `token_use=api`. Either way, add the missing assertion beside `PublicApiTests.cs:268-270`: ban the
  account while a hub connection is open and assert the next `SubmitMove` fails.

#### S-4-2 — Nothing bounds the bot API per account; ten tokens buy ten times every limit
- Severity: Medium
- Where: `src/KiriChess.Server/Infrastructure/RateLimiting.cs:459-463` (`TokenKey` partitions on
  `token_id` alone), `:328-361` (the three `/api/v1` policies use it), `:377-379` (`/api/v1` is
  exempt from the global per-IP backstop), `src/KiriChess.Server/Auth/ApiTokenClaims.cs:28`
  (`MaxActivePerUser = 10`), `src/KiriChess.Server/Infrastructure/HubRateLimitFilter.cs:57` (hub
  buckets keyed on `ConnectionId`); the behaviour is pinned as intended by
  `tests/KiriChess.Server.Tests/PublicApiTests.cs:323-346`
- Impact: per-token partitioning is the right primitive and the packet asks for it, but there is no
  ceiling above it. One account may hold ten active tokens, so its real limits are 3000 reads/minute,
  120 game creations/minute and ten independent 60-token move buckets, not the "per token" numbers
  `/developers` publishes (`ui/messages/en.json`, `limitRead`/`limitCreate`/`limitMove`). Because
  `RateLimiting.cs:378` exempts the whole of `/api/v1` from the global per-IP limiter, there is
  nothing underneath to catch it, and `POST /api/v1/games` runs no
  `EnsureNotAlreadyInGameAsync` (`PublicApiEndpoints.cs:272-307`), so those 120 creations a minute are
  120 `Game` rows plus 120 live `GameSession`s. On the hub the same gap is worse in kind: the token
  bucket is per connection id, so throughput scales linearly with sockets, capped only by
  `HubConnectionBudget`'s per-IP 20 (`HubConnectionBudget.cs:115-143`), and API-token connections count
  as authenticated (`:194-198`) so they draw from the reserve 10a.2 sized for human players. Medium and
  not High because every one of these is a resource claim by an identified, bannable account, not a
  stranger-facing failure.
- Fix: add one per-user backstop above the per-token partitions rather than tightening any per-token
  number. A `RateLimitPolicies.ApiAccount` fixed window on `PartitionKey(httpContext)` applied to the
  `/api/v1` group builders (`PublicApiEndpoints.cs:33-38`) is the smallest version, and it composes
  with the group-level policy S-1-7 already asks for. For the hub, keying `HubRateLimitFilter`'s
  buckets on the subject rather than the connection id for `token_use=api` callers would close the
  socket-multiplication half.

#### S-4-3 — `kc_` is too short and checksum-free to be caught by leaked-credential scanning
- Severity: Medium (trigger: the repo is public and the tokens are meant to be pasted into
  third-party bot code, which is where credentials leak)
- Where: `src/KiriChess.Server/Auth/ApiTokenClaims.cs:27` (`Prefix = "kc_"`),
  `src/KiriChess.Server/Auth/ApiTokenService.cs:151-156` (`kc_` plus 43 base64url characters, no
  checksum), `ApiTokenAuthenticationHandler.cs:51-71` and `Program.cs:299-311` (both dispatch purely
  on that three-character prefix)
- Impact: the entropy is fine; the *shape* is what fails. `kc_` is three characters with no vendor
  identity, so it cannot be registered as a distinctive pattern with GitHub secret scanning or any
  similar service, and there is no trailing checksum, so a scanner cannot distinguish a real token
  from any other `kc_`-prefixed string without asking this server. That matters more here than it
  normally would, because the two controls that would otherwise limit a leak are both absent by
  finding: the token never expires (S-1-4) and `/developers` currently instructs bot authors to put it
  in a URL (S-1-3). Detection is the only remaining line, and the format forecloses it. The cost of
  fixing it is only ever going to rise: today the token table is effectively empty and the format is
  a pre-launch decision, after launch it is a migration.
- Fix: move to a longer, unmistakable prefix (`kirichess_pat_` is the conventional shape) and append a
  short checksum of the random part, then dispatch on the new prefix in the two `StartsWith` sites
  above. Do it before launch so no live token has to be rotated, and record the format in
  `/developers` so a scanner rule can be written against it.

#### S-4-4 — `PresenceDto` ships in the public OpenAPI document for a hub a `kc_` token cannot use
- Severity: Low
- Where: `src/KiriChess.Server/Program.cs:839-847` (the `ShouldInclude` filter is applied to *paths*
  only) and `:851` (`HubPayloadDocumentTransformer` is added to both documents unconditionally);
  result at `docs/api/openapi-public.json:1695` (`PresenceDto`), plus eleven other schemas referenced
  by no path; `src/KiriChess.Server/Auth/HubAuthOverlayMiddleware.cs:15` states that presence stays on
  the JWT default and "a `kc_` token there is simply anonymous"
- Impact: the packet's separation question is "internal-only endpoints must not leak into
  `openapi-public.json`", and the path half of that holds exactly. The schema half does not: the hub
  payload transformer runs identically on both documents, so twelve transport-only schemas land in the
  public artifact, and one of them describes `/hubs/presence`, which no API token can authenticate to.
  Nothing secret is disclosed (these are payload shapes for a hub whose contract is public in
  `HubContract.cs`), but it is an internal surface appearing in the third-party promise, and the
  public document's `info.description` (`Program.cs:856-861`) never mentions the
  `x-kirichess-transport` extension at all, so a reader has no way to know what these twelve entries
  are. Only the internal document's description explains it (`:862-868`).
- Fix: pass `publicDocument` into `HubPayloadDocumentTransformer` and have it emit only the
  `/hubs/game` payloads a `play` token can actually reach, dropping `PresenceDto`. Add one sentence
  about `x-kirichess-transport` to the public description while the file is open. Regenerate and let
  `OpenApiContractTests` re-baseline.

#### S-4-5 — The ten-token cap is a read-then-write with no transaction or constraint behind it
- Severity: Low
- Where: `src/KiriChess.Server/Auth/ApiTokenService.cs:38-60` (`CountAsync`, then `Add` +
  `SaveChangesAsync`, no transaction), `src/KiriChess.Server/Data/KiriChessDbContext.cs:180-189` (the
  only unique index is on `TokenHash`), rate limit `RateLimitPolicies.ApiTokenManage` at 20/hour
  (`RateLimiting.cs:153`)
- Impact: concurrent `POST /api/account/api-tokens` calls can each read a count below the cap and each
  insert, so an account can hold more than ten active tokens. On its own this is a product rule
  quietly not holding; combined with S-4-2 it is a direct multiplier on the only per-account budget
  this surface has, since every extra token is another full set of `/api/v1` limits. Low because the
  window is small, the caller is authenticated and bannable, and 20/hour caps how far it can go.
- Fix: wrap the count and the insert in one transaction with an appropriate isolation level, or drop
  the count in favour of a `SELECT ... FOR UPDATE` on the user row. A partial unique index is not
  available here (the cap is a count, not a uniqueness rule), so the transaction is the honest fix.

#### S-4-6 — `GET /api/v1/challenges/mine` is a read that requires the `play` scope
- Severity: Low
- Where: `src/KiriChess.Server/Games/PublicApiEndpoints.cs:415-425` (mapped on the `play` group at
  `:37-38`, yet carrying `.RequireRateLimiting(RateLimitPolicies.ApiRead)` at `:423`)
- Impact: it is the only `GET` on `/api/v1` that a `read` token cannot call, and the endpoint's own
  rate-limit policy says the author thought of it as a read. A read-only bot that wants to report on
  the account's outstanding challenges must be given `play`, which also grants game creation, moves
  and resignation, so the least-privilege scope split stops being usable for exactly the monitoring
  case it exists for. `/developers` does not list challenges under either scope
  (`ui/messages/en.json`, `scopeRead`/`scopePlay`), so the reader cannot tell this is deliberate.
- Fix: move the route to the `read` group, or, if listing challenges is meant to be a `play`-only
  capability, say so in `scopeRead`/`scopePlay` and swap the rate-limit policy to
  `ApiPlayCreate` so the code stops contradicting itself.

### Already covered

- **API tokens never expire; `ExpiresAt` is dead schema** — `docs/audits/S.md:92-105` (S-1-4).
  Confirmed still true: `ApiTokenService.cs:47-58` sets no expiry and `AccountDtos.cs:123` has no
  field for one. It is the multiplier under S-4-1 and S-4-3, not a separate finding.
- **A password reset revokes sessions but not API tokens** — `docs/audits/S.md:35-50` (S-1-1).
- **`/developers` tells bot authors to put a `kc_` token in a URL query string** —
  `docs/audits/S.md:73-90` (S-1-3).
- **`DELETE /api/v1/challenges/{token}` has no rate limit, and `/api/v1` is exempt from the global
  backstop** — `docs/audits/S.md:137-151` (S-1-7). S-4-2 is the account-level gap above that route
  level one, not a restatement of it.
- **`/openapi/v1.json` is anonymous, uncached and limiter-exempt** — `docs/audits/S.md:107-120`
  (S-1-5).
- **HTTP move and resign bypass `GameCommandRouter`, including the whole bot API** —
  `docs/audits/S.md:253-267` (S-1-15), handed to **C-5**.
- **No kill switch can disable `/api/v1`** — `docs/audits/S.md:88-100` (S-5-4), which names the
  overlap with this packet explicitly. Confirmed: only `GET /api/v1/seeks` consults one
  (`PublicApiEndpoints.cs:260`).
- **`openapi-public.json` carries no `security` requirement and no `servers`** —
  `docs/audits/M.md:94-108` (M-6-5). S-4-4 is the schema-curation half, which that finding does not
  cover.
- **`/developers` omits the token cap, expiry and revocation path; names a preset that does not
  exist; omits `JoinGame`; has no stability statement or example bot** — `docs/audits/M.md:29-125`
  (M-6-1 through M-6-7).
- **`/api/v1/account/playing` caps the live scan at 200 and filters client-side** —
  `docs/audits/I.md (I-1)` (AS-9) and `docs/audits/I.md:53` (I-1-3). Confirmed at
  `PublicApiEndpoints.cs:60`.
- **Bot-pool segregation is honour-based for a scripted client on a JWT** —
  `docs/audits/S.md:126-139`. The token side of that boundary holds:
  `RemapApiTokenQueuePool` (`MatchmakingService.cs:70-76`) is the only way into `bot:*`, and
  `PublicApiTests.cs:284-321` pins both directions.
- **Missing `ApiTokenTests.cs` / thin API-token coverage** — `docs/audits/C.md:184`. The hub
  revocation assertion S-4-1 asks for belongs in that list.
- **A ban revokes refresh and API tokens but not the live access token** —
  `docs/audits/C.md (C-1 improvement plan)`. S-4-1 is the hub-connection analogue on the token
  surface, which that item does not reach.

### Action list

1. S-4-1: make revocation and `AccountBan` reach an open `/hubs/game` connection, and add
   the ban-while-connected test beside `PublicApiTests.cs:268-270`. This is the one finding here that
   defeats a guarantee the code already states it is making.
2. S-4-3: change the token format to a long, distinctive prefix plus a checksum, before
   launch, while no live token has to be rotated. Fold it into the same change as S-1-4's default
   expiry so the format and lifetime are decided once.
3. S-4-2: add one per-user backstop policy on the `/api/v1` group builders, layered on top
   of the per-token partitions rather than replacing them, in the same edit as S-1-7's group-level
   `RequireRateLimiting`. Separately, decide whether hub buckets should key on the subject for
   `token_use=api` callers.
4. S-4-5: put the cap check and the insert in one transaction.
5. S-4-4: scope `HubPayloadDocumentTransformer` to `/hubs/game` for the public document
   and explain `x-kirichess-transport` in its description. Do it in the same pass as M-6-5's
   `security`/`servers` fix so the document is regenerated once.
6. **[Owner]** S-4-6: decide whether listing challenges is a `read` or a `play` capability, then make
   the group, the rate-limit policy and `/developers` agree.

---

## S-5 — Kill switch

Date: 2026-08-29
Packet: docs/audits/INDEX.md

### Verdict

The kill-switch mechanism (Phase 10c.4) is well-built for what it wires: a two-column table read
into a 10-second in-process cache, a write that updates cache and database together, an admin
GET/PUT behind `ModerationPolicies.Admin` with `UpdatedByUserId` attribution, and — the constraint
the roadmap item pinned — a stated `{"error":...}` reason on every gated path rather than a dead
socket. `KillSwitchService`'s fail-open-on-read-error behaviour is deliberate and correct: a
database blip must not stack a second outage on top of the first.

The gap is coverage, not machinery. Two of the four switches guard a single entry point each while
the threat they are named for has several. `Pairing` — described in its own source as "the
stop-the-world lever … nobody enters a new game while it holds" — is checked only in
`GameHub.JoinQueue`; challenge accept, rematch accept, `POST /api/games` and `POST /api/v1/games`
all reach `GameRegistry.CreateAsync` (a durable `Game` row from ply 0, exactly the write the switch
exists to stop) without ever consulting it. `Registration` is checked in `/api/auth/register` but
not on the Google OAuth account-creation path, which goes live at launch (DH.5 / #13). And the
lever is undiscoverable: no `/moderation` UI panel, no line in `DEPLOYMENT.md`, no runbook — a
responder has to already know the endpoint exists, mint an admin token, and `curl` it during the
incident.

### Findings

#### S-5-1 — `Pairing` kill switch does not stop most game creation

- Severity: High
- Where: `src/KiriChess.Server/Games/GameHub.cs:196` (the only enforcement) versus
  `src/KiriChess.Server/Games/GameHub.cs:176` (rematch accept),
  `src/KiriChess.Server/Games/ChallengeEndpoints.cs:188` (challenge accept, shared by first-party
  and `/api/v1`), `src/KiriChess.Server/Games/GameEndpoints.cs:79` (`POST /api/games`),
  `src/KiriChess.Server/Games/PublicApiEndpoints.cs:306` (`POST /api/v1/games`).
- Impact: `KillSwitch.cs:19` states that flipping `Pairing`
  means "nobody enters a new game" — the response to an unwell database. In fact only the
  matchmaking queue stops. A player mid-session can still start a rematch, any challenge link still
  resolves to a live game, and both `POST /api/games` (bot/custom/solo games) and the public bot
  API's `POST /api/v1/games` keep inserting `Game` rows and spinning up `GameSession`s. During the
  exact incident the switch is for, four of five creation paths keep hitting Postgres, and the
  operator believes they are stopped. False confidence in an incident lever is worse than not
  having it.
- Fix: move the check into `GameRegistry.CreateAsync` itself (one gate, every path), or — if the
  intent is genuinely "queue only" — rename the switch and correct `KillSwitch.cs:19`,
  `KillSwitchState.cs` and `KillSwitchService.cs:9-15` so the lever's stated
  scope matches its real one. The first option is the one the incident wants.

#### S-5-2 — `Registration` kill switch is bypassed by Google OAuth signup

- Severity: Medium (trigger: DH.5 / Todo #13 makes Google OAuth live at launch; harmless only
  while OAuth is dark)
- Where: `src/KiriChess.Server/Auth/ExternalAuthEndpoints.cs:78` →
  `src/KiriChess.Server/Auth/ExternalLoginService.cs:112-140` (`SignInOrCreateAsync` creates an
  account with no `KillSwitchService` dependency anywhere on the path). Compare
  `src/KiriChess.Server/Auth/AuthEndpoints.cs:37`.
- Impact: `Registration` is named for "a signup flood". Once the Google consent screen is live,
  the OAuth callback is a second account-creation path that the switch does not touch, so a
  scripted OAuth flood (or simply organic signups the operator is trying to pause) proceeds while
  `Registration` reads disabled. The blast radius is smaller than the password path — each account
  needs a distinct Google identity — but "smaller" is not "gated", and the operator has no way to
  tell from the switch state.
- Fix: check `KillSwitch.Registration` in `ExternalAuthEndpoints` before calling
  `SignInOrCreateAsync`, gating only the `Created` outcome path (an existing user signing in is not
  registration and should not be blocked). Simplest: pass the switch state into
  `SignInOrCreateAsync` and have it return a distinct "registration paused" outcome when it would
  otherwise create.

#### S-5-3 — no operator can find the kill switches during an incident

- Severity: Medium
- Where: `docs/ops/DEPLOYMENT.md` (no occurrence of "kill switch" anywhere, including §10
  monitoring/alerting and the incident text around `:659`); `ui/app/[locale]/moderation/page.tsx:53`
  (tab set is `reports | sanctions | sweep | names | lookup | retention` — no kill-switch panel);
  the only client-side artifact is `ui/lib/api.generated.ts`.
- Impact: The packet's own question — "whether a future on-call would find it" — resolves to no.
  The mechanism is reachable only as a raw `GET`/`PUT /api/moderation/kill-switches` with an
  Admin+`mfa` bearer token. Under load, the responder must remember the feature exists, know the
  route, and produce an admin token by hand. `DEPLOYMENT.md:659` already concedes "nobody is
  staffed to answer a page"; the one lever that needs zero deploy is also the one with zero
  documentation.
- Fix: add a short "Operational kill switches" subsection to `DEPLOYMENT.md` §10 — the four names,
  the threat each maps to, the exact `curl` (route, `{"enabled":false}` body, the Admin/`mfa`
  requirement), and the ~10s propagation window. A `/moderation` "Controls" tab over the existing
  generated client is the fuller fix but the doc is the one that matters in the first incident.

#### S-5-4 — no lever disables the public `/api/v1` surface

- Severity: Low (overlaps S-4; noted here because the packet names "the API" explicitly)
- Where: `src/KiriChess.Server/Games/PublicApiEndpoints.cs` — `SeekList` gates `GET /api/v1/seeks`
  (`:260`) but nothing gates `POST /api/v1/games` (`:306`), `POST /api/v1/games/{id}/moves`
  (`:315`), or `POST /api/v1/challenges` (`:397`).
- Impact: If abuse or load originates from bot traffic rather than the human lobby, flipping
  `Pairing` and `SeekList` still leaves the token-authenticated bot API fully able to open games
  and push moves. The only response left is a deploy (revoke tokens / pull the endpoints), which
  10a.3 says drops every live game — the outcome 10c.4 was written to avoid.
- Fix: either add an `Api` member to `KillSwitch` checked once in the `/api/v1` route group, or
  document that per-token revocation (`ApiTokenEndpoints`) plus `RateLimitPolicies.ApiPlayCreate`
  is the accepted answer at this scale and that a coarse API switch is deliberately out of scope.

#### S-5-5 — `Spectating` and `SeekList` propagation gap is real but bounded

- Severity: Low
- Where: `src/KiriChess.Server/Infrastructure/KillSwitchService.cs:26` (`CacheDuration = 10s`),
  `:80` (read-your-own-write is per-instance only).
- Impact: A flip takes effect immediately on the instance that served the `PUT` and up to 10
  seconds later on every other instance. Fine for the current single Fly machine; worth a sentence
  in the runbook from S-5-3 so a future multi-instance operator does not read "still enabled on one
  node" as the switch having failed. Not a defect — it is the documented cost of not building a
  push mechanism for four booleans — just an undocumented one.
- Fix: one line in the DEPLOYMENT.md subsection: "propagates within ~10s per instance; the node you
  PUT to is instant."

### Already covered

- **Spectator overlay bypasses the `Spectating` switch and the tracker** — `docs/audits/C.md`
  (finding at `C.md:171`, `GameHub` spectator-count path that skips both
  `KillSwitch.Spectating` and `SpectatorTracker`). Not re-reported here; the fix for that finding
  also closes a `Spectating` hole.
- **Kill-switch `503` not declared for client codegen** — `docs/audits/C.md` / `docs/audits/C.md`
  (`C.md:138`, `/register`'s kill-switch 503 missing from the OpenAPI contract). Contract-drift
  packet, not this one.
- **`AddKillSwitches` fails closed on a dropped table** — `docs/ops/MIGRATIONS.md:81` already
  documents that dropping the table silently reverts every switch to "enabled" and that a
  mid-incident disable would be lost. No new finding.
- **No on-call rotation / every alert emails the team** — `docs/ops/DEPLOYMENT.md:659`, ROADMAP 8.7.
  S-5-3 is about discoverability of one specific lever, not staffing.

### Action list

1. S-5-1: move the `Pairing` check into `GameRegistry.CreateAsync` so every creation
   path is gated, and adjust the doc comments; or, if "queue only" is the real intent, rename the
   switch and fix the four places that overclaim its scope. Add a `KillSwitchTests` case per
   creation path.
2. S-5-3 + S-5-5: write the "Operational kill switches" subsection in `DEPLOYMENT.md`
   §10 (names, threats, `curl`, admin/`mfa` requirement, ~10s per-instance propagation).
3. S-5-2: gate the Google OAuth `Created` path on `KillSwitch.Registration`, before
   #13 puts OAuth live.
4. **Owner** — S-5-4: decide whether the public `/api/v1` surface gets its own kill switch or
   whether token revocation + rate limiting is the accepted answer; record the decision (this
   feeds S-4).
5. **Owner** — optional: a `/moderation` "Controls" tab over the existing generated client,
   so the lever has a UI and not only a runbook `curl`.

