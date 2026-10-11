import react from "@vitejs/plugin-react";
import { fileURLToPath } from "node:url";
import { defineConfig } from "vitest/config";

const root = fileURLToPath(new URL(".", import.meta.url));

export default defineConfig({
  test: {
    // Reported, not gated (Phase 10b.3): a threshold on a young suite gets satisfied by testing
    // whatever is easiest, a number printed in the CI log gets read by whoever is deciding what to
    // test next, which is the actual purpose here.
    coverage: {
      provider: "v8",
      reporter: ["text", "text-summary"],
      include: ["lib/**/*.ts", "components/**/*.tsx"],
    },
    projects: [
      {
        // Unchanged from before 10b.3: lib/a11y.ts in particular is deliberately pure and
        // node-testable (see its own header comment), and this project must keep proving that.
        test: {
          name: "lib",
          environment: "node",
          include: ["lib/**/*.test.ts"],
        },
      },
      {
        // The React layer 10b.3 adds coverage for: components that hold logic rather than markup
        // (the announcer, the accessible board's roving tabindex, the clock display, cost-preview's
        // consumers), not the ~23 route files under app/ wholesale.
        plugins: [react()],
        resolve: {
          alias: { "@": root },
        },
        test: {
          name: "components",
          environment: "jsdom",
          include: [
            "components/**/*.test.tsx",
            // The OAuth callback is the one route whose behaviour is a counter, not markup.
            "app/**/auth/callback/page.test.tsx",
          ],
          setupFiles: ["./vitest.setup.ts"],
          server: {
            // 9f.4. next-intl's published ESM imports `next/navigation` without a file extension,
            // and `next` ships no `exports` map, so Node's ESM resolver (which Vitest uses for
            // externalised dependencies) cannot find it. Inlining hands next-intl to Vite's own
            // resolver, which does handle extensionless imports. Without this, every component
            // test touching `@/i18n/navigation` fails to import rather than failing an assertion.
            deps: { inline: ["next-intl"] },
          },
        },
      },
    ],
  },
});
