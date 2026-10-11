import type { ReactNode } from "react";
import { AuthBrandHeading } from "@/components/BrandMark";

interface AuthShellProps {
  /** Wordmark text for `AuthBrandHeading`, e.g. `t("brand")`. */
  brand: ReactNode;
  /**
   * The page's one h1: names this screen, not the site. Visible card-page heading by default.
   * `/login` passes `srOnlyHeading` because the Log in / Register tabs already name the mode.
   */
  heading: ReactNode;
  children: ReactNode;
  /** Centre the body copy. Used by the status screens (verify-email, unsubscribe, callback). */
  center?: boolean;
  /**
   * Keep the h1 in the document and hide it visually. The heading still updates with the tab so
   * the accessible name stays in sync. Other auth cards have no control that names the screen
   * and leave this off.
   */
  srOnlyHeading?: boolean;
}

/**
 * The chrome-less shell every auth and mail-link screen sits in: no NavBar, no Footer, one centred
 * `page-card`. This markup was previously repeated verbatim in six page files, which is how the
 * inputs on those pages drifted apart from the rest of the app.
 *
 * The card is the overlay elevation tier (`shadow-2xl`) per `.claude/rules/styling.md`, not the
 * `shadow-xl` the copies happened to carry.
 */
export function AuthShell({
  brand,
  heading,
  children,
  center = false,
  srOnlyHeading = false,
}: AuthShellProps) {
  return (
    <div className="mx-auto flex min-h-screen w-full flex-col justify-center bg-[var(--background)] px-6 font-sans text-[var(--foreground)] selection:bg-[var(--bounty)] selection:text-[#282c34]">
      <main
        id="main-content"
        className={`page-card scroll-mt-24 border border-[var(--border)] bg-[var(--surface)] p-8 shadow-2xl${center ? " text-center" : ""}`}
      >
        <AuthBrandHeading>{brand}</AuthBrandHeading>
        <h1
          className={
            srOnlyHeading
              ? "sr-only"
              : "mb-6 text-2xl font-bold tracking-tight text-white text-balance"
          }
        >
          {heading}
        </h1>
        {children}
      </main>
    </div>
  );
}
