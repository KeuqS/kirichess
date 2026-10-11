import { existsSync, readFileSync, readdirSync, statSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { gzipSync } from "node:zlib";

/**
 * P-7-5. Runs after `next build`. Fails if the 404/403 graph still pulls react-chessboard, or if
 * a hashed client chunk is so large it would wreck a volunteer's phone.
 *
 * The gzip ceiling is a tripwire for a catastrophic import, not a tuned First Load JS budget.
 * Tighten it once a real Sentry DSN or another heavy client SDK is actually on.
 */
const uiRoot = join(dirname(fileURLToPath(import.meta.url)), "..");
const nextDir = join(uiRoot, ".next");

const MAX_CHUNK_GZIP_BYTES = 800_000;

function walk(dir) {
  const out = [];
  if (!existsSync(dir)) return out;
  for (const entry of readdirSync(dir, { withFileTypes: true })) {
    const path = join(dir, entry.name);
    if (entry.isDirectory()) out.push(...walk(path));
    else out.push(path);
  }
  return out;
}

if (!existsSync(nextDir)) {
  console.error("assert-bundle-budget: .next is missing; run next build first");
  process.exit(1);
}

const serverApp = walk(join(nextDir, "server"));
const errorRoutes = serverApp.filter((path) =>
  /not-found|forbidden/i.test(path.replace(/\\/g, "/")),
);
for (const file of errorRoutes) {
  if (!/\.(js|json|rsc)$/.test(file)) continue;
  const body = readFileSync(file, "utf8");
  if (body.includes("react-chessboard") || body.includes("react-dnd")) {
    console.error(
      `P-7-1: ${file} still mentions react-chessboard or react-dnd`,
    );
    process.exit(1);
  }
}

const chunksDir = join(nextDir, "static", "chunks");
const oversized = [];
for (const file of walk(chunksDir)) {
  if (!file.endsWith(".js")) continue;
  const gzip = gzipSync(readFileSync(file)).byteLength;
  if (gzip > MAX_CHUNK_GZIP_BYTES) {
    oversized.push({ file, gzip, raw: statSync(file).size });
  }
}
if (oversized.length > 0) {
  console.error(
    `P-7-5: client chunk(s) over the ${MAX_CHUNK_GZIP_BYTES} gzip-byte tripwire:`,
  );
  for (const row of oversized) {
    console.error(`  gzip ${row.gzip} raw ${row.raw} ${row.file}`);
  }
  process.exit(1);
}

console.log(
  `assert-bundle-budget: ok (${errorRoutes.length} error-route files scanned, no gzip chunk over ${MAX_CHUNK_GZIP_BYTES} bytes)`,
);
