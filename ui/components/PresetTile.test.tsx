import { fireEvent, render, screen } from "@testing-library/react";
import { NextIntlClientProvider } from "next-intl";
import type { ReactNode } from "react";
import { describe, expect, it, vi } from "vitest";
import en from "../messages/en.json";
import { PRESETS } from "../lib/presets";
import { PresetTile } from "./PresetTile";

/** Read rather than typed: config/kirichess.rules.json owns the name and PoolLabelDriftTests fails
 *  the build on a hardcoded one. This is the pool `renderTile` mounts. */
const POOL = PRESETS.bounty1.label;

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

function renderTile(offerBot: boolean, onPlayBot?: () => void) {
  return render(
    <NextIntlClientProvider locale="en" messages={en}>
      <PresetTile
        presetKey="bounty1"
        rating={1500}
        searching
        offerBot={offerBot}
        onToggle={() => undefined}
        onPlayBot={onPlayBot}
      />
    </NextIntlClientProvider>,
  );
}

describe("PresetTile", () => {
  it("hides the bot fallback until the grid says the queue has stalled", () => {
    renderTile(false, () => undefined);
    expect(
      screen.queryByRole("button", { name: `Play the bot in ${POOL}` }),
    ).toBeNull();
  });

  it("opens the bot path from the stalled-queue affordance without queuing again", () => {
    const onPlayBot = vi.fn();
    const onToggle = vi.fn();
    render(
      <NextIntlClientProvider locale="en" messages={en}>
        <PresetTile
          presetKey="bounty1"
          rating={1500}
          searching
          offerBot
          onToggle={onToggle}
          onPlayBot={onPlayBot}
        />
      </NextIntlClientProvider>,
    );
    fireEvent.click(
      screen.getByRole("button", { name: `Play the bot in ${POOL}` }),
    );
    expect(onPlayBot).toHaveBeenCalledTimes(1);
    expect(onToggle).not.toHaveBeenCalled();
  });
});
