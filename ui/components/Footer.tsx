"use client";

import React from "react";
import { Link } from "@/i18n/navigation";
import { useTranslations } from "next-intl";
import {
  IconBrandDiscord,
  IconBrandGithub,
  IconBrandInstagram,
  IconBrandReddit,
  IconBrandX,
  IconMail,
} from "@tabler/icons-react";
import { BrandMark } from "@/components/BrandMark";
import { LocaleSwitcher } from "@/components/LocaleSwitcher";
import { DISCORD_URL, GITHUB_URL } from "@/lib/site";

/**
 * Site footer: the counterpart to `NavBar`, and rendered by the same pages, with two
 * deliberate exceptions: the live board views (`/game/[id]`, `/watch/[id]`), where chrome
 * under a running clock is noise, and the auth screens, which carry no chrome at all.
 */

// Wire these up once the addresses exist. Each link renders only when its constant is set,
// so a half-configured footer can never ship a dead `mailto:` or an expired invite.
// Keep the explicit `string | null` annotations: without them TypeScript narrows the type
// to `null`, and the JSX guards below start reading as dead code.
// GitHub / Discord come from `lib/site.ts` so JSON-LD and /about cannot drift from this row.
const CONTACT_EMAIL: string | null = "contact@kirichess.com";
const INSTAGRAM_URL: string | null = "https://www.instagram.com/kirichess/";
const REDDIT_URL: string | null = "https://www.reddit.com/user/KiriChess/";
const X_URL: string | null = "https://x.com/kirichess";

const linkClass =
  "inline-flex items-center gap-2 text-[var(--foreground)]/80 hover:text-[var(--bounty)] transition-colors";

function FooterColumn({
  title,
  children,
}: {
  title: string;
  children: React.ReactNode;
}) {
  return (
    <div className="flex min-w-[8rem] flex-1 flex-col gap-4">
      <h2 className="text-sm tracking-widest text-muted font-semibold">
        {title}
      </h2>
      <ul className="flex flex-col gap-3 text-sm font-medium">{children}</ul>
    </div>
  );
}

export function Footer() {
  const t = useTranslations("Footer");
  const year = new Date().getFullYear();

  // Terms, privacy, and the licence/NOTICE grant. This row is the only site-wide route to
  // them: nothing in `NavBar` links here, and the only other inbound links are the register
  // form's consent line and `AccountDataCard`.
  const legalLinks: { href: string; label: string }[] = [
    { href: "/terms", label: t("terms") },
    { href: "/privacy", label: t("privacy") },
    { href: "/about#licence", label: t("licence") },
  ];

  const playLinks = [
    { href: "/", label: t("play") },
    { href: "/custom", label: t("customGame") },
    { href: "/watch", label: t("watch") },
    { href: "/leaderboards", label: t("leaderboards") },
  ];

  const learnLinks = [
    { href: "/rules", label: t("rules") },
    { href: "/learn", label: t("tutorial") },
  ];

  return (
    <footer className="footer-safe-bottom mt-auto w-full border-t border-[var(--border)] bg-[var(--surface)]">
      <div className="grid w-full grid-cols-1 gap-10 px-10 py-12 sm:grid-cols-2 xl:grid-cols-[minmax(14rem,1.4fr)_repeat(4,minmax(8rem,1fr))]">
        <div className="flex max-w-sm flex-col gap-4 sm:col-span-2 xl:col-span-1">
          <Link href="/" className="flex items-center gap-3">
            <BrandMark />
            <span className="font-semibold tracking-widest text-sm text-white">
              {t("brand")}
            </span>
          </Link>
          <p className="whitespace-pre-line text-sm leading-relaxed text-[var(--foreground)]/80">
            {t("tagline")}
          </p>
        </div>

        <nav aria-label={t("footerNav")} className="contents">
          <FooterColumn title={t("playHeading")}>
            {playLinks.map((link) => (
              <li key={link.href}>
                <Link href={link.href} className={linkClass}>
                  {link.label}
                </Link>
              </li>
            ))}
          </FooterColumn>

          <FooterColumn title={t("learnHeading")}>
            {learnLinks.map((link) => (
              <li key={link.href}>
                <Link href={link.href} className={linkClass}>
                  {link.label}
                </Link>
              </li>
            ))}
          </FooterColumn>

          <FooterColumn title={t("projectHeading")}>
            <li>
              <Link href="/about" className={linkClass}>
                {t("about")}
              </Link>
            </li>
            <li>
              <Link href="/developers" className={linkClass}>
                {t("developers")}
              </Link>
            </li>
            <li>
              <Link href="/donate" className={linkClass}>
                {t("donate")}
              </Link>
            </li>
          </FooterColumn>

          <FooterColumn title={t("socialsHeading")}>
            {CONTACT_EMAIL && (
              <li>
                <a href={`mailto:${CONTACT_EMAIL}`} className={linkClass}>
                  <IconMail size={16} stroke={1.75} />
                  {t("contact")}
                </a>
              </li>
            )}
            {DISCORD_URL && (
              <li>
                <a
                  href={DISCORD_URL}
                  target="_blank"
                  rel="noopener noreferrer"
                  className={linkClass}
                >
                  <IconBrandDiscord size={16} stroke={1.75} />
                  {t("discord")}
                </a>
              </li>
            )}
            {GITHUB_URL && (
              <li>
                <a
                  href={GITHUB_URL}
                  target="_blank"
                  rel="noopener noreferrer"
                  className={linkClass}
                >
                  <IconBrandGithub size={16} stroke={1.75} />
                  {t("github")}
                </a>
              </li>
            )}
            {REDDIT_URL && (
              <li>
                <a
                  href={REDDIT_URL}
                  target="_blank"
                  rel="noopener noreferrer"
                  className={linkClass}
                >
                  <IconBrandReddit size={16} stroke={1.75} />
                  {t("reddit")}
                </a>
              </li>
            )}
            {INSTAGRAM_URL && (
              <li>
                <a
                  href={INSTAGRAM_URL}
                  target="_blank"
                  rel="noopener noreferrer"
                  className={linkClass}
                >
                  <IconBrandInstagram size={16} stroke={1.75} />
                  {t("instagram")}
                </a>
              </li>
            )}
            {X_URL && (
              <li>
                <a
                  href={X_URL}
                  target="_blank"
                  rel="noopener noreferrer"
                  className={linkClass}
                >
                  <IconBrandX size={16} stroke={1.75} />
                  {t("x")}
                </a>
              </li>
            )}
          </FooterColumn>
        </nav>
      </div>

      <div className="flex w-full flex-wrap items-center justify-between gap-4 border-t border-[var(--border)] px-10 py-6">
        <span className="text-sm font-semibold tracking-widest text-muted">
          {t("copyright", { year })}
        </span>
        <div className="flex flex-wrap items-center gap-6 text-sm font-medium">
          <LocaleSwitcher />
          {legalLinks.length > 0 && (
            <div className="flex flex-wrap gap-6 text-sm font-medium">
              {legalLinks.map((link) => (
                <Link
                  key={link.href}
                  href={link.href}
                  className="text-muted hover:text-[var(--bounty)] transition-colors"
                >
                  {link.label}
                </Link>
              ))}
            </div>
          )}
        </div>
      </div>
    </footer>
  );
}
