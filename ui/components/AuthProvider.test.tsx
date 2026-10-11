import { act, render, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { AuthProvider, useAuth } from "./AuthProvider";
import type { AuthResponse, TwoFactorChallengeResponse } from "@/lib/types";

const STORAGE_KEY = "cs_auth";

const push = vi.fn();

vi.mock("@/i18n/navigation", () => ({
  useRouter: () => ({ push }),
}));

// Captures the listeners AuthProvider registers with api.ts, so tests can fire them directly -
// this is how api.ts itself would call back in after a silent 401 refresh (setOnTokenRefreshed)
// or once the refresh token itself is dead (setOnRefreshFailed).
const { tokenRefreshedRef, refreshFailedRef } = vi.hoisted(() => ({
  tokenRefreshedRef: { current: null as ((res: AuthResponse) => void) | null },
  refreshFailedRef: { current: null as (() => void) | null },
}));

const apiMocks = vi.hoisted(() => ({
  login: vi.fn(),
  register: vi.fn(),
  logout: vi.fn(),
  logoutAll: vi.fn(),
  getPublicProfile: vi.fn(),
  setAuthToken: vi.fn(),
  setRefreshToken: vi.fn(),
}));

vi.mock("@/lib/api", async (importOriginal) => {
  // Keep everything real (in particular `isTwoFactorChallenge`, the union-narrowing predicate
  // AuthProvider's login() relies on) except the network-touching functions and the two
  // listener setters, which are swapped for controllable test doubles.
  const actual = await importOriginal<typeof import("@/lib/api")>();
  return {
    ...actual,
    login: apiMocks.login,
    register: apiMocks.register,
    logout: apiMocks.logout,
    logoutAll: apiMocks.logoutAll,
    getPublicProfile: apiMocks.getPublicProfile,
    setAuthToken: apiMocks.setAuthToken,
    setRefreshToken: apiMocks.setRefreshToken,
    setOnTokenRefreshed: (fn: ((res: AuthResponse) => void) | null) => {
      tokenRefreshedRef.current = fn;
    },
    setOnRefreshFailed: (fn: (() => void) | null) => {
      refreshFailedRef.current = fn;
    },
  };
});

type AuthCtx = ReturnType<typeof useAuth>;

function Capture({ onAuth }: { onAuth: (auth: AuthCtx) => void }) {
  const auth = useAuth();
  onAuth(auth);
  return null;
}

/** Mounts a real AuthProvider and returns a getter for its latest context value. */
function renderAuth() {
  let auth!: AuthCtx;
  render(
    <AuthProvider>
      <Capture onAuth={(a) => (auth = a)} />
    </AuthProvider>,
  );
  return { getAuth: () => auth };
}

// `avatarUrl: null` (already "backfilled") so tests unrelated to the avatar fetch don't race
// its effect - only the avatar-backfill tests below persist a session that omits the field.
const SESSION: AuthResponse & { avatarUrl: string | null } = {
  userId: "user-1",
  email: "ann@example.com",
  displayName: "Ann",
  token: "tok-1",
  refreshToken: "ref-1",
  expiresAt: "2030-01-01T00:00:00Z",
  roles: ["Player"],
  avatarUrl: null,
};

function persistSession(session: object) {
  window.localStorage.setItem(STORAGE_KEY, JSON.stringify(session));
}

function readStored(): (AuthResponse & { avatarUrl?: string | null }) | null {
  const raw = window.localStorage.getItem(STORAGE_KEY);
  return raw ? JSON.parse(raw) : null;
}

beforeEach(() => {
  window.localStorage.clear();
  vi.clearAllMocks();
  tokenRefreshedRef.current = null;
  refreshFailedRef.current = null;
  // Most tests don't care about the avatar backfill; give it a harmless default so it doesn't
  // leave an unresolved fetch dangling across tests that never await it.
  apiMocks.getPublicProfile.mockResolvedValue({ avatarUrl: null });
});

describe("AuthProvider hydrate", () => {
  it("hydrates to an empty session when localStorage has nothing stored", () => {
    const { getAuth } = renderAuth();
    expect(getAuth().hydrated).toBe(true);
    expect(getAuth().userId).toBeNull();
    expect(getAuth().avatarUrl).toBeUndefined();
  });

  it("hydrates a stored session and exposes it through context", () => {
    persistSession(SESSION);
    const { getAuth } = renderAuth();
    expect(getAuth().hydrated).toBe(true);
    expect(getAuth().userId).toBe("user-1");
    expect(getAuth().token).toBe("tok-1");
    expect(getAuth().roles).toEqual(["Player"]);
  });

  it("drops an unparsable stored blob instead of crashing", () => {
    window.localStorage.setItem(STORAGE_KEY, "{not valid json");
    const { getAuth } = renderAuth();
    expect(getAuth().hydrated).toBe(true);
    expect(getAuth().userId).toBeNull();
    expect(window.localStorage.getItem(STORAGE_KEY)).toBeNull();
  });

  it("normalizeRoles guards a corrupted roles field instead of crashing on .includes", () => {
    // A blob written before `roles` existed, or corrupted some other way: roles is a string,
    // not an array. The old field predates the roles feature entirely.
    const corrupted = { ...SESSION, roles: "Admin" as unknown as string[] };
    persistSession(corrupted);
    const { getAuth } = renderAuth();
    expect(getAuth().roles).toEqual([]);
    expect(getAuth().isAdmin).toBe(false);
  });
});

describe("AuthProvider avatar backfill", () => {
  // Undefined `avatarUrl` (the field just missing, as an old or never-backfilled blob would be)
  // is what triggers the backfill effect - `SESSION` itself already carries `avatarUrl: null` so
  // the other describe blocks don't race this effect.
  const UNBACKFILLED_SESSION: Partial<typeof SESSION> = { ...SESSION };
  delete UNBACKFILLED_SESSION.avatarUrl;

  it("fetches and persists the avatar once, after hydrate, when none is stored yet", async () => {
    persistSession(UNBACKFILLED_SESSION);
    apiMocks.getPublicProfile.mockResolvedValue({
      avatarUrl: "https://example.com/ann.png",
    });

    const { getAuth } = renderAuth();
    expect(getAuth().avatarUrl).toBeUndefined();

    await waitFor(() =>
      expect(getAuth().avatarUrl).toBe("https://example.com/ann.png"),
    );
    expect(readStored()?.avatarUrl).toBe("https://example.com/ann.png");
    expect(apiMocks.getPublicProfile).toHaveBeenCalledWith("user-1");
  });

  it("backfills null (not undefined) when the profile fetch fails, so it doesn't retry forever", async () => {
    persistSession(UNBACKFILLED_SESSION);
    apiMocks.getPublicProfile.mockRejectedValue(new Error("network down"));

    const { getAuth } = renderAuth();
    await waitFor(() => expect(getAuth().avatarUrl).toBeNull());
    expect(readStored()?.avatarUrl).toBeNull();
  });
});

describe("AuthProvider login", () => {
  it("returns the twoFactor challenge without persisting a session", async () => {
    const challenge: TwoFactorChallengeResponse = {
      twoFactorRequired: true,
      twoFactorTicket: "ticket-1",
      expiresInSeconds: 300,
    };
    apiMocks.login.mockResolvedValue(challenge);

    const { getAuth } = renderAuth();
    let result: Awaited<ReturnType<AuthCtx["login"]>> | undefined;
    await act(async () => {
      result = await getAuth().login("ann@example.com", "pw");
    });

    expect(result).toEqual({ twoFactor: challenge });
    expect(getAuth().token).toBeNull();
    expect(window.localStorage.getItem(STORAGE_KEY)).toBeNull();
  });

  it("persists a full session when the server returns tokens directly (no 2FA)", async () => {
    apiMocks.login.mockResolvedValue(SESSION);

    const { getAuth } = renderAuth();
    let result: Awaited<ReturnType<AuthCtx["login"]>> | undefined;
    await act(async () => {
      result = await getAuth().login("ann@example.com", "pw");
    });

    expect(result).toEqual({ twoFactor: null });
    expect(getAuth().userId).toBe("user-1");
    expect(getAuth().token).toBe("tok-1");
    expect(readStored()?.refreshToken).toBe("ref-1");
  });
});

describe("AuthProvider token rotation and dead-refresh teardown", () => {
  it("re-persists a rotated refresh token when a silent refresh succeeds, so a reload can't reuse the dead one", async () => {
    persistSession(SESSION);
    const { getAuth } = renderAuth();
    expect(getAuth().token).toBe("tok-1");
    expect(tokenRefreshedRef.current).not.toBeNull();

    const rotated: AuthResponse = {
      ...SESSION,
      token: "tok-2",
      refreshToken: "ref-2",
      expiresAt: "2031-01-01T00:00:00Z",
    };
    act(() => {
      tokenRefreshedRef.current?.(rotated);
    });

    // Observable through context...
    expect(getAuth().token).toBe("tok-2");
    // ...and, critically, through what a page reload would actually read back.
    const stored = readStored();
    expect(stored?.refreshToken).toBe("ref-2");
    expect(stored?.refreshToken).not.toBe("ref-1");
    expect(stored?.token).toBe("tok-2");
  });

  it("normalizes roles on a rotated pair too, not just at hydrate", () => {
    persistSession(SESSION);
    const { getAuth } = renderAuth();

    act(() => {
      tokenRefreshedRef.current?.({
        ...SESSION,
        // A truthy, non-array value - unlike `undefined`/`null`, this is NOT caught by the
        // `auth?.roles ?? []` fallback the context value applies, so this genuinely exercises
        // normalizeRoles() itself rather than that separate guard.
        roles: "Admin" as unknown as string[],
      });
    });

    expect(getAuth().roles).toEqual([]);
    expect(getAuth().isAdmin).toBe(false);
  });

  it("clears the session and redirects to /login when the refresh token itself is dead", () => {
    persistSession(SESSION);
    const { getAuth } = renderAuth();
    expect(getAuth().userId).toBe("user-1");
    expect(refreshFailedRef.current).not.toBeNull();

    act(() => {
      refreshFailedRef.current?.();
    });

    expect(getAuth().userId).toBeNull();
    expect(window.localStorage.getItem(STORAGE_KEY)).toBeNull();
    expect(push).toHaveBeenCalledWith("/login");
  });
});

describe("AuthProvider adoptSession / completeExternalLogin", () => {
  it("replaces the whole stored session: the old pair is dead the moment the server responds", () => {
    persistSession(SESSION);
    const { getAuth } = renderAuth();

    const adopted: AuthResponse = {
      userId: "user-2",
      email: "bo@example.com",
      displayName: "Bo",
      token: "tok-adopted",
      refreshToken: "ref-adopted",
      expiresAt: "2032-01-01T00:00:00Z",
      roles: ["Player", "Admin"],
    };
    act(() => {
      getAuth().adoptSession(adopted);
    });

    expect(getAuth().userId).toBe("user-2");
    expect(getAuth().isAdmin).toBe(true);
    const stored = readStored();
    expect(stored?.refreshToken).toBe("ref-adopted");
    expect(stored?.refreshToken).not.toBe("ref-1");
  });

  it("completeExternalLogin adopts the session the same way adoptSession does", () => {
    const { getAuth } = renderAuth();
    const res: AuthResponse = {
      ...SESSION,
      userId: "user-3",
      token: "tok-external",
      refreshToken: "ref-external",
    };
    act(() => {
      getAuth().completeExternalLogin(res);
    });

    expect(getAuth().userId).toBe("user-3");
    expect(readStored()?.token).toBe("tok-external");
  });
});

describe("AuthProvider logout vs signOutEverywhere", () => {
  it("logout clears the session and redirects immediately, without waiting on the server revoke", () => {
    persistSession(SESSION);
    // Never resolves within this test: proves logout() doesn't wait on it.
    apiMocks.logout.mockReturnValue(new Promise(() => {}));

    const { getAuth } = renderAuth();
    act(() => {
      getAuth().logout();
    });

    expect(getAuth().userId).toBeNull();
    expect(window.localStorage.getItem(STORAGE_KEY)).toBeNull();
    expect(push).toHaveBeenCalledWith("/login");
    expect(apiMocks.logout).toHaveBeenCalledWith("ref-1");
  });

  it("logout swallows a server-side revoke failure; the local session is already gone regardless", async () => {
    persistSession(SESSION);
    apiMocks.logout.mockRejectedValue(new Error("network down"));

    const { getAuth } = renderAuth();
    await act(async () => {
      getAuth().logout();
      // Let the fire-and-forget rejection settle without it escaping as an unhandled rejection.
      await Promise.resolve();
    });

    expect(getAuth().userId).toBeNull();
  });

  it("signOutEverywhere does not clear the local session when the server revoke fails", async () => {
    persistSession(SESSION);
    apiMocks.logoutAll.mockRejectedValue(new Error("network down"));

    const { getAuth } = renderAuth();
    await act(async () => {
      await expect(getAuth().signOutEverywhere()).rejects.toThrow(
        "network down",
      );
    });

    expect(getAuth().userId).toBe("user-1");
    expect(readStored()?.refreshToken).toBe("ref-1");
    expect(push).not.toHaveBeenCalledWith("/login");
  });

  it("signOutEverywhere clears the session and redirects only after the server call resolves", async () => {
    persistSession(SESSION);
    apiMocks.logoutAll.mockResolvedValue(undefined);

    const { getAuth } = renderAuth();
    await act(async () => {
      await getAuth().signOutEverywhere();
    });

    expect(getAuth().userId).toBeNull();
    expect(window.localStorage.getItem(STORAGE_KEY)).toBeNull();
    expect(push).toHaveBeenCalledWith("/login");
  });
});
