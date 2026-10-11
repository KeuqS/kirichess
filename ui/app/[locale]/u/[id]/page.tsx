"use client";

import { use, useEffect, useState } from "react";
import { Link } from "@/i18n/navigation";
import { useTranslations } from "next-intl";
import { NavBar } from "@/components/NavBar";
import { Footer } from "@/components/Footer";
import { Avatar } from "@/components/Avatar";
import { Card } from "@/components/Card";
import { EmptyState } from "@/components/EmptyState";
import { PatronIcon } from "@/components/PatronIcon";
import { PoolRatingCard } from "@/components/PoolRatingCard";
import { SkeletonRows } from "@/components/SkeletonRows";
import { useAuth } from "@/components/AuthProvider";
import { getPublicProfile, ApiError } from "@/lib/api";
import { resolveAvatarUrl } from "@/lib/avatarUrl";
import { ReportDialog } from "@/components/ReportDialog";
import type { PublicProfileDto } from "@/lib/types";

/** Same shape as `MatchHistoryCard`'s and `PatronBadge`'s local helpers: not exported from here either. */
function formatJoined(iso: string): string {
  return new Date(iso).toLocaleDateString(undefined, {
    year: "numeric",
    month: "long",
  });
}

/**
 * One player as a stranger sees them (`GET /api/users/{id}`).
 *
 * Anonymous, like /leaderboards and /replay: the leaderboard already hands every visitor a name
 * and a user id, and "who is this player" is the question that follows. Everything rendered here
 * is public elsewhere on the site, which is the whole reason the route needs no auth.
 *
 * An unknown id and an erased account are one case on purpose (the server 404s both identically,
 * see `Account/PublicProfileEndpoints.cs`), so this page must not word the empty state as though
 * deletion were distinguishable from a typo.
 */
function PublicProfileContent({ userId }: { userId: string }) {
  const t = useTranslations("publicProfile");
  const { userId: myId } = useAuth();
  const [profile, setProfile] = useState<PublicProfileDto | null>(null);
  const [loading, setLoading] = useState(true);
  const [notFound, setNotFound] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [reportOpen, setReportOpen] = useState(false);

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    setNotFound(false);
    setError(null);

    getPublicProfile(userId)
      .then((data) => {
        if (cancelled) return;
        setProfile(data);
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
  }, [userId, t]);

  const isMe = myId !== null && myId === userId;

  return (
    <div className="min-h-screen flex flex-col bg-[var(--background)] text-[var(--foreground)] font-sans selection:bg-[var(--bounty)] selection:text-[#282c34]">
      <NavBar />

      <main
        id="main-content"
        className="flex-1 page-width pt-10 pb-24 scroll-mt-24"
      >
        {loading ? (
          <>
            <h1 className="sr-only">{t("loadingTitle")}</h1>
            <Card padding="py-12 px-8" className="shadow-lg">
              <SkeletonRows count={6} />
            </Card>
          </>
        ) : notFound || error ? (
          <EmptyState
            headingLevel={1}
            title={notFound ? t("notFoundTitle") : t("failedTitle")}
            description={
              notFound ? t("notFoundDescription") : (error ?? undefined)
            }
            className="py-24"
          >
            <Link
              href="/leaderboards"
              className="btn-secondary px-6 py-3 text-sm"
            >
              {t("backToLeaderboards")}
            </Link>
          </EmptyState>
        ) : profile ? (
          <>
            <header className="flex items-center gap-6 flex-wrap pb-8 border-b border-[var(--border)] mb-8">
              <Avatar
                label={profile.displayName.slice(0, 2).toUpperCase()}
                src={resolveAvatarUrl(profile.avatarUrl)}
                size="lg"
                shape="square"
              />
              <div className="flex-1 min-w-[220px]">
                <div className="flex items-center gap-3 flex-wrap">
                  <h1 className="text-3xl font-bold leading-tight tracking-tight mb-4 text-white text-balance">
                    {profile.displayName}
                  </h1>
                  {/* Renders nothing for "None" or an unknown tier, so it needs no guard here. */}
                  <PatronIcon tier={profile.patronTier} size={22} />
                </div>
                <div className="flex items-center gap-4 mt-3 text-sm text-muted flex-wrap">
                  <span>
                    {t("joined", { date: formatJoined(profile.joinedAt) })}
                  </span>
                  <span aria-hidden="true" className="text-muted">
                    -
                  </span>
                  <span>
                    {t("gamesPlayed", { count: profile.gamesPlayed })}
                  </span>
                </div>
              </div>
              {isMe ? (
                <Link
                  href="/profile"
                  className="btn-secondary px-5 py-2 text-sm"
                >
                  {t("editYourProfile")}
                </Link>
              ) : myId ? (
                <button
                  type="button"
                  onClick={() => setReportOpen(true)}
                  className="btn-secondary px-5 py-2 text-sm"
                >
                  {t("reportPlayer")}
                </button>
              ) : null}
            </header>

            <h2 className="profile-category">{t("ratedPoolsHeading")}</h2>
            {profile.ratings.length === 0 ? (
              <p className="text-sm text-muted mt-4">{t("noRatedGames")}</p>
            ) : (
              <div className="grid grid-cols-[repeat(auto-fit,minmax(min(100%,20rem),1fr))] gap-6 mt-4">
                {profile.ratings.map((rating) => (
                  <PoolRatingCard key={rating.pool} rating={rating} />
                ))}
              </div>
            )}
          </>
        ) : null}
      </main>

      <Footer />

      {reportOpen && !isMe ? (
        <ReportDialog
          reportedUserId={userId}
          gameId={null}
          onClose={() => setReportOpen(false)}
        />
      ) : null}
    </div>
  );
}

export default function PublicProfilePage({
  params,
}: {
  params: Promise<{ id: string }>;
}) {
  const { id } = use(params);
  return <PublicProfileContent userId={id} />;
}
