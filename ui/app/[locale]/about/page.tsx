import type { Metadata } from "next";
import React from "react";
import { Link } from "@/i18n/navigation";
import { getLocale, getTranslations } from "next-intl/server";
import { NavBar } from "@/components/NavBar";
import { Footer } from "@/components/Footer";
import { routeMetadata } from "@/lib/seo";
import { GITHUB_URL, DISCORD_URL } from "@/lib/site";

// M-1-1 / M-1-2: `routeMetadata` adds the openGraph/twitter card and self-referencing
// canonical/hreflang this page was missing, alongside the translated title/description it already
// had. Without the explicit openGraph/twitter here this page inherited the root layout's whole
// object (title "KiriChess", url "/"), so a shared /about link previewed as the homepage.
export async function generateMetadata(): Promise<Metadata> {
  const [locale, t] = await Promise.all([
    getLocale(),
    getTranslations("about"),
  ]);
  return routeMetadata({
    locale,
    path: "/about",
    title: t("metaTitle"),
    description: t("metaDescription"),
  });
}

const CONTACT_EMAIL = "contact@kirichess.com";
const CC_LICENSE_URL = "https://creativecommons.org/licenses/by/4.0/";
const STOCKFISH_LICENSE_PATH = "/STOCKFISH.LICENSE";
const SOUNDS_LICENSE_PATH = "/sounds/SOUNDS.LICENSE";

// Set once the chessvariants.com entry is live (ROADMAP AD.3). Until then the
// catalogue block uses standing prose, not an invented URL and not a [TODO].
const CHESSVARIANTS_URL: string | null = null;

const inlineLinkClass = "text-[var(--bounty)] font-medium hover:underline";

function MailLink(chunks: React.ReactNode) {
  return (
    <a href={`mailto:${CONTACT_EMAIL}`} className={inlineLinkClass}>
      {chunks}
    </a>
  );
}

function ExternalLink(href: string) {
  return function Linked(chunks: React.ReactNode) {
    return (
      <a
        href={href}
        target="_blank"
        rel="noopener noreferrer"
        className={inlineLinkClass}
      >
        {chunks}
      </a>
    );
  };
}

function InternalLink(href: string) {
  return function Linked(chunks: React.ReactNode) {
    return (
      <Link href={href} className={inlineLinkClass}>
        {chunks}
      </Link>
    );
  };
}

// The page a catalogue entry or a Wikipedia editor cites: who invented KiriChess, when, and
// why, on a page under the owner's own control. See docs/ROADMAP.md's AD.2 for why the "why"
// paragraph below is hand-written rather than generated, an origin story on the one page
// whose whole purpose is attribution is worth less than nothing if it isn't the real one.
export default async function About() {
  const t = await getTranslations("about");

  return (
    <div className="min-h-screen flex flex-col bg-[var(--background)] text-[var(--foreground)] font-sans selection:bg-[var(--bounty)] selection:text-[#282c34]">
      <NavBar />

      <main
        id="main-content"
        className="flex-1 page-prose pt-10 pb-32 scroll-mt-24"
      >
        <section>
          <h1 className="text-3xl font-bold leading-tight tracking-tight mb-4 text-white text-balance">
            {t("heading")}
          </h1>
          <p className="text-[15px] text-[var(--foreground)] leading-relaxed mb-10">
            {t("subheading")}
          </p>

          <div className="grid grid-cols-1 sm:grid-cols-3 gap-4 mb-5">
            <div className="border border-[var(--border)] bg-[var(--surface)] p-5">
              <div className="text-sm tracking-widest text-muted mb-2">
                {t("inventedByLabel")}
              </div>
              <div className="text-white font-medium">
                {t("inventedByValue")}
              </div>
            </div>
            <div className="border border-[var(--border)] bg-[var(--surface)] p-5">
              <div className="text-sm tracking-widest text-muted mb-2">
                {t("dateLabel")}
              </div>
              <div className="text-white font-medium">{t("dateValue")}</div>
            </div>
            <div className="border border-[var(--border)] bg-[var(--surface)] p-5">
              <div className="text-sm tracking-widest text-muted mb-2">
                {t("workingTitleLabel")}
              </div>
              <div className="text-white font-medium">
                {t("workingTitleValue")}
              </div>
            </div>
          </div>
          <p className="text-[15px] text-[var(--foreground)] leading-relaxed mb-14">
            {t("repoPara")}
          </p>

          <article className="mb-14">
            <h2 className="text-xl font-bold text-white mb-4 tracking-tight">
              {t("ideaHeading")}
            </h2>
            <div className="flex flex-col gap-5">
              <p className="text-[15px] text-[var(--foreground)] leading-relaxed">
                {t("ideaPara1")}
              </p>
              <p className="text-[15px] text-[var(--foreground)] leading-relaxed">
                {t.rich("ideaPara2", { rules: InternalLink("/rules") })}
              </p>
              <p className="text-[15px] text-[var(--foreground)] leading-relaxed">
                {t("ideaPara3")}
              </p>
            </div>
          </article>

          <article className="mb-14">
            <h2 className="text-xl font-bold text-white mb-4 tracking-tight">
              {t("whyHeading")}
            </h2>
            <div className="flex flex-col gap-5">
              <p className="text-[15px] text-[var(--foreground)] leading-relaxed">
                {t("whyPara1")}
              </p>
              <p className="text-[15px] text-[var(--foreground)] leading-relaxed">
                {t("whyPara2")}
              </p>
            </div>
          </article>

          <article className="mb-14">
            <h2 className="text-xl font-bold text-white mb-4 tracking-tight">
              {t("nameHeading")}
            </h2>
            <p className="text-[15px] text-[var(--foreground)] leading-relaxed">
              {t("namePara")}
            </p>
          </article>

          <article className="mb-14">
            <h2 className="text-xl font-bold text-white mb-4 tracking-tight">
              {t("platformHeading")}
            </h2>
            <div className="flex flex-col gap-5">
              <p className="text-[15px] text-[var(--foreground)] leading-relaxed">
                {t.rich("platformPara", { watch: InternalLink("/watch") })}
              </p>
              <p className="text-[15px] text-[var(--foreground)] leading-relaxed">
                {t("platformPara2")}
              </p>
            </div>
          </article>

          <article className="mb-14">
            <h2 className="text-xl font-bold text-white mb-4 tracking-tight">
              {t("projectHeading")}
            </h2>
            <div className="flex flex-col gap-5">
              <p className="text-[15px] text-[var(--foreground)] leading-relaxed">
                {t.rich("projectPara", { donate: InternalLink("/donate") })}
              </p>
              <p className="text-[15px] text-[var(--foreground)] leading-relaxed">
                {t("projectPara2")}
              </p>
            </div>
          </article>

          <article className="mb-14">
            <h2 className="text-xl font-bold text-white mb-4 tracking-tight">
              {t("tryHeading")}
            </h2>
            <div className="flex items-center gap-3 flex-wrap">
              <Link href="/" className="btn-primary px-5 py-2 text-sm">
                {t("playLinkLabel")}
              </Link>
              <Link
                href="/#play-bot"
                className="btn-secondary px-5 py-2 text-sm"
              >
                {t("practiceLinkLabel")}
              </Link>
              <Link href="/learn" className="btn-secondary px-5 py-2 text-sm">
                {t("learnLinkLabel")}
              </Link>
              <Link href="/rules" className="btn-secondary px-5 py-2 text-sm">
                {t("rulesLinkLabel")}
              </Link>
              <Link href="/watch" className="btn-secondary px-5 py-2 text-sm">
                {t("watchLinkLabel")}
              </Link>
            </div>
          </article>

          <article className="mb-14">
            <h2 className="text-xl font-bold text-white mb-4 tracking-tight">
              {t("contactHeading")}
            </h2>
            <div className="flex flex-col gap-5">
              <p className="text-[15px] text-[var(--foreground)] leading-relaxed">
                {t.rich("contactPara", {
                  mail: MailLink,
                  discord: ExternalLink(DISCORD_URL),
                })}
              </p>
              <p className="text-[15px] text-[var(--foreground)] leading-relaxed">
                {t("reportPara")}
              </p>
              <p className="text-[15px] text-[var(--foreground)] leading-relaxed">
                {t("reportInGame")}
              </p>
            </div>
          </article>

          <article className="mb-14">
            <h2 className="text-xl font-bold text-white mb-4 tracking-tight">
              {t("a11yHeading")}
            </h2>
            <div className="flex flex-col gap-5">
              <p className="text-[15px] text-[var(--foreground)] leading-relaxed">
                {t("a11yPara")}
              </p>
              <p className="text-[15px] text-[var(--foreground)] leading-relaxed">
                {t.rich("a11yReport", { mail: MailLink })}
              </p>
            </div>
          </article>

          <article className="mb-14">
            <h2 className="text-xl font-bold text-white mb-4 tracking-tight">
              {t("citeHeading")}
            </h2>
            <div className="flex flex-col gap-5">
              <p className="text-[15px] text-[var(--foreground)] leading-relaxed">
                {t("citePara")}
              </p>
              <p className="text-[15px] text-[var(--foreground)] leading-relaxed">
                {t("citePara2")}
              </p>
              {CHESSVARIANTS_URL ? (
                <p className="text-[15px] text-[var(--foreground)] leading-relaxed">
                  {t.rich("cataloguePara", {
                    catalogue: ExternalLink(CHESSVARIANTS_URL!),
                  })}
                </p>
              ) : (
                <p className="text-[15px] text-[var(--foreground)] leading-relaxed">
                  {t("catalogueIntro")}
                </p>
              )}
            </div>
          </article>

          {/* LICENCE, CC BY 4.0 on the manual, Apache-2.0 on the platform source
              (see NOTICE). Surfaced here and on /rules so the grant is readable on
              the site, not only in git. Stockfish is a separate GPL-3.0 program;
              board-move sounds are CC0, outcome sounds original and Apache-2.0. */}
          <article>
            <h2
              id="licence"
              className="text-xl font-bold text-white mb-4 tracking-tight scroll-mt-24"
            >
              {t("licenceH2")}
            </h2>
            <div className="flex flex-col gap-5">
              <p className="text-[15px] text-[var(--foreground)] leading-relaxed">
                {t("licenceBody")} {t("licenceAttribution")}{" "}
                <a
                  href={CC_LICENSE_URL}
                  target="_blank"
                  rel="noopener noreferrer license"
                  className={inlineLinkClass}
                >
                  {t("licenceLinkLabel")}
                </a>
                .
              </p>
              <p className="text-[15px] text-[var(--foreground)] leading-relaxed">
                {t("sourceBody")}{" "}
                {GITHUB_URL ? (
                  <a
                    href={GITHUB_URL}
                    target="_blank"
                    rel="noopener noreferrer"
                    className={inlineLinkClass}
                  >
                    {t("sourceLinkLabel")}
                  </a>
                ) : (
                  t("sourcePlaceholder")
                )}
              </p>
              <p className="text-[15px] text-[var(--foreground)] leading-relaxed">
                {t("stockfishBody")}{" "}
                <a href={STOCKFISH_LICENSE_PATH} className={inlineLinkClass}>
                  {t("stockfishLinkLabel")}
                </a>
                .
              </p>
              <p className="text-[15px] text-[var(--foreground)] leading-relaxed">
                {t("soundsBody")}{" "}
                <a href={SOUNDS_LICENSE_PATH} className={inlineLinkClass}>
                  {t("soundsLinkLabel")}
                </a>
                .
              </p>
            </div>
          </article>
        </section>
      </main>

      <Footer />
    </div>
  );
}
