"use client";

import { Suspense, use, useCallback, useEffect, useRef, useState } from "react";
import { useSearchParams } from "next/navigation";
import { Link, useRouter } from "@/i18n/navigation";
import { useTranslations } from "next-intl";
import { NavBar } from "@/components/NavBar";
import { Footer } from "@/components/Footer";
import { Card } from "@/components/Card";
import { SkeletonBlock, SkeletonRows } from "@/components/SkeletonRows";
import { AlreadyInGameNotice } from "@/components/AlreadyInGameNotice";
import { EmptyState } from "@/components/EmptyState";
import { PoolBadge } from "@/components/PoolBadge";
import { RuleSummary } from "@/components/RuleSummary";
import { useAuth } from "@/components/AuthProvider";
import { acceptChallenge, getChallenge, ApiError } from "@/lib/api";
import { PRESETS, isPresetKey, timeControlLabel } from "@/lib/presets";
import type { ChallengeDto } from "@/lib/types";

const CTA_PRIMARY = "btn-primary w-full py-2.5 text-sm";
const CTA_SECONDARY = "btn-secondary w-full py-2.5 text-sm";

const STATUS_MESSAGE_KEY: Record<
  Exclude<ChallengeDto["status"], "Open">,
  string
> = {
  Accepted: "statusAccepted",
  Cancelled: "statusCancelled",
  Expired: "statusExpired",
};

function ChallengeLoading() {
  const t = useTranslations("challenge");
  return (
    <div className="min-h-screen flex flex-col bg-[var(--background)] text-[var(--foreground)] font-sans selection:bg-[var(--bounty)] selection:text-[#282c34]">
      <NavBar />
      <main
        id="main-content"
        className="flex flex-1 items-center justify-center px-4 py-12 scroll-mt-24"
      >
        <Card className="page-card">
          <h1 className="sr-only">{t("loadingChallenge")}</h1>
          <SkeletonRows count={5} />
        </Card>
      </main>
      <Footer />
    </div>
  );
}

function ChallengeContent({ token }: { token: string }) {
  const t = useTranslations("challenge");
  const router = useRouter();
  const searchParams = useSearchParams();
  const { token: authToken, hydrated } = useAuth();

  const [challenge, setChallenge] = useState<ChallengeDto | null>(null);
  const [loading, setLoading] = useState(true);
  const [notFound, setNotFound] = useState(false);
  const [accepting, setAccepting] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // Live "expires in N minutes" readout, ticked coarsely, see ChallengeFriendPanel for the same
  // tradeoff.
  const [nowTick, setNowTick] = useState(() => Date.now());

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    setNotFound(false);
    setError(null);

    getChallenge(token)
      .then((data) => {
        if (cancelled) return;
        setChallenge(data);
      })
      .catch((err) => {
        if (cancelled) return;
        if (err instanceof ApiError && err.status === 404) {
          setNotFound(true);
        } else {
          setError(err instanceof Error ? err.message : t("failedToLoad"));
        }
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });

    return () => {
      cancelled = true;
    };
  }, [token, t]);

  useEffect(() => {
    const id = setInterval(() => setNowTick(Date.now()), 30_000);
    return () => clearInterval(id);
  }, []);

  const handleAccept = useCallback(async () => {
    setAccepting(true);
    setError(null);
    try {
      const res = await acceptChallenge(token);
      router.push(`/game/${res.gameId}?preset=${res.pool}`);
    } catch (err) {
      setAccepting(false);
      setError(err instanceof ApiError ? err.message : t("failedToAccept"));
      // The most likely failure here is "no longer open" (someone else got there first, or it
      // expired between load and click), re-fetch so the honest status replaces a stale "Open"
      // card with a dead button, instead of leaving one on screen.
      getChallenge(token)
        .then(setChallenge)
        .catch(() => {});
    }
  }, [token, router, t]);

  const wantsAutoAccept = searchParams.get("accept") === "1";
  // React 19 strict-mode double-mounts effects in dev; the auto-accept below must fire at most
  // once, or the second call gets a 409 and renders "no longer open" over a game that was created
  // fine (TASK-B-FRONTEND.md step 5).
  const autoAcceptedRef = useRef(false);

  useEffect(() => {
    if (autoAcceptedRef.current) return;
    if (!hydrated || !authToken) return;
    if (!wantsAutoAccept) return;
    if (!challenge || challenge.status !== "Open") return;
    // A bot challenge has to be read before it is accepted. ?accept=1 is how a
    // login detour returns here, and skipping the click would hide the label.
    if (challenge.challengerIsBot) return;
    autoAcceptedRef.current = true;
    handleAccept();
  }, [hydrated, authToken, wantsAutoAccept, challenge, handleAccept]);

  if (loading) return <ChallengeLoading />;

  if (notFound || !challenge) {
    return (
      <div className="min-h-screen flex flex-col bg-[var(--background)] text-[var(--foreground)] font-sans selection:bg-[var(--bounty)] selection:text-[#282c34]">
        <NavBar />
        <main
          id="main-content"
          className="page-card flex flex-1 flex-col items-center px-4 py-16 scroll-mt-24"
        >
          <EmptyState
            headingLevel={1}
            title={t("challengeNotFound")}
            description={error ?? t("notValid")}
          >
            <Link href="/" className={`${CTA_PRIMARY} mt-2`}>
              {t("findAGame")}
            </Link>
          </EmptyState>
        </main>
        <Footer />
      </div>
    );
  }

  const preset = isPresetKey(challenge.pool) ? PRESETS[challenge.pool] : null;
  // The DTO's `colour` is the CHALLENGER's side (README "The wire contract"), invert it for the
  // person reading this page, who would be playing the other one.
  const colourLabel =
    challenge.colour === "Random"
      ? t("colourDecided")
      : t("youPlay", {
          side: challenge.colour === "White" ? "Black" : "White",
        });
  const expiresInMinutes = Math.max(
    0,
    Math.ceil((new Date(challenge.expiresAt).getTime() - nowTick) / 60000),
  );

  return (
    <div className="min-h-screen flex flex-col bg-[var(--background)] text-[var(--foreground)] font-sans selection:bg-[var(--bounty)] selection:text-[#282c34]">
      <NavBar />
      <main
        id="main-content"
        className="flex flex-1 items-center justify-center px-4 py-12 scroll-mt-24"
      >
        <Card padding="p-6" className="page-card">
          <p className="text-sm font-semibold text-muted">
            {t("challengedYou", { name: challenge.challengerName })}
          </p>
          {challenge.challengerIsBot && (
            <p className="mt-3 text-sm text-[var(--foreground)] leading-relaxed">
              {t("opponentIsBot", { name: challenge.challengerName })}
            </p>
          )}

          {preset ? (
            <div className="mt-4">
              <div className="flex flex-wrap items-baseline justify-between gap-x-4">
                <h1 className="text-2xl font-bold tracking-tight text-white text-balance">
                  {preset.label}
                </h1>
                <p className="text-2xl font-bold text-white whitespace-nowrap">
                  {preset.time}
                </p>
              </div>
              <div className="mt-3 flex items-center gap-2">
                <PoolBadge economy={preset.economy} variant="plain" />
                <span aria-hidden="true" className="text-sm text-muted">
                  -
                </span>
                <span className="inline-flex items-center text-sm tracking-widest font-semibold text-muted">
                  {preset.multiplier}
                </span>
              </div>
            </div>
          ) : (
            <div className="mt-4">
              <div className="flex flex-wrap items-baseline justify-between gap-x-4">
                <h1 className="text-2xl font-bold tracking-tight text-white text-balance">
                  {challenge.pool === "custom" ? t("customGame") : challenge.pool}
                </h1>
                {challenge.pool === "custom" && (
                  <p className="text-2xl font-bold text-white whitespace-nowrap">
                    {timeControlLabel(
                      challenge.economy.startTimeSeconds,
                      challenge.economy.baseIncrement,
                    )}
                  </p>
                )}
              </div>
              {challenge.pool === "custom" && (
                <RuleSummary economy={challenge.economy} />
              )}
            </div>
          )}

          <div className="mt-6 flex flex-col gap-4 border-t border-[var(--border)] pt-5">
            <p className="text-sm text-muted">
              {t("unrated")} - {colourLabel}
            </p>

            {challenge.status !== "Open" ? (
              <>
                <p className="text-sm text-[var(--foreground)]">
                  {t(STATUS_MESSAGE_KEY[challenge.status])}
                </p>
                {challenge.status === "Accepted" && challenge.gameId && (
                  <Link
                    href={`/watch/${challenge.gameId}`}
                    className={CTA_SECONDARY}
                  >
                    {t("watchTheGame")}
                  </Link>
                )}
                <Link href="/" className={CTA_PRIMARY}>
                  {t("findAGame")}
                </Link>
              </>
            ) : (
              <>
                <p className="text-sm text-muted">
                  {t("expiresIn", { minutes: expiresInMinutes })}
                </p>

                {error && (
                  <AlreadyInGameNotice
                    message={error}
                    className="text-sm text-loss"
                  />
                )}

                {!hydrated ? (
                  <div role="status">
                    <span className="sr-only">{t("loadingEllipsis")}</span>
                    <SkeletonBlock className="h-11 w-full" />
                  </div>
                ) : authToken ? (
                  <button
                    type="button"
                    onClick={handleAccept}
                    disabled={accepting}
                    className={CTA_PRIMARY}
                  >
                    {accepting ? t("accepting") : t("acceptChallenge")}
                  </button>
                ) : (
                  <div className="flex flex-col gap-3">
                    <Link
                      href={`/login?next=${encodeURIComponent(`/challenge/${token}`)}`}
                      className={CTA_PRIMARY}
                    >
                      {t("createAccount")}
                    </Link>
                    <Link
                      href={`/login?next=${encodeURIComponent(`/challenge/${token}`)}`}
                      className={CTA_SECONDARY}
                    >
                      {t("logIn")}
                    </Link>
                    {!challenge.challengerIsBot && (
                      <p className="text-sm text-muted">{t("autoAcceptNote")}</p>
                    )}
                  </div>
                )}
              </>
            )}
          </div>
        </Card>
      </main>
      <Footer />
    </div>
  );
}

// `useSearchParams` (for `?accept=1` after login) requires a Suspense boundary for static
// rendering in the App Router, see ui/app/login/page.tsx for the same shape. Without it, pasting
// a generated challenge link lands on error.tsx.
export default function ChallengePage({
  params,
}: {
  params: Promise<{ token: string }>;
}) {
  const { token } = use(params);
  return (
    <Suspense fallback={<ChallengeLoading />}>
      <ChallengeContent token={token} />
    </Suspense>
  );
}
