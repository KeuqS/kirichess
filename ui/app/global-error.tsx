"use client";

import { useEffect } from "react";
import * as Sentry from "@sentry/nextjs";
import { IconAlertTriangle } from "@tabler/icons-react";

/**
 * Last-resort boundary: this replaces the root layout, so it renders its own <html>/<body> and
 * cannot use any provider or shared chrome. Styles are inlined as raw hex (the ONE place in this
 * codebase that is allowed) rather than the `var(--token)` vocabulary used everywhere else,
 * because a failure this deep may well be `globals.css` itself failing to load: do not "fix"
 * this into a token and reintroduce an unstyled white page.
 *
 * Layout and copy mirror `app/[locale]/error.tsx` (card, typography, CTAs) without NavBar/Footer.
 * Hex values are frozen copies of globals.css at the time of writing.
 */
const COLORS = {
  background: "#282c34",
  foreground: "#abb2bf",
  surface: "#21252b",
  border: "#3e4451",
  muted: "#9197a3",
  loss: "#e8838a",
  bounty: "#e5c07b",
  white: "#ffffff",
} as const;

export default function GlobalError({
  error,
  reset,
}: {
  error: Error & { digest?: string };
  reset: () => void;
}) {
  useEffect(() => {
    Sentry.captureException(error);
  }, [error]);

  return (
    <html lang="en">
      <head>
        <link
          href="https://fonts.googleapis.com/css2?family=Montserrat:wght@400;600;700&display=swap"
          rel="stylesheet"
        />
        <style>{`
          *, *::before, *::after { box-sizing: border-box; }
          a { text-decoration: none; }
          body {
            min-height: 100vh;
            min-height: 100dvh;
          }
          .btn-primary,
          .btn-secondary {
            display: inline-flex;
            align-items: center;
            justify-content: center;
            cursor: pointer;
            border-style: solid;
            border-width: 1px;
            padding: 0.5rem 1.25rem;
            font-size: 0.875rem;
            font-family: inherit;
            transition:
              color 0.15s ease,
              background-color 0.15s ease,
              border-color 0.15s ease,
              box-shadow 0.15s ease;
          }
          .btn-primary {
            font-weight: 700;
            background-color: ${COLORS.bounty};
            color: ${COLORS.background};
            border-color: ${COLORS.bounty};
          }
          .btn-primary:hover {
            background-color: ${COLORS.bounty};
            color: ${COLORS.background};
            box-shadow: 0 0 0 1px ${COLORS.bounty};
          }
          .btn-secondary {
            font-weight: 600;
            background-color: transparent;
            color: ${COLORS.foreground};
            border-color: ${COLORS.border};
          }
          .btn-secondary:hover {
            border-color: ${COLORS.bounty};
            color: ${COLORS.bounty};
          }
        `}</style>
      </head>
      <body
        style={{
          margin: 0,
          display: "flex",
          alignItems: "center",
          justifyContent: "center",
          padding: "6rem 2.5rem",
          background: COLORS.background,
          color: COLORS.foreground,
          fontFamily: "Montserrat, Arial, Helvetica, sans-serif",
        }}
      >
        <div
          style={{
            width: "100%",
            maxWidth: "28rem",
            borderRadius: "1rem",
            border: `1px solid ${COLORS.border}`,
            background: COLORS.surface,
            padding: "1.25rem",
          }}
        >
          <div
            style={{
              display: "flex",
              flexDirection: "column",
              alignItems: "center",
              textAlign: "center",
              gap: "0.75rem",
            }}
          >
            <IconAlertTriangle size={32} color={COLORS.loss} />
            <div
              style={{
                fontWeight: 700,
                fontSize: "1rem",
                color: COLORS.white,
              }}
            >
              Something went wrong
            </div>
            <p
              style={{
                fontSize: "0.875rem",
                color: COLORS.muted,
                maxWidth: 400,
                margin: 0,
                lineHeight: 1.625,
              }}
            >
              This page hit an unexpected error. Trying again usually fixes it.
            </p>
            {error.digest && (
              <p
                style={{
                  fontSize: "0.875rem",
                  color: COLORS.muted,
                  margin: 0,
                }}
              >
                Error id: {error.digest}
              </p>
            )}
            <div
              style={{
                display: "flex",
                alignItems: "center",
                gap: "0.75rem",
                marginTop: "0.5rem",
              }}
            >
              <button type="button" onClick={reset} className="btn-primary">
                Try again
              </button>
              {/*
                Deliberately a plain <a>, not next/link. global-error.tsx replaces the root
                layout when the app shell itself has failed, so the router context Link needs
                may not exist. A full document navigation is also the point here: it discards
                the broken client state rather than soft-navigating within it.
              */}
              {/* eslint-disable-next-line @next/next/no-html-link-for-pages */}
              <a href="/" className="btn-secondary">
                Back to home
              </a>
            </div>
          </div>
        </div>
      </body>
    </html>
  );
}
