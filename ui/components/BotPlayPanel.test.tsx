import { fireEvent, render, screen } from "@testing-library/react";
import { NextIntlClientProvider } from "next-intl";
import type { ReactNode } from "react";
import { describe, expect, it, vi } from "vitest";
import en from "../messages/en.json";
import { PRESETS } from "../lib/presets";
import { BotPlayPanel } from "./BotPlayPanel";

const useAuth = vi.fn();
const push = vi.fn();
const pathname = vi.fn(() => "/");

/** Read rather than typed: config/kirichess.rules.json owns the name and PoolLabelDriftTests fails
 *  the build on a hardcoded one. `bounty1` is the pool this panel offers. */
const POOL = PRESETS.bounty1.label;

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
  useRouter: () => ({ push }),
}));

vi.mock("@/components/BotGameDialog", () => ({
  BotGameDialog: ({ presetKey }: { presetKey: string }) => (
    <div role="dialog">bot:{presetKey}</div>
  ),
}));

function renderPanel() {
  return render(
    <NextIntlClientProvider locale="en" messages={en}>
      <BotPlayPanel />
    </NextIntlClientProvider>,
  );
}

describe("BotPlayPanel", () => {
  it("labels the bot path and keeps next on the login bounce", () => {
    useAuth.mockReturnValue({ token: null, hydrated: true });
    renderPanel();
    expect(screen.getByText("Sign in to play the bot")).toBeInTheDocument();
    fireEvent.click(
      screen.getByRole("button", {
        name: `Sign in to play ${POOL} against a bot`,
      }),
    );
    expect(push).toHaveBeenCalledWith("/login?next=%2F");
    expect(screen.queryByRole("dialog")).toBeNull();
  });

  it("opens the strength dialog for a signed-in visitor", () => {
    useAuth.mockReturnValue({ token: "session", hydrated: true });
    renderPanel();
    fireEvent.click(screen.getByRole("button", { name: POOL }));
    expect(screen.getByRole("dialog")).toHaveTextContent("bot:bounty1");
  });
});
