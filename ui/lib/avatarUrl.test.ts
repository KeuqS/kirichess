import { describe, expect, it } from "vitest";
import { resolveAvatarUrl } from "./avatarUrl";

describe("resolveAvatarUrl", () => {
  it("returns null for empty values", () => {
    expect(resolveAvatarUrl(null)).toBeNull();
    expect(resolveAvatarUrl(undefined)).toBeNull();
    expect(resolveAvatarUrl("")).toBeNull();
  });

  it("prefixes same-API paths with the API base", () => {
    expect(resolveAvatarUrl("/api/users/x/avatar?v=1")).toBe(
      "http://localhost:5053/api/users/x/avatar?v=1",
    );
  });

  it("leaves absolute URLs untouched", () => {
    const url = "https://cdn.example/avatar.jpg";
    expect(resolveAvatarUrl(url)).toBe(url);
  });

  it("leaves data and blob URLs untouched", () => {
    const data = "data:image/png;base64,abc";
    const blob = "blob:http://localhost:3000/11111111-1111-1111-1111-111111111111";
    expect(resolveAvatarUrl(data)).toBe(data);
    expect(resolveAvatarUrl(blob)).toBe(blob);
  });
});
