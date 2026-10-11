"use client";

import { Suspense, useEffect, useRef, useState, type FormEvent } from "react";
import { useSearchParams } from "next/navigation";
import { Link, useRouter } from "@/i18n/navigation";
import { useTranslations } from "next-intl";
import { IconBrandGoogle, IconEye, IconEyeOff } from "@tabler/icons-react";
import { useAuth } from "@/components/AuthProvider";
import { AuthShell } from "@/components/AuthShell";
import { TabChip } from "@/components/TabChip";
import { TurnstileScript } from "@/components/TurnstileScript";
import { TwoFactorPrompt } from "@/components/TwoFactorPrompt";
import { ApiError, googleSignInUrl, recordCounter } from "@/lib/api";
import { COUNTER_KEYS } from "@/lib/analytics";
import { useDisplayNameCheck } from "@/lib/useDisplayNameCheck";
import {
  TURNSTILE_SCRIPT_FAILED_EVENT,
  TURNSTILE_SITE_KEY,
  didTurnstileScriptFail,
  waitForTurnstileApi,
} from "@/lib/turnstile";
import { challengeDestination, safeNextPath } from "@/lib/challengeLink";
import type { TwoFactorChallengeResponse } from "@/lib/types";

type Tab = "login" | "register";

// Second switch, independent of the server's: the backend fails closed (no Google client id
// configured ⇒ /api/auth/external/google/start 404s), and a button that navigates to a 404 is
// worse than no button. This flag can drift from the server's own gate: the server remains the
// authority. A shared config endpoint that lets the frontend ask the server directly is the
// eventual fix; out of scope here.
const googleEnabled = process.env.NEXT_PUBLIC_GOOGLE_SIGN_IN === "true";

function LoginPageInner() {
  const t = useTranslations("login");
  const router = useRouter();
  const searchParams = useSearchParams();
  const { login, register, adoptSession } = useAuth();
  const [tab, setTab] = useState<Tab>("login");

  // Phase 9d.8. Where to land after signing in. A challenge link handed to somebody without an
  // account is the most important arrival this product has, and before this the intent was simply
  // dropped (router.push("/") unconditionally).
  //
  // Only same-origin RELATIVE paths are honoured. Anything else - an absolute URL, a protocol-relative
  // "//evil.example", a path that does not start with a single "/" - falls back to "/". A login form
  // that will redirect anywhere the query string says is an open redirect, and an open redirect on the
  // page where people type their password is the worst place to have one. See ui/lib/challengeLink.ts.
  const next = safeNextPath(searchParams.get("next"));
  // If `next` points back at a challenge landing page, append `accept=1` so that page's auto-accept
  // fires the instant this newly authenticated session lands on it. Used by every post-auth
  // navigation on this page, the Google button included, a friend who signs up with Google is
  // arriving from exactly the same link as one who types a password, and dropping the flag for
  // half of them would leave them staring at an Accept button they never asked to press.
  // ExternalAuthEndpoints.SafeReturnPath permits the query string (it rejects ':' and '\\', not
  // '?'), so this survives the round trip through the provider.
  const destination = challengeDestination(next);

  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [showPassword, setShowPassword] = useState(false);
  const [displayName, setDisplayName] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);
  // Phase 8a.2. Covers both the age declaration and terms/privacy acceptance in one tick. Reset on
  // every tab switch (below): a box left ticked across a switch would record consent the user did
  // not knowingly give on this attempt.
  const [consented, setConsented] = useState(false);
  // Phase 9d.2. When set, the tab bar and the email/password form are replaced by the code form:
  // the sign-in is not finished until the second factor is presented (README § 1.1).
  const [challenge, setChallenge] = useState<TwoFactorChallengeResponse | null>(
    null,
  );
  // DH.3. Set by the widget's callback; cleared on tab switch (below) so a stale token from a
  // previous register attempt can never be reused across it.
  const [turnstileToken, setTurnstileToken] = useState<string | null>(null);
  const [turnstileBlocked, setTurnstileBlocked] = useState(false);
  const turnstileContainerRef = useRef<HTMLDivElement>(null);
  const turnstileWidgetId = useRef<string | null>(null);

  // Explicit rendering, not the class="cf-turnstile" implicit form: that variant only auto-renders
  // elements present in the DOM when the script executes, and this container is mounted later, only
  // when the register tab is selected. `next/script`'s afterInteractive load means window.turnstile
  // may not exist yet on the first run, hence the poll. P-4-5 bounds that poll (~10s) and treats a
  // blocked api.js the same as a timeout. P-4-6 clears a stale token on expire/error so submit
  // cannot send a dead challenge. Cancelled/removed on tab switch or unmount so a stale widget is
  // never left rendered into a container that no longer matches the tab.
  useEffect(() => {
    if (tab !== "register" || !TURNSTILE_SITE_KEY) return;

    let cancelled = false;
    setTurnstileBlocked(false);

    const onScriptFailed = () => {
      if (!cancelled) setTurnstileBlocked(true);
    };
    window.addEventListener(TURNSTILE_SCRIPT_FAILED_EVENT, onScriptFailed);

    void (async () => {
      const result = await waitForTurnstileApi({
        isReady: () =>
          Boolean(window.turnstile && turnstileContainerRef.current),
        isCancelled: () => cancelled,
        hasScriptFailed: didTurnstileScriptFail,
      });
      if (cancelled) return;
      if (
        result !== "ready" ||
        !window.turnstile ||
        !turnstileContainerRef.current
      ) {
        if (result === "failed") setTurnstileBlocked(true);
        return;
      }
      turnstileWidgetId.current = window.turnstile.render(
        turnstileContainerRef.current,
        {
          sitekey: TURNSTILE_SITE_KEY,
          action: "turnstile-spin-v1",
          callback: setTurnstileToken,
          "expired-callback": () => setTurnstileToken(null),
          "error-callback": () => setTurnstileToken(null),
        },
      );
    })();

    return () => {
      cancelled = true;
      window.removeEventListener(TURNSTILE_SCRIPT_FAILED_EVENT, onScriptFailed);
      if (turnstileWidgetId.current && window.turnstile) {
        window.turnstile.remove(turnstileWidgetId.current);
        turnstileWidgetId.current = null;
      }
    };
  }, [tab]);

  // Advisory live feedback only: see step 4 of TASK-D. Passing "" while not on the register tab
  // keeps this hook inert (below its minimum length) without conditionally calling it.
  const { verdict: nameVerdict } = useDisplayNameCheck(
    tab === "register" ? displayName : "",
  );

  // AD.6. Fired here rather than from CounterBeacon because registration is a tab on this page, not a
  // route. The ref makes it once per mount: toggling back and forth between the two tabs is one person
  // looking at one form.
  const registerViewSent = useRef(false);
  useEffect(() => {
    if (tab === "register" && !registerViewSent.current) {
      registerViewSent.current = true;
      recordCounter(COUNTER_KEYS.register);
    }
  }, [tab]);

  async function handleSubmit(e: FormEvent) {
    e.preventDefault();
    setError(null);

    if (tab === "register" && password.length < 8) {
      setError(t("passwordTooShort"));
      return;
    }

    setSubmitting(true);
    try {
      if (tab === "login") {
        const result = await login(email, password);
        if (result.twoFactor) {
          setChallenge(result.twoFactor);
          return; // no navigation: the sign-in is not finished
        }
      } else {
        // DH.3: require a widget token, then let the API redeem it. Do not siteverify in the
        // browser first, a Turnstile token is single-use, and the old Worker pre-check consumed
        // it so `/api/auth/register` always failed with "Verification failed." Skipped entirely
        // when TURNSTILE_SITE_KEY is unset, same as the widget itself not rendering.
        if (TURNSTILE_SITE_KEY && !turnstileToken) {
          setError(t("turnstileRequired"));
          return;
        }
        // One checkbox covers both server-side flags: see the comment on `register` in api.ts.
        // `register` cannot produce a challenge: a brand-new account has no authenticator.
        await register(
          email,
          password,
          displayName,
          consented,
          consented,
          turnstileToken,
        );
        // AD.6. The only way to attribute a signup to a campaign source without putting a
        // SignupSource column on the account row, which would be new personal data. AspNetUsers
        // .CreatedAt stays the authoritative signup count and the two can legitimately disagree -
        // see AD.6 / 11a.3 scope decision 15. keepalive is what gets this out
        // before the navigation below.
        recordCounter(COUNTER_KEYS.signup);
      }
      router.push(destination);
    } catch (err) {
      setError(err instanceof ApiError ? err.message : t("genericError"));
      // The API (or a previous attempt) may have already redeemed this token.
      if (tab === "register" && turnstileWidgetId.current && window.turnstile) {
        window.turnstile.reset(turnstileWidgetId.current);
        setTurnstileToken(null);
      }
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <AuthShell
      brand={t("brand")}
      heading={tab === "register" ? t("register") : t("logIn")}
      srOnlyHeading
    >
      {challenge ? (
        <TwoFactorPrompt
          ticket={challenge.twoFactorTicket}
          onSuccess={(res) => {
            adoptSession(res);
            router.push(destination);
          }}
          onLockout={(message) => {
            // Nothing useful to do on the code form for the next fifteen minutes: drop back to
            // the password step and show the server's own lockout text there.
            setError(message);
            setChallenge(null);
          }}
          onBack={() => {
            setChallenge(null);
            setError(null);
          }}
        />
      ) : (
        <>
          <div className="mb-6 flex gap-3">
            <TabChip
              active={tab === "login"}
              onClick={() => {
                setTab("login");
                setError(null);
                setConsented(false);
                setChallenge(null);
                setTurnstileToken(null);
              }}
              className="flex-1"
            >
              {t("logIn")}
            </TabChip>
            <TabChip
              active={tab === "register"}
              onClick={() => {
                setTab("register");
                setError(null);
                setConsented(false);
                setChallenge(null);
                setTurnstileToken(null);
              }}
              className="flex-1"
            >
              {t("register")}
            </TabChip>
          </div>

          <form onSubmit={handleSubmit} className="flex flex-col gap-5">
            {tab === "register" && (
              <label className="flex flex-col gap-1.5 text-sm font-medium text-[var(--foreground)]">
                {t("displayNameLabel")}
                <input
                  className="w-full border border-[var(--border)] bg-[var(--background)] px-4 py-2.5 text-white focus:border-[var(--bounty)] focus:outline-none"
                  value={displayName}
                  onChange={(e) => setDisplayName(e.target.value)}
                  required
                />
                {/*
                Advisory only (README route table): the server is the authority and submit is
                never blocked on this. A network hiccup here must not stop someone registering.
              */}
                {nameVerdict && (
                  <span
                    className={`text-sm mt-0.5 ${
                      nameVerdict.allowed
                        ? "text-[var(--gain)]"
                        : "text-[var(--loss)]"
                    }`}
                  >
                    {nameVerdict.allowed
                      ? t("nameAvailable")
                      : nameVerdict.message}
                  </span>
                )}
              </label>
            )}
            <label className="flex flex-col gap-1.5 text-sm font-medium text-[var(--foreground)]">
              {t("emailLabel")}
              <input
                type="email"
                className="w-full border border-[var(--border)] bg-[var(--background)] px-4 py-2.5 text-white focus:border-[var(--bounty)] focus:outline-none"
                value={email}
                onChange={(e) => setEmail(e.target.value)}
                required
              />
            </label>
            <label className="flex flex-col gap-1.5 text-sm font-medium text-[var(--foreground)]">
              {t("passwordLabel")}
              <div className="relative flex">
                <input
                  type={showPassword ? "text" : "password"}
                  className="w-full border border-[var(--border)] bg-[var(--background)] px-4 py-2.5 pr-10 text-white focus:border-[var(--bounty)] focus:outline-none"
                  value={password}
                  onChange={(e) => setPassword(e.target.value)}
                  required
                  minLength={tab === "register" ? 8 : undefined}
                />
                <button
                  type="button"
                  onClick={() => setShowPassword((prev) => !prev)}
                  className="absolute right-0 top-0 flex h-full items-center px-3 text-muted hover:text-[var(--bounty)] focus:outline-none transition-colors"
                  tabIndex={-1}
                  aria-label={
                    showPassword ? t("hidePassword") : t("showPassword")
                  }
                >
                  {showPassword ? (
                    <IconEyeOff size={16} stroke={2} />
                  ) : (
                    <IconEye size={16} stroke={2} />
                  )}
                </button>
              </div>
              {tab === "register" && (
                <span className="text-sm text-muted mt-0.5">
                  {t("passwordRequirements")}
                </span>
              )}
            </label>

            {tab === "login" && (
              <Link
                href="/forgot-password"
                className="text-sm text-muted hover:text-[var(--bounty)] self-end -mt-1"
              >
                {t("forgotPassword")}
              </Link>
            )}

            {tab === "register" && (
              <div className="flex items-start gap-2">
                <input
                  id="consent-checkbox"
                  type="checkbox"
                  checked={consented}
                  onChange={(e) => setConsented(e.target.checked)}
                  className="checkbox-bounty mt-0.5"
                />
                <label
                  htmlFor="consent-checkbox"
                  className="text-sm text-[var(--foreground)]/80"
                >
                  {t.rich("ageConsent", {
                    terms: (chunks) => (
                      <Link
                        href="/terms"
                        className="text-[var(--bounty)] underline"
                      >
                        {chunks}
                      </Link>
                    ),
                    privacy: (chunks) => (
                      <Link
                        href="/privacy"
                        className="text-[var(--bounty)] underline"
                      >
                        {chunks}
                      </Link>
                    ),
                  })}
                </label>
              </div>
            )}

            {tab === "register" && TURNSTILE_SITE_KEY && (
              <div ref={turnstileContainerRef} />
            )}

            {turnstileBlocked && (
              <p className="error-banner">{t("turnstileUnavailable")}</p>
            )}
            {error && <p className="error-banner">{error}</p>}

            <div className="mt-4 flex flex-col gap-3">
              <button
                type="submit"
                disabled={
                  submitting ||
                  (tab === "register" &&
                    (!consented || (!!TURNSTILE_SITE_KEY && !turnstileToken)))
                }
                className="btn-primary w-full py-2.5 text-sm"
              >
                {submitting
                  ? t("pleaseWait")
                  : tab === "login"
                    ? t("logIn")
                    : t("createAccount")}
              </button>
              {googleEnabled && (
                <>
                  <div className="flex items-center gap-3">
                    <div className="h-px flex-1 bg-[var(--border)]" />
                    <span className="text-sm text-muted">{t("or")}</span>
                    <div className="h-px flex-1 bg-[var(--border)]" />
                  </div>
                  <a
                    href={googleSignInUrl(destination)}
                    className="btn-secondary w-full py-2.5 text-sm flex items-center justify-center gap-2"
                  >
                    <IconBrandGoogle size={18} stroke={2} />
                    {t("continueWithGoogle")}
                  </a>
                  {/*
                  Phase 8a.2. This is the whole basis on which ExternalLoginService stamps consent
                  for a Google sign-in, weaker than the register tab's tick, but it must actually
                  be on the page or the stamp records something the user was never shown.
                */}
                  <p className="text-sm text-muted">
                    {t.rich("googleConsent", {
                      terms: (chunks) => (
                        <Link
                          href="/terms"
                          className="text-[var(--bounty)] underline"
                        >
                          {chunks}
                        </Link>
                      ),
                      privacy: (chunks) => (
                        <Link
                          href="/privacy"
                          className="text-[var(--bounty)] underline"
                        >
                          {chunks}
                        </Link>
                      ),
                    })}
                  </p>
                </>
              )}
              <Link href="/" className="btn-secondary w-full py-2.5 text-sm">
                {t("backToKiriChess")}
              </Link>
            </div>
          </form>
        </>
      )}
    </AuthShell>
  );
}

// `useSearchParams` (for `?next=`, Phase 9d.8) requires a Suspense boundary for static rendering in
// the App Router, see ui/app/auth/callback/page.tsx for the same shape.
export default function LoginPage() {
  return (
    <>
      <TurnstileScript />
      <Suspense fallback={null}>
        <LoginPageInner />
      </Suspense>
    </>
  );
}
