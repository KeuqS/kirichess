import { act, render, screen, waitFor } from "@testing-library/react";
import { NextIntlClientProvider } from "next-intl";
import { beforeEach, describe, expect, it, vi } from "vitest";
import en from "../messages/en.json";
import { SessionsCard } from "./SessionsCard";

const useAuth = vi.fn();

vi.mock("@/components/AuthProvider", () => ({
  useAuth: () => useAuth(),
}));

const api = vi.hoisted(() => ({
  getSessions: vi.fn(),
  revokeSession: vi.fn(),
}));

vi.mock("@/lib/api", () => ({
  ApiError: class ApiError extends Error {
    status: number;
    constructor(status: number, message: string) {
      super(message);
      this.status = status;
    }
  },
  getSessions: api.getSessions,
  revokeSession: api.revokeSession,
}));

function renderCard() {
  return render(
    <NextIntlClientProvider locale="en" messages={en}>
      <SessionsCard />
    </NextIntlClientProvider>,
  );
}

function session(id: string, createdAt: string) {
  return {
    id,
    createdAt,
    expiresAt: "2099-01-01T00:00:00Z",
    isCurrent: false,
  };
}

describe("SessionsCard", () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it("does not fetch sessions when there is no token (C-10-10)", () => {
    useAuth.mockReturnValue({ token: null, signOutEverywhere: vi.fn() });
    renderCard();
    expect(api.getSessions).not.toHaveBeenCalled();
  });

  it("drops a stale response from a superseded token instead of overwriting the fresh one (C-10-10)", async () => {
    // Two calls to getSessions: the first (for tokenA) hangs until we resolve it by hand; the
    // second (for tokenB, after the token rotates while this card stays mounted) resolves
    // immediately. Without the `cancelled` guard, resolving the first call late would clobber the
    // rows the second call already rendered - mirrors MatchHistoryCard.test.tsx's own pattern for
    // the same class of bug, adapted to a token-keyed effect instead of a mount-only one.
    let resolveStale!: (rows: ReturnType<typeof session>[]) => void;
    const stale = new Promise<ReturnType<typeof session>[]>((resolve) => {
      resolveStale = resolve;
    });
    api.getSessions.mockReturnValueOnce(stale);
    api.getSessions.mockResolvedValueOnce([
      session("fresh-1", "2026-01-02T00:00:00Z"),
    ]);

    useAuth.mockReturnValue({ token: "tokenA", signOutEverywhere: vi.fn() });
    const { rerender } = renderCard();

    await waitFor(() => expect(api.getSessions).toHaveBeenCalledTimes(1));

    useAuth.mockReturnValue({ token: "tokenB", signOutEverywhere: vi.fn() });
    rerender(
      <NextIntlClientProvider locale="en" messages={en}>
        <SessionsCard />
      </NextIntlClientProvider>,
    );

    await waitFor(() => expect(api.getSessions).toHaveBeenCalledTimes(2));
    await waitFor(() => expect(screen.getByText(/2026/)).toBeInTheDocument());

    // The stale (tokenA) response lands after the fresh (tokenB) one has already rendered.
    await act(async () => {
      resolveStale([session("stale-1", "2020-01-01T00:00:00Z")]);
      await Promise.resolve();
    });

    expect(screen.queryByText(/2020/)).toBeNull();
    expect(screen.getByText(/2026/)).toBeInTheDocument();
  });
});
