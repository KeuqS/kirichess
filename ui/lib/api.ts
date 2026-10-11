import type {
  AuthResponse,
  BalanceSummaryDto,
  ChallengeAcceptedDto,
  ChallengeColour,
  ChallengeDto,
  CreateSanctionRequest,
  DeleteAccountResponse,
  DetectionReportDto,
  DisplayNameResponse,
  DisplayNameScanRowDto,
  DisplayNameVerdict,
  EmailStatusDto,
  AvatarResponse,
  DonationReferenceDto,
  DormantAccountScanDto,
  EraseAccountRequest,
  GameHistoryEntryDto,
  GameStateResponse,
  LeaderboardEntryDto,
  LiveGameDto,
  LoginResult,
  ExternalExchangeResult,
  ModerationReportDto,
  ModerationReportStatus,
  ModerationUserDto,
  MySanctionDto,
  NotificationCategory,
  NotificationPreferencesDto,
  OpenSeekDto,
  PatronStatusDto,
  PublicProfileDto,
  RatingDto,
  ReplayResponse,
  RetentionSummaryDto,
  ReportDto,
  ReportReason,
  SanctionDto,
  SessionDto,
  ApiTokenDto,
  CreatedApiTokenDto,
  CurrentGameResponse,
  TwoFactorChallengeResponse,
  TwoFactorDisableResponse,
  TwoFactorEnableResponse,
  TwoFactorEnrolment,
  TwoFactorRecoveryCodesResponse,
  TwoFactorStatus,
  TrafficSummaryDto,
  VapidPublicKeyDto,
} from "./types";
import type { CustomGameConfig } from "./customConfig";

export const API_BASE =
  process.env.NEXT_PUBLIC_API_BASE ?? "http://localhost:5053";

export class ApiError extends Error {
  status: number;

  constructor(status: number, message: string) {
    super(message);
    this.name = "ApiError";
    this.status = status;
  }
}

let authToken: string | null = null;

/** Set by AuthProvider on login/register/hydrate/logout. Keeps this module framework-agnostic. */
export function setAuthToken(token: string | null) {
  authToken = token;
}

/** Current bearer token, for transports (e.g. SignalR) that need it outside the fetch helper. */
export function getAuthToken(): string | null {
  return authToken;
}

let refreshTokenValue: string | null = null;

/** Set by AuthProvider on login/register/hydrate/logout. Mirrors setAuthToken. */
export function setRefreshToken(token: string | null) {
  refreshTokenValue = token;
}

export function getRefreshToken(): string | null {
  return refreshTokenValue;
}

type AuthListener = (auth: AuthResponse) => void;
let onRefreshed: AuthListener | null = null;

/** AuthProvider registers this to re-persist rotated tokens (localStorage + React state). */
export function setOnTokenRefreshed(fn: AuthListener | null) {
  onRefreshed = fn;
}

let onRefreshFailed: (() => void) | null = null;

/** AuthProvider registers this to log the user out when the refresh token itself is dead. */
export function setOnRefreshFailed(fn: (() => void) | null) {
  onRefreshFailed = fn;
}

// De-duplicates concurrent refresh attempts: if several requests 401 at once, only one
// `/api/auth/refresh` call happens and everyone awaits the same promise. Without this, two
// concurrent refreshes would each try to rotate the same token and the second one would look like
// token reuse to the server, wiping the session (see the README's rotation contract).
let refreshInFlight: Promise<boolean> | null = null;

async function ensureFreshToken(): Promise<boolean> {
  if (refreshInFlight) return refreshInFlight;

  refreshInFlight = (async () => {
    const rt = refreshTokenValue;
    if (!rt) return false;

    try {
      const res = await fetch(`${API_BASE}/api/auth/refresh`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ refreshToken: rt }),
      });
      if (!res.ok) {
        onRefreshFailed?.();
        return false;
      }
      const data = (await res.json()) as AuthResponse;
      authToken = data.token;
      refreshTokenValue = data.refreshToken;
      onRefreshed?.(data);
      return true;
    } catch {
      return false;
    }
  })();

  try {
    return await refreshInFlight;
  } finally {
    refreshInFlight = null;
  }
}

interface RequestOptions {
  method?: "GET" | "POST" | "PUT" | "DELETE";
  body?: unknown;
  auth?: boolean;
  /** Overrides API_BASE for this call. See getReplay's `base` param for why this exists. */
  base?: string;
  /** Extra headers merged in after Content-Type/Authorization. See getSessions' X-Refresh-Token. */
  headers?: Record<string, string>;
}

async function extractErrorMessage(res: Response): Promise<string> {
  try {
    const data = await res.json();
    if (typeof data?.error === "string") {
      return data.error;
    }
    if (data?.errors && typeof data.errors === "object") {
      const messages = Object.values(data.errors as Record<string, string[]>)
        .flat()
        .filter((m): m is string => typeof m === "string");
      if (messages.length > 0) {
        return messages.join(" ");
      }
    }
  } catch {
    // response wasn't JSON; fall through to generic message
  }
  // The server always sends `error` on a 429 (Phase 6e.1), but a proxy-generated 429 (e.g. from an
  // intermediary in front of the API) won't have a JSON body at all: give that case a readable
  // message instead of the generic "Request failed with status 429" below.
  if (res.status === 429) {
    return "Too many requests. Please wait a moment and try again.";
  }
  return `Request failed with status ${res.status}`;
}

async function request<T>(
  path: string,
  options: RequestOptions = {},
): Promise<T> {
  const { method = "GET", body, auth = false, base = API_BASE } = options;

  const headers: Record<string, string> = { ...options.headers };
  if (body !== undefined) {
    headers["Content-Type"] = "application/json";
  }
  if (auth) {
    if (!authToken) {
      throw new ApiError(401, "Not authenticated");
    }
    headers["Authorization"] = `Bearer ${authToken}`;
  }

  let res = await fetch(`${base}${path}`, {
    method,
    headers,
    body: body !== undefined ? JSON.stringify(body) : undefined,
  });

  if (res.status === 401 && auth) {
    const refreshed = await ensureFreshToken();
    if (refreshed) {
      headers["Authorization"] = `Bearer ${authToken}`;
      res = await fetch(`${base}${path}`, {
        method,
        headers,
        body: body !== undefined ? JSON.stringify(body) : undefined,
      });
    }
  }

  if (!res.ok) {
    const message = await extractErrorMessage(res);
    throw new ApiError(res.status, message);
  }

  if (res.status === 204) {
    return undefined as T;
  }

  return (await res.json()) as T;
}

// --- Auth ---

/**
 * Phase 8a.2 added the two consent flags Task B's `RegisterRequest` now requires. Both are the same
 * checkbox on the register form (see `ui/app/login/page.tsx`): the server keeps them separate
 * because they are separate legal facts (age declaration vs. terms acceptance), and a future form
 * may split them. DH.3 added `turnstileToken`: the unused widget token, forwarded so the backend
 * can siteverify it. The browser must not redeem the token first, it is single-use.
 */
export function register(
  email: string,
  password: string,
  displayName: string,
  acceptedTerms: boolean,
  confirmedMinimumAge: boolean,
  turnstileToken?: string | null,
) {
  return request<AuthResponse>("/api/auth/register", {
    method: "POST",
    body: {
      email,
      password,
      displayName,
      acceptedTerms,
      confirmedMinimumAge,
      turnstileToken,
    },
  });
}

/**
 * Phase 9d.2. `LoginResult` is a union: narrow with `isTwoFactorChallenge` before treating the
 * result as a session. See README § 6.1: the discriminator is the presence of `twoFactorRequired`.
 */
export function login(email: string, password: string) {
  return request<LoginResult>("/api/auth/login", {
    method: "POST",
    body: { email, password },
  });
}

/**
 * Narrows the login/exchange union. Presence of the flag is the whole test: an `AuthResponse` has no
 * such field, so this is safe against a server that adds fields to either shape later.
 */
export function isTwoFactorChallenge(
  result: ExternalExchangeResult,
): result is TwoFactorChallengeResponse;
export function isTwoFactorChallenge(
  result: LoginResult,
): result is TwoFactorChallengeResponse;
export function isTwoFactorChallenge(
  result: LoginResult | ExternalExchangeResult,
): result is TwoFactorChallengeResponse {
  return "twoFactorRequired" in result;
}

export function refreshSession(refreshToken: string) {
  return request<AuthResponse>("/api/auth/refresh", {
    method: "POST",
    body: { refreshToken },
  });
}

export function logout(refreshToken: string) {
  return request<void>("/api/auth/logout", {
    method: "POST",
    body: { refreshToken },
  });
}

/**
 * Revokes every refresh token for the caller, including the one this tab is using - see
 * `AuthProvider.signOutEverywhere`, which clears the local session immediately after this resolves,
 * the same way `logout` clears it after a single-token revoke.
 */
export function logoutAll() {
  return request<void>("/api/auth/logout-all", { method: "POST", auth: true });
}

export function confirmEmail(userId: string, token: string) {
  return request<{ confirmed: boolean }>("/api/auth/confirm-email", {
    method: "POST",
    body: { userId, token },
  });
}

export function resendConfirmation(email: string) {
  return request<{ sent: boolean }>("/api/auth/resend-confirmation", {
    method: "POST",
    body: { email },
  });
}

/**
 * The caller's own address and where it stands with the confirmed-email play gate. `playBlocked` is
 * the server's verdict, not `!confirmed`: the gate is off by default and older accounts are
 * grandfathered, so an unconfirmed address is usually not a block. Never re-derive it here.
 */
export function emailStatus() {
  return request<EmailStatusDto>("/api/account/email", { auth: true });
}

export function forgotPassword(email: string) {
  return request<{ sent: boolean }>("/api/auth/forgot-password", {
    method: "POST",
    body: { email },
  });
}

export function resetPassword(
  email: string,
  token: string,
  newPassword: string,
) {
  return request<{ reset: boolean }>("/api/auth/reset-password", {
    method: "POST",
    body: { email, token, newPassword },
  });
}

/**
 * Full-page destination that begins the Google handshake.
 *
 * This is deliberately a URL to navigate to, not something to `fetch`. The endpoint answers with a
 * 302 to accounts.google.com; a fetch would try to follow that cross-origin, fail CORS, and leave
 * the user staring at a dead button. OAuth requires a real top-level navigation.
 */
export function googleSignInUrl(returnPath = "/"): string {
  return `${API_BASE}/api/auth/external/google/start?returnPath=${encodeURIComponent(returnPath)}`;
}

/**
 * Trades the one-time callback code for a real token pair. Single use: see the callback page.
 * Phase 9d.2: can also come back as a challenge (README § 6.1), a linked Google account must not be
 * a way around the account's own second factor. Narrow with `isTwoFactorChallenge`.
 */
export function exchangeExternalCode(code: string) {
  return request<ExternalExchangeResult>("/api/auth/external/exchange", {
    method: "POST",
    body: { code },
  });
}

// --- Two-factor authentication (Phase 9d.2) ---

/**
 * Completes the second step of login after a `TwoFactorChallengeResponse`. Deliberately not
 * `auth: true`, the caller has no access token yet, only the opaque challenge ticket.
 */
export function completeTwoFactorLogin(ticket: string, code: string) {
  return request<AuthResponse>("/api/auth/login/2fa", {
    method: "POST",
    body: { ticket, code },
  });
}

export function getTwoFactorStatus() {
  return request<TwoFactorStatus>("/api/account/2fa", { auth: true });
}

export function beginTwoFactorEnrolment() {
  return request<TwoFactorEnrolment>("/api/account/2fa/enrol", {
    method: "POST",
    auth: true,
  });
}

export function enableTwoFactor(
  code: string,
  opts?: { password?: string; googleReauthCode?: string },
) {
  return request<TwoFactorEnableResponse>("/api/account/2fa/enable", {
    method: "POST",
    body: {
      code,
      ...(opts?.password != null && opts.password !== ""
        ? { password: opts.password }
        : {}),
      ...(opts?.googleReauthCode != null && opts.googleReauthCode !== ""
        ? { googleReauthCode: opts.googleReauthCode }
        : {}),
    },
    auth: true,
  });
}

export function disableTwoFactor(code: string, password?: string) {
  return request<TwoFactorDisableResponse>("/api/account/2fa/disable", {
    method: "POST",
    body: {
      code,
      ...(password != null && password !== "" ? { password } : {}),
    },
    auth: true,
  });
}

export function regenerateRecoveryCodes(code: string) {
  return request<TwoFactorRecoveryCodesResponse>(
    "/api/account/2fa/recovery-codes",
    { method: "POST", body: { code }, auth: true },
  );
}

// --- Games ---

export function createGame(opts: {
  opponentId: string | null;
  preset: string | null;
  rated: boolean;
  /** When set, the server builds the config from this instead of `preset` and forces unrated. */
  custom?: CustomGameConfig;
}) {
  return request<GameStateResponse>("/api/games", {
    method: "POST",
    body: opts,
    auth: true,
  });
}

export function getGame(id: string) {
  return request<GameStateResponse>(`/api/games/${id}`, { auth: true });
}

/**
 * The live game the caller is already sitting in, if any. 204 (undefined here) when they are not.
 * Play surfaces use it to turn "You're already in a game." into a link back to that board.
 */
export async function getCurrentGame(): Promise<CurrentGameResponse | null> {
  const game = await request<CurrentGameResponse | undefined>(
    "/api/games/playing",
    { auth: true },
  );
  return game ?? null;
}

export function resign(id: string) {
  return request<GameStateResponse>(`/api/games/${id}/resign`, {
    method: "POST",
    auth: true,
  });
}

/**
 * Live games anyone can watch (Phase 8.1). Anonymous, no token, like the leaderboards: the visitor
 * this exists for has not signed up yet.
 */
export function getLiveGames(limit?: number) {
  const qs = limit === undefined ? "" : `?limit=${limit}`;
  return request<LiveGameDto[]>(`/api/games/live${qs}`);
}

/**
 * Open seeks (Phase 9e.4): pools with a player already waiting in the matchmaking queue. Anonymous,
 * same reasoning as `getLiveGames`. "Accepting" a seek is just joining that pool's queue over
 * SignalR (`JoinQueue`), there is no separate accept call.
 */
export function getOpenSeeks() {
  return request<OpenSeekDto[]>("/api/seeks");
}

/**
 * A finished game's full replay. Deliberately unauthenticated (`auth` omitted): a shared link has to
 * work for someone who has never signed in. The server answers 404 for an unknown id *and* for a
 * game still in progress: a live move list is not public.
 *
 * `base` lets server-only callers (generateMetadata, opengraph-image) reach the backend over the
 * compose-internal network instead of the browser-facing NEXT_PUBLIC_API_BASE, which may not be
 * reachable from inside the frontend container itself. See API_BASE_INTERNAL in
 * docker-compose.e2e.yml.
 */
export function getReplay(id: string, base?: string) {
  return request<ReplayResponse>(`/api/games/${id}/replay`, { base });
}

/**
 * The download URL for a finished game's PGN (Phase 9e.2).
 *
 * Deliberately a URL to put in an `href`, not something to `fetch`, for the same reason
 * `googleSignInUrl` is: the route is anonymous and answers with `Content-Disposition: attachment`,
 * so the browser saves the file itself. Fetching it would mean reimplementing the blob-and-filename
 * dance `exportAccountData` needs only because that endpoint is authenticated. This one is not.
 */
export function gamePgnUrl(id: string): string {
  return `${API_BASE}/api/games/${id}/pgn`;
}

/**
 * The caller's own finished-game history (Phase 9e.1), newest first. Ongoing games never appear,
 * the same 8.2 rule `getReplay` enforces server-side: every row here is also independently fetchable
 * from `getReplay`.
 */
export function getMyGames(opts?: {
  take?: number;
  /** Offset page. Ignored by the server when `before` and `beforeId` are both set. */
  skip?: number;
  /** Keyset cursor: `endedAt` of the last row already shown. Send with `beforeId`. */
  before?: string;
  /** Keyset cursor: `gameId` of that same row. */
  beforeId?: string;
}) {
  const params = new URLSearchParams();
  if (opts?.take !== undefined) params.set("take", String(opts.take));
  if (opts?.skip !== undefined) params.set("skip", String(opts.skip));
  if (opts?.before !== undefined) params.set("before", opts.before);
  if (opts?.beforeId !== undefined) params.set("beforeId", opts.beforeId);
  const qs = params.toString();
  return request<GameHistoryEntryDto[]>(
    `/api/games/mine${qs ? `?${qs}` : ""}`,
    {
      auth: true,
    },
  );
}

// --- Challenges (Phase 9d.8) ---
//
// The play-a-friend link. `getChallenge` is the one anonymous route in this section, the landing
// page (`ui/app/challenge/[token]/page.tsx`) has to render for a friend who has not signed up yet ,
// every other call requires the caller's own session. See Phase 9d.8 "The wire
// contract" for the shapes and Phase 9d.8 scope decision 1 for why the token
// itself is not a secret worth hashing.

export function createChallenge(
  pool: string,
  colour: ChallengeColour,
  /** When set, mints an unrated custom-config invite (`pool` should be `"custom"`). */
  custom?: CustomGameConfig,
) {
  return request<ChallengeDto>("/api/challenges", {
    method: "POST",
    body: custom ? { pool, colour, custom } : { pool, colour },
    auth: true,
  });
}

/** Anonymous on purpose: the landing page has to render for a friend who has no account yet. */
export function getChallenge(token: string) {
  return request<ChallengeDto>(`/api/challenges/${encodeURIComponent(token)}`);
}

export function acceptChallenge(token: string) {
  return request<ChallengeAcceptedDto>(
    `/api/challenges/${encodeURIComponent(token)}/accept`,
    { method: "POST", auth: true },
  );
}

export function cancelChallenge(token: string) {
  return request<void>(`/api/challenges/${encodeURIComponent(token)}`, {
    method: "DELETE",
    auth: true,
  });
}

export function getMyChallenges() {
  return request<ChallengeDto[]>("/api/challenges/mine", { auth: true });
}

// --- Ratings ---

export function getMyRatings() {
  return request<RatingDto[]>("/api/ratings/me", { auth: true });
}

export function getUserRatings(userId: string) {
  return request<RatingDto[]>(`/api/ratings/users/${userId}`);
}

/**
 * The public profile page's one call. Anonymous, and 404s for an unknown id and for an erased
 * account alike, so callers treat "no such profile" as a single case.
 *
 * `base` is the same compose-internal override `getReplay` takes, for `u/[id]/layout.tsx`
 * generateMetadata running inside the frontend container.
 */
export function getPublicProfile(userId: string, base?: string) {
  return request<PublicProfileDto>(`/api/users/${userId}`, { base });
}

export function getLeaderboard(
  pool: string,
  opts?: { limit?: number; includeProvisional?: boolean },
) {
  const params = new URLSearchParams();
  if (opts?.limit !== undefined) {
    params.set("limit", String(opts.limit));
  }
  if (opts?.includeProvisional !== undefined) {
    params.set("includeProvisional", String(opts.includeProvisional));
  }
  const qs = params.toString();
  return request<LeaderboardEntryDto[]>(
    `/api/leaderboards/${pool}${qs ? `?${qs}` : ""}`,
  );
}

// --- Reports (Phase 6e.2) ---

export function submitReport(input: {
  reportedUserId: string;
  gameId: string | null;
  reason: ReportReason;
  details: string | null;
}) {
  return request<ReportDto>("/api/reports", {
    method: "POST",
    body: input,
    auth: true,
  });
}

export function getMyReports() {
  return request<ReportDto[]>("/api/reports/mine", { auth: true });
}

// --- Analytics (Phase 8.3 / AD.6 / 11a.3) ---

/**
 * AD.6 moved the whole /api/analytics surface from 8.3's static `Analytics:AccessToken` bearer onto the
 * admin role, so this is now an ordinary authenticated request: 401 when signed out, 403 when signed in
 * without the role. The 404-when-unconfigured case is gone with the token that caused it - the route
 * always exists now.
 */
export async function getBalanceAnalytics(opts?: {
  sinceDays?: number;
  ratedOnly?: boolean;
}): Promise<BalanceSummaryDto> {
  const params = new URLSearchParams();
  if (opts?.sinceDays !== undefined) {
    params.set("sinceDays", String(opts.sinceDays));
  }
  if (opts?.ratedOnly !== undefined) {
    params.set("ratedOnly", String(opts.ratedOnly));
  }
  const qs = params.toString();
  return request<BalanceSummaryDto>(
    `/api/analytics/balance${qs ? `?${qs}` : ""}`,
    { auth: true },
  );
}

export async function getRetentionAnalytics(opts?: {
  sinceDays?: number;
  grain?: "day" | "week";
}): Promise<RetentionSummaryDto> {
  const params = new URLSearchParams();
  if (opts?.sinceDays !== undefined) {
    params.set("sinceDays", String(opts.sinceDays));
  }
  if (opts?.grain !== undefined) {
    params.set("grain", opts.grain);
  }
  const qs = params.toString();
  return request<RetentionSummaryDto>(
    `/api/analytics/retention${qs ? `?${qs}` : ""}`,
    { auth: true },
  );
}

export async function getTrafficAnalytics(opts?: {
  sinceDays?: number;
}): Promise<TrafficSummaryDto> {
  const params = new URLSearchParams();
  if (opts?.sinceDays !== undefined) {
    params.set("sinceDays", String(opts.sinceDays));
  }
  const qs = params.toString();
  return request<TrafficSummaryDto>(
    `/api/analytics/traffic${qs ? `?${qs}` : ""}`,
    { auth: true },
  );
}

// --- Counter beacon (AD.6 / 11a.3) ---

/**
 * AD.6 / 11a.3. Allowlisted campaign buckets, mirroring CounterSources in
 * src/KiriChess.Server/Analytics/CounterKeys.cs. Anything not in this set is sent anyway and the
 * server counts it as "direct" - the visit is real, only its origin is unknown - so the two lists
 * drifting costs attribution, never an error.
 */
const COUNTER_SOURCES = new Set([
  "reddit",
  "hn",
  "instagram",
  "x",
  "discord",
  "youtube",
  "lichess",
  "chesscom",
  "press",
  "directory",
  "qr",
  "friend",
]);

/**
 * The campaign bucket for this tab, resolved once from `?src=` on the first counter fired and cached
 * in memory for the rest of the tab's life.
 *
 * In memory ON PURPOSE, and this is the constraint the whole design rests on: no cookie and no
 * persisted browser storage of any kind, nothing to consent to, and it is gone when the tab closes.
 * Without the cache, only landing pages could ever be attributed, because a client-side navigation to
 * /learn carries no query string. Persisting it across a reload would turn this into a tracker. See
 * AD.6 / 11a.3 scope decision 13.
 *
 * A hard refresh without the query string re-attributes the rest of the visit to "direct". That is an
 * accepted inaccuracy, stated rather than hidden.
 *
 * Never derived from the browser's referrer header/property: that is a raw string that can carry a
 * search query or a private URL, and taking one would break the "holds no personal data" property
 * invisibly (scope decision 12).
 */
let campaignSource: string | null = null;

function resolveCampaignSource(): string {
  if (campaignSource !== null) return campaignSource;
  if (typeof window === "undefined") return "direct";

  const raw = new URLSearchParams(window.location.search)
    .get("src")
    ?.trim()
    .toLowerCase();
  campaignSource = raw && COUNTER_SOURCES.has(raw) ? raw : "direct";
  return campaignSource;
}

/**
 * AD.6 / 11a.3. Fire-and-forget counter. Anonymous, no cookie, no identifier of any kind - the server
 * records only (UTC date, allowlisted key, allowlisted source, count).
 *
 * Swallows every error on purpose. A failed beacon must never surface to a visitor, never reach
 * Sentry, and never delay a render: it is a nice-to-have counter, and the page it is counting is the
 * thing that matters. `keepalive` lets it survive a navigation that starts before the request lands,
 * which is exactly the case for the tutorial-skip and register counters.
 */
export function recordCounter(key: string): void {
  void fetch(`${API_BASE}/api/analytics/counter`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ key, source: resolveCampaignSource() }),
    keepalive: true,
  }).catch(() => {});
}

// --- Moderation (Phase 8.5) ---
//
// Same authenticated `request()` path as the analytics reads above (bearer JWT + auto-refresh on
// 401). A non-admin caller gets a 403 from the server; an unauthenticated caller gets a 401.

export function getModerationReports(opts?: {
  status?: string;
  take?: number;
  skip?: number;
}) {
  const params = new URLSearchParams();
  if (opts?.status !== undefined) params.set("status", opts.status);
  if (opts?.take !== undefined) params.set("take", String(opts.take));
  if (opts?.skip !== undefined) params.set("skip", String(opts.skip));
  const qs = params.toString();
  return request<ModerationReportDto[]>(
    `/api/moderation/reports${qs ? `?${qs}` : ""}`,
    { auth: true },
  );
}

export function resolveModerationReport(
  id: string,
  body: { status: ModerationReportStatus; moderatorNote?: string },
) {
  return request<ModerationReportDto>(`/api/moderation/reports/${id}/resolve`, {
    method: "POST",
    body,
    auth: true,
  });
}

export function getModerationUser(userId: string) {
  return request<ModerationUserDto>(`/api/moderation/users/${userId}`, {
    auth: true,
  });
}

/**
 * L-2-3: same JSON download as `exportAccountData`, for an account that cannot sign in
 * (banned, or an under-16 close). Admin-only; the server logs the moderator id.
 */
export async function exportModerationAccount(userId: string): Promise<{
  blob: Blob;
  filename: string;
}> {
  if (!authToken) {
    throw new ApiError(401, "Not authenticated");
  }

  const doFetch = () =>
    fetch(`${API_BASE}/api/moderation/users/${userId}/export`, {
      headers: { Authorization: `Bearer ${authToken}` },
    });

  let res = await doFetch();

  if (res.status === 401) {
    const refreshed = await ensureFreshToken();
    if (refreshed) {
      res = await doFetch();
    }
  }

  if (!res.ok) {
    throw new ApiError(res.status, await extractErrorMessage(res));
  }

  const disposition = res.headers.get("Content-Disposition") ?? "";
  const match = /filename="?([^"; ]+)"?/i.exec(disposition);
  const filename = match?.[1] ?? `kirichess-export-${userId}.json`;

  const blob = await res.blob();
  return { blob, filename };
}

/** L-2-3 / L-4-2. Phrase is `ERASE THIS ACCOUNT`, distinct from the self-serve delete. */
export function eraseModerationAccount(
  userId: string,
  acknowledgement: EraseAccountRequest["acknowledgement"],
) {
  const body: EraseAccountRequest = { acknowledgement };
  return request<DeleteAccountResponse>(
    `/api/moderation/users/${userId}/erase`,
    {
      method: "POST",
      body,
      auth: true,
    },
  );
}

export function getSanctions(opts?: {
  activeOnly?: boolean;
  userId?: string;
  take?: number;
  skip?: number;
}) {
  const params = new URLSearchParams();
  if (opts?.activeOnly !== undefined) {
    params.set("activeOnly", String(opts.activeOnly));
  }
  if (opts?.userId !== undefined) params.set("userId", opts.userId);
  if (opts?.take !== undefined) params.set("take", String(opts.take));
  if (opts?.skip !== undefined) params.set("skip", String(opts.skip));
  const qs = params.toString();
  return request<SanctionDto[]>(
    `/api/moderation/sanctions${qs ? `?${qs}` : ""}`,
    { auth: true },
  );
}

export function createSanction(body: CreateSanctionRequest) {
  return request<SanctionDto>("/api/moderation/sanctions", {
    method: "POST",
    body,
    auth: true,
  });
}

export function liftSanction(id: string, body: { reason: string }) {
  return request<SanctionDto>(`/api/moderation/sanctions/${id}/lift`, {
    method: "POST",
    body,
    auth: true,
  });
}

/** The one moderation route a non-admin may call: what a sanctioned player sees of themselves. */
export function getMySanctions() {
  return request<MySanctionDto[]>("/api/moderation/my-sanctions", {
    auth: true,
  });
}

/**
 * Phase 8.5 Task B. Not yet deployed in every environment: callers should expect a 404/501 and
 * treat it as "this detector is not deployed yet", not a hard error. See `DetectionReportCard`.
 */
export function getSandbaggingReport(
  userId: string,
  opts?: { sinceDays?: number; pool?: string },
) {
  const params = new URLSearchParams();
  if (opts?.sinceDays !== undefined) {
    params.set("sinceDays", String(opts.sinceDays));
  }
  if (opts?.pool !== undefined) params.set("pool", opts.pool);
  const qs = params.toString();
  return request<DetectionReportDto[]>(
    `/api/moderation/detect/sandbagging/${userId}${qs ? `?${qs}` : ""}`,
    { auth: true },
  );
}

/**
 * Phase 8.5 Task C. Not yet deployed in every environment: see `getSandbaggingReport` above.
 */
export function getEngineReport(
  userId: string,
  opts?: { sinceDays?: number; maxGames?: number },
) {
  const params = new URLSearchParams();
  if (opts?.sinceDays !== undefined) {
    params.set("sinceDays", String(opts.sinceDays));
  }
  if (opts?.maxGames !== undefined) {
    params.set("maxGames", String(opts.maxGames));
  }
  const qs = params.toString();
  return request<DetectionReportDto>(
    `/api/moderation/detect/engine/${userId}${qs ? `?${qs}` : ""}`,
    { auth: true },
  );
}

/**
 * The sandbagging sweep (Phase 8.5 Task B): the highest-scoring players across the whole window
 * rather than one named account. This is the only proactive route in the moderation surface: every
 * other view starts from a report somebody filed, which by definition only finds the abusers a
 * player noticed. Advisory like everything else here; the score does nothing on its own.
 */
export function getSandbaggingSweep(opts?: {
  sinceDays?: number;
  minScore?: number;
  take?: number;
}) {
  const params = new URLSearchParams();
  if (opts?.sinceDays !== undefined) {
    params.set("sinceDays", String(opts.sinceDays));
  }
  if (opts?.minScore !== undefined) {
    params.set("minScore", String(opts.minScore));
  }
  if (opts?.take !== undefined) params.set("take", String(opts.take));
  const qs = params.toString();
  return request<DetectionReportDto[]>(
    `/api/moderation/detect/sandbagging${qs ? `?${qs}` : ""}`,
    { auth: true },
  );
}

/**
 * Per-game engine-assistance breakdown: one report per player in that game (Phase 8.5 Task C).
 * This is what a moderator opens from a report scoped to a specific game. Expect `Insufficient`
 * most of the time: one game rarely clears the detector's minimum ply and game counts, and that is
 * correct behaviour rather than a failure to work.
 */
export function getEngineGameReport(gameId: string) {
  return request<DetectionReportDto[]>(
    `/api/moderation/detect/engine/game/${gameId}`,
    { auth: true },
  );
}

// --- Display names (Phase 8a.4) ---

/**
 * Anonymous by design (`auth` omitted): the register form calls this before a token exists, and
 * display names are already public on leaderboards/game pages/replays, so this route leaks nothing
 * a scraper could not already read. Carries `RateLimitPolicies.Auth` server-side.
 */
export function checkDisplayName(candidate: string) {
  return request<DisplayNameVerdict>(
    `/api/account/display-name/check?candidate=${encodeURIComponent(candidate)}`,
  );
}

export function changeDisplayName(displayName: string) {
  return request<DisplayNameResponse>("/api/account/display-name", {
    method: "POST",
    body: { displayName },
    auth: true,
  });
}

export async function uploadAvatar(file: Blob): Promise<AvatarResponse> {
  if (!authToken) {
    throw new ApiError(401, "Not authenticated");
  }

  const form = new FormData();
  form.append("file", file, "avatar.jpg");

  const doFetch = () =>
    fetch(`${API_BASE}/api/account/avatar`, {
      method: "PUT",
      headers: { Authorization: `Bearer ${authToken}` },
      body: form,
    });

  let res = await doFetch();

  if (res.status === 401) {
    const refreshed = await ensureFreshToken();
    if (refreshed) {
      res = await doFetch();
    }
  }

  if (!res.ok) {
    throw new ApiError(res.status, await extractErrorMessage(res));
  }

  return (await res.json()) as AvatarResponse;
}

export function clearAvatar() {
  return request<AvatarResponse>("/api/account/avatar", {
    method: "DELETE",
    auth: true,
  });
}

/** Admin-only. `problemsOnly` mirrors the server default of `true`. */
export function getDisplayNameScan(opts?: {
  problemsOnly?: boolean;
  take?: number;
  skip?: number;
}) {
  const params = new URLSearchParams();
  if (opts?.problemsOnly !== undefined) {
    params.set("problemsOnly", String(opts.problemsOnly));
  }
  if (opts?.take !== undefined) params.set("take", String(opts.take));
  if (opts?.skip !== undefined) params.set("skip", String(opts.skip));
  const qs = params.toString();
  return request<DisplayNameScanRowDto[]>(
    `/api/moderation/content/display-names${qs ? `?${qs}` : ""}`,
    { auth: true },
  );
}

/** Admin-only. Bypasses and resets the rename cooldown: see README scope decision 7. */
export function moderatorRenameDisplayName(
  userId: string,
  body: { displayName: string; reason: string },
) {
  return request<DisplayNameResponse>(
    `/api/moderation/content/display-names/${userId}`,
    { method: "POST", body, auth: true },
  );
}

// --- Account data export & erasure (Phase 8a.2) ---

/**
 * Phase 8a.2. The one call in this file that does not go through `request<T>()`: that helper parses
 * the body as JSON, which discards the Content-Disposition filename the server sets and hands back a
 * parsed object when what is wanted is bytes to write to disk. So this mirrors `request`'s auth and
 * 401-refresh behaviour by hand and returns the blob.
 */
export async function exportAccountData(): Promise<{
  blob: Blob;
  filename: string;
}> {
  if (!authToken) {
    throw new ApiError(401, "Not authenticated");
  }

  const doFetch = () =>
    fetch(`${API_BASE}/api/account/export`, {
      headers: { Authorization: `Bearer ${authToken}` },
    });

  let res = await doFetch();

  if (res.status === 401) {
    const refreshed = await ensureFreshToken();
    if (refreshed) {
      res = await doFetch();
    }
  }

  if (!res.ok) {
    throw new ApiError(res.status, await extractErrorMessage(res));
  }

  // Results.File (server side) sets this; fall back to a sane default rather than fail the
  // download over a missing/unparseable header.
  const disposition = res.headers.get("Content-Disposition") ?? "";
  const match = /filename="?([^"; ]+)"?/i.exec(disposition);
  const filename = match?.[1] ?? "kirichess-export.json";

  const blob = await res.blob();
  return { blob, filename };
}

/**
 * Phase 8a.2. Irreversible: there is no grace period and no undo. The acknowledgement string is
 * checked server-side too: this is not client-only validation.
 */
export function deleteAccount(acknowledgement: string, password?: string) {
  return request<DeleteAccountResponse>("/api/account/delete", {
    method: "POST",
    body: {
      acknowledgement,
      ...(password != null && password !== "" ? { password } : {}),
    },
    auth: true,
  });
}

// --- Active sessions (Phase 9e.6) ---
//
// `getSessions` sends the caller's own current refresh token as a header (never a query string, see
// the server-side comment on this route) so the server can mark which row is "this device" without
// the client having to guess by comparing timestamps.

export function getSessions() {
  const current = getRefreshToken();
  return request<SessionDto[]>("/api/account/sessions", {
    auth: true,
    headers: current ? { "X-Refresh-Token": current } : {},
  });
}

export function revokeSession(id: string) {
  return request<void>(`/api/account/sessions/${id}`, {
    method: "DELETE",
    auth: true,
  });
}

// --- API tokens (Phase 9f.5) ---

export function getApiTokens() {
  return request<ApiTokenDto[]>("/api/account/api-tokens", { auth: true });
}

export function createApiToken(
  name: string,
  scopes: string[],
  password?: string,
) {
  return request<CreatedApiTokenDto>("/api/account/api-tokens", {
    method: "POST",
    body: {
      name,
      scopes,
      ...(password != null && password !== "" ? { password } : {}),
    },
    auth: true,
  });
}

export function revokeApiToken(id: string) {
  return request<void>(`/api/account/api-tokens/${id}`, {
    method: "DELETE",
    auth: true,
  });
}

// --- Mail preferences (Phase 11b.1) ---

export function getNotificationPreferences() {
  return request<NotificationPreferencesDto>("/api/account/notifications", {
    auth: true,
  });
}

export function updateNotificationPreference(
  category: NotificationCategory,
  subscribed: boolean,
) {
  return request<void>("/api/account/notifications", {
    method: "PUT",
    body: { category, subscribed },
    auth: true,
  });
}

/**
 * The one-click unsubscribe link's own call, anonymous by design - see the server-side comment on
 * `NotificationEndpoints`. `userId`/`category`/`token` are round-tripped verbatim from the email
 * link's own query parameters, `ui/app/unsubscribe/page.tsx` never generates or edits them.
 */
export function unsubscribeFromNotifications(
  userId: string,
  category: string,
  token: string,
) {
  return request<void>("/api/notifications/unsubscribe", {
    method: "POST",
    body: { userId, category, token },
  });
}

// --- Web push (Phase 11b.1 follow-up) ---

/** Anonymous: the VAPID public key is handed to every browser the instant it subscribes, nothing to protect by gating it. `publicKey` is null when push is not configured server-side. */
export function getVapidPublicKey() {
  return request<VapidPublicKeyDto>("/api/push/vapid-public-key");
}

/** `subscription` is the flattened shape of the browser's own `PushSubscription.toJSON()` - see `PushNotificationToggle`, which does the flattening. */
export function subscribeToPush(subscription: {
  endpoint: string;
  p256dh: string;
  auth: string;
}) {
  return request<void>("/api/push/subscribe", {
    method: "POST",
    body: subscription,
    auth: true,
  });
}

export function unsubscribeFromPush(endpoint: string) {
  return request<void>("/api/push/unsubscribe", {
    method: "POST",
    body: { endpoint },
    auth: true,
  });
}

// --- Data retention (Phase 8a.3) ---

/** Admin-only. Report-only - there is no companion mutation, by design (8a.3). */
export function getDormantAccounts(opts?: {
  inactiveDays?: number;
  take?: number;
  skip?: number;
}) {
  const params = new URLSearchParams();
  if (opts?.inactiveDays !== undefined)
    params.set("inactiveDays", String(opts.inactiveDays));
  if (opts?.take !== undefined) params.set("take", String(opts.take));
  if (opts?.skip !== undefined) params.set("skip", String(opts.skip));
  const qs = params.toString();
  return request<DormantAccountScanDto>(
    `/api/moderation/retention/dormant${qs ? `?${qs}` : ""}`,
    { auth: true },
  );
}

// --- Donations / patron badge (Plan B, patron badge) ---

/**
 * Mints a single-use reference to append to a Stripe payment link's `client_reference_id`, so the
 * webhook can connect the checkout back to this account without Stripe ever seeing a KiriChess
 * account id directly. The reference is a random single-use row, not the account id.
 * Expires: see `DonationReferenceDto.expiresAt`.
 */
export function createDonationReference() {
  return request<DonationReferenceDto>("/api/donations/reference", {
    method: "POST",
    auth: true,
  });
}

/** The caller's own patron badge. `isPatron` is already the derived answer, see `PatronStatusDto`. */
export function getMyPatronStatus() {
  return request<PatronStatusDto>("/api/donations/me", { auth: true });
}
