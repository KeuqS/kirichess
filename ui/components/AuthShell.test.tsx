import { render, screen } from "@testing-library/react";
import { NextIntlClientProvider } from "next-intl";
import { describe, expect, it } from "vitest";
import { AuthShell } from "./AuthShell";

describe("AuthShell", () => {
  it("names the page with an h1 and keeps the wordmark as a home link", () => {
    render(
      <NextIntlClientProvider locale="en" messages={{}}>
        <AuthShell brand="KIRICHESS" heading="Log in">
          <p>form</p>
        </AuthShell>
      </NextIntlClientProvider>,
    );
    expect(screen.getByRole("heading", { level: 1 })).toHaveTextContent(
      "Log in",
    );
    expect(screen.getByRole("link", { name: "KIRICHESS" })).toHaveAttribute(
      "href",
      "/",
    );
    expect(screen.getByRole("main")).toHaveAttribute("id", "main-content");
  });

  it("keeps the h1 for assistive tech when the screen already names itself", () => {
    render(
      <NextIntlClientProvider locale="en" messages={{}}>
        <AuthShell brand="KIRICHESS" heading="Log in" srOnlyHeading>
          <p>form</p>
        </AuthShell>
      </NextIntlClientProvider>,
    );
    const heading = screen.getByRole("heading", { level: 1 });
    expect(heading).toHaveTextContent("Log in");
    expect(heading).toHaveClass("sr-only");
  });
});
