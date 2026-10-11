/**
 * AD.6 / 11a.3. Counter keys, mirroring CounterKeys in
 * src/KiriChess.Server/Analytics/CounterKeys.cs. Keys the server does not recognise are silently
 * dropped by it, so the two lists drifting apart costs a counter, not an error - but keep them in step.
 */
export const COUNTER_KEYS = {
  landing: "landing",
  register: "register",
  login: "login",
  rules: "rules",
  learn: "learn",
  watch: "watch",
  leaderboards: "leaderboards",
  custom: "custom",
  donate: "donate",
  about: "about",
  learnComplete: "learn-complete",
  learnSkip: "learn-skip",
  signup: "signup",
  firstGameFinish: "first-game-finish",
  emptyLobbyLeave: "empty-lobby-leave",
} as const;

/**
 * Route to page key. Deliberately a fixed exact-match map and not a slug derived from the path: a
 * derived key would send whatever happens to be in the URL - including game ids and user ids - to an
 * endpoint whose entire purpose is to hold nothing identifying.
 */
export const PAGE_KEYS: Record<string, string> = {
  "/": COUNTER_KEYS.landing,
  "/login": COUNTER_KEYS.login,
  "/rules": COUNTER_KEYS.rules,
  "/learn": COUNTER_KEYS.learn,
  "/watch": COUNTER_KEYS.watch,
  "/leaderboards": COUNTER_KEYS.leaderboards,
  "/custom": COUNTER_KEYS.custom,
  "/donate": COUNTER_KEYS.donate,
  "/about": COUNTER_KEYS.about,
};
