import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { BoardPageLayout } from "./BoardPageLayout";

function renderLayout(
  extra: { afterBoard?: boolean; headers?: boolean } = {},
) {
  return render(
    <BoardPageLayout
      opponentHeader={extra.headers ? <span>opp-header</span> : undefined}
      opponentClock={<span>opp-clock</span>}
      gapMeter={<span>gap</span>}
      localHeader={extra.headers ? <span>local-header</span> : undefined}
      localClock={<span>local-clock</span>}
      board={<span>board</span>}
      afterBoard={extra.afterBoard ? <span>after-board</span> : undefined}
      sidebar={<span>sidebar</span>}
    />,
  );
}

describe("BoardPageLayout", () => {
  it("keeps the lg three-column grid and orders the board between the clocks below lg", () => {
    const { container } = renderLayout();
    const grid = container.firstElementChild as HTMLElement;
    expect(grid.className).toContain("lg:grid-cols-[240px_1fr_320px]");
    expect(grid.className).toContain("grid-cols-1");

    expect(screen.getByText("opp-clock").parentElement).toHaveClass("order-1");
    expect(screen.getByText("board").parentElement).toHaveClass("order-2");
    expect(screen.getByText("local-clock").parentElement).toHaveClass(
      "order-3",
    );
    expect(screen.getByText("gap").parentElement).toHaveClass("order-5");
    expect(screen.getByText("sidebar").parentElement).toHaveClass("order-6");
  });

  it("places afterBoard after the local clock below lg", () => {
    renderLayout({ afterBoard: true });
    expect(screen.getByText("after-board").parentElement).toHaveClass(
      "order-4",
    );
  });

  it("keeps watch-page names with their clocks", () => {
    renderLayout({ headers: true });
    expect(screen.getByText("opp-header").parentElement).toHaveClass("order-1");
    expect(screen.getByText("local-header").parentElement).toHaveClass(
      "order-3",
    );
  });
});
