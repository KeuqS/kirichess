import { describe, expect, it } from "vitest";
import {
  challengeDestination,
  loginNextHref,
  safeNextPath,
} from "./challengeLink";

describe("safeNextPath", () => {
  it("accepts a same-origin relative path", () => {
    expect(safeNextPath("/challenge/abc")).toBe("/challenge/abc");
  });

  it("accepts the root path", () => {
    expect(safeNextPath("/")).toBe("/");
  });

  it("rejects an absolute URL", () => {
    expect(safeNextPath("https://evil.example")).toBe("/");
  });

  it("rejects a protocol-relative URL", () => {
    expect(safeNextPath("//evil.example")).toBe("/");
  });

  it("rejects a javascript: scheme", () => {
    expect(safeNextPath("javascript:alert(1)")).toBe("/");
  });

  // The WHATWG URL parser treats a backslash like a slash right after the leading "/", so this
  // resolves to the origin "evil.example", the classic guard bypass a bare "//" check misses.
  it("rejects a backslash used as a second slash", () => {
    expect(safeNextPath("/\\evil.example")).toBe("/");
    expect(safeNextPath("/\\/evil.example")).toBe("/");
  });

  // Browsers strip tab/CR/LF from a URL before parsing it, so this arrives as "//evil.example".
  it("rejects a protocol-relative URL smuggled past the check with control characters", () => {
    expect(safeNextPath("/\t/evil.example")).toBe("/");
    expect(safeNextPath("/\n/evil.example")).toBe("/");
  });

  it("strips control characters from an otherwise safe path", () => {
    expect(safeNextPath("/chall\tenge/abc")).toBe("/challenge/abc");
  });

  it("falls back to / when there is nothing to read", () => {
    expect(safeNextPath(null)).toBe("/");
  });
});

describe("loginNextHref", () => {
  it("encodes a same-origin path as the login next parameter", () => {
    expect(loginNextHref("/watch")).toBe("/login?next=%2Fwatch");
    expect(loginNextHref("/")).toBe("/login?next=%2F");
  });

  it("runs the destination through safeNextPath first", () => {
    expect(loginNextHref("https://evil.example")).toBe("/login?next=%2F");
    expect(loginNextHref("//evil.example")).toBe("/login?next=%2F");
  });
});

describe("challengeDestination", () => {
  it("appends accept=1 to a bare challenge path", () => {
    expect(challengeDestination("/challenge/abc")).toBe(
      "/challenge/abc?accept=1",
    );
  });

  it("uses & when the challenge path already carries a query string", () => {
    expect(challengeDestination("/challenge/abc?ref=discord")).toBe(
      "/challenge/abc?ref=discord&accept=1",
    );
  });

  it("leaves non-challenge paths alone", () => {
    expect(challengeDestination("/")).toBe("/");
    expect(challengeDestination("/profile")).toBe("/profile");
  });
});
