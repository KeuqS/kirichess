import { render } from "@testing-library/react";
import { NextIntlClientProvider } from "next-intl";
import { describe, expect, it, vi } from "vitest";
import { MoveList, type MoveListEntry } from "./MoveList";
import en from "../messages/en.json";

const moves: MoveListEntry[] = Array.from({ length: 12 }, (_, i) => ({
  moveNumber: i + 1,
  side: i % 2 === 0 ? "White" : "Black",
  uci: `a${(i % 8) + 1}a${((i + 1) % 8) + 1}`,
  whiteDelta: -1,
  blackDelta: 0,
}));

function renderList(props: {
  moves: MoveListEntry[];
  selectedPly?: number;
}) {
  return render(
    <NextIntlClientProvider locale="en" messages={en}>
      <MoveList
        moves={props.moves}
        selectedPly={props.selectedPly}
        onSelectPly={props.selectedPly != null ? () => {} : undefined}
      />
    </NextIntlClientProvider>,
  );
}

describe("MoveList", () => {
  it("pins an append-only list to the newest move", () => {
    const view = renderList({ moves: moves.slice(0, 2) });
    const list = view.container.querySelector(".overflow-y-auto") as HTMLDivElement;
    Object.defineProperty(list, "scrollHeight", {
      configurable: true,
      get: () => 480,
    });
    list.scrollTop = 0;

    view.rerender(
      <NextIntlClientProvider locale="en" messages={en}>
        <MoveList moves={moves} />
      </NextIntlClientProvider>,
    );

    expect(list.scrollTop).toBe(480);
  });

  it("scrolls the selected ply into view", () => {
    const scrollIntoView = vi.fn();
    HTMLElement.prototype.scrollIntoView = scrollIntoView;

    renderList({ moves, selectedPly: 12 });

    expect(scrollIntoView).toHaveBeenCalledWith({ block: "nearest" });
  });
});
