// Loaded only by the jsdom project (vitest.config.mts): unmounts every component after each test
// (globals: true is deliberately off - see costPreview.test.ts's explicit vitest imports - so
// Testing Library's own auto-cleanup, which relies on a global afterEach, never fires without this)
// and extends `expect` with the DOM matchers (toHaveTextContent, etc.) the component tests use.
import { cleanup } from "@testing-library/react";
import { afterEach } from "vitest";
import "@testing-library/jest-dom/vitest";

afterEach(() => {
  cleanup();
});
