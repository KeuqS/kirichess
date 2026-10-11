import { fireEvent, render, screen } from "@testing-library/react";
import { NextIntlClientProvider } from "next-intl";
import { describe, expect, it } from "vitest";
import { ClockGraph, nearestClockPointIndex } from "./ClockGraph";
import en from "../messages/en.json";

describe("nearestClockPointIndex", () => {
  it("maps the left pad to the first point", () => {
    expect(nearestClockPointIndex(8, 5)).toBe(0);
  });

  it("maps the right edge to the last point", () => {
    expect(nearestClockPointIndex(640 - 8, 5)).toBe(4);
  });

  it("clamps past either end", () => {
    expect(nearestClockPointIndex(0, 5)).toBe(0);
    expect(nearestClockPointIndex(999, 5)).toBe(4);
  });

  it("returns 0 for a single point", () => {
    expect(nearestClockPointIndex(200, 1)).toBe(0);
  });
});

describe("ClockGraph", () => {
  it("renders axis tick numbers", () => {
    render(
      <NextIntlClientProvider locale="en" messages={en}>
        <ClockGraph
          history={[
            { moveNumber: 0, whiteTime: 180, blackTime: 180 },
            { moveNumber: 1, whiteTime: 170, blackTime: 175 },
          ]}
          startTimeSeconds={180}
        />
      </NextIntlClientProvider>,
    );
    expect(screen.getByText("180")).toBeInTheDocument();
    expect(screen.getAllByText("0").length).toBeGreaterThan(0);
  });

  it("shows a hover tooltip for the nearest ply", () => {
    const { container } = render(
      <NextIntlClientProvider locale="en" messages={en}>
        <ClockGraph
          history={[
            { moveNumber: 0, whiteTime: 180, blackTime: 180 },
            { moveNumber: 1, whiteTime: 170, blackTime: 175 },
          ]}
          startTimeSeconds={180}
        />
      </NextIntlClientProvider>,
    );
    const svg = container.querySelector("svg")!;
    svg.getBoundingClientRect = () =>
      ({
        x: 0,
        y: 0,
        top: 0,
        left: 0,
        bottom: 280,
        right: 640,
        width: 640,
        height: 280,
        toJSON: () => {},
      }) as DOMRect;
    fireEvent.pointerMove(svg, { clientX: 48, clientY: 40 });
    expect(
      screen.getByText("Move 0: White 180s, Black 180s"),
    ).toBeInTheDocument();
  });
});
