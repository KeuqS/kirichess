import { act, render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { useAnnouncer, type Announcer } from "./LiveAnnouncer";

/**
 * Phase 10b.3: the first jsdom-project test in this repo. useAnnouncer (Phase 9f.2) is the
 * component most worth pinning here - a screen-reader user gets no second channel to notice a
 * regression the way a sighted user notices a rendering glitch.
 */
function Harness({ capture }: { capture: (announcer: Announcer) => void }) {
  const announcer = useAnnouncer();
  capture(announcer);
  return <>{announcer.node}</>;
}

function renderHarness() {
  let announcer!: Announcer;
  render(<Harness capture={(a) => (announcer = a)} />);
  return () => announcer;
}

describe("useAnnouncer", () => {
  it("mounts both live regions empty", () => {
    renderHarness();
    expect(screen.getByRole("status")).toHaveTextContent("");
    expect(screen.getByRole("alert")).toHaveTextContent("");
  });

  it("polite() writes into the polite (status) region only", () => {
    const getAnnouncer = renderHarness();
    act(() => getAnnouncer().polite("White plays e4."));

    expect(screen.getByRole("status")).toHaveTextContent("White plays e4.");
    expect(screen.getByRole("alert")).toHaveTextContent("");
  });

  it("assertive() writes into the assertive (alert) region only", () => {
    const getAnnouncer = renderHarness();
    act(() => getAnnouncer().assertive("Check."));

    expect(screen.getByRole("alert")).toHaveTextContent("Check.");
    expect(screen.getByRole("status")).toHaveTextContent("");
  });

  it("changes the rendered text node on a repeated identical announcement", () => {
    // React only touches the DOM when the text actually changes; the clock can cross the same
    // urgency threshold more than once in a game (see the module's own doc comment on `flip`), and
    // an unrepeated announcement here would go unspoken the second time.
    const getAnnouncer = renderHarness();

    act(() => getAnnouncer().polite("10 seconds."));
    const first = screen.getByRole("status").textContent;

    act(() => getAnnouncer().polite("10 seconds."));
    const second = screen.getByRole("status").textContent;

    expect(first).not.toBe(second);
    expect(first?.replace(/​/g, "")).toBe("10 seconds.");
    expect(second?.replace(/​/g, "")).toBe("10 seconds.");
  });
});
