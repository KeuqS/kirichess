"use client";

import { useEffect, useRef, useState, useMemo } from "react";
import { Link, useRouter } from "@/i18n/navigation";
import { useTranslations } from "next-intl";
import { IconArrowRight, IconBook2 } from "@tabler/icons-react";
import { NavBar } from "@/components/NavBar";
import { Footer } from "@/components/Footer";
import { Board } from "@/components/Board";
import { BotGameDialog } from "@/components/BotGameDialog";
import { useAuth } from "@/components/AuthProvider";
import { TutorialClocks } from "@/components/learn/TutorialClocks";
import { TutorialProgress } from "@/components/learn/TutorialProgress";
import { TutorialStepCard } from "@/components/learn/TutorialStepCard";
import type { TimeDeltaPopup } from "@/components/TimeDeltaLayer";
import { remainingAfterThink, TUTORIAL_STEPS } from "@/lib/tutorial";
import { recordCounter } from "@/lib/api";
import { COUNTER_KEYS } from "@/lib/analytics";
import { checkChipDeltas } from "@/lib/checkChip";
import { computeCostPreview, economyFromPreset } from "@/lib/costPreview";
import { soundForMove } from "@/lib/chessSounds";
import { safeNextPath } from "@/lib/challengeLink";
import { sideToMoveFromFen } from "@/lib/fen";
import { POOL_NAMES, PRESETS } from "@/lib/presets";
import { POOLS } from "@/lib/rules.generated";
import type { Side } from "@/lib/types";
import { useChessSounds } from "@/lib/useChessSounds";
import { Chess } from "chess.js";

const TOTAL_STEPS = TUTORIAL_STEPS.length;

function sideOf(fen: string): Side {
  return sideToMoveFromFen(fen) === "w" ? "White" : "Black";
}

export default function LearnPage() {
  const t = useTranslations("learn");
  // NOTICE lists this page under CC BY 4.0 alongside /rules; reuse /rules's existing
  // licenceH2/licenceBody/licenceAttribution/licenceLinkLabel keys rather than adding new
  // ones, so the two pages read the notice from one place (docs/audits/L.md L-9-7).
  const tRules = useTranslations("rules");
  const router = useRouter();
  const playSound = useChessSounds();
  const { token, hydrated } = useAuth();
  const [stepIndex, setStepIndex] = useState(0);
  const step = TUTORIAL_STEPS[stepIndex];
  const isLastStep = stepIndex === TOTAL_STEPS - 1;

  const [currentFen, setCurrentFen] = useState<string | null>(step.fen);
  const [lastMoveUci, setLastMoveUci] = useState<string | null>(null);
  const [played, setPlayed] = useState(false);
  const [payoffText, setPayoffText] = useState("");
  const [whiteSeconds, setWhiteSeconds] = useState(step.whiteSeconds);
  const [blackSeconds, setBlackSeconds] = useState(step.blackSeconds);
  // P-2-4: monotonic origin of the current think interval. Subtracted from the stored remaining
  // only while a board is up; the opener's frozen 180-vs-7 clocks pass ticking: false.
  const syncAtRef = useRef(0);
  const [clockNow, setClockNow] = useState(0);
  const [whitePopups, setWhitePopups] = useState<TimeDeltaPopup[]>([]);
  const [blackPopups, setBlackPopups] = useState<TimeDeltaPopup[]>([]);
  const popupCounter = useRef(0);
  // Which ply of `step.scriptedUci` is next. Only meaningful for the check and repetition steps;
  // every other step ignores it (scriptedUci is undefined, so nothing reads this).
  const [scriptedPlyIndex, setScriptedPlyIndex] = useState(0);
  const [showBotDialog, setShowBotDialog] = useState(false);

  const [chess, setChess] = useState(() =>
    step.fen ? new Chess(step.fen) : null,
  );

  // Reset all per-step state whenever the step changes.
  useEffect(() => {
    setCurrentFen(step.fen);
    setLastMoveUci(null);
    setPlayed(false);
    setPayoffText("");
    setWhiteSeconds(step.whiteSeconds);
    setBlackSeconds(step.blackSeconds);
    const origin = performance.now();
    syncAtRef.current = origin;
    setClockNow(origin);
    setWhitePopups([]);
    setBlackPopups([]);
    setScriptedPlyIndex(0);
    setChess(step.fen ? new Chess(step.fen) : null);
  }, [stepIndex, step]);

  // P-2-4: run the side-to-move clock down in real time on board steps. Copy-only steps stay
  // frozen. Flag-fall is the existing `seconds <= 0` treatment on TutorialClocks; no extra sandbox
  // ending is applied.
  const boardStep = step.fen !== null;
  useEffect(() => {
    if (!boardStep) return;
    const id = window.setInterval(() => setClockNow(performance.now()), 250);
    return () => window.clearInterval(id);
  }, [boardStep, stepIndex]);

  // 11a.3: the completion counter goes out once per mount when the player reaches the final step.
  // "How many arrivals finish the tutorial" is the first number on the launch cliff and the
  // tutorial is entirely client-side, so there is nothing on the server that could infer it.
  const completeSent = useRef(false);
  useEffect(() => {
    if (isLastStep && !completeSent.current) {
      completeSent.current = true;
      recordCounter(COUNTER_KEYS.learnComplete);
    }
  }, [isLastStep]);

  const canGoNext =
    step.fen === null ||
    (step.scriptedUci ? scriptedPlyIndex >= step.scriptedUci.length : played);

  function goBack() {
    setStepIndex((i) => Math.max(0, i - 1));
  }

  function goNext() {
    if (!canGoNext) return;
    setStepIndex((i) => Math.min(TOTAL_STEPS - 1, i + 1));
  }

  // Keyboard navigation: left/right arrows move between steps.
  useEffect(() => {
    function onKeyDown(e: KeyboardEvent) {
      if (e.key === "ArrowLeft") {
        goBack();
      } else if (e.key === "ArrowRight") {
        goNext();
      }
    }
    window.addEventListener("keydown", onKeyDown);
    return () => window.removeEventListener("keydown", onKeyDown);
  }, [stepIndex, canGoNext]);

  function spawnPopup(side: Side, amount: number) {
    if (amount === 0) return;
    const id = `t${popupCounter.current++}`;
    const popup: TimeDeltaPopup = { id, amount };
    const setter = side === "White" ? setWhitePopups : setBlackPopups;
    setter((prev) => [...prev, popup]);
    setTimeout(() => {
      setter((prev) => prev.filter((p) => p.id !== id));
    }, 1300);
  }

  function costFor(from: string, to: string): number | undefined {
    if (!currentFen) return undefined;
    const economy = economyFromPreset(PRESETS[step.preset]);
    return computeCostPreview(currentFen, from, to, economy).moverNet;
  }

  const legalUci = useMemo(() => {
    if (!chess) return [];
    const scriptedMove = step.scriptedUci?.[scriptedPlyIndex];
    return chess
      .moves({ verbose: true })
      .filter((m) => !step.disallowCaptures || !m.captured)
      .map((m) => m.from + m.to + (m.promotion || ""))
      .filter((uci) => !scriptedMove || uci === scriptedMove);
  }, [
    chess,
    currentFen,
    step.disallowCaptures,
    step.scriptedUci,
    scriptedPlyIndex,
  ]);

  function handleMove(uci: string) {
    if (!currentFen || !chess) return;

    let moveObj;
    try {
      moveObj = chess.move({
        from: uci.slice(0, 2),
        to: uci.slice(2, 4),
        promotion: uci.length > 4 ? uci.slice(4) : undefined,
      });
    } catch {
      return; // invalid move
    }

    playSound(soundForMove(currentFen, uci));

    const newFen = chess.fen();

    const from = uci.slice(0, 2);
    const to = uci.slice(2, 4);
    const economy = economyFromPreset(PRESETS[step.preset]);
    const preview = computeCostPreview(currentFen, from, to, economy);

    const isCheck = chess.isCheck();
    const chip = checkChipDeltas(chess, POOLS[step.preset].config);
    const checkChipMover = chip.mover;
    const checkChipOpponent = chip.opponent;

    const rawMoverDelta = preview.moverNet + checkChipMover;
    const rawOpponentDelta = -preview.opponentLoss + checkChipOpponent;

    // Rule 4: the last ply of a scripted repetition shuffle repeats a position already seen in
    // this step, so its entire computed economy (toll, capture, chip, all of it) is cancelled to
    // zero, exactly as the engine's `RepeatedPositionNullifiesTime` does. See tutorial.ts's header
    // comment on `nullifiesLastMove` for why this can't just be read off computeCostPreview.
    const isFinalScriptedPly =
      step.scriptedUci !== undefined &&
      scriptedPlyIndex === step.scriptedUci.length - 1;
    const nullified = Boolean(step.nullifiesLastMove) && isFinalScriptedPly;
    const moverDelta = nullified ? 0 : rawMoverDelta;
    const opponentDelta = nullified ? 0 : rawOpponentDelta;

    const mover = sideOf(currentFen);
    const opponent: Side = mover === "White" ? "Black" : "White";

    // Fold think time into the stored remaining before applying the event prices, otherwise the
    // display would jump back up when the move lands (stored remaining still had the pre-think
    // amount). The toll then sits on top of the time already spent thinking, which is the lesson.
    const now = performance.now();
    const beforeMove = remainingAfterThink(
      whiteSeconds,
      blackSeconds,
      mover,
      now - syncAtRef.current,
      true,
    );
    const nextWhite =
      mover === "White"
        ? beforeMove.white + moverDelta
        : beforeMove.white + opponentDelta;
    const nextBlack =
      mover === "Black"
        ? beforeMove.black + moverDelta
        : beforeMove.black + opponentDelta;
    setWhiteSeconds(nextWhite);
    setBlackSeconds(nextBlack);
    syncAtRef.current = now;
    setClockNow(now);
    spawnPopup(mover, moverDelta);
    spawnPopup(opponent, opponentDelta);

    setCurrentFen(newFen);
    setLastMoveUci(uci);
    setPlayed(true);
    if (step.scriptedUci) {
      setScriptedPlyIndex((i) => i + 1);
    }

    if (step.generatePayoff) {
      setPayoffText(
        step.generatePayoff(
          {
            piece: moveObj.piece,
            captured: moveObj.captured,
            isCheck,
            moverDelta,
            opponentDelta,
            nullified,
            wouldBeMoverDelta: nullified ? rawMoverDelta : undefined,
            wouldBeOpponentDelta: nullified ? rawOpponentDelta : undefined,
            plyNumber: step.scriptedUci ? scriptedPlyIndex + 1 : undefined,
            plyTotal: step.scriptedUci?.length,
          },
          t,
        ),
      );
    }
  }

  const activeSide = currentFen ? sideOf(currentFen) : null;
  const displayClocks = remainingAfterThink(
    whiteSeconds,
    blackSeconds,
    activeSide,
    boardStep ? clockNow - syncAtRef.current : 0,
    boardStep,
  );

  const stepCard = (
    <TutorialStepCard
      title={t(step.titleKey)}
      body={t(step.bodyKey, POOL_NAMES)}
      payoff={payoffText}
      payoffVisible={step.fen === null || played}
      onBack={goBack}
      onNext={goNext}
      backDisabled={stepIndex === 0}
      nextDisabled={!canGoNext || isLastStep}
      isLastStep={isLastStep}
      nextSlot={
        isLastStep ? (
          <div className="flex flex-col gap-3 w-full">
            {/*
              P-2-3: signed in, the terminal CTA opens BotGameDialog for the pool this step's
              own `preset` names, so it is one click to an actual game rather than a link to a
              lobby that bounces an anonymous visitor to login. Signed out, it goes to
              /login?next=/ with a line of copy first, so the redirect isn't a surprise. P-2-6:
              the button is worded as practice vs the bot, not as a lesser leftover. Deliberately
              not `hydrated ? null : ...`: an invisible placeholder the same size as the real
              button keeps the Back/Next row from jumping height once auth state resolves.
            */}
            {!hydrated ? (
              <span
                className="btn-primary flex w-full gap-2 py-2.5 text-sm invisible"
                aria-hidden
              >
                {t("practiceVsBot")}
              </span>
            ) : token ? (
              <button
                type="button"
                onClick={() => setShowBotDialog(true)}
                className="btn-primary flex w-full gap-2 py-2.5 text-sm"
              >
                {t("practiceVsBot")}
                <IconArrowRight size={16} stroke={2} />
              </button>
            ) : (
              <>
                <p className="text-sm text-muted leading-relaxed">
                  {t("playRequiresAccount")}
                </p>
                <Link
                  href={`/login?next=${encodeURIComponent(safeNextPath("/"))}`}
                  className="btn-primary flex w-full gap-2 py-2.5 text-sm"
                >
                  {t("practiceVsBot")}
                  <IconArrowRight size={16} stroke={2} />
                </Link>
              </>
            )}
            <Link
              href="/rules"
              className="btn-secondary flex w-full gap-2 py-2.5 text-sm"
            >
              <IconBook2 size={16} stroke={1.75} />
              {t("readTheRules")}
            </Link>
          </div>
        ) : undefined
      }
    />
  );

  return (
    <div className="min-h-screen flex flex-col bg-[var(--background)] text-[var(--foreground)] font-sans selection:bg-[var(--bounty)] selection:text-[#282c34]">
      <NavBar />
      <main
        id="main-content"
        className="flex-1 page-board flex flex-col gap-6 py-6 scroll-mt-24"
      >
        <div className="w-full max-w-4xl mx-auto">
          <TutorialProgress
            stepNumber={stepIndex + 1}
            totalSteps={TOTAL_STEPS}
            onSkip={() => {
              // 11a.3. A deliberate exit, which is a different fact from simply closing the tab - and
              // the only one of the two that is observable at all. keepalive is what gets it out
              // before the navigation.
              recordCounter(COUNTER_KEYS.learnSkip);
              router.push("/");
            }}
          />
        </div>

        {currentFen ? (
          <div className="flex flex-col lg:flex-row items-center lg:items-start justify-center gap-5 lg:gap-8 w-full mt-4 lg:justify-between">
            {/* Left Side: Step Card */}
            <div className="w-full lg:w-[380px] flex justify-center lg:justify-end order-2 lg:order-1">
              <div className="w-full max-w-[380px]">{stepCard}</div>
            </div>

            {/* Center: Board */}
            <div className="w-full lg:w-[min(48vw,700px)] shrink-0 order-1 lg:order-2">
              <Board
                fen={currentFen}
                legalUci={legalUci}
                sideToMove={sideOf(currentFen)}
                onMove={handleMove}
                costFor={costFor}
                lastMoveUci={lastMoveUci}
              />
            </div>

            {/* Right Side: Clocks */}
            <div className="w-full lg:w-[280px] flex justify-center lg:justify-start order-3">
              <div className="w-full max-w-[280px]">
                <TutorialClocks
                  whiteSeconds={displayClocks.white}
                  blackSeconds={displayClocks.black}
                  activeSide={activeSide}
                  whitePopups={whitePopups}
                  blackPopups={blackPopups}
                  orientation="vertical"
                />
              </div>
            </div>
          </div>
        ) : (
          <div className="w-full max-w-4xl mx-auto flex flex-col gap-6 mt-4">
            <div className="w-full max-w-[720px] mx-auto">
              <TutorialClocks
                whiteSeconds={displayClocks.white}
                blackSeconds={displayClocks.black}
                activeSide={activeSide}
                whitePopups={whitePopups}
                blackPopups={blackPopups}
                orientation="horizontal"
              />
            </div>
            {stepCard}
          </div>
        )}

        {/* LICENCE, CC BY 4.0 on this manual's text (NOTICE lists /learn alongside /rules).
            Reuses the "rules" namespace's licence keys, same text as /rules and /about. */}
        <section
          id="licence"
          className="w-full max-w-4xl mx-auto pt-10 mt-4 border-t border-[var(--border)] scroll-mt-24"
        >
          <h2 className="text-sm font-bold m-0 mb-3 tracking-wide text-muted">
            {tRules("licenceH2")}
          </h2>
          <p className="text-sm text-muted leading-relaxed max-w-2xl">
            {tRules("licenceBody")} {tRules("licenceAttribution")}{" "}
            <a
              href="https://creativecommons.org/licenses/by/4.0/"
              target="_blank"
              rel="noopener noreferrer license"
              className="underline hover:text-[var(--bounty)] transition-colors"
            >
              {tRules("licenceLinkLabel")}
            </a>
            .
          </p>
        </section>
      </main>
      {showBotDialog && (
        <BotGameDialog
          presetKey={step.preset}
          onClose={() => setShowBotDialog(false)}
        />
      )}
      <Footer />
    </div>
  );
}
