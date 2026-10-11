import { render, screen } from "@testing-library/react";
import { NextIntlClientProvider } from "next-intl";
import { describe, expect, it } from "vitest";
import { LiveGameCard } from "./LiveGameCard";
import { PRESETS } from "@/lib/presets";
import { PIECE_BLACK, PIECE_WHITE } from "@/lib/boardTheme";
import type { LiveGameDto } from "@/lib/types";

/** jsdom normalises inline colours to `rgb(...)`, so compare in that form. */
function toRgb(hex: string): string {
  const n = parseInt(hex.slice(1), 16);
  return `rgb(${(n >> 16) & 255}, ${(n >> 8) & 255}, ${n & 255})`;
}

const START_FEN = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";

const messages = {
  watch: {
    watchRowAria: "Watch {white} ({whiteSide}) vs {black} ({blackSide})",
    customLabel: "Custom",
    moveCount: "{count, plural, one {# move} other {# moves}}",
    white: "White",
    black: "Black",
  },
  DonationTiers: {
    patronBadgeLabel: "Patron",
  },
};

const game: LiveGameDto = {
  gameId: "00000000-0000-4000-a000-000000000001",
  whiteName: "Mira",
  blackName: "Ren",
  whitePatronTier: "Queen",
  blackPatronTier: "None",
  pool: "steal1",
  rated: true,
  whiteTime: 248,
  blackTime: 261,
  sideToMove: "White",
  moveCount: 5,
  spectators: 0,
  startedAt: "2026-08-22T18:00:00.000Z",
  fen: START_FEN,
};

function renderCard(href?: string | null) {
  return render(
    <NextIntlClientProvider locale="en" messages={messages}>
      <LiveGameCard
        game={game}
        whiteSeconds={game.whiteTime}
        blackSeconds={game.blackTime}
        href={href}
      />
    </NextIntlClientProvider>,
  );
}

describe("LiveGameCard", () => {
  it("links to the spectate page by default", () => {
    renderCard();
    const link = screen.getByRole("link", {
      name: "Watch Mira (White) vs Ren (Black)",
    });
    expect(link).toHaveAttribute("href", `/watch/${game.gameId}`);
  });

  it("renders as a static card when there is no game to open", () => {
    renderCard(null);
    expect(screen.queryByRole("link")).toBeNull();
    expect(screen.getByText("Mira")).toBeInTheDocument();
    expect(screen.getByText("Ren")).toBeInTheDocument();
  });

  it("shows the pool name rather than the economy", () => {
    renderCard();
    expect(screen.getByText(PRESETS.steal1.label)).toBeInTheDocument();
  });

  it("marks each name with the matching piece colour", () => {
    renderCard();
    const black = screen.getByText("Ren").previousElementSibling as HTMLElement;
    const white = screen.getByText("Mira").previousElementSibling as HTMLElement;
    expect(black.style.backgroundColor).toBe(toRgb(PIECE_BLACK));
    expect(white.style.backgroundColor).toBe(toRgb(PIECE_WHITE));
  });
});
