import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { GameHubProvider, useGameHub } from "./GameHubProvider";

vi.mock("@/i18n/navigation", () => ({
  useRouter: () => ({ push: vi.fn() }),
}));

const realtime = vi.hoisted(() => ({
  createGameConnection: vi.fn(() => ({
    on: vi.fn(),
    onreconnected: vi.fn(),
    start: vi.fn().mockResolvedValue(undefined),
    stop: vi.fn(),
    invoke: vi.fn().mockResolvedValue(undefined),
    state: "Connected",
  })),
  stopWhenSettled: vi.fn(),
}));

vi.mock("@/lib/realtime", () => realtime);

function EnsureButton({ label }: { label: string }) {
  const { ensure } = useGameHub();
  return (
    <button type="button" onClick={() => void ensure()}>
      {label}
    </button>
  );
}

describe("GameHubProvider (I-1-1)", () => {
  it("does not open a game hub until ensure() runs", () => {
    render(
      <GameHubProvider>
        <EnsureButton label="queue" />
      </GameHubProvider>,
    );
    expect(realtime.createGameConnection).not.toHaveBeenCalled();
  });

  it("shares one connection across lobby widgets", async () => {
    render(
      <GameHubProvider>
        <EnsureButton label="queue" />
        <EnsureButton label="seek" />
      </GameHubProvider>,
    );
    fireEvent.click(screen.getByRole("button", { name: "queue" }));
    fireEvent.click(screen.getByRole("button", { name: "seek" }));
    await waitFor(() =>
      expect(realtime.createGameConnection).toHaveBeenCalledTimes(1),
    );
  });

  it("throws outside the provider", () => {
    function Bare() {
      useGameHub();
      return null;
    }
    expect(() => render(<Bare />)).toThrow(/GameHubProvider/);
  });
});
