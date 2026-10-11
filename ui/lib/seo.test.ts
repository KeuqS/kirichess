import { describe, expect, it } from "vitest";
import { localizedUrl, pageAlternates, routeMetadata } from "./seo";
import { routing } from "../i18n/routing";

describe("localizedUrl", () => {
  it("leaves the default locale unprefixed", () => {
    expect(localizedUrl("en", "/rules")).toBe("http://localhost:3000/rules");
  });

  it("prefixes every other locale with its code", () => {
    expect(localizedUrl("nl", "/rules")).toBe(
      "http://localhost:3000/nl/rules",
    );
    expect(localizedUrl("pt-BR", "/rules")).toBe(
      "http://localhost:3000/pt-BR/rules",
    );
  });

  it("does not turn the home path into a trailing-slash locale prefix", () => {
    // "/" would otherwise become "/nl/", a different URL to the one the proxy serves. The
    // default locale's home URL carries no trailing slash either, matching sitemap.ts's own
    // `<loc>http://localhost:3000</loc>` (no trailing slash) entry.
    expect(localizedUrl("nl", "/")).toBe("http://localhost:3000/nl");
    expect(localizedUrl("en", "/")).toBe("http://localhost:3000");
  });
});

describe("pageAlternates", () => {
  it("sets canonical to the current locale's own URL", () => {
    expect(pageAlternates("nl", "/rules").canonical).toBe(
      "http://localhost:3000/nl/rules",
    );
    expect(pageAlternates("en", "/rules").canonical).toBe(
      "http://localhost:3000/rules",
    );
  });

  it("carries an x-default alongside every configured locale", () => {
    const { languages } = pageAlternates("en", "/rules");
    expect(languages["x-default"]).toBe("http://localhost:3000/rules");
    for (const locale of routing.locales) {
      expect(languages[locale]).toBe(localizedUrl(locale, "/rules"));
    }
    // x-default plus one entry per configured locale, no more and no less.
    expect(Object.keys(languages).sort()).toEqual(
      ["x-default", ...routing.locales].sort(),
    );
  });
});

describe("routeMetadata", () => {
  it("sets an explicit openGraph/twitter card rather than leaving it to inherit", () => {
    const metadata = routeMetadata({
      locale: "en",
      path: "/about",
      title: "About",
      description: "Who invented KiriChess.",
    });
    // M-1-1: Next merges `openGraph`/`twitter` shallowly per top-level key, so a page that omits
    // them inherits the parent's *whole* object. Setting them here, even to the page's own
    // title/description, is what stops that inheritance rather than what looks redundant.
    expect(metadata.openGraph).toMatchObject({
      title: "About",
      description: "Who invented KiriChess.",
      url: "http://localhost:3000/about",
      type: "website",
    });
    expect(metadata.twitter).toMatchObject({
      title: "About",
      description: "Who invented KiriChess.",
    });
  });

  it("sets a self-referencing canonical and full hreflang set", () => {
    const metadata = routeMetadata({
      locale: "nl",
      path: "/about",
      title: "Over",
      description: "Wie KiriChess heeft uitgevonden.",
    });
    expect(metadata.alternates?.canonical).toBe(
      "http://localhost:3000/nl/about",
    );
    expect(
      (metadata.alternates?.languages as Record<string, string>)["x-default"],
    ).toBe("http://localhost:3000/about");
  });
});
