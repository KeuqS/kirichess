import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { NextIntlClientProvider } from "next-intl";
import type { ReactNode } from "react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { getMyChallenges } from "@/lib/api";
import { presetToCustomConfig } from "@/lib/customConfig";
import en from "../messages/en.json";
import { ChallengeFriendPanel } from "./ChallengeFriendPanel";
import { GameHubProvider } from "./GameHubProvider";

const useAuth = vi.fn();

vi.mock("@/components/AuthProvider", () => ({
  useAuth: () => useAuth(),
}));

vi.mock("@/i18n/navigation", () => ({
  Link: ({
    href,
    children,
    className,
  }: {
    href: string;
    children: ReactNode;
    className?: string;
  }) => (
    <a href={href} className={className}>
      {children}
    </a>
  ),
  useRouter: () => ({ push: vi.fn() }),
}));

vi.mock("@/lib/api", () => ({
  ApiError: class ApiError extends Error {},
  cancelChallenge: vi.fn(),
  createChallenge: vi.fn(),
  getMyChallenges: vi.fn().mockResolvedValue([]),
}));

const hub = vi.hoisted(() => ({
  on: vi.fn(),
  onreconnected: vi.fn(),
  start: vi.fn().mockResolvedValue(undefined),
  stop: vi.fn(),
  invoke: vi.fn().mockResolvedValue(undefined),
  state: "Connected",
}));

vi.mock("@/lib/realtime", () => ({
  createGameConnection: () => hub,
  stopWhenSettled: (
    conn: { stop: () => void },
    started: Promise<unknown>,
  ) => {
    void started.catch(() => {}).then(() => conn.stop());
  },
}));

function renderPanel() {
  return render(
    <NextIntlClientProvider locale="en" messages={en}>
      <GameHubProvider>
        <ChallengeFriendPanel />
      </GameHubProvider>
    </NextIntlClientProvider>,
  );
}

describe("ChallengeFriendPanel", () => {
  beforeEach(() => {
    vi.mocked(getMyChallenges).mockResolvedValue([]);
  });

  it("puts the guest sign-in CTA on the heading row, matching Custom game", async () => {
    useAuth.mockReturnValue({ token: null, hydrated: true });
    renderPanel();
    const link = await screen.findByRole("link", {
      name: "Sign in to challenge a friend",
    });
    expect(link).toHaveAttribute("href", "/login?next=%2F");
    expect(link).toHaveClass("btn-primary");
    expect(link.parentElement).toHaveClass("items-center", "justify-between");
  });

  it("copies the challenge link without throwing", async () => {
    const writeText = vi.fn().mockResolvedValue(undefined);
    Object.assign(navigator, { clipboard: { writeText } });

    useAuth.mockReturnValue({ token: "session", hydrated: true });
    vi.mocked(getMyChallenges).mockResolvedValue([
      {
        token: "abc123",
        challengerName: "Michiel",
        pool: "steal1",
        colour: "Random",
        status: "Open",
        createdAt: new Date().toISOString(),
        expiresAt: new Date(Date.now() + 60 * 60 * 1000).toISOString(),
        gameId: null,
        economy: presetToCustomConfig("steal1"),
        challengerIsBot: false,
      },
    ]);

    renderPanel();
    const copy = await screen.findByRole("button", { name: "Copy" });
    fireEvent.click(copy);

    await waitFor(() => {
      expect(writeText).toHaveBeenCalled();
      expect(screen.getByRole("button", { name: "Copied" })).toBeInTheDocument();
    });
  });

  it("does not restore a custom-config invite as a lobby pool challenge", async () => {
    useAuth.mockReturnValue({ token: "session", hydrated: true });
    vi.mocked(getMyChallenges).mockResolvedValue([
      {
        token: "custom1",
        challengerName: "Michiel",
        pool: "custom",
        colour: "Random",
        status: "Open",
        createdAt: new Date().toISOString(),
        expiresAt: new Date(Date.now() + 60 * 60 * 1000).toISOString(),
        gameId: null,
        economy: presetToCustomConfig("steal1"),
        challengerIsBot: false,
      },
    ]);

    renderPanel();
    expect(
      await screen.findByRole("button", { name: "Create link" }),
    ).toBeInTheDocument();
    expect(
      screen.queryByRole("button", { name: "Copy" }),
    ).not.toBeInTheDocument();
  });
});
