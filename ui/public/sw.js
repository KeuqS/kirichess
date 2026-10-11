// 9f.3's offline shell. Deliberately hand-written instead of a generated (next-pwa/Workbox) worker:
// this app has exactly one route that is honestly offline-capable (see below), so a build-time
// precache manifest of every hashed chunk buys nothing a small runtime cache doesn't already cover,
// and it would be a new dependency on a Next major version (16) nothing here has vetted yet.
//
// The constraint that shapes all of this is the roadmap item's own: the clock is server-authoritative
// (Phase 3), so a cached /game, /watch or /replay page that *looks* playable with no socket is worse
// than an honest offline notice. Only /learn is fully client-side with no fetch and no SignalR
// dependency (confirmed by reading ui/app/learn/page.tsx before writing this), so it is the only route
// this worker ever serves from cache. Everything else is network-only, falling back to /offline.

const SHELL_CACHE = "kirichess-shell-v1";
const STATIC_CACHE = "kirichess-static-v1";
const CACHES = [SHELL_CACHE, STATIC_CACHE];

// Phase 9f.4. Mirrors `ui/i18n/routing.ts`, and has to: this file is served straight out of
// public/ with no build step, so it cannot import the routing config. `lib/locales.test.ts` fails
// if the two lists drift apart, which is the only thing keeping this honest.
//
// The default locale is unprefixed under `localePrefix: "as-needed"`, so its entry is the empty
// string rather than "/en" - that asymmetry is the whole reason this needs a helper rather than a
// startsWith check.
const LOCALE_PREFIXES = ["", "/nl", "/es", "/fr", "/de", "/pt-BR"];

// One offline shell per locale. A Dutch reader who loses connectivity gets the Dutch notice, and
// each shell is one small document.
const PRECACHE_URLS = [
  ...LOCALE_PREFIXES.map((prefix) => `${prefix}/offline`),
  "/manifest.webmanifest",
  "/icons/icon-192.png",
  "/icons/icon-maskable-512.png",
  "/logo.png",
];

// Splits "/nl/learn" into its prefix and the unprefixed route, so the two path checks below stay
// written against the routes this worker actually reasons about ("/learn", "/offline") rather than
// growing a branch per locale.
function splitLocale(pathname) {
  for (const prefix of LOCALE_PREFIXES) {
    if (prefix === "") continue;
    if (pathname === prefix || pathname.startsWith(`${prefix}/`)) {
      return { prefix, route: pathname.slice(prefix.length) || "/" };
    }
  }
  return { prefix: "", route: pathname };
}

self.addEventListener("install", (event) => {
  event.waitUntil(
    caches.open(SHELL_CACHE).then((cache) => cache.addAll(PRECACHE_URLS)),
  );
  self.skipWaiting();
});

self.addEventListener("activate", (event) => {
  event.waitUntil(
    caches
      .keys()
      .then((keys) =>
        Promise.all(
          keys
            .filter((key) => !CACHES.includes(key))
            .map((key) => caches.delete(key)),
        ),
      )
      .then(() => self.clients.claim()),
  );
});

function isImmutableAsset(pathname) {
  return (
    pathname.startsWith("/_next/static/") ||
    pathname.startsWith("/icons/") ||
    pathname === "/logo.svg" ||
    pathname === "/logo.png" ||
    pathname === "/logo-transparent.png" ||
    pathname === "/manifest.webmanifest" ||
    pathname === "/stockfish-19-lite-single.js" ||
    pathname === "/stockfish-19-lite-single.wasm"
  );
}

self.addEventListener("fetch", (event) => {
  const { request } = event;
  if (request.method !== "GET") return;

  const url = new URL(request.url);
  if (url.origin !== self.location.origin) return;

  // Hashed build output and brand assets are content-addressed: safe to serve cache-first and top
  // up in the background, which is also how /learn's JS/CSS end up cached the first time it's
  // visited online, without this worker needing to know Next's chunk hashes ahead of time.
  if (isImmutableAsset(url.pathname)) {
    event.respondWith(
      caches.open(STATIC_CACHE).then(async (cache) => {
        const cached = await cache.match(request);
        const network = fetch(request)
          .then((response) => {
            if (response.ok) cache.put(request, response.clone());
            return response;
          })
          .catch(() => cached);
        return cached || network;
      }),
    );
    return;
  }

  if (request.mode === "navigate") {
    const { prefix, route } = splitLocale(url.pathname);
    event.respondWith(
      fetch(request)
        .then((response) => {
          // Cache /learn's document itself so the page (not just its assets) survives offline.
          // Keyed on `request`, so /learn and /nl/learn are separate cache entries and a Dutch
          // reader is never served the English tutorial back.
          if (response.ok && route === "/learn") {
            caches
              .open(STATIC_CACHE)
              .then((cache) => cache.put(request, response.clone()));
          }
          return response;
        })
        .catch(async () => {
          if (route === "/learn") {
            const learnCache = await caches.open(STATIC_CACHE);
            const cachedLearn = await learnCache.match(request);
            if (cachedLearn) return cachedLearn;
          }
          const shellCache = await caches.open(SHELL_CACHE);
          // Fall back to the reader's own locale, then to the default one: an unknown prefix
          // should still produce the offline notice rather than a browser error page.
          const offline =
            (await shellCache.match(`${prefix}/offline`)) ||
            (await shellCache.match("/offline"));
          return offline || Response.error();
        }),
    );
    return;
  }

  // API calls, RSC data requests, everything else: network only. In particular this never touches
  // /game, /watch or /replay, and never intercepts the SignalR websocket (fetch events don't fire
  // for WebSocket upgrades at all, so there is nothing to special-case there).
});

// 11b.1 follow-up: web push. The payload is PushNotificationService's own tiny shape - a title, a
// body, and where to go on click - kept intentionally minimal rather than mirroring the richer mail
// templates, since a system notification has no room for more than that anyway.
self.addEventListener("push", (event) => {
  const data = event.data ? event.data.json() : {};
  const title = data.title || "KiriChess";

  event.waitUntil(
    self.registration.showNotification(title, {
      body: data.body || "",
      icon: "/icons/icon-192.png",
      badge: "/icons/icon-192.png",
      data: { url: data.url || "/" },
    }),
  );
});

// Focuses an already-open tab on that URL rather than always opening a new one - the same instinct
// behind GameHub's SignalR broadcast this duplicates for someone who has navigated away.
self.addEventListener("notificationclick", (event) => {
  event.notification.close();
  const url =
    event.notification.data && event.notification.data.url
      ? event.notification.data.url
      : "/";

  event.waitUntil(
    self.clients
      .matchAll({ type: "window", includeUncontrolled: true })
      .then((windowClients) => {
        for (const client of windowClients) {
          if (client.url === url && "focus" in client) return client.focus();
        }
        if (self.clients.openWindow) return self.clients.openWindow(url);
      }),
  );
});
