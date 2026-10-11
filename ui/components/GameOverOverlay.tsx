"use client";

import { useEffect, useId, useRef, useState } from "react";
import { Link } from "@/i18n/navigation";
import { useTranslations } from "next-intl";
import { ClockGraph, type ClockHistoryPoint } from "@/components/ClockGraph";
import { ReportDialog } from "@/components/ReportDialog";
import { Card } from "@/components/Card";
import { useAuth } from "@/components/AuthProvider";
import type { Termination, Side, GameRatingChange } from "@/lib/types";

/**
 * Translation keys, not display text (Phase 9f.4): `app/game/[id]/page.tsx` needs the identical
 * twelve labels for its own live-region announcement (see that file's `resultAnnouncement`,
 * "Same wording GameOverOverlay shows visually"), and holds its own copy of the same mapping under
 * its own "game" namespace, so both files call their own translator with matching keys.
 */
export const TERMINATION_LABEL_KEY: Record<Termination, string> = {
  None: "terminationNone",
  Checkmate: "terminationCheckmate",
  Flagfall: "terminationFlagfall",
  Stalemate: "terminationStalemate",
  Resignation: "terminationResignation",
  DrawByRepetition: "terminationDrawByRepetition",
  DrawByInsufficientMaterial: "terminationDrawByInsufficientMaterial",
  DrawByFiftyMove: "terminationDrawByFiftyMove",
  Abandoned: "terminationAbandoned",
  DrawByAgreement: "terminationDrawByAgreement",
  ServerRestart: "terminationServerRestart",
  NoShow: "terminationNoShow",
};

interface GameOverOverlayProps {
  termination: Termination;
  winner: Side | null;
  history: ClockHistoryPoint[];
  startTimeSeconds: number;
  /** Opponent's user id, for the "Report opponent" button. Omitted/null hides the button (solo games). */
  opponentId?: string | null;
  gameId?: string | null;
  /** Whether this game counted toward Glicko-2 (9d.8's rated flag, echoed on GameStateResponse). */
  rated?: boolean;
  /** Which colour the viewer played, to pick their half of `ratingChange`. Omitted for a spectator. */
  mySide?: Side;
  /**
   * This game's Glicko-2 delta (Phase 11a.2), from the `GameEnded`/`MovePlayed` payload that just
   * ended it. Null/undefined whenever `rated` is false, or the payload hasn't arrived yet (e.g. the
   * page was reloaded after the game already ended, see GameOverOverlay.tsx's doc comment history).
   */
  ratingChange?: GameRatingChange | null;
  /**
   * Where this game's rematch offer stands (Phase 11a.1): "idle" (nobody has offered, or the opponent
   * is a bot/yourself and there is nothing to offer), "offered" (this viewer offered, waiting), or
   * "incoming" (the opponent offered, this viewer can accept). Cold load of the rematch-mail link
   * derives this from JoinGame's rematchOfferedById; the live RematchOffered event still flips it
   * for a player who never left. Hub interaction itself lives in the page, not here, the same split
   * as `onOfferDraw`/`onResign` on `GameControls`.
   */
  rematchState?: "idle" | "offered" | "incoming";
  onOfferRematch?: () => void;
  onAcceptRematch?: () => void;
  /** P-4-3. Rematch invokes the hub; disable while the socket is not Connected. */
  rematchDisabled?: boolean;
}

/** Rounds and signs a rating delta the same way `GapMeter` signs a seconds delta: "+8" / "-6" / "0". */
export function formatRatingDelta(before: number, after: number): string {
  const rounded = Math.round(after - before);
  return rounded > 0 ? `+${rounded}` : `${rounded}`;
}

/**
 * Result panel for a finished game. It sits in the sidebar beside the board, not over it: the
 * mating position (or the final position of a draw) stays visible, and the last-move highlight
 * stays on the board. It is not a dialog - there is nothing to dismiss, and a modal scrim would
 * hide the position the panel exists to leave on screen. Focus moves to the heading on mount so a
 * keyboard user lands on the result, then Tab continues into the page, including the board.
 */
export function GameOverOverlay({
  termination,
  winner,
  history,
  startTimeSeconds,
  opponentId,
  gameId,
  rated = false,
  mySide,
  ratingChange,
  rematchState = "idle",
  onOfferRematch,
  onAcceptRematch,
  rematchDisabled = false,
}: GameOverOverlayProps) {
  const t = useTranslations("GameOverOverlay");
  const { userId } = useAuth();
  const [reportOpen, setReportOpen] = useState(false);
  const headingId = useId();
  const headingRef = useRef<HTMLHeadingElement>(null);

  /**
   * Mounted only while the result is showing. Move focus to the heading so a keyboard user lands
   * on the result (the assertive live region already speaks it). Tab is not trapped: the board
   * stays in the page and is the thing to look at. Restore focus on unmount.
   */
  useEffect(() => {
    const previouslyFocused =
      document.activeElement instanceof HTMLElement
        ? document.activeElement
        : null;
    headingRef.current?.focus();
    return () => {
      if (previouslyFocused && document.body.contains(previouslyFocused)) {
        previouslyFocused.focus();
      }
    };
  }, []);

  const resultText = winner
    ? t("winnerWins", { winner })
    : termination === "ServerRestart"
      ? t("gameAbortedNoRating")
      : t("drawResult");

  const canReport = !!opponentId && opponentId !== userId;
  const rematchIsAction =
    canReport && (rematchState === "idle" || rematchState === "incoming");
  const terminationKey = TERMINATION_LABEL_KEY[termination];
  const terminationLabel = terminationKey ? t(terminationKey) : termination;

  const myRatingChange =
    ratingChange && mySide
      ? mySide === "White"
        ? ratingChange.white
        : ratingChange.black
      : null;

  return (
    <section aria-labelledby={headingId}>
      <Card className="text-center">
        <h2
          ref={headingRef}
          id={headingId}
          tabIndex={-1}
          className="text-xl font-bold tracking-tight text-white"
        >
          {terminationLabel}
        </h2>
        <p className="mt-1 text-sm text-white">{resultText}</p>
        {!rated && (
          <p className="mt-1 text-sm text-muted">{t("practiceGameUnrated")}</p>
        )}
        {rated && myRatingChange && (
          <p
            className={`mt-1 text-sm ${
              myRatingChange.after > myRatingChange.before
                ? "text-[var(--gain)]"
                : myRatingChange.after < myRatingChange.before
                  ? "text-[var(--loss)]"
                  : "text-muted"
            }`}
          >
            {t("ratingChange", {
              before: Math.round(myRatingChange.before),
              after: Math.round(myRatingChange.after),
              delta: formatRatingDelta(
                myRatingChange.before,
                myRatingChange.after,
              ),
            })}
          </p>
        )}

        {canReport && rematchState === "idle" && (
          <button
            type="button"
            onClick={onOfferRematch}
            disabled={rematchDisabled}
            className="btn-primary mt-4 w-full py-2.5 text-sm"
          >
            {t("offerRematch")}
          </button>
        )}
        {canReport && rematchState === "offered" && (
          <p className="mt-4 text-sm text-muted">{t("rematchPending")}</p>
        )}
        {canReport && rematchState === "incoming" && (
          <button
            type="button"
            onClick={onAcceptRematch}
            disabled={rematchDisabled}
            className="btn-primary mt-4 w-full py-2.5 text-sm"
          >
            {t("acceptRematch")}
          </button>
        )}

        <div className="mt-4 flex flex-col gap-3">
          <Link
            href="/"
            className={`${rematchIsAction ? "btn-secondary" : "btn-primary"} w-full py-2.5 text-sm`}
          >
            {t("newGame")}
          </Link>
          {gameId && (
            <Link
              href={`/replay/${gameId}`}
              className="btn-secondary w-full py-2.5 text-sm"
            >
              {t("viewAndShareReplay")}
            </Link>
          )}
        </div>

        {canReport && (
          <div className="mt-3">
            <button
              type="button"
              onClick={() => setReportOpen(true)}
              className="text-sm text-muted underline transition hover:text-white"
            >
              {t("reportOpponent")}
            </button>
          </div>
        )}

        <div className="mt-4">
          <ClockGraph history={history} startTimeSeconds={startTimeSeconds} />
        </div>
      </Card>

      {reportOpen && opponentId && (
        <ReportDialog
          reportedUserId={opponentId}
          gameId={gameId ?? null}
          onClose={() => setReportOpen(false)}
        />
      )}
    </section>
  );
}
