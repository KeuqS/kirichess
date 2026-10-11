import { render, screen } from "@testing-library/react";
import { NextIntlClientProvider } from "next-intl";
import type { ReactNode } from "react";
import { describe, expect, it, vi } from "vitest";
import { Footer } from "./Footer";
import en from "../messages/en.json";

vi.mock("@/i18n/navigation", () => ({
  Link: ({
    href,
    children,
    ...props
  }: {
    href: string;
    children: ReactNode;
  }) => (
    <a href={href} {...props}>
      {children}
    </a>
  ),
  usePathname: () => "/",
  useRouter: () => ({ replace: vi.fn(), push: vi.fn() }),
}));

describe("Footer", () => {
  it("links the legal row to the about licence grant", () => {
    render(
      <NextIntlClientProvider locale="en" messages={en}>
        <Footer />
      </NextIntlClientProvider>,
    );
    const licence = screen.getByRole("link", { name: en.Footer.licence });
    expect(licence).toHaveAttribute("href", "/about#licence");
  });
});
