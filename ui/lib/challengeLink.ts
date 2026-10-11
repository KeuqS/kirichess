// Phase 9d.8. Two pure decisions pulled out of `ui/app/login/page.tsx` so they can be pinned by
// `ui/lib/challengeLink.test.ts`, there is no React Testing Library set up in this repo (see
// Phase 9d.8 step 7), so anything worth asserting on has to be
// extractable as plain logic. The login page imports both rather than duplicating them.

/**
 * The open-redirect guard for `/login?next=`. Only a same-origin RELATIVE path is honoured ,
 * anything else (an absolute URL, a protocol-relative "//evil.example", a scheme like
 * "javascript:...", or a path that does not start with a single "/") falls back to "/". A login
 * form that will redirect anywhere the query string says is an open redirect, and an open redirect
 * on the page where people type their password is the worst place to have one.
 *
 * These are deliberately the same five rules as the server's own
 * `ExternalAuthEndpoints.SafeReturnPath`, in the same order, because the Google sign-in button on
 * that page hands this value to that function, two guards with two different definitions of "safe"
 * is how the weaker one becomes the one that matters.
 *
 * The backslash and control-character rules are not paranoia. The WHATWG URL parser treats a
 * backslash exactly like a slash immediately after a leading "/", so "/\evil.example" resolves to
 * the origin "evil.example"; and browsers strip tab/CR/LF from a URL *before* parsing it, so
 * "/\t/evil.example" becomes "//evil.example". A guard that only looks for a literal "//" misses
 * both, which is why the strip happens before the test rather than after.
 */
export function safeNextPath(raw: string | null): string {
  if (!raw) return "/";
  const path = raw.replace(/[\t\n\r]/g, "");
  if (!path.startsWith("/")) return "/"; // absolute or scheme-relative
  if (path.startsWith("//")) return "/"; // protocol-relative -> another origin
  if (path.includes(":")) return "/"; // scheme smuggling
  if (path.includes("\\")) return "/"; // backslash normalisation tricks
  return path;
}

/**
 * Login bounce that keeps the visitor's intent. `safeNextPath` is the open-redirect
 * guard; encoding is what puts a path like `/watch` into `?next=` without turning a
 * query string into a second parameter.
 */
export function loginNextHref(path: string): string {
  return `/login?next=${encodeURIComponent(safeNextPath(path))}`;
}

/**
 * Appends `accept=1` to a challenge-landing destination so `/challenge/[token]`'s auto-accept
 * (README "The wire contract"; TASK-B-FRONTEND.md step 5) fires the moment the newly authenticated
 * user lands back on the link they arrived from. Every other destination passes through unchanged.
 */
export function challengeDestination(next: string): string {
  return next.startsWith("/challenge/")
    ? `${next}${next.includes("?") ? "&" : "?"}accept=1`
    : next;
}
