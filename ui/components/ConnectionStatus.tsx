"use client";

import { useTranslations } from "next-intl";
import { Dialog } from "@/components/Dialog";

/**
 * What the hub connection is doing, as the board pages see it (P-4-1, P-4-2, P-4-7).
 *
 * `"online"` is also the state before the first `start()` resolves: the loading branch already
 * says the page is loading, and a "reconnecting" banner over it would be a lie.
 */
export type ConnectionState =
  | "online"
  | "reconnecting"
  | "closed"
  | "resyncFailed";

/**
 * The only thing on a board page that tells a viewer their socket is gone.
 *
 * Two failures, plus one half-success, because SignalR has those three endings and they need
 * different answers:
 *
 * - **reconnecting** is `withAutomaticReconnect()` working through P-6-5's
 *   `[0, 2000, 5000, 10000, 30000, 30000, 60000]` schedule. It usually succeeds, so this is a banner and not an
 *   interruption: the board stays readable underneath. Player pages pass `forfeitWarning` so the
 *   copy names the 30s abandon timer (P-4-4); spectators keep the generic sentence.
 * - **closed** is the end of that schedule. SignalR has given
 *   up for good and `MovePlayed`/`GameEnded` can never arrive again, so a banner over a
 *   live-looking board would keep the same lie going. This one blocks, because there is nothing
 *   useful left to do with the page except reload it. A reload rejoins through `JoinGame` /
 *   `JoinAsSpectator` and resyncs correctly, which is why telling the viewer is the whole fix.
 * - **resyncFailed** is the socket back but `join()` still throwing after the short retry
 *   (P-4-7). The page may be stale. A Reload button is the recovery, not a bare line of text.
 *
 * The assertive announcement is NOT fired from here: the pages announce it from their own
 * `onreconnecting`/`onclose`/`onreconnected` handlers, next to their other announcements, so this
 * component stays a pure render of a state it is handed.
 */
export function ConnectionStatus({
  state,
  forfeitWarning = false,
}: {
  state: ConnectionState;
  forfeitWarning?: boolean;
}) {
  const t = useTranslations("ConnectionStatus");

  if (state === "reconnecting") {
    return (
      <p className="mb-4 notice-banner">
        {forfeitWarning ? t("reconnectingWithForfeit") : t("reconnecting")}
      </p>
    );
  }

  if (state === "resyncFailed") {
    return (
      <div className="mb-4 notice-banner">
        <p>{t("resyncFailed")}</p>
        <div className="mt-3 flex items-center gap-3 flex-wrap">
          <button
            type="button"
            onClick={() => window.location.reload()}
            className="btn-primary px-5 py-2 text-sm"
          >
            {t("reload")}
          </button>
        </div>
      </div>
    );
  }

  if (state === "closed") {
    return (
      <Dialog
        title={t("closedTitle")}
        // Deliberately inert. Escape and a backdrop click close every other dialog in the app, but
        // there is nothing behind this one to go back to: the page underneath is frozen and cannot
        // receive another event. The reload below is the only exit.
        onClose={() => {}}
      >
        <p className="mt-3 text-sm text-[var(--foreground)] leading-relaxed">
          {t("closedBody")}
        </p>
        <div className="mt-6 flex items-center gap-3 flex-wrap">
          <button
            type="button"
            onClick={() => window.location.reload()}
            className="btn-primary px-5 py-2 text-sm"
          >
            {t("reload")}
          </button>
        </div>
      </Dialog>
    );
  }

  return null;
}
