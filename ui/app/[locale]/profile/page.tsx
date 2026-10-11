"use client";

import { useEffect, useState } from "react";
import { useTranslations } from "next-intl";
import { NavBar } from "@/components/NavBar";
import { Footer } from "@/components/Footer";
import { EditableAvatar } from "@/components/EditableAvatar";
import { PoolRatingCard } from "@/components/PoolRatingCard";
import { SanctionBanner } from "@/components/SanctionBanner";
import { EmailConfirmationBanner } from "@/components/EmailConfirmationBanner";
import { PatronBadge } from "@/components/PatronBadge";
import { DisplayNameCard } from "@/components/DisplayNameCard";
import { AccountDataCard } from "@/components/AccountDataCard";
import { SecurityCard } from "@/components/SecurityCard";
import { ApiTokensCard } from "@/components/ApiTokensCard";
import { NotificationPreferencesCard } from "@/components/NotificationPreferencesCard";
import { MatchHistoryCard } from "@/components/MatchHistoryCard";
import { SkeletonRows } from "@/components/SkeletonRows";
import { useAuth } from "@/components/AuthProvider";
import { getMyRatings, getPublicProfile } from "@/lib/api";
import type { RatingDto } from "@/lib/types";

function formatJoined(iso: string): string {
  return new Date(iso).toLocaleDateString(undefined, {
    year: "numeric",
    month: "long",
  });
}

export default function Profile() {
  const t = useTranslations("profile");
  // Reuses publicProfile.noRatedGames rather than adding a duplicate string to the profile
  // namespace: same empty-ratings case as the public profile page, just for the signed-in viewer.
  const tPublic = useTranslations("publicProfile");
  const { displayName, avatarUrl, token, userId } = useAuth();
  const headerName = displayName;
  const initials = headerName ? headerName.slice(0, 2).toUpperCase() : "?";

  const [ratings, setRatings] = useState<RatingDto[]>([]);
  const [ratingsLoading, setRatingsLoading] = useState(true);
  const [accountMeta, setAccountMeta] = useState<{
    joinedAt: string;
    gamesPlayed: number;
  } | null>(null);

  useEffect(() => {
    if (!token || !userId) {
      setRatings([]);
      setAccountMeta(null);
      setRatingsLoading(false);
      return;
    }
    let cancelled = false;
    setRatingsLoading(true);
    getMyRatings()
      .then((rows) => {
        if (cancelled) return;
        setRatings(rows);
      })
      .catch(() => {
        if (!cancelled) setRatings([]);
      })
      .finally(() => {
        if (!cancelled) setRatingsLoading(false);
      });
    getPublicProfile(userId)
      .then((profile) => {
        if (cancelled) return;
        setAccountMeta({
          joinedAt: profile.joinedAt,
          gamesPlayed: profile.gamesPlayed,
        });
      })
      .catch(() => {
        if (!cancelled) setAccountMeta(null);
      });
    return () => {
      cancelled = true;
    };
  }, [token, userId]);

  return (
    <div className="min-h-screen flex flex-col bg-[var(--background)] text-[var(--foreground)] font-sans selection:bg-[var(--bounty)] selection:text-[#282c34]">
      <NavBar />

      <main
        id="main-content"
        className="flex-1 page-width pt-10 pb-24 scroll-mt-24"
      >
        {/* IDENTITY HEADER */}
        <header className="flex items-center gap-6 flex-wrap pb-8 border-b border-[var(--border)] mb-8">
          <EditableAvatar
            label={initials}
            avatarUrl={avatarUrl}
            size="lg"
            shape="square"
          />
          <div className="flex-1 min-w-[220px]">
            {/*
              Phase 8a.4: the pencil on the name is the rename control. Signed out, there is
              nothing to rename, so the heading stays the guest label.
            */}
            {token ? (
              <DisplayNameCard />
            ) : (
              <h1 className="text-3xl font-bold leading-tight tracking-tight mb-4 text-white text-balance">
                {t("guestName")}
              </h1>
            )}
            {/* Peak rating isn't tracked, so it isn't rendered rather than shown as a number. */}
            <div className="mt-3 text-sm text-muted">
              <PatronBadge />
              {accountMeta ? (
                <>
                  <span className="whitespace-nowrap">
                    {tPublic("joined", {
                      date: formatJoined(accountMeta.joinedAt),
                    })}
                  </span>
                  <span className="whitespace-nowrap">
                    <span aria-hidden="true" className="mx-4">
                      -
                    </span>
                    {tPublic("gamesPlayed", {
                      count: accountMeta.gamesPlayed,
                    })}
                  </span>
                </>
              ) : null}
            </div>
          </div>
        </header>

        {/*
          Phase 8.5: a RatedPlayBan otherwise surfaces only as a terse "you are not currently
          eligible for rated play" thrown from the matchmaking hub, with the reason nowhere on the
          site. This is where the player reads it. Renders nothing when there is nothing to show,
          and stays silent on a fetch failure - a profile page must not break because a moderation
          call failed.
        */}
        <SanctionBanner />

        <EmailConfirmationBanner />

        {/*
          Phase 9e.1 follow-up (P-1-1): real GET /api/ratings/me, replacing the MOCK_RATINGS
          fixture that used to render here unconditionally. No rating-history endpoint is wired to
          any caller yet (see RatingEndpoints.cs), so unlike the pool cards below, that chart has
          no real data to show and was removed rather than faked - same call the P-1-1 fix made for
          peak rating above. Renders nothing when signed out, matching the cards around it.
        */}
        {token && (
          <div className="animate-[fadeIn_0.3s_ease]">
            <div className="flex items-baseline justify-between">
              <h2 className="profile-category">{t("ratedPoolsHeading")}</h2>
              <span className="text-sm text-muted tracking-widest">
                {t("ratedPoolsCaption")}
              </span>
            </div>
            {ratingsLoading ? (
              <div className="mb-8">
                <SkeletonRows count={3} />
              </div>
            ) : ratings.length === 0 ? (
              <p className="text-sm text-muted mt-4 mb-8">
                {tPublic("noRatedGames")}
              </p>
            ) : (
              <div className="grid grid-cols-[repeat(auto-fit,minmax(min(100%,20rem),1fr))] gap-6 mb-8">
                {ratings.map((rating) => (
                  <PoolRatingCard key={rating.pool} rating={rating} />
                ))}
              </div>
            )}
          </div>
        )}

        {/* Phase 9e.1: paginated GET /api/games/mine. Renders nothing when signed out. */}
        <MatchHistoryCard />

        {/*
          Mail preferences, plus a device push row when this browser can receive it.
          Live, signed-in only.
        */}
        <NotificationPreferencesCard />

        {/*
          Phase 9d.2 + 9e.6: 2FA enrolment and revocable sessions, one card.
        */}
        <SecurityCard />

        {/*
          Phase 9f.5: independently revocable bot tokens. Logout and 2FA toggles do not revoke
          these; that independence is the point, so the card has to say so. Live, signed-in only.
        */}
        <ApiTokensCard />

        {/*
          Phase 8a.2: export and erasure. Last on the page. Live, signed-in only.
        */}
        <AccountDataCard />
      </main>
      <Footer />
    </div>
  );
}
