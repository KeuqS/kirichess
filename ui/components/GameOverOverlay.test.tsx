import { fireEvent, render, screen } from "@testing-library/react";
import { NextIntlClientProvider } from "next-intl";
import type { ComponentProps, ReactNode } from "react";
import { describe, expect, it, vi } from "vitest";
import { GameOverOverlay } from "./GameOverOverlay";
import en from "../messages/en.json";

vi.mock("@/components/AuthProvider", () => ({
  useAuth: () => ({ userId: "me" }),
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
}));

const history = [
  { moveNumber: 0, whiteTime: 180, blackTime: 180 },
  { moveNumber: 1, whiteTime: 170, blackTime: 180 },
];

describe("GameOverOverlay", () => {
  it("shows Accept rematch when a standing offer is incoming", () => {
    render(
      <NextIntlClientProvider locale="en" messages={en}>
        <GameOverOverlay
          termination="Resignation"
          winner="White"
          history={history}
          startTimeSeconds={180}
          gameId="game-1"
          opponentId="them"
          rematchState="incoming"
          onAcceptRematch={() => undefined}
        />
      </NextIntlClientProvider>,
    );
    expect(
      screen.getByRole("button", { name: "Accept rematch" }),
    ).toBeInTheDocument();
    expect(
      screen.queryByRole("button", { name: "Offer rematch" }),
    ).not.toBeInTheDocument();
  });

  it("disables rematch while the hub is not connected (P-4-3)", () => {
    const onOfferRematch = vi.fn();
    render(
      <NextIntlClientProvider locale="en" messages={en}>
        <GameOverOverlay
          termination="Resignation"
          winner="White"
          history={history}
          startTimeSeconds={180}
          gameId="game-1"
          opponentId="them"
          rematchState="idle"
          rematchDisabled
          onOfferRematch={onOfferRematch}
        />
      </NextIntlClientProvider>,
    );
    const button = screen.getByRole("button", { name: "Offer rematch" });
    expect(button).toBeDisabled();
    fireEvent.click(button);
    expect(onOfferRematch).not.toHaveBeenCalled();
  });

  it("uses primary and secondary recipes for New game and View & share", () => {
    render(
      <NextIntlClientProvider locale="en" messages={en}>
        <GameOverOverlay
          termination="Checkmate"
          winner="White"
          history={history}
          startTimeSeconds={180}
          gameId="game-1"
        />
      </NextIntlClientProvider>,
    );
    expect(screen.getByRole("link", { name: "New game" })).toHaveClass(
      "btn-primary",
    );
    expect(
      screen.getByRole("link", { name: "View & share replay" }),
    ).toHaveClass("btn-secondary");
  });
});

/**
 * The result sits beside the board, so it is a named region rather than a modal. Focus still
 * moves to the heading on mount; Tab is not trapped, so the board stays reachable.
 */
describe("GameOverOverlay focus", () => {
  function renderOverlay(
    props: Partial<ComponentProps<typeof GameOverOverlay>> = {},
  ) {
    return render(
      <NextIntlClientProvider locale="en" messages={en}>
        <GameOverOverlay
          termination="Checkmate"
          winner="White"
          history={history}
          startTimeSeconds={180}
          gameId="game-1"
          opponentId="them"
          rematchState="idle"
          onOfferRematch={() => undefined}
          {...props}
        />
      </NextIntlClientProvider>,
    );
  }

  it("names the result region from the heading and does not cover the page", () => {
    renderOverlay();
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    const heading = screen.getByRole("heading", {
      level: 2,
      name: "Checkmate",
    });
    expect(heading.id).toBeTruthy();
    expect(screen.getByRole("region", { name: "Checkmate" })).toHaveAttribute(
      "aria-labelledby",
      heading.id,
    );
  });

  it("moves focus to the heading on mount", () => {
    renderOverlay();
    const heading = screen.getByRole("heading", {
      level: 2,
      name: "Checkmate",
    });
    expect(heading).toHaveFocus();
  });

  it("restores focus to the element that was focused before the overlay mounted", () => {
    render(<button type="button">Outside control</button>);
    const outside = screen.getByRole("button", { name: "Outside control" });
    outside.focus();
    expect(outside).toHaveFocus();

    const { unmount } = renderOverlay();
    expect(outside).not.toHaveFocus();

    unmount();
    expect(outside).toHaveFocus();
  });

  it("does not trap Tab, so focus can leave the result for the board", () => {
    renderOverlay();
    const last = screen.getByRole("button", { name: "Report opponent" });
    last.focus();
    fireEvent.keyDown(last, { key: "Tab" });
    expect(last).toHaveFocus();
    expect(
      screen.getByRole("button", { name: "Offer rematch" }),
    ).not.toHaveFocus();
  });
});
