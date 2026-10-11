import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { TimeDeltaLayer } from "./TimeDeltaLayer";

describe("TimeDeltaLayer", () => {
  it("renders the popup label at full strength, not opacity-70", () => {
    render(<TimeDeltaLayer popups={[{ id: "1", amount: 5, label: "steal" }]} />);
    const label = screen.getByText("steal");
    expect(label).toHaveClass("text-sm");
    expect(label.className).not.toMatch(/opacity-70/);
  });
});
