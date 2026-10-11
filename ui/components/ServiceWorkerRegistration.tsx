"use client";

import { useEffect } from "react";

/**
 * Registers `public/sw.js` in production only. Dev is excluded on purpose: Next's dev server
 * rewrites `/_next/static/*` on every save, and a caching service worker fighting that produces
 * exactly the "why isn't my change showing up" confusion PWA setups are notorious for.
 */
export function ServiceWorkerRegistration() {
  useEffect(() => {
    if (typeof navigator === "undefined" || !("serviceWorker" in navigator))
      return;

    if (process.env.NODE_ENV !== "production") {
      // A SW registered during a previous `next start` or production visit survives until
      // unregistered. Its cache-first `/_next/static/` path is exactly what hides Fast Refresh.
      void navigator.serviceWorker
        .getRegistrations()
        .then((registrations) =>
          Promise.all(registrations.map((registration) => registration.unregister())),
        );
      return;
    }

    navigator.serviceWorker.register("/sw.js").catch(() => {
      // Installability degrades gracefully to a normal page load; nothing to surface to the player.
    });
  }, []);

  return null;
}
