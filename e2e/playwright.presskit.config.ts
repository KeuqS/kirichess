import { defineConfig, devices } from "@playwright/test";

/**
 * AD.1 (docs/ROADMAP.md): a separate config from playwright.config.ts on purpose. The main suite
 * is a CI gate (one worker, retries: 0, video only on failure) and this is an artefact generator
 * (video always on, a single scripted spec, never run in CI). Reuses the same global-setup - it
 * still needs a real stack answering on localhost, started the same way (`npm run stack:up`).
 */
export default defineConfig({
  testDir: "./press-kit",
  fullyParallel: false,
  workers: 1,
  retries: 0,
  forbidOnly: false,
  timeout: 120_000,
  expect: { timeout: 10_000 },

  globalSetup: require.resolve("./fixtures/global-setup"),

  reporter: [["list"]],

  use: {
    baseURL: "http://localhost:3000",
    actionTimeout: 15_000,
  },

  projects: [{ name: "chromium", use: { ...devices["Desktop Chrome"] } }],
});
