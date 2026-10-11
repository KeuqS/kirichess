import { render, screen } from "@testing-library/react";
import { NextIntlClientProvider } from "next-intl";
import type { ComponentProps } from "react";
import { describe, expect, it } from "vitest";
import en from "../messages/en.json";
import { Clock } from "./Clock";

function renderClock(overrides: Partial<ComponentProps<typeof Clock>> = {}) {
  const defaults: ComponentProps<typeof Clock> = {
    side: "White",
    seconds: 65,
    active: false,
    popups: [],
  };
  return render(
    <NextIntlClientProvider locale="en" messages={en}>
      <Clock {...defaults} {...overrides} />
    </NextIntlClientProvider>,
  );
}

/** Tenths live in a sibling span, so a string matcher cannot see `0:03.9` as one node. */
function getDigitRow(text: string) {
  return screen.getByText((_, node) => {
    return (
      node instanceof HTMLElement &&
      node.classList.contains("tabular-nums") &&
      node.textContent === text
    );
  });
}

describe("Clock (Phase 10b.3)", () => {
  it("formats seconds as M:SS at or above the urgent threshold", () => {
    renderClock({ seconds: 65 });
    expect(screen.getByText("1:05")).toBeInTheDocument();
  });

  it("floors and clamps a negative reading to 0:00.0 (below the urgent threshold)", () => {
    renderClock({ seconds: -3.7 });
    expect(getDigitRow("0:00.0")).toBeInTheDocument();
  });

  it("shows tenths below 20 seconds, floored rather than rounded up", () => {
    renderClock({ seconds: 3.96 });
    expect(getDigitRow("0:03.9")).toBeInTheDocument();
  });

  it("shows whole seconds at exactly the 20 second threshold", () => {
    renderClock({ seconds: 20 });
    expect(screen.getByText("0:20")).toBeInTheDocument();
  });

  it("shows tenths just under the threshold", () => {
    renderClock({ seconds: 19.9 });
    expect(getDigitRow("0:19.9")).toBeInTheDocument();
  });

  it("shows the flagged label only once the clock reaches zero", () => {
    renderClock({ seconds: 1 });
    expect(screen.queryByText("time!")).toBeNull();
  });

  it("shows the flagged label at zero", () => {
    renderClock({ seconds: 0 });
    expect(screen.getByText("time!")).toBeInTheDocument();
  });

  it("labels the side from the side prop", () => {
    renderClock({ side: "White" });
    expect(screen.getByText("White")).toBeInTheDocument();
  });

  it("labels the other side too", () => {
    renderClock({ side: "Black" });
    expect(screen.getByText("Black")).toBeInTheDocument();
  });

  it("keeps the same M:SS formatting on the compact variant", () => {
    renderClock({ variant: "compact", seconds: 65 });
    expect(screen.getByText("1:05")).toBeInTheDocument();
  });

  it("keeps tenths below 20 seconds on the compact variant", () => {
    renderClock({ variant: "compact", seconds: 3.96 });
    expect(getDigitRow("0:03.9")).toBeInTheDocument();
  });

  it("uses text-3xl digits on the compact variant and text-5xl on full", () => {
    const { unmount } = renderClock({ variant: "compact", seconds: 65 });
    expect(screen.getByText("1:05").parentElement?.className).toContain(
      "text-3xl",
    );
    expect(screen.getByText("1:05").parentElement?.className).not.toContain(
      "text-6xl",
    );
    unmount();
    renderClock({ seconds: 65 });
    expect(screen.getByText("1:05").parentElement?.className).toContain(
      "text-5xl",
    );
    expect(screen.getByText("1:05").parentElement?.className).not.toContain(
      "text-3xl",
    );
  });

  it("renders tenths as a smaller glyph than the M:SS", () => {
    renderClock({ seconds: 3.96 });
    const tenths = screen.getByText(".9");
    expect(tenths.tagName).toBe("SPAN");
    expect(tenths.className).toContain("text-2xl");
    expect(screen.getByText("0:03").parentElement?.className).toContain(
      "text-5xl",
    );
  });

  it("names the clock with spokenTime and hides the M:SS digits from assistive tech (P-5-5)", () => {
    renderClock({ seconds: 92 });
    expect(
      screen.getByRole("group", { name: "White, 1 minute 32 seconds" }),
    ).toBeInTheDocument();
    expect(screen.getByText("1:32").parentElement).toHaveAttribute(
      "aria-hidden",
      "true",
    );
  });

  it("folds the flagged label into the accessible name", () => {
    renderClock({ seconds: 0 });
    expect(
      screen.getByRole("group", { name: "White, 0 seconds, time!" }),
    ).toBeInTheDocument();
  });

  it("greys a frozen clock and suppresses Flagged (P-4-8)", () => {
    renderClock({ seconds: 0, active: true, frozen: true });
    const digits = getDigitRow("0:00.0");
    expect(digits).toHaveClass("text-muted");
    expect(digits).not.toHaveClass("text-[var(--loss)]");
    expect(screen.queryByText("time!")).toBeNull();
    expect(
      screen.getByRole("group", { name: "White, 0 seconds" }),
    ).toBeInTheDocument();
  });
});
