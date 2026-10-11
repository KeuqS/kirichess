import { defineConfig, devices } from "@playwright/test";

export default defineConfig({
  testDir: "./tests",
  // One worker, no parallelism, no retries. Two of these specs create real accounts against one
  // shared Postgres and one shared IP-partitioned rate limiter, and, per Phase 9b.2/
  // README.md decision 1, a flaky run must go RED rather than be retried into a green that trains
  // everyone to ignore this job.
  fullyParallel: false,
  workers: 1,
  retries: 0,
  forbidOnly: !!process.env.CI,

  // A rated bounty1 game is 60 s of real clock, plus register/confirm/login for two accounts and
  // several page loads. Three minutes is headroom, not a target.
  timeout: 180_000,
  expect: { timeout: 10_000 },

  globalSetup: require.resolve("./fixtures/global-setup"),

  reporter: process.env.CI
    ? [["list"], ["html", { open: "never" }]]
    : [["list"]],

  use: {
    baseURL: "http://localhost:3000",
    trace: "retain-on-failure",
    video: "retain-on-failure",
    screenshot: "only-on-failure",
    actionTimeout: 15_000,
  },

  projects: [{ name: "chromium", use: { ...devices["Desktop Chrome"] } }],
});
