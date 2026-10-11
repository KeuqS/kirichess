import React from "react";
import { getTranslations } from "next-intl/server";
import { NavBar } from "@/components/NavBar";
import { Footer } from "@/components/Footer";
import { PresenceIndicator } from "@/components/PresenceIndicator";
import { RatedPoolsGrid } from "@/components/RatedPoolsGrid";
import { OpenSeekList } from "@/components/OpenSeekList";
import { BotPlayPanel } from "@/components/BotPlayPanel";
import { ChallengeFriendPanel } from "@/components/ChallengeFriendPanel";
import { CustomGameCta } from "@/components/CustomGameCta";
import { SanctionBanner } from "@/components/SanctionBanner";
import { EmailConfirmationBanner } from "@/components/EmailConfirmationBanner";
import { GameHubProvider } from "@/components/GameHubProvider";

export default async function Home() {
  const t = await getTranslations("home");
  return (
    <div className="min-h-screen flex flex-col bg-[var(--background)] text-[var(--foreground)] font-sans selection:bg-[var(--bounty)] selection:text-[#282c34]">
      <NavBar />

      <main id="main-content" className="flex-1 scroll-mt-24">
        <section className="page-width pt-8 pb-16">
          {/*
            P-2-1: the one thing every `?src=` link, press-kit URL and search result lands on, and
            until now the only place it said what KiriChess is was <meta>, invisible to a human
            reader. SITE_DESCRIPTION (ui/lib/site.ts) is the source of this h1's English text; this
            key carries a translated close variant rather than importing that constant directly,
            since SITE_DESCRIPTION itself is deliberately not locale-aware (it only ever reaches a
            <meta> tag, read by crawlers and unfurlers, not by this page's non-English readers).
            Kept sr-only: the visible tagline was removed from the page, but the lobby still owes
            an accessible h1 (styling.md section 4 / P-10-1).
          */}
          <h1 className="sr-only">{t("heroTitle")}</h1>

          {/*
            S-3-8: a Warning changes nothing about what a player can do, so being seen is its whole
            function - /profile is a page nobody has a particular reason to visit, the lobby is the
            page every signed-in player actually lands on. SanctionBanner no-ops for a signed-out
            visitor (gated on useAuth()'s token).
          */}
          <SanctionBanner />

          {/*
            Above the pools on purpose: this is the page whose every control the gate refuses, so the
            explanation and the way out belong ahead of the buttons rather than behind a 403.
          */}
          <EmailConfirmationBanner />

          {/*
          POOLS BY PACE: each tile is a rated-matchmaking queue entry (RatedPoolsGrid), not an
          instant self-play game. This is now the only route to a two-player rated game: it pairs
          you with the closest-rated stranger online in that pool and moves your Glicko-2 rating.
        */}
          <div className="mt-10 mb-6 flex flex-wrap items-center justify-between gap-x-6 gap-y-2">
            <p className="basis-full text-sm leading-relaxed text-muted sm:basis-auto sm:min-w-0 sm:flex-1">
              {t("feelLine")}
            </p>
            <div className="ml-auto">
              <PresenceIndicator />
            </div>
          </div>
          {/*
            I-1-1: one lazy game-hub connection for queue, seeks, and challenge watch.
            BotPlayPanel does not call ensure(), so wrapping the others column does not open a
            socket for a visitor who only plays the bot.
          */}
          <GameHubProvider>
            <RatedPoolsGrid />

            {/*
          Phase 9e.4. Directly under the pools, before the friend-link fallback: if someone is
          already waiting in a pool, joining it is a one-click, zero-setup match, cheaper than
          minting and sending a challenge link.
        */}
            <OpenSeekList />

            <div className="mt-16">
              <div className="flex items-center gap-4 mb-4">
                <span className="text-sm tracking-widest text-muted font-semibold">
                  {t("others")}
                </span>
                <div className="flex-1 h-px bg-[var(--border)]" />
              </div>
              <div className="flex flex-col gap-6">
                {/*
                Challenge link: the fallback when the rated queues above are empty. Always unrated,
                a shared link is too easy to farm Glicko against a cooperating partner.
              */}
                <ChallengeFriendPanel />

                {/* Instant, unrated vs-bot picker. Separate from the rated tiles on purpose. */}
                <div id="play-bot" className="scroll-mt-24">
                  <BotPlayPanel />
                </div>

                {/*
                Workshop, not a queue: invent a ruleset and share it as a link. Always unrated
                because a private config is too easy to farm, and because the point is
                experimenting, not climbing.
              */}
                <div className="flex items-center justify-between gap-6 flex-wrap border border-dashed border-[var(--border)] bg-[var(--surface)] p-5">
                  <div className="max-w-xl">
                    <h2 className="text-lg font-bold text-white">
                      {t("customGameTitle")}
                    </h2>
                    <p className="mt-1 text-sm text-[var(--foreground)] leading-relaxed">
                      {t("customGameBody")}
                    </p>
                  </div>
                  <CustomGameCta
                    buildLabel={t("customGameCta")}
                    signInLabel={t("customGameSignInCta")}
                  />
                </div>
              </div>
            </div>
          </GameHubProvider>
        </section>
      </main>

      <Footer />
    </div>
  );
}
