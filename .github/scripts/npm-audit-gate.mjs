#!/usr/bin/env node
// Phase 9a.1: fails CI on a high/critical npm advisory unless it is explicitly allowlisted
// below with a reason and an expiry date. `npm audit` alone has no way to accept a single
// advisory, so this wraps it: everything not listed here must be fixed, and everything listed
// here is a decision, not an oversight.
//
// Run from ui/ (this is invoked as a step in the frontend CI job).

import { spawnSync } from "node:child_process";

// Package name -> { reason, expires: "YYYY-MM-DD" }. Re-run `npm audit fix` first, most
// advisories clear that way. Only add an entry here when the fix is a breaking upgrade that
// needs its own change, not a routine bump.
//
// next was 16.3.3. GHSA-vcvr-r3jv-pc5j (critical, next/og ImageResponse) is fixed in 16.3.6,
// so the package is pinned to 16.3.8 rather than waived. undici and brace-expansion cleared
// with `npm audit fix` in the same change.
const ALLOWLIST = {
  // GHSA-vfj7-8cjw-p6xm: braces stack-exhaustion DoS. Every published braces release is in
  // range, and the only `npm audit fix` path is `eslint-config-next@14.2.35`, a downgrade from
  // 15.5.21. The chain is eslint's file globbing (devDependency). It is not in the server image.
  // Re-decide when a braces release exists outside `<=3.0.3`, or when eslint-config-next stops
  // depending on it.
  braces: {
    reason: "GHSA-vfj7-8cjw-p6xm, no patched release; eslint toolchain only",
    expires: "2026-12-02",
  },
  micromatch: {
    reason: "high only via braces (GHSA-vfj7-8cjw-p6xm); eslint toolchain only",
    expires: "2026-12-02",
  },
  "fast-glob": {
    reason: "high only via braces (GHSA-vfj7-8cjw-p6xm); eslint toolchain only",
    expires: "2026-12-02",
  },
  "@next/eslint-plugin-next": {
    reason: "high only via braces (GHSA-vfj7-8cjw-p6xm); eslint toolchain only",
    expires: "2026-12-02",
  },
  "eslint-config-next": {
    reason: "high only via braces (GHSA-vfj7-8cjw-p6xm); eslint toolchain only",
    expires: "2026-12-02",
  },
};

const FAIL_SEVERITIES = new Set(["high", "critical"]);

// npm ships as npm.cmd on Windows, which Node can only launch through a shell. The command is a
// fixed literal (no interpolated input), so shell:true carries no injection risk here.
const result = spawnSync("npm audit --json", { encoding: "utf8", shell: true });
if (result.error) {
  console.error(result.error);
  process.exit(1);
}

let report;
try {
  report = JSON.parse(result.stdout);
} catch {
  console.error("Could not parse `npm audit --json` output:");
  console.error(result.stdout);
  console.error(result.stderr);
  process.exit(1);
}

const vulnerabilities = report.vulnerabilities ?? {};
const today = new Date().toISOString().slice(0, 10);

const failures = [];
const waived = [];

for (const [name, vuln] of Object.entries(vulnerabilities)) {
  if (!FAIL_SEVERITIES.has(vuln.severity)) continue;

  const entry = ALLOWLIST[name];
  if (entry && entry.expires >= today) {
    waived.push(`${name} (${vuln.severity}), waived: ${entry.reason}, expires ${entry.expires}`);
    continue;
  }
  if (entry) {
    failures.push(`${name} (${vuln.severity}), allowlist entry expired ${entry.expires}, must be re-decided`);
    continue;
  }
  failures.push(`${name} (${vuln.severity}), not allowlisted`);
}

if (waived.length > 0) {
  console.log("Waived (see .github/scripts/npm-audit-gate.mjs):");
  for (const line of waived) console.log(`  - ${line}`);
}

if (failures.length > 0) {
  console.error("\nUnwaived high/critical vulnerabilities:");
  for (const line of failures) console.error(`  - ${line}`);
  console.error("\nRun `npm audit` for details. Fix it, or add a dated, reasoned entry to");
  console.error("ALLOWLIST in .github/scripts/npm-audit-gate.mjs if the fix genuinely can't land yet.");
  process.exit(1);
}

console.log("\nNo unwaived high/critical npm vulnerabilities.");
