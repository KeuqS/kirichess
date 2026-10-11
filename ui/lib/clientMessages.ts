/**
 * P-7-3. Namespaces serialised into the root `NextIntlClientProvider`. Server components still
 * read the full catalogue via `getTranslations`; only the client payload is trimmed.
 *
 * `rules` stays off this list on purpose: it is the largest namespace and only `/learn` reads it
 * from a client component. `learn/layout.tsx` re-provides it there.
 */
export const CLIENT_MESSAGE_NAMESPACES = [
  "AccountDataCard",
  "a11y",
  "a11yBoard",
  "ApiTokensCard",
  "authCallback",
  "AvatarPicker",
  "BotGameDialog",
  "BotPlayPanel",
  "CapturedMaterialPanel",
  "ChallengeFriendPanel",
  "challenge",
  "Clock",
  "ClockGraph",
  "ConnectionStatus",
  "custom",
  "DisplayNameCard",
  "DisplayNameRejection",
  "DonationTiers",
  "EmailConfirmationBanner",
  "error",
  "Footer",
  "forbidden",
  "forgotPassword",
  "game",
  "GameControls",
  "GameOverOverlay",
  "GapMeter",
  "home",
  "InstallAppButton",
  "leaderboards",
  "learn",
  "loading",
  "LocaleSwitcher",
  "login",
  "MatchHistoryCard",
  "MoveList",
  "NavBar",
  "NotificationPreferencesCard",
  "OpenSeekList",
  "PlayButton",
  "PoolBadge",
  "PoolRatingCard",
  "PresetTile",
  "PresenceIndicator",
  "play",
  "profile",
  "publicProfile",
  "PushNotificationToggle",
  "RatedPoolsGrid",
  "replay",
  "report",
  "RequireAuth",
  "resetPassword",
  "ruleSummary",
  "SanctionBanner",
  "SecurityCard",
  "SessionsCard",
  "TutorialClocks",
  "TutorialProgress",
  "TutorialStepCard",
  "twoFactorPrompt",
  "TwoFactorCard",
  "unsubscribe",
  "verifyEmail",
  "watch",
] as const;

/** `/learn` is the one client tree that reads the `rules` namespace. */
export const LEARN_CLIENT_NAMESPACES = [
  ...CLIENT_MESSAGE_NAMESPACES,
  "rules",
] as const;

export type ClientMessageNamespace = (typeof CLIENT_MESSAGE_NAMESPACES)[number];

export function pickMessages(
  messages: Record<string, unknown>,
  namespaces: readonly string[] = CLIENT_MESSAGE_NAMESPACES,
): Record<string, unknown> {
  const picked: Record<string, unknown> = {};
  for (const ns of namespaces) {
    if (ns in messages) picked[ns] = messages[ns];
  }
  return picked;
}
