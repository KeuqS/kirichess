import { render, screen } from "@testing-library/react";
import { NextIntlClientProvider } from "next-intl";
import type { ReactNode } from "react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { AlreadyInGameNotice } from "./AlreadyInGameNotice";
import { getCurrentGame } from "@/lib/api";

vi.mock("@/lib/api", () => ({
  getCurrentGame: vi.fn(),
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
}));

const messages = {
  play: {
    alreadyInGame: "You're already in a game, <link>go back to it</link>.",
    alreadyInGamePlain: "You're already in a game.",
  },
};

function renderNotice(message: string) {
  return render(
    <NextIntlClientProvider locale="en" messages={messages}>
      <AlreadyInGameNotice message={message} />
    </NextIntlClientProvider>,
  );
}

describe("AlreadyInGameNotice", () => {
  beforeEach(() => {
    vi.mocked(getCurrentGame).mockReset();
    localStorage.clear();
  });

  it("leaves any other refusal as the server sent it", () => {
    renderNotice("Your opponent is already in a game.");
    expect(
      screen.getByText("Your opponent is already in a game."),
    ).toBeInTheDocument();
    expect(getCurrentGame).not.toHaveBeenCalled();
  });

  it("links the seated player back to the game they left", async () => {
    vi.mocked(getCurrentGame).mockResolvedValue({
      gameId: "11111111-1111-4111-8111-111111111111",
      pool: "steal1",
      solo: false,
    });
    renderNotice("You're already in a game.");
    const link = await screen.findByRole("link", { name: "go back to it" });
    expect(link).toHaveAttribute(
      "href",
      "/game/11111111-1111-4111-8111-111111111111?preset=steal1",
    );
    expect(link).toHaveClass("underline");
  });
});
