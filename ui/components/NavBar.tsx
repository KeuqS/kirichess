"use client";

import { useEffect, useState } from "react";
import { Link, usePathname } from "@/i18n/navigation";
import { useTranslations } from "next-intl";
import { IconHeart, IconMenu2, IconX } from "@tabler/icons-react";
import { useAuth } from "@/components/AuthProvider";
import { Avatar } from "@/components/Avatar";
import { BrandMark } from "@/components/BrandMark";
import { resolveAvatarUrl } from "@/lib/avatarUrl";

const navItem =
  "text-sm font-medium transition-colors hover:text-[var(--bounty)]";

function navItemClass(active: boolean, extra = "", idle = "") {
  return `${navItem} ${active ? "text-[var(--bounty)]" : idle} ${extra}`.trim();
}

export function NavBar() {
  const t = useTranslations("NavBar");
  const { displayName, token, logout, isAdmin, avatarUrl } = useAuth();
  const pathname = usePathname();
  const [menuOpen, setMenuOpen] = useState(false);
  const onProfile = pathname === "/profile";

  const navLinks = [
    { href: "/", label: t("play") },
    { href: "/leaderboards", label: t("leaderboards") },
    { href: "/watch", label: t("watch") },
    { href: "/rules", label: t("rules") },
    { href: "/learn", label: t("learn") },
  ];

  // isAdmin only hides the links: it is a UI affordance, not a security boundary. Every
  // moderation and analytics route is enforced server-side by `ModerationPolicies.Admin`.
  const links = isAdmin
    ? [
        ...navLinks,
        { href: "/moderation", label: t("moderation") },
        { href: "/analytics", label: t("analytics") },
      ]
    : navLinks;

  // The mobile disclosure panel is route-scoped state, not page-scoped: close it on navigation
  // so a link tap doesn't leave a stale open panel behind on the next page.
  useEffect(() => {
    setMenuOpen(false);
  }, [pathname]);

  const initials = displayName ? displayName.slice(0, 2).toUpperCase() : "?";
  const donateLabel = (
    <>
      <IconHeart size={16} stroke={2} />
      {t("donate")}
    </>
  );

  const profileLink = (
    <Link
      href="/profile"
      aria-label={t("profile")}
      className="group flex min-w-0 items-center gap-2.5"
    >
      <Avatar
        label={initials}
        src={resolveAvatarUrl(avatarUrl)}
        className={
          onProfile
            ? "border-[var(--bounty)]"
            : "group-hover:border-[var(--bounty)]"
        }
      />
      <span
        className={`max-w-[10rem] truncate text-sm font-medium transition-colors ${
          onProfile
            ? "text-[var(--bounty)]"
            : "text-muted group-hover:text-[var(--bounty)]"
        }`}
      >
        {displayName}
      </span>
    </Link>
  );

  return (
    <>
      <a
        href="#main-content"
        className="skip-link btn-primary px-5 py-2 text-sm"
      >
        {t("skipToContent")}
      </a>
      <nav className="nav-safe-top border-b border-[var(--border)] sticky top-0 z-30 bg-[var(--surface)]/90 backdrop-blur-md w-full">
        <div className="flex h-16 w-full items-center gap-4 px-4 lg:gap-8 lg:px-6">
          <Link
            href="/"
            className="flex items-center gap-3 text-white transition-colors hover:text-[var(--bounty)] hover:[&_.brand-mark-outline]:opacity-0 hover:[&_.brand-mark-solid]:opacity-100"
          >
            <BrandMark solidOnHover />
            <span className="font-semibold tracking-widest text-sm">
              {t("brand")}
            </span>
          </Link>
          <div className="hidden lg:flex items-center gap-6 text-sm font-medium text-[var(--foreground)]">
            {links.map((link) => (
              <Link
                key={link.href}
                href={link.href}
                className={navItemClass(pathname === link.href)}
              >
                {link.label}
              </Link>
            ))}
            <Link
              href="/donate"
              className={navItemClass(
                pathname === "/donate",
                "inline-flex items-center gap-1",
                "text-white",
              )}
            >
              {donateLabel}
            </Link>
          </div>
          <div className="hidden lg:flex ml-auto items-center gap-4 text-sm font-medium text-[var(--foreground)]">
            {token ? (
              <>
                {profileLink}
                <button onClick={logout} className={navItemClass(false)}>
                  {t("logOut")}
                </button>
              </>
            ) : (
              <Link href="/login" className={navItemClass(false)}>
                {t("logIn")}
              </Link>
            )}
          </div>
          <button
            type="button"
            onClick={() => setMenuOpen((o) => !o)}
            aria-expanded={menuOpen}
            aria-controls="mobile-nav-panel"
            aria-label={menuOpen ? t("closeMenu") : t("openMenu")}
            className="ml-auto lg:hidden p-2 text-[var(--foreground)] hover:text-[var(--bounty)] transition-colors"
          >
            {menuOpen ? (
              <IconX size={20} stroke={2} />
            ) : (
              <IconMenu2 size={20} stroke={2} />
            )}
          </button>
        </div>

        {menuOpen && (
          <div
            id="mobile-nav-panel"
            className="lg:hidden border-t border-[var(--border)] px-4 py-4 flex flex-col gap-1 text-sm font-medium text-[var(--foreground)]"
          >
            {links.map((link) => (
              <Link
                key={link.href}
                href={link.href}
                className={navItemClass(pathname === link.href, "px-3 py-2.5")}
              >
                {link.label}
              </Link>
            ))}
            <Link
              href="/donate"
              className={navItemClass(
                pathname === "/donate",
                "inline-flex items-center gap-1 px-3 py-2.5",
                "text-white",
              )}
            >
              {donateLabel}
            </Link>

            {token ? (
              <div className="mt-3 pt-3 border-t border-[var(--border)] flex items-center gap-4 px-3">
                {profileLink}
                <button onClick={logout} className={`ml-auto ${navItem}`}>
                  {t("logOut")}
                </button>
              </div>
            ) : (
              <Link
                href="/login"
                className={navItemClass(false, "px-3 py-2.5")}
              >
                {t("logIn")}
              </Link>
            )}
          </div>
        )}
      </nav>
    </>
  );
}
