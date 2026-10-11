import { render, screen } from "@testing-library/react";
import { NextIntlClientProvider } from "next-intl";
import { describe, expect, it } from "vitest";
import en from "../messages/en.json";
import { DEFAULT_RULES } from "../lib/rules.generated";
import { timeControlLabel, multiplierLabel } from "../lib/presets";
import type { EconomyConfig } from "../lib/types";
import { RuleSummary } from "./RuleSummary";

function economy(overrides: Partial<EconomyConfig> = {}): EconomyConfig {
  return { ...DEFAULT_RULES, ...overrides };
}

function renderSummary(value: EconomyConfig, showTime = false) {
  return render(
    <NextIntlClientProvider locale="en" messages={en}>
      <RuleSummary economy={value} showTime={showTime} />
    </NextIntlClientProvider>,
  );
}

describe("timeControlLabel", () => {
  it("reads a whole-minute clock the way a pool tile does", () => {
    expect(timeControlLabel(180, 0)).toBe("3+0");
  });

  it("keeps a non-minute clock in seconds", () => {
    expect(timeControlLabel(123, 4)).toBe("123s+4");
  });
});

describe("multiplierLabel", () => {
  it("names capture-only scaling against the steal1 base", () => {
    expect(multiplierLabel(DEFAULT_RULES.captureMult, 1)).toBe("C1");
    expect(multiplierLabel(DEFAULT_RULES.captureMult * 2, 1)).toBe("C2");
  });

  it("names full-economy scaling as F", () => {
    expect(multiplierLabel(DEFAULT_RULES.captureMult, 2)).toBe("F2");
  });
});

describe("RuleSummary", () => {
  it("states the capture economy, the move cost, and the four end conditions", () => {
    renderSummary(economy());
    expect(screen.getByText("STEAL")).toBeInTheDocument();
    expect(screen.getByText("C1")).toBeInTheDocument();
    expect(screen.getByText("Per piece")).toBeInTheDocument();
    expect(screen.getByText("Repeated position nullifies time")).toBeInTheDocument();
    expect(screen.getAllByText("On").length).toBeGreaterThanOrEqual(4);
    expect(screen.queryByText("Time control")).toBeNull();
  });

  it("does not call a clock with no capture time a burn economy", () => {
    renderSummary(
      economy({
        captureCaptorGainEnabled: false,
        captureVictimLossEnabled: false,
        moveCostMode: "Flat",
        moveCostFlat: 2,
        economyScale: 2,
        flagFallLoses: false,
      }),
    );
    expect(screen.getByText("No capture time")).toBeInTheDocument();
    expect(screen.queryByText("BURN")).toBeNull();
    expect(screen.getByText("Flat, 4s")).toBeInTheDocument();
    expect(screen.getByText("Off")).toBeInTheDocument();
  });

  it("states the clock when the page has no time of its own", () => {
    renderSummary(economy({ startTimeSeconds: 123, baseIncrement: 4 }), true);
    expect(screen.getByText("Time control")).toBeInTheDocument();
    expect(screen.getByText("123s+4")).toBeInTheDocument();
  });
});
