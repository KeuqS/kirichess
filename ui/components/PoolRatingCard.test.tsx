import { render, screen } from "@testing-library/react";
import { NextIntlClientProvider } from "next-intl";
import { describe, expect, it } from "vitest";
import { PoolRatingCard } from "./PoolRatingCard";
import { PRESETS } from "@/lib/presets";
import type { RatingDto } from "@/lib/types";

const messages = {
  PoolRatingCard: {
    rdLabel: "±RD",
    gamesSettling:
      "{games, plural, one {# game - settling} other {# games - settling}}",
    gamesEstablished:
      "{games, plural, one {# game - <established>established</established>} other {# games - <established>established</established>}}",
  },
};

function rating(overrides: Partial<RatingDto> = {}): RatingDto {
  return {
    pool: "steal1",
    rating: 1642.4,
    ratingDeviation: 48.2,
    volatility: 0.06,
    gamesPlayed: 118,
    provisional: false,
    updatedAt: "",
    ...overrides,
  };
}

function renderCard(dto: RatingDto) {
  return render(
    <NextIntlClientProvider locale="en" messages={messages}>
      <PoolRatingCard rating={dto} />
    </NextIntlClientProvider>,
  );
}

describe("PoolRatingCard", () => {
  it("puts the rating top-right and the time control under the pool name", () => {
    renderCard(rating());
    expect(screen.getByRole("heading", { name: PRESETS.steal1.label })).toBeInTheDocument();
    expect(screen.getByText("1642")).toBeInTheDocument();
    expect(screen.getByText(PRESETS.steal1.time)).toBeInTheDocument();
  });

  it("does not show an economy badge", () => {
    renderCard(rating({ pool: "bounty1" }));
    expect(screen.queryByText(/bounty/i)).toBeNull();
    expect(screen.queryByText(/steal/i)).toBeNull();
    expect(screen.queryByText(/burn/i)).toBeNull();
  });

  it("marks a provisional rating", () => {
    renderCard(rating({ pool: "burn2", rating: 1401, gamesPlayed: 6, provisional: true }));
    expect(screen.getByText("1401?")).toBeInTheDocument();
    expect(screen.getByText("6 games - settling")).toBeInTheDocument();
  });

  it("uses the singular for one provisional game", () => {
    renderCard(rating({ pool: "burn2", gamesPlayed: 1, provisional: true }));
    expect(screen.getByText("1 game - settling")).toBeInTheDocument();
  });
});
