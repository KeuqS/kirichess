import { act, fireEvent, render, screen } from "@testing-library/react";
import { NextIntlClientProvider } from "next-intl";
import type { ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { resetEmptyLobbyLeave } from "@/lib/emptyLobbyLeave";
import en from "../messages/en.json";
import { PRESETS } from "../lib/presets";
import { GameHubProvider } from "./GameHubProvider";
import { QUEUE_BOT_NUDGE_MS, RatedPoolsGrid } from "./RatedPoolsGrid";

const useAuth = vi.fn();
const push = vi.fn();
const pathname = vi.fn(() => "/");

/** Read rather than typed: config/kirichess.rules.json owns the name and PoolLabelDriftTests fails
 *  the build on a hardcoded one. `bounty1` is the pool the assertions below queue into. */
const POOL = PRESETS.bounty1.label;

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
  usePathname: () => pathname(),
  useRouter: () => ({ push }),
}));

const api = vi.hoisted(() => ({
  getMyRatings: vi.fn().mockResolvedValue([]),
}));

vi.mock("@/lib/api", () => ({
  getMyRatings: api.getMyRatings,
}));

const hub = vi.hoisted(() => ({
  invoke: vi.fn().mockResolvedValue(undefined),
  start: vi.fn().mockResolvedValue(undefined),
  stop: vi.fn(),
  on: vi.fn(),
  onreconnected: vi.fn(),
  state: "Connected",
}));

vi.mock("@/lib/realtime", () => ({
  createGameConnection: () => hub,
  stopWhenSettled: vi.fn(),
}));

vi.mock("@/components/BotGameDialog", () => ({
  BotGameDialog: ({
    presetKey,
    onClose,
  }: {
    presetKey: string;
    onClose: () => void;
  }) => (
    <div role="dialog">
      bot:{presetKey}
      <button type="button" onClick={onClose}>
        close
      </button>
    </div>
  ),
}));

function renderGrid() {
  return render(
    <NextIntlClientProvider locale="en" messages={en}>
      <GameHubProvider>
        <RatedPoolsGrid />
      </GameHubProvider>
    </NextIntlClientProvider>,
  );
}

describe("RatedPoolsGrid", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    hub.invoke.mockResolvedValue(undefined);
    hub.start.mockResolvedValue(undefined);
    api.getMyRatings.mockResolvedValue([]);
    resetEmptyLobbyLeave();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it("sends guests to login with next set to the current path", () => {
    useAuth.mockReturnValue({ token: null, hydrated: true });
    renderGrid();
    fireEvent.click(screen.getByRole("button", { name: `Queue ${POOL}` }));
    expect(push).toHaveBeenCalledWith("/login?next=%2F");
    expect(hub.invoke).not.toHaveBeenCalled();
  });

  it("offers a bot game for the same pool after a stalled queue", async () => {
    vi.useFakeTimers();
    useAuth.mockReturnValue({ token: "session", hydrated: true });
    renderGrid();
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: `Queue ${POOL}` }));
    });
    expect(hub.invoke).toHaveBeenCalledWith("JoinQueue", "bounty1");
    expect(
      screen.queryByRole("button", { name: `Play the bot in ${POOL}` }),
    ).toBeNull();

    await act(async () => {
      await vi.advanceTimersByTimeAsync(QUEUE_BOT_NUDGE_MS);
    });
    fireEvent.click(
      screen.getByRole("button", { name: `Play the bot in ${POOL}` }),
    );
    expect(screen.getByRole("dialog")).toHaveTextContent("bot:bounty1");
    await act(async () => {
      await Promise.resolve();
    });
    expect(hub.invoke).toHaveBeenCalledWith("LeaveQueue");
  });
});
