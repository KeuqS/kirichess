"use client";

import { Suspense, useEffect, useRef, useState } from "react";
import { useSearchParams } from "next/navigation";
import { Link, useRouter } from "@/i18n/navigation";
import { useTranslations } from "next-intl";
import { useAuth } from "@/components/AuthProvider";
import { AuthShell } from "@/components/AuthShell";
import { SkeletonStatus } from "@/components/SkeletonRows";
import { TwoFactorPrompt } from "@/components/TwoFactorPrompt";
import {
  ApiError,
  exchangeExternalCode,
  isTwoFactorChallenge,
  recordCounter,
} from "@/lib/api";
import { COUNTER_KEYS } from "@/lib/analytics";
import { safeNextPath } from "@/lib/challengeLink";
import type { TwoFactorChallengeResponse } from "@/lib/types";

const ERROR_MESSAGE_KEYS: Record<string, string> = {
  cancelled: "errorCancelled",
  unverified_email: "errorUnverifiedEmail",
  link_refused: "errorLinkRefused",
  unavailable: "errorUnavailable",
};

function messageForSlug(
  slug: string | null,
  t: ReturnType<typeof useTranslations>,
): string {
  if (!slug) return t("errorDefault");
  const key = ERROR_MESSAGE_KEYS[slug];
  return key ? t(key) : t("errorDefault");
}

// C-1-9: this page used to carry its own `safeReturnPath`, a hand-rolled duplicate of
// `safeNextPath` (`@/lib/challengeLink`) that mirrors `ExternalAuthEndpoints.SafeReturnPath` on
// the server. The duplicate was weaker and untested: it did not strip tab/CR/LF before checking,
// so a candidate like "/\t/evil.example" (a real tab character, not the two-character escape)
// passed every one of its checks unchanged and only became "//evil.example", an open redirect,
// once the browser itself stripped the control character on navigation. `safeNextPath` strips
// those characters first and is pinned by `ui/lib/challengeLink.test.ts`; this page now uses it
// instead of keeping a second, divergent definition of "safe" for the one page in the app where
// that value drives an automatic redirect straight out of an OAuth callback.

function AuthCallback() {
  const t = useTranslations("authCallback");
  const router = useRouter();
  const params = useSearchParams();
  const { completeExternalLogin, token, hydrated } = useAuth();

  // The exchange code is single-use: React StrictMode double-invokes effects in dev, and a naive
  // effect would fire the exchange twice, turning a perfectly good sign-in into a 400. This latch
  // is set synchronously before the await so the second invocation is a no-op.
  const started = useRef(false);
  const [exchangeError, setExchangeError] = useState<string | null>(null);
  // Phase 9d.2. A linked Google account can still have 2FA on: a second factor must not be
  // bypassable through OAuth (README § 6.1). When set, the code form below replaces the
  // "Signing you in…" state instead of the exchange being treated as done or as an error.
  const [challenge, setChallenge] = useState<TwoFactorChallengeResponse | null>(
    null,
  );

  const code = params.get("code");
  const errorSlug = params.get("error");
  const returnPath = params.get("returnPath");

  const safePath = safeNextPath(returnPath);

  useEffect(() => {
    if (!hydrated) return;
    if (started.current) return;
    started.current = true;

    if (!code) return;

    const isReauth = safePath.includes("reauth=2fa");
    if (isReauth && token) {
      // S-1-2: this code is the Google reauth proof for /2fa/enable, not a sign-in. Exchanging
      // it here would consume it before enable can.
      sessionStorage.setItem("kirichess.googleReauthCode", code);
      router.replace("/profile");
      return;
    }

    (async () => {
      try {
        const res = await exchangeExternalCode(code);
        if (isTwoFactorChallenge(res)) {
          setChallenge(res);
          return;
        }
        // M-4-1. Password registration already fires this on the register form. A Google account
        // created by this handshake is the other signup, and a returning sign-in is not one.
        // `?src=` does not survive the round trip to Google, so the source is almost always direct.
        if (res.created) recordCounter(COUNTER_KEYS.signup);
        completeExternalLogin(res);
        router.replace(safePath);
      } catch (err) {
        setExchangeError(
          err instanceof ApiError ? err.message : t("errorDefault"),
        );
      }
    })();
  }, [code, safePath, completeExternalLogin, router, t, hydrated, token]);

  const isLoading = Boolean(code) && !exchangeError && !challenge;
  const message = exchangeError ?? messageForSlug(errorSlug, t);

  return (
    <AuthShell brand={t("brand")} heading={t("pageTitle")} center>
      {isLoading ? (
        <SkeletonStatus label={t("signingIn")} count={2} />
      ) : challenge ? (
        <div className="text-left">
          <TwoFactorPrompt
            ticket={challenge.twoFactorTicket}
            onSuccess={(res) => {
              completeExternalLogin(res);
              router.replace(safePath);
            }}
            onLockout={(msg) => {
              setChallenge(null);
              setExchangeError(msg);
            }}
          />
        </div>
      ) : (
        <div className="flex flex-col gap-5">
          <p className="error-banner">{message}</p>
          <Link href="/login" className="btn-primary w-full py-2.5 text-sm">
            {t("backToSignIn")}
          </Link>
        </div>
      )}
    </AuthShell>
  );
}

function AuthCallbackFallback() {
  const t = useTranslations("authCallback");
  return (
    <AuthShell brand={t("brand")} heading={t("pageTitle")} center>
      <SkeletonStatus label={t("signingIn")} count={2} />
    </AuthShell>
  );
}

export default function AuthCallbackPage() {
  return (
    <Suspense fallback={<AuthCallbackFallback />}>
      <AuthCallback />
    </Suspense>
  );
}
