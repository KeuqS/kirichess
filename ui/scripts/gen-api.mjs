#!/usr/bin/env node
/**
 * Generates the TypeScript client types from the committed OpenAPI document:
 *
 *   docs/api/openapi.json   ->   ui/lib/api.generated.ts
 *
 * docs/api/openapi.json is itself generated from the C# endpoints (Task A of Phase 9d.7); this
 * script is Task B's half of the pipeline. The server side is kept honest by
 * OpenApiContractTests; this side is kept honest by `--check` below, run in the frontend CI job.
 *
 *   node scripts/gen-api.mjs            write the output
 *   node scripts/gen-api.mjs --check    exit 1 if the output is stale (CI)
 */

import { readFileSync, writeFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join, relative } from "node:path";
import openapiTS, { astToString } from "openapi-typescript";

const HERE = dirname(fileURLToPath(import.meta.url));
const REPO = join(HERE, "..", "..");
const SOURCE = join(REPO, "docs", "api", "openapi.json");
const TS_OUT = join(REPO, "ui", "lib", "api.generated.ts");

const BANNER = `// GENERATED FILE, DO NOT EDIT.
//
// Source of truth: docs/api/openapi.json, itself generated from the C# endpoints by
//                  \`dotnet run --project src/KiriChess.Server -- --export-openapi docs/api/openapi.json\`
// Regenerate:      npm run gen:api   (npm run gen:api:check verifies it in CI)
//
// Every type below is the shape the server actually serializes. Change the C# DTO, re-export the
// document, and regenerate: never edit this file, and never hand-write a mirror of it in types.ts.

`;

const schema = JSON.parse(readFileSync(SOURCE, "utf8"));
const ast = await openapiTS(schema);
const body = astToString(ast);

const content = BANNER + body;

// ---------------------------------------------------------------------------------------------
// Write / check
// ---------------------------------------------------------------------------------------------

const check = process.argv.includes("--check");
let stale = false;

const rel = relative(REPO, TS_OUT).replace(/\\/g, "/");
let current = null;
try {
  current = readFileSync(TS_OUT, "utf8");
} catch {
  /* not generated yet */
}
const normalize = (s) => s.replace(/\r\n/g, "\n");

if (current !== null && normalize(current) === normalize(content)) {
  if (check) console.log(`  ok    ${rel}`);
} else if (check) {
  stale = true;
  console.error(`  STALE ${rel}`);
} else {
  writeFileSync(TS_OUT, content, "utf8");
  console.log(`  wrote ${rel}`);
}

if (check && stale) {
  console.error(
    "\nui/lib/api.generated.ts is out of date with docs/api/openapi.json.\n" +
      "Run `npm --prefix ui run gen:api` and commit the result.",
  );
  process.exit(1);
}
