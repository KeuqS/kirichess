"use client";

import Script from "next/script";
import { markTurnstileScriptFailed } from "@/lib/turnstile";

/**
 * P-4-5. The widget renders only on the login page's register tab, so this script is mounted
 * there and nowhere else (L-1-6). `onError` has to sit in a client component; the login page is
 * one. A blocked `challenges.cloudflare.com` then sets the same failed flag the login page's
 * render poll reads.
 */
export function TurnstileScript() {
  return (
    <Script
      src="https://challenges.cloudflare.com/turnstile/v0/api.js"
      strategy="afterInteractive"
      async
      defer
      onError={markTurnstileScriptFailed}
    />
  );
}
