import { render, screen } from "@testing-library/react";
import { NextIntlClientProvider } from "next-intl";
import { describe, expect, it } from "vitest";
import { AuthBrandHeading } from "./BrandMark";

describe("AuthBrandHeading", () => {
  it("links the wordmark home so chrome-less auth screens can leave", () => {
    // 9f.4. The wordmark links through `@/i18n/navigation`'s `Link`, which reads the active
    // locale from context; no messages are needed, but the provider is.
    render(
      <NextIntlClientProvider locale="en" messages={{}}>
        <AuthBrandHeading>KIRICHESS</AuthBrandHeading>
      </NextIntlClientProvider>,
    );
    const link = screen.getByRole("link", { name: "KIRICHESS" });
    expect(link).toHaveAttribute("href", "/");
    expect(screen.queryByRole("heading")).toBeNull();
  });
});
