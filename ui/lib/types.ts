// The wire contract, as the server actually defines it (Phase 9d.7, completed by Phase 9e.9).
//
// Every type in this file is an alias of a schema generated from docs/api/openapi.json, there is
// no hand-written mirror of a C# DTO left here, which is the whole point: before 9d.7 these were typed
// by hand from reading the C# and drifted silently (RatingHistoryPointDto had literally no field in
// common with the record the server sends). Change the C# DTO and this file's consumers stop
// compiling, which is what a contract is for.
//
// The SignalR hub payloads at the bottom were the one exception until Phase 9e.9 and are no longer:
// see the heading down there for how a payload that never touches HTTP ends up in an HTTP document,
// and for the one caveat that comes with it.

import type { components } from "./api.generated";

type S = components["schemas"];

// --- Core game types ---

export type Side = S["GameStateResponse"]["sideToMove"];

export type GameStatus = S["GameStateResponse"]["status"];

/**
 * `Termination` and `GameStatus` are genuinely different unions with a genuinely different spelling of
 * flagfall (`Flagfall` vs `FlagFall`). That is correct and pre-existing, not a typo introduced by the
 * 9d.7 cutover.
 */
export type Termination = S["GameStateResponse"]["termination"];

export type AuthResponse = S["AuthResponse"];

/**
 * Phase 9d.2. The half-finished sign-in: a ticket to exchange at /api/auth/login/2fa, never a
 * session. Named `...ChallengeResponse` here and `TwoFactorRequiredResponse` in C#; the alias is
 * where the two names meet.
 *
 * `twoFactorRequired` generates as `boolean` here rather than the literal `true` the pre-9d.7
 * hand-written type declared: the schema has no way to pin a plain C# `bool` to a single value. This
 * costs nothing: `isTwoFactorChallenge` (`ui/lib/api.ts`) narrows on the field's *presence*
 * (`"twoFactorRequired" in result`), never its value, so the discriminator still works exactly as
 * before.
 */
export type TwoFactorChallengeResponse = S["TwoFactorRequiredResponse"];

/**
 * `POST /api/auth/login` (9d.2) is a genuine `oneOf` of two 200 shapes in the OpenAPI document. This
 * union is written by hand rather than derived from `paths["/api/auth/login"]["post"]["responses"]`
 * on purpose: deriving it through the operation type is correct but unreadable at every call site.
 * A union of two generated aliases is still a union *of generated types*: the drift surface is zero
 * and `isTwoFactorChallenge` keeps narrowing cleanly. Narrow with `isTwoFactorChallenge`.
 */
export type LoginResult = AuthResponse | TwoFactorChallengeResponse;

/**
 * `POST /api/auth/external/exchange`. Same union as login, with `created` on the token-pair half
 * (M-4-1): true only when this handshake registered the account. Narrow with `isTwoFactorChallenge`.
 */
export type ExternalExchangeResponse = S["ExternalExchangeResponse"];

export type ExternalExchangeResult =
  ExternalExchangeResponse | TwoFactorChallengeResponse;

// --- Two-factor authentication (Phase 9d.2) ---
//
// `TwoFactorStatus` and `TwoFactorEnrolment` drop the `Response` suffix the C# record carries
// (`TwoFactorStatusResponse`, `TwoFactorEnrolmentResponse`), the other two name mismatches in this
// file, alongside `TwoFactorChallengeResponse` above. The TS names stay; only the alias target moves.

export type TwoFactorStatus = S["TwoFactorStatusResponse"];

/**
 * `sharedKey` is raw base32, unformatted. Group it for display; never send the grouped form back.
 */
export type TwoFactorEnrolment = S["TwoFactorEnrolmentResponse"];

export type TwoFactorEnableResponse = S["TwoFactorEnableResponse"];

export type TwoFactorDisableResponse = S["TwoFactorDisableResponse"];

export type TwoFactorRecoveryCodesResponse =
  S["TwoFactorRecoveryCodesResponse"];

/**
 * The economy actually governing a game, echoed by the server (`GameSession.Config` via
 * `ToEconomyDto`) so the client can price move previews, size the game-over clock graph, and
 * state a custom game's increment and end conditions without hardcoding preset tables. See
 * `ui/lib/customConfig.ts` for the full builder-side shape. Named `EconomyConfig` here and
 * `EconomyDto` in the generated schema, the TS name predates 9d.7 and stays.
 */
export type EconomyConfig = S["EconomyDto"];

/**
 * `economy` is `required` in the generated schema (no `?`): the pre-9d.7 hand-written type marked it
 * optional "for backward compatibility with older cached/mocked snapshots", but the live server has
 * always sent it. Existing optional-chaining on `state.economy?.x` still compiles; it was never load
 * bearing.
 */
export type GameStateResponse = S["GameStateResponse"];

/** GET /api/games/playing. Absent (204) when the caller is not seated. */
export type CurrentGameResponse = S["CurrentGameResponse"];

export type ClockEventType = S["ClockEventDto"]["type"];

/** `amount` is signed seconds; positive = added to `side`. */
export type ClockEventDto = S["ClockEventDto"];

export type MoveResponse = S["MoveResponse"];

export type RatingDto = S["RatingDto"];

export type LeaderboardEntryDto = S["LeaderboardEntryDto"];

/**
 * One account as a stranger sees it (`GET /api/users/{id}`). Every field on it is already public
 * elsewhere - the name, badge and avatar on /leaderboards, the ratings on
 * `/api/ratings/users/{id}` - which is why the route is anonymous; see
 * `Account/PublicProfileEndpoints.cs`. `avatarUrl` is a same-API path, never an off-site URL.
 */
export type PublicProfileDto = S["PublicProfileDto"];

/**
 * Adopts the server's real shape (`{ gameId, valueBefore, valueAfter, rdAfter, createdAt }`,
 * `Ratings/RatingEndpoints.cs:89`) instead of the pre-9d.7 hand-written `{ pool, rating,
 * ratingDeviation, createdAt }`, which shared not one field name with what the server actually sends.
 * Nothing in `ui/` calls this route, so nobody noticed for however long it was wrong, this is Phase
 * 9d.7's exhibit A for why this file needed to become generated.
 */
export type RatingHistoryPointDto = S["RatingHistoryPointDto"];

/**
 * ASP.NET's real validation-problem shape (`HttpValidationProblemDetails`), not the pre-9d.7
 * hand-written `{ errors: Record<string, string[]> }`. Nothing in `ui/` currently imports this type,
 * so the shape change is free, flagged here in case that changes.
 */
export type ValidationProblem = S["HttpValidationProblemDetails"];

// --- Reports (Phase 6e.2) ---

export type ReportReason = S["ReportDto"]["reason"];

export type ReportDto = S["ReportDto"];

// --- Spectating (Phase 8.1) ---

/** One row of the public live-games list. Solo/bot games are excluded server-side. */
export type LiveGameDto = S["LiveGameDto"];

// --- Open seek list (Phase 9e.4) ---

/** One pool with a player already waiting in its matchmaking queue. No opponent identity: joining that pool (JoinQueue) pairs instantly. */
export type OpenSeekDto = S["OpenSeekDto"];

// --- Balance analytics (Phase 8.3) ---

export type TerminationSliceDto = S["TerminationSliceDto"];

/**
 * `pool` is a rating-pool key, or "custom" (unrated/custom configs), or "all" for the roll-up.
 */
export type BalancePoolDto = S["BalancePoolDto"];

/**
 * `truncated` is true when the server's row cap was hit: the numbers are a recent sample, not the
 * whole history.
 */
export type BalanceSummaryDto = S["BalanceSummaryDto"];

// --- Analytics: retention and traffic (AD.6 / 11a.3) ---

export type RetentionPointDto = S["RetentionPointDto"];
export type FunnelStepDto = S["FunnelStepDto"];
export type CohortRowDto = S["CohortRowDto"];
export type RetentionSummaryDto = S["RetentionSummaryDto"];

export type CounterCountDto = S["CounterCountDto"];
export type SourceCountDto = S["SourceCountDto"];
export type TrafficDayDto = S["TrafficDayDto"];
export type FirstSessionDto = S["FirstSessionDto"];
export type TrafficSummaryDto = S["TrafficSummaryDto"];

// --- Replays (Phase 8.2) ---

export type ReplayPlyDto = S["ReplayPlyDto"];

export type ReplayResponse = S["ReplayResponse"];

// --- Moderation (Phase 8.5) ---

export type SanctionTypeName = S["SanctionDto"]["type"];

export type DetectionVerdictName = S["DetectionReportDto"]["verdict"];

export type ModerationReportStatus = S["ModerationReportDto"]["status"];

/**
 * A sanction record (admin routes). Two fields carry warnings that were inline comments on the
 * pre-9d.7 hand-written interface and must not be lost:
 *   - `moderatorNote`: NEVER shown to the sanctioned player; admin routes only.
 *   - `expiresAt`: `null` means permanent.
 */
export type SanctionDto = S["SanctionDto"];

/** What the sanctioned player themselves sees. No note, no issuer, no report id. */
export type MySanctionDto = S["MySanctionDto"];

/**
 * `openReportsAgainstReportedUser` is a repeat-offender signal, straight in the queue row.
 */
export type ModerationReportDto = S["ModerationReportDto"];

export type ModerationRatingDto = S["ModerationRatingDto"];

export type ModerationUserDto = S["ModerationUserDto"];

/**
 * `moderatorNote`, `expiresAt` and `reportId` are `required` in the generated schema (present, but
 * nullable) because the C# record (`CreateSanctionRequest(Guid UserId, string Type, string Reason,
 * string? ModeratorNote, DateTimeOffset? ExpiresAt, Guid? ReportId)`) has no default for any of those
 * three constructor parameters: the server expects the keys present even when the value is `null`.
 * The pre-9d.7 hand-written type marked them optional (`moderatorNote?`, `expiresAt?`, `reportId?`),
 * which let callers omit them entirely; see the Task B report for the real call-site bug this
 * surfaced in `ui/app/moderation/[userId]/page.tsx`.
 */
export type CreateSanctionRequest = S["CreateSanctionRequest"];

export type LiftSanctionRequest = S["LiftSanctionRequest"];

export type ResolveReportRequest = S["ResolveReportRequest"];

export type EraseAccountRequest = S["EraseAccountRequest"];

/**
 * The shared detector envelope (README's pinned "Detector DTOs" contract). One shape for both
 * the sandbagging detector (Task B) and the engine-assistance detector (Task C).
 *   Insufficient: not enough data to say anything. Score is always 0. This is NOT "clean".
 *   Unremarkable: enough data, nothing stands out.
 *   Watch:         signals present, well short of a case.
 *   Elevated:      several signals agree; a human should look at the games.
 *
 * Field notes carried over from the pre-9d.7 hand-written interface:
 *   - `detector`: "sandbagging" | "engine-assistance".
 *   - `pool`: null when the report is across all pools.
 *   - `score`: 0..1, always 0 when verdict is Insufficient.
 *   - `sampleSize`: games (sandbagging) or scored plies (engine-assistance) the score was computed
 *     from.
 *   - `caveats`: plain-language limits of this report; never empty.
 *   - `verdict`: see `DetectionVerdictName` above.
 */
export type DetectionReportDto = S["DetectionReportDto"];

/**
 * Field notes carried over from the pre-9d.7 hand-written interface:
 *   - `name`: stable machine name, e.g. "loss-cluster-after-peak".
 *   - `label`: human label for the UI, e.g. "Loss cluster after rating peak".
 *   - `contribution`: 0..1, how much this signal pushed the score.
 *   - `evidence`: one sentence showing the working, with the actual numbers in it.
 */
export type DetectionSignalDto = S["DetectionSignalDto"];

// --- Display names (Phase 8a.4) ---

/**
 * A kebab-case slug, not a C# enum: the server never declares this as a schema-visible type, so it
 * generates as bare `string` on both `DisplayNameVerdict.rejection` and
 * `DisplayNameScanRowDto.rejection`. This is the one deliberate exception to "no hand-written unions
 * in this file" (see the phase plan § 4a): there is no generated literal to alias, and losing this
 * union would weaken `ui/lib/useDisplayNameCheck.ts`.
 */
export type DisplayNameRejection =
  | "too-short"
  | "too-long"
  | "illegal-character"
  | "separator-placement"
  | "not-enough-substance"
  | "reserved"
  | "profanity"
  | "taken"
  | "cooldown";

export type DisplayNameVerdict = S["DisplayNameVerdict"];

/**
 * Admin-only: never render `storedNormalized`, `recomputedNormalized`, `normalizedDrift` or
 * `collidesWithDisplayNames` to a player. See the moderator scan tab.
 */
export type DisplayNameScanRowDto = S["DisplayNameScanRowDto"];

export type DisplayNameResponse = S["DisplayNameResponse"];

export type AvatarResponse = S["AvatarResponse"];

export type EmailStatusDto = S["EmailStatusDto"];

// --- Account data export & erasure (Phase 8a.2) ---
//
// Mirrors src/KiriChess.Server/Account/AccountDtos.cs. Two rules govern every record here, and both
// exist because "everything about me" is not the same request as "every row that mentions me", see
// the phase README's scope decision 5:
//   1. Nothing identifies another user beyond their display name, which is already public on
//      /leaderboards, every game page and every replay.
//   2. No moderator note and no credential material appears anywhere in it, ever.

/**
 * `twoFactorEnabled` (Phase 9d.2): the fact only, never the secret, see the phase README scope
 * decision 2.
 */
export type AccountExportProfileDto = S["AccountExportProfileDto"];

export type AccountExportRatingDto = S["AccountExportRatingDto"];

export type AccountExportRatingHistoryDto = S["AccountExportRatingHistoryDto"];

/**
 * One game the user played. `opponentDisplayName` is the ONLY thing said about the other player:
 * no id, no email.
 */
export type AccountExportGameDto = S["AccountExportGameDto"];

/** A report the user filed. Theirs, so it is complete, minus the moderator's note. */
export type AccountExportReportDto = S["AccountExportReportDto"];

/**
 * A report filed AGAINST the user. Deliberately narrower: no reporter id, no reporter name, the
 * accusation concerns this user, and who made it is that other person's data.
 */
export type AccountExportReportAgainstDto = S["AccountExportReportAgainstDto"];

/**
 * A sanction against the user. `moderatorNote` and `issuedByUserId` are withheld, same as the
 * player-facing `MySanctionDto`/`SanctionDto` rule.
 */
export type AccountExportSanctionDto = S["AccountExportSanctionDto"];

/**
 * A refresh-token session. Timestamps only: `tokenHash` is never exported, it is credential
 * material.
 */
export type AccountExportSessionDto = S["AccountExportSessionDto"];

/**
 * Whether any collection hit its cap. A silently truncated export is worse than none; one that
 * says so is fine.
 */
export type AccountExportTruncationDto = S["AccountExportTruncationDto"];

/**
 * The full GDPR data-portability payload (Art. 20), served as a file download by
 * `GET /api/account/export`.
 */
export type AccountExportDto = S["AccountExportDto"];

/** Response from `POST /api/account/delete` on success. */
export type DeleteAccountResponse = S["DeleteAccountResponse"];

// --- Active sessions (Phase 9e.6) ---

/**
 * One active refresh-token session (`GET /api/account/sessions`). No device name or IP is tracked
 * (6b.1 never captured either): a row is identified by when it signed in and when it expires.
 * `isCurrent` marks the session the caller is looking at this page from, computed server-side from
 * the `X-Refresh-Token` header - never trust a client-computed guess here.
 */
export type SessionDto = S["SessionDto"];

/**
 * One active personal access token (`GET /api/account/api-tokens`). The raw secret is never listed.
 */
export type ApiTokenDto = S["ApiTokenDto"];

/**
 * Create-token response: the list shape plus the raw `kirichess_pat_...` secret, shown once.
 */
export type CreatedApiTokenDto = S["CreatedApiTokenDto"];

// --- Mail preferences (Phase 11b.1) ---

/**
 * Every mail category a player can opt out of independently. Also the exact string this app's
 * `/unsubscribe` link carries as its `category` query parameter - see the C# enum's own doc comment
 * on why a member is never renamed, only added.
 */
export type NotificationCategory =
  S["NotificationCategoryPreferenceDto"]["category"];

/** `GET /api/account/notifications`'s body: every category, current subscription state. */
export type NotificationPreferencesDto = S["NotificationPreferencesDto"];

// --- Web push (Phase 11b.1 follow-up) ---

/** `GET /api/push/vapid-public-key`'s body. `publicKey` is null when push is not configured server-side. */
export type VapidPublicKeyDto = S["VapidPublicKeyDto"];

// --- Data retention (Phase 8a.3) ---
//
// Mirrors src/KiriChess.Server/Retention/DormantDtos.cs. Admin-only. Deliberately carries no email
// address: see Phase 8a.3 scope decision 8. Do not add one.

/** One of the reasons an account appears in the dormant report. */
export type DormancyReason = S["DormantAccountRowDto"]["reason"];

/**
 * `hasSanctions`: true blocks nothing. It is a "look before you act" flag, an account with a
 * sanction on it is exactly the one whose erasure would launder that sanction.
 */
export type DormantAccountRowDto = S["DormantAccountRowDto"];

/**
 * Report-only. Nothing in this phase erases a dormant account. `totalAccounts` counts live accounts
 * considered (tombstoned/already-erased accounts are excluded); `dormantCount` is matches across the
 * whole scan, not the page; `truncated` true means a cap was hit and `dormantCount` is a floor, not a
 * total.
 */
export type DormantAccountScanDto = S["DormantAccountScanDto"];

// --- Game history (Phase 9e.1) ---

/**
 * One row of the caller's own finished-game history, newest first. `mySide` says which colour the
 * caller played, so `result` (White/Black-relative) can be read from their perspective. `opponentName`
 * reads "Deleted account" for an erased opponent (8a.2 anonymises rather than deletes). `ratingBefore`/
 * `ratingAfter` are only set for rated games.
 */
export type GameHistoryEntryDto = S["GameHistoryEntryDto"];

// --- Challenges (Phase 9d.8) ---
//
// Mirrors Phase 9d.8 "The wire contract". `pool` is a plain `string` here rather
// than `PresetKey`, matching every other DTO with a pool field (`LiveGameDto`, `ReplayResponse`).

export type ChallengeColour = S["ChallengeDto"]["colour"];

export type ChallengeStatus = S["ChallengeDto"]["status"];

/**
 * A pending play-a-friend invitation. `GET /api/challenges/{token}` is anonymous, so this is
 * deliberately the challenger's display name and nothing that identifies the account.
 */
export type ChallengeDto = S["ChallengeDto"];

/** `POST /api/challenges/{token}/accept`'s 200 body. */
export type ChallengeAcceptedDto = S["ChallengeAcceptedDto"];

// --- Donations / patron badge (Plan B, patron badge) ---

/** Tier the badge renders as. Shared literal union across every surface that carries one. */
export type PatronTier = S["PatronStatusDto"]["tier"];

/** `POST /api/donations/reference`'s 200 body: an opaque, single-use reference to append to a payment link as `client_reference_id`. */
export type DonationReferenceDto = S["DonationReferenceDto"];

/** `GET /api/donations/me`'s 200 body. `isPatron` is already the derived answer: false once a one-off has lapsed, even though the row persists. */
export type PatronStatusDto = S["PatronStatusDto"];

// --- SignalR hub payloads (Phase 9e.9) ---
//
// These arrive over /hubs/game and /hubs/presence rather than over HTTP, and until Phase 9e.9 that
// meant they were hand-written: OpenAPI describes HTTP, so they appeared in no document and these
// types were typed by reading the C#. They are generated now. A document transformer puts the hub
// payload records into components.schemas of the same docs/api/openapi.json, referenced by no path
// and each marked with an x-kirichess-transport extension naming its hub and its event or method,
// which is what lets them come down the pipeline everything above already uses.
//
// The reason they live in that document rather than one of their own: every envelope composes
// GameStateResponse or ClockEventDto, and a separate document would have to duplicate those two, the
// exact drift this phase exists to remove. See Phase 9e.9 scope decision 1.
//
// One thing worth knowing before you trust these types. Hub payloads are serialized by SignalR's own
// AddJsonProtocol options, NOT by minimal APIs' JsonOptions, and the schema above is produced from
// the latter. They agree (both camelCase) by configuration, not by construction, so
// HubContractTests.Hub_schemas_match_what_SignalRs_serializer_actually_sends compares the document
// against what SignalR's serializer really emits. That test, not the schema, is what makes these
// types true.
//
// The names below keep their pre-9e.9 spelling (GameEndedPayload, not GameEndedDto) so this change
// touched no consumer, the same convention as EconomyConfig = S["EconomyDto"] above.

/**
 * `JoinGame`'s invocation result: current state, the side-to-move's legal moves, and the game's move
 * log so far. `moves` is the C-1-2 fix, and is the same `SpectatorMoveDto` the watch page rebuilds
 * from, so the play page can rebuild its move list, clock graph and captured material on every join
 * rather than only on the first one.
 */
export type GameSnapshot = S["GameSnapshotDto"];

export type MovePlayedDto = S["MovePlayedDto"];

export type GameEndedPayload = S["GameEndedDto"];

/**
 * Both players' Glicko-2 before/after for a game that just ended rated (Phase 11a.2). Carried on
 * `MovePlayedDto` (a move that ends the game, e.g. checkmate) and on `GameEndedDto` (every other
 * terminal transition), identical inline shape on both, so this alias reads off whichever arrived.
 * Undefined/null on an unrated, solo, or aborted game.
 */
export type GameRatingChange = NonNullable<GameEndedPayload["ratingChange"]>;

export type DrawOfferedPayload = S["DrawOfferedDto"];

/**
 * How long the disconnected player has to return before the abandon timer fires. Before Phase 9e.9
 * the server sent this as an anonymous object (`new { graceSeconds = ... }`), so this was the one hub
 * payload with no C# record behind it and nothing a schema could point at. `OpponentDisconnectedDto`
 * exists now; the JSON did not change.
 */
export type OpponentDisconnectedPayload = S["OpponentDisconnectedDto"];

export type MatchFoundPayload = S["MatchFoundDto"];

/** Broadcast to both players when a rematch offer (Phase 11a.1) is accepted; carries no colour of its own, see the type's C# doc comment. */
export type RematchAcceptedPayload = S["RematchAcceptedDto"];

/** Live lobby counts from the presence hub: signed-in users online, and of those, how many are playing. */
export type PresencePayload = S["PresenceDto"];

/**
 * One ply as a spectator sees it. Deliberately narrower than the server's stored `MoveRecord`: the
 * Phase 6e.2 anti-cheat telemetry (think time, lag credit) is not published on this channel. That
 * omission is a privacy rule, not an oversight, and `SpectatorTests` asserts it.
 */
export type SpectatorMoveDto = S["SpectatorMoveDto"];

/**
 * `JoinAsSpectator`'s invocation result. Carries the full move log so a spectator arriving mid-game
 * gets a complete move list and clock graph, and carries no legal moves: a spectator has no move to
 * make, so the engine's move-generator output is never shipped to an unauthenticated caller.
 */
export type SpectatorSnapshot = S["SpectatorSnapshotDto"];

export type SpectatorsChangedPayload = S["SpectatorsChangedDto"];

/**
 * Pushed to the challenger on group `challenge:{token}` when a friend accepts. See
 * `GameHub.WatchChallenge`. `yourColour` is the challenger's side: only the challenger's connections
 * are ever in that group. The C# record is `ChallengeAcceptedEvent`, with no `Dto` suffix, because it
 * is an event rather than a response body; `ChallengeAcceptedDto` is the separate HTTP shape returned
 * by `POST /api/challenges/{token}/accept`.
 */
export type ChallengeAcceptedPayload = S["ChallengeAcceptedEvent"];
