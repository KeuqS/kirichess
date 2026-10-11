import { fireEvent, render, screen } from "@testing-library/react";
import { NextIntlClientProvider } from "next-intl";
import type { ReactNode } from "react";
import { describe, expect, it, vi } from "vitest";
import en from "../messages/en.json";
import { PRESETS } from "../lib/presets";
import { PlayButton } from "./PlayButton";

/** Read rather than typed: config/kirichess.rules.json owns the name, and PoolLabelDriftTests
 *  fails the build on a hardcoded one. The button's accessible name is this label, so a rebrand
 *  should move the assertion with it. */
const ANCHOR = PRESETS.steal1.label;

const useAuth = vi.fn();
const pathname = vi.fn(() => "/watch");

vi.mock("@/components/AuthProvider", () => ({
  useAuth: () => useAuth(),
}));

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
  usePathname: () => pathname(),
  useRouter: () => ({ push: vi.fn() }),
}));

vi.mock("@/components/BotGameDialog", () => ({
  BotGameDialog: ({
    presetKey,
    onClose,
  }: {
    presetKey: string;
    onClose: () => void;
  }) => (
    <div role="dialog">
      bot:{presetKey}
      <button type="button" onClick={onClose}>
        close
      </button>
    </div>
  ),
}));

function renderPlay() {
  return render(
    <NextIntlClientProvider locale="en" messages={en}>
      <PlayButton preset="steal1" className="btn-primary">
        Play {ANCHOR}
      </PlayButton>
    </NextIntlClientProvider>,
  );
}

describe("PlayButton", () => {
  it("sends anonymous visitors to login with the current path as next", () => {
    useAuth.mockReturnValue({ token: null, hydrated: true });
    renderPlay();
    const link = screen.getByRole("link", { name: "Sign in to play" });
    expect(link).toHaveAttribute("href", "/login?next=%2Fwatch");
    expect(screen.queryByRole("dialog")).toBeNull();
  });

  it("opens BotGameDialog for a signed-in visitor instead of a hot-seat board", () => {
    useAuth.mockReturnValue({ token: "session", hydrated: true });
    renderPlay();
    fireEvent.click(screen.getByRole("button", { name: `Play ${ANCHOR}` }));
    expect(screen.getByRole("dialog")).toHaveTextContent("bot:steal1");
  });
});
