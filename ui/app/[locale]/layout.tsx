import type { Metadata, Viewport } from "next";
import { Montserrat } from "next/font/google";
import { NextIntlClientProvider } from "next-intl";
import { getLocale, getMessages } from "next-intl/server";
import { pickMessages } from "@/lib/clientMessages";
import { notFound } from "next/navigation";
import { hasLocale } from "next-intl";
import "../globals.css";
import { AuthProvider } from "@/components/AuthProvider";
import { PresenceProvider } from "@/components/PresenceProvider";
import { CounterBeacon } from "@/components/CounterBeacon";
import { ServiceWorkerRegistration } from "@/components/ServiceWorkerRegistration";
import {
  SITE_URL,
  SITE_NAME,
  SITE_DESCRIPTION,
  GITHUB_URL,
  DISCORD_URL,
} from "@/lib/site";
import { localizedUrl, pageAlternates } from "@/lib/seo";
import { routing } from "@/i18n/routing";

const montserrat = Montserrat({
  subsets: ["latin"],
  variable: "--font-montserrat",
});


// M-1-4. Minimal WebSite + Organization graph: name, url, sameAs. No `logo` field - M-2 owns the
// press-kit/logo-asset work, and opengraph-image.tsx:10-11 notes this app has no logo asset to
// embed yet; add `Organization.logo` here once one exists rather than fabricating a placeholder.
// No ratings/review markup either, per the finding's own instruction to keep this minimal.
function jsonLdGraph() {
  return {
    "@context": "https://schema.org",
    "@graph": [
      {
        "@type": "WebSite",
        name: SITE_NAME,
        url: SITE_URL,
        description: SITE_DESCRIPTION,
      },
      {
        "@type": "Organization",
        name: SITE_NAME,
        url: SITE_URL,
        sameAs: [GITHUB_URL, DISCORD_URL].filter(
          (url): url is string => typeof url === "string" && url.length > 0,
        ),
      },
    ],
  };
}

// M-1-2 / M-1-5: locale-aware, so a non-English reader's canonical/hreflang point at their own
// locale's home URL rather than always the English one. `generateMetadata` (not a static `metadata`
// object) is what makes `getLocale()` available here; this stays statically rendered because the
// locale itself is already known at build time via `generateStaticParams` below.
export async function generateMetadata(): Promise<Metadata> {
  const locale = await getLocale();
  return {
    metadataBase: new URL(SITE_URL),
    title: { default: SITE_NAME, template: `%s - ${SITE_NAME}` },
    description: SITE_DESCRIPTION,
    alternates: pageAlternates(locale, "/"),
    openGraph: {
      type: "website",
      siteName: SITE_NAME,
      title: SITE_NAME,
      description: SITE_DESCRIPTION,
      url: localizedUrl(locale, "/"),
    },
    twitter: {
      card: "summary_large_image",
      title: SITE_NAME,
      description: SITE_DESCRIPTION,
    },
    appleWebApp: {
      capable: true,
      statusBarStyle: "black-translucent",
      title: SITE_NAME,
    },
  };
}

export const viewport: Viewport = {
  themeColor: "#282c34",
  viewportFit: "cover",
};

// Phase 9f.4. Prerenders both locales at build time, which is what keeps the ~27 statically
// rendered routes static after the move under `[locale]`. Without this the segment is dynamic and
// every page falls back to per-request rendering, which next.config.ts's CSP comment specifically
// notes this app does not want.
export function generateStaticParams() {
  return routing.locales.map((locale) => ({ locale }));
}

export default async function RootLayout({
  children,
  params,
}: Readonly<{
  children: React.ReactNode;
  params: Promise<{ locale: string }>;
}>) {
  // The `[locale]` segment is an effective catch-all for unmatched top-level paths, so an invalid
  // value reaches here rather than 404ing on its own.
  const { locale } = await params;
  if (!hasLocale(routing.locales, locale)) notFound();

  const messages = pickMessages(
    (await getMessages()) as Record<string, unknown>,
  );

  return (
    // `suppressHydrationWarning` here is for the same reason as the one on <body>, but a
    // different mutator: Chrome's built-in page translation (and the Google Translate extension)
    // rewrites <html> before React hydrates, setting lang to the *target* language and adding
    // class="translated-ltr". React then sees lang="en" class="translated-ltr" in the DOM against
    // the lang="es" it rendered and reports a mismatch on every non-English page. Nothing in this
    // repo emits `translated-ltr`. This only became visible when the extra locales shipped, since
    // Chrome only offers to translate once a page's lang differs from the reader's own.
    <html lang={locale} suppressHydrationWarning>
      <body
        className={`${montserrat.className} ${montserrat.variable}`}
        suppressHydrationWarning
      >
        <NextIntlClientProvider messages={messages}>
          <AuthProvider>
            <PresenceProvider>
              <CounterBeacon />
              {children}
            </PresenceProvider>
          </AuthProvider>
        </NextIntlClientProvider>
        <ServiceWorkerRegistration />
        {/* M-1-4: JSON-LD is structured data, not executable code, so a plain `<script>` is the
            documented approach rather than `next/script` (which is for loading/executing JS). The
            `<` escape follows Next's own JSON-LD guide: JSON.stringify does not sanitize against
            XSS, and SITE_NAME/SITE_URL/DISCORD_URL/GITHUB_URL are all trusted constants anyway, but
            the escape costs nothing and keeps this safe if a field here is ever made dynamic. */}
        <script
          type="application/ld+json"
          dangerouslySetInnerHTML={{
            __html: JSON.stringify(jsonLdGraph()).replace(/</g, "\\u003c"),
          }}
        />
      </body>
    </html>
  );
}
