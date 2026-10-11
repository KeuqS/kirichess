import { defineConfig, globalIgnores } from "eslint/config";
import nextVitals from "eslint-config-next/core-web-vitals";
import nextTs from "eslint-config-next/typescript";

// eslint-config-next 16 exports flat config. FlatCompat (the 15.x path) crashes on that
// shape, which is why the bump was reverted. Import the flat configs directly.
const eslintConfig = defineConfig([
  // A config object containing ONLY `ignores` is a global ignore; combining it with `rules`
  // merely scopes those rules, leaving build output and vendored JS still linted.
  globalIgnores([
    "node_modules/**",
    ".next/**",
    "out/**",
    "build/**",
    "next-env.d.ts",
    "public/**", // vendored Stockfish wasm glue, not ours to lint
    "lib/api.generated.ts", // Phase 9d.7: generated from docs/api/openapi.json, see gen-api.mjs
  ]),
  ...nextVitals,
  ...nextTs,
  {
    rules: {
      "react/no-unescaped-entities": "off",
      "react-hooks/exhaustive-deps": "off",
      // eslint-plugin-react-hooks 7 (pulled in by eslint-config-next 16) turns the React
      // Compiler rules on inside `recommended`. These two flag patterns this app already
      // uses: setState at the start of a fetch effect, and writing a ref during render so
      // an event handler sees the latest callback. Clearing them rewrites the board, auth,
      // and account screens. That is a separate pass; the 15.x config did not have them.
      "react-hooks/set-state-in-effect": "off",
      "react-hooks/refs": "off",
    },
  },
  // P-8-5. Scoped to the three files that shipped hardcoded English through the i18n migration.
  // ignoreProps keeps className / aria attributes out of the rule; JSX children must go through
  // useTranslations. Staff surfaces (moderation, analytics) stay out of this list on purpose.
  {
    files: [
      "components/AccessibleBoard.tsx",
      "components/TwoFactorPrompt.tsx",
      "app/[locale]/error.tsx",
    ],
    rules: {
      "react/jsx-no-literals": [
        "error",
        { noStrings: true, ignoreProps: true, allowedStrings: [""] },
      ],
    },
  },
]);

export default eslintConfig;
