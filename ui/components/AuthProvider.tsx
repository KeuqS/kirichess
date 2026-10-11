"use client";

import {
  createContext,
  useContext,
  useEffect,
  useState,
  type ReactNode,
} from "react";
import { useRouter } from "@/i18n/navigation";
import {
  isTwoFactorChallenge,
  login as apiLogin,
  logout as apiLogout,
  logoutAll as apiLogoutAll,
  register as apiRegister,
  getPublicProfile,
  setAuthToken,
  setRefreshToken,
  setOnTokenRefreshed,
  setOnRefreshFailed,
} from "@/lib/api";
import type {
  AuthResponse,
  AvatarResponse,
  TwoFactorChallengeResponse,
} from "@/lib/types";

const STORAGE_KEY = "cs_auth";

interface StoredAuth {
  userId: string;
  email: string;
  displayName: string;
  token: string;
  refreshToken: string;
  expiresAt: string;
  roles: string[];
  /** Undefined until first fetch; null means initials. */
  avatarUrl?: AvatarResponse["avatarUrl"];
}

interface AuthContextValue {
  userId: string | null;
  email: string | null;
  displayName: string | null;
  token: string | null;
  roles: string[];
  /** Null is initials. Undefined means not loaded yet. */
  avatarUrl: AvatarResponse["avatarUrl"] | undefined;
  /**
   * UI affordance only: NOT a security boundary. It hides the moderation nav link so the surface
   * isn't advertised to non-admins; every moderation route is still enforced server-side by
   * `ModerationPolicies.Admin`. A user who edits their own `localStorage` to add "Admin" gets a
   * dossier full of 403s, which is the correct (and only) outcome.
   */
  isAdmin: boolean;
  hydrated: boolean;
  /**
   * Phase 9d.2. Resolves to a challenge instead of a session when the account has 2FA enabled: the
   * refresh token is not minted until the second factor is presented, so there is deliberately
   * nothing to persist yet. The caller drives the second step.
   */
  login: (
    email: string,
    password: string,
  ) => Promise<{ twoFactor: TwoFactorChallengeResponse } | { twoFactor: null }>;
  register: (
    email: string,
    password: string,
    displayName: string,
    acceptedTerms: boolean,
    confirmedMinimumAge: boolean,
    turnstileToken?: string | null,
  ) => Promise<void>;
  logout: () => void;
  /**
   * Phase 9e.6. Revokes every refresh token for the account (server-side, via `/api/auth/logout-all`)
   * then clears the local session and redirects to /login, same destination `logout` uses. Unlike
   * `logout`, this is not best-effort: the whole point is that it actually reaches the server before
   * the local session is dropped, since every other tab/device's session dies with this one.
   */
  signOutEverywhere: () => Promise<void>;
  /**
   * Completes an external (OAuth) sign-in from an already-exchanged token pair. The provider dance
   * happens outside React entirely: this is only the "we have tokens now, adopt them" half.
   * Delegates to `adoptSession`: the two calls are the same operation under different names kept
   * for call-site clarity.
   */
  completeExternalLogin: (auth: AuthResponse) => void;
  /**
   * Adopts a token pair the server minted outside the normal login flow: the 2FA login step, and the
   * enable/disable calls that deliberately revoke every other refresh token for the account (README
   * § 6.3). The caller must call this immediately on success: the old pair is dead the moment the
   * server responds.
   */
  adoptSession: (auth: AuthResponse) => void;
  /**
   * Called after a successful rename (Phase 8a.4) so `NavBar` shows the new name immediately.
   * Updates the stored `displayName` only: deliberately does NOT force a token refresh. The JWT's
   * own `displayName` claim stays stale until the next natural refresh; nothing in the UI reads
   * that claim, so there is nothing to work around.
   */
  setDisplayName: (name: string) => void;
  /**
   * Called after a successful avatar change so `NavBar` shows the new picture immediately.
   * Same localStorage-only update as `setDisplayName`: the JWT is not involved.
   */
  setAvatarUrl: (url: AvatarResponse["avatarUrl"]) => void;
}

/** A stored `roles` that predates this field, or was corrupted, must not crash `.includes`. */
function normalizeRoles(value: unknown): string[] {
  return Array.isArray(value)
    ? value.filter((v): v is string => typeof v === "string")
    : [];
}

const AuthContext = createContext<AuthContextValue | null>(null);

export function AuthProvider({ children }: { children: ReactNode }) {
  const router = useRouter();
  const [auth, setAuth] = useState<StoredAuth | null>(null);
  const [hydrated, setHydrated] = useState(false);

  useEffect(() => {
    const raw = window.localStorage.getItem(STORAGE_KEY);
    if (raw) {
      try {
        const parsed = JSON.parse(raw) as StoredAuth;
        // A `cs_auth` blob written before roles existed has no `roles` field at all: treat
        // missing/non-array as `[]` rather than crashing on `.includes` downstream.
        const normalized: StoredAuth = {
          ...parsed,
          roles: normalizeRoles(parsed.roles),
        };
        setAuth(normalized);
        setAuthToken(normalized.token);
        setRefreshToken(normalized.refreshToken);
      } catch {
        window.localStorage.removeItem(STORAGE_KEY);
      }
    }
    setHydrated(true);
  }, []);

  useEffect(() => {
    if (!hydrated || !auth?.userId) return;
    if (auth.avatarUrl !== undefined) return;
    let cancelled = false;
    getPublicProfile(auth.userId)
      .then((profile) => {
        if (cancelled) return;
        setAuth((prev) => {
          if (!prev) return prev;
          const next: StoredAuth = { ...prev, avatarUrl: profile.avatarUrl };
          window.localStorage.setItem(STORAGE_KEY, JSON.stringify(next));
          return next;
        });
      })
      .catch(() => {
        if (cancelled) return;
        setAuth((prev) => {
          if (!prev) return prev;
          const next: StoredAuth = { ...prev, avatarUrl: null };
          window.localStorage.setItem(STORAGE_KEY, JSON.stringify(next));
          return next;
        });
      });
    return () => {
      cancelled = true;
    };
  }, [hydrated, auth?.userId, auth?.avatarUrl]);

  useEffect(() => {
    // A silent refresh succeeded somewhere in api.ts (401 → retry path): re-persist the rotated
    // pair so a page reload doesn't reuse the now-dead old refresh token.
    setOnTokenRefreshed((res: AuthResponse) => {
      setAuth((prev) => {
        if (!prev) return prev;
        const next: StoredAuth = {
          ...prev,
          token: res.token,
          refreshToken: res.refreshToken,
          expiresAt: res.expiresAt,
          roles: normalizeRoles(res.roles),
        };
        window.localStorage.setItem(STORAGE_KEY, JSON.stringify(next));
        return next;
      });
    });

    // The refresh token itself is dead (expired/revoked/reused): there is no way back without
    // logging in again.
    setOnRefreshFailed(() => {
      clearSession();
      router.push("/login");
    });

    return () => {
      setOnTokenRefreshed(null);
      setOnRefreshFailed(null);
    };
  }, [router]);

  function persist(value: StoredAuth) {
    setAuth(value);
    setAuthToken(value.token);
    setRefreshToken(value.refreshToken);
    window.localStorage.setItem(STORAGE_KEY, JSON.stringify(value));
  }

  function clearSession() {
    setAuth(null);
    setAuthToken(null);
    setRefreshToken(null);
    window.localStorage.removeItem(STORAGE_KEY);
  }

  async function login(email: string, password: string) {
    const res = await apiLogin(email, password);
    if (isTwoFactorChallenge(res)) {
      return { twoFactor: res };
    }
    persist(res);
    return { twoFactor: null };
  }

  async function register(
    email: string,
    password: string,
    displayName: string,
    acceptedTerms: boolean,
    confirmedMinimumAge: boolean,
    turnstileToken?: string | null,
  ) {
    const res = await apiRegister(
      email,
      password,
      displayName,
      acceptedTerms,
      confirmedMinimumAge,
      turnstileToken,
    );
    persist(res);
  }

  function adoptSession(res: AuthResponse) {
    persist(res);
  }

  function completeExternalLogin(res: AuthResponse) {
    adoptSession(res);
  }

  function setDisplayName(name: string) {
    setAuth((prev) => {
      if (!prev) return prev;
      const next: StoredAuth = { ...prev, displayName: name };
      window.localStorage.setItem(STORAGE_KEY, JSON.stringify(next));
      return next;
    });
  }

  function setAvatarUrl(url: AvatarResponse["avatarUrl"]) {
    setAuth((prev) => {
      if (!prev) return prev;
      if (prev.avatarUrl === url) return prev;
      const next: StoredAuth = { ...prev, avatarUrl: url };
      window.localStorage.setItem(STORAGE_KEY, JSON.stringify(next));
      return next;
    });
  }

  function logout() {
    const refreshToken = auth?.refreshToken;
    clearSession();
    router.push("/login");
    if (refreshToken) {
      // Best-effort: revoke server-side so the refresh token can't be replayed. The user is
      // already logged out locally regardless of whether this succeeds.
      apiLogout(refreshToken).catch(() => {});
    }
  }

  async function signOutEverywhere() {
    // Unlike logout(), this waits for the server call: revoking-then-clearing means a failed request
    // (network blip) leaves the local session intact rather than claiming a sign-out that didn't
    // happen everywhere.
    await apiLogoutAll();
    clearSession();
    router.push("/login");
  }

  const roles = auth?.roles ?? [];

  return (
    <AuthContext.Provider
      value={{
        userId: auth?.userId ?? null,
        email: auth?.email ?? null,
        displayName: auth?.displayName ?? null,
        token: auth?.token ?? null,
        roles,
        avatarUrl: auth?.avatarUrl,
        isAdmin: roles.includes("Admin"),
        hydrated,
        login,
        register,
        logout,
        signOutEverywhere,
        completeExternalLogin,
        adoptSession,
        setDisplayName,
        setAvatarUrl,
      }}
    >
      {children}
    </AuthContext.Provider>
  );
}

export function useAuth(): AuthContextValue {
  const ctx = useContext(AuthContext);
  if (!ctx) {
    throw new Error("useAuth must be used within an AuthProvider");
  }
  return ctx;
}
