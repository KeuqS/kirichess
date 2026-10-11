#!/usr/bin/env node
/**
 * Phase 9f.4. Drafts a message catalogue for one locale from ui/messages/en.json.
 *
 *   node scripts/gen-locale.mjs nl --dry-run   report what would be sent, call nothing
 *   node scripts/gen-locale.mjs es             create ui/messages/es.json (refuses to overwrite)
 *   node scripts/gen-locale.mjs nl --fill      translate ONLY keys missing from an existing file
 *   node scripts/gen-locale.mjs nl --force     regenerate from scratch, discarding what is there
 *
 * WHAT THIS DOES NOT DO
 *
 * It does not produce a shippable locale. ROADMAP 9f.4's rule is that a locale ships when a human
 * who plays the variant has read it, and nothing here changes that: the output is a draft for that
 * person to correct, which is a better starting point than 989 blank strings and a worse one than a
 * translation. Adding a locale to i18n/routing.ts is a separate, deliberate act.
 *
 * WHY --fill IS THE MODE THAT MATTERS
 *
 * ui/messages/en.json moved on eight commits in the six days before this was written. The one-off
 * cost of a new locale is small; the recurring cost of keeping five of them in step with a
 * catalogue that churns is not, and it is what makes locales expensive to own. `--fill` translates
 * only what is new, leaves every reviewed string untouched, and drops keys English no longer has,
 * so a human's corrections survive regeneration. Default and `--force` both clobber, which is why
 * the default refuses when the file already exists.
 *
 * HOW THE ICU SYNTAX SURVIVES
 *
 * 133 of the 989 strings carry something a translation engine will happily destroy: 106 simple
 * placeholders, 22 rich-text tags, and 5 ICU plural blocks. The plural blocks are the dangerous
 * ones, because `one` and `other` are ICU keywords that look like ordinary English words and `#`
 * looks like punctuation. So the message is taken apart before translation rather than sent whole:
 *
 *   - `{name}` and `<tag>` become opaque sentinels, translated around, restored afterwards.
 *   - `{count, plural, one {...} other {...}}` keeps its header and branch keywords verbatim; each
 *     branch body is translated on its own, because the branches are independent sentences and
 *     sending the block as one blob produces one.
 *   - Terms in i18n/glossary.json's `doNotTranslate` are protected the same way as placeholders,
 *     which is what stops `Steal`, `Burn` and `Bounty` coming back as three ordinary verbs.
 *
 * Nothing is trusted afterwards: every output string is checked against its English source for
 * placeholder and tag parity, and lib/locales.test.ts re-checks the whole catalogue in CI.
 *
 * Dependency-free on purpose, like the other generators here: node builtins only.
 */

import { readFileSync, writeFileSync, existsSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join, relative } from "node:path";

const HERE = dirname(fileURLToPath(import.meta.url));
const UI = join(HERE, "..");
const REPO = join(UI, "..");
const MESSAGES = join(UI, "messages");
const SOURCE = join(MESSAGES, "en.json");
const GLOSSARY = join(UI, "i18n", "glossary.json");

// ---------------------------------------------------------------------------------------------
// Arguments
// ---------------------------------------------------------------------------------------------

const args = process.argv.slice(2);
const flags = new Set(args.filter((a) => a.startsWith("--")));
const locale = args.find((a) => !a.startsWith("--"));

const DRY_RUN = flags.has("--dry-run");
const FILL = flags.has("--fill");
const FORCE = flags.has("--force");

if (!locale) {
  console.error(
    "usage: node scripts/gen-locale.mjs <locale> [--dry-run|--fill|--force]\n" +
      "       locale is a BCP-47 code, e.g. nl, es, fr, de, pt-BR",
  );
  process.exit(1);
}
if (FILL && FORCE) {
  console.error("--fill and --force are mutually exclusive.");
  process.exit(1);
}

const OUT = join(MESSAGES, `${locale}.json`);
const rel = (p) => relative(REPO, p).replace(/\\/g, "/");

// ---------------------------------------------------------------------------------------------
// Message deconstruction
//
// Lives in its own module so lib/icuProtect.test.ts can prove the round-trip property: no message
// may lose a placeholder, a rich-text tag or a plural branch on the way through.
// ---------------------------------------------------------------------------------------------

import { deconstruct, reconstruct, structuralTokens } from "./icu-protect.mjs";

// ---------------------------------------------------------------------------------------------
// Catalogue walking
// ---------------------------------------------------------------------------------------------

function flatten(obj, prefix = "", out = {}) {
  for (const [k, v] of Object.entries(obj)) {
    const key = prefix ? `${prefix}.${k}` : k;
    if (typeof v === "string") out[key] = v;
    else flatten(v, key, out);
  }
  return out;
}

/** Rebuilds nested shape in en.json's key order, so the two files diff cleanly side by side. */
function nest(english, flat) {
  const out = {};
  const walk = (src, dst, prefix) => {
    for (const [k, v] of Object.entries(src)) {
      const key = prefix ? `${prefix}.${k}` : k;
      if (typeof v === "string") {
        if (flat[key] !== undefined) dst[k] = flat[key];
      } else {
        dst[k] = {};
        walk(v, dst[k], key);
        if (Object.keys(dst[k]).length === 0) delete dst[k];
      }
    }
  };
  walk(english, out, "");
  return out;
}

// ---------------------------------------------------------------------------------------------
// DeepL
// ---------------------------------------------------------------------------------------------

function apiBase(key) {
  // DeepL marks free-tier keys with a ":fx" suffix and serves them from a different host.
  return key.endsWith(":fx")
    ? "https://api-free.deepl.com"
    : "https://api.deepl.com";
}

async function deepl(path, key, body, method = "POST") {
  const res = await fetch(`${apiBase(key)}${path}`, {
    method,
    headers: {
      Authorization: `DeepL-Auth-Key ${key}`,
      "Content-Type": "application/json",
    },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  if (!res.ok) {
    throw new Error(
      `DeepL ${method} ${path} failed: ${res.status} ${await res.text()}`,
    );
  }
  return res.status === 204 ? null : res.json();
}

/**
 * Best-effort: a glossary is a hint, and DeepL supports them only for some pairs. A failure here
 * degrades the draft, it does not invalidate it, so it warns rather than aborting.
 */
async function createGlossary(key, target, entries) {
  if (Object.keys(entries).length === 0) return null;
  const tsv = Object.entries(entries)
    .map(([from, to]) => `${from}\t${to}`)
    .join("\n");
  try {
    const g = await deepl("/v2/glossaries", key, {
      name: `kirichess-${target}-${Date.now()}`,
      source_lang: "en",
      target_lang: target.split("-")[0].toLowerCase(),
      entries: tsv,
      entries_format: "tsv",
    });
    return g.glossary_id;
  } catch (e) {
    console.warn(`  warn  glossary not applied (${e.message.split("\n")[0]})`);
    return null;
  }
}

async function translateBatch(texts, key, target, glossaryId) {
  const body = {
    text: texts,
    source_lang: "EN",
    target_lang: target.toUpperCase(),
    tag_handling: "xml",
    ignore_tags: ["x"],
    preserve_formatting: true,
  };
  if (glossaryId) body.glossary_id = glossaryId;
  const json = await deepl("/v2/translate", key, body);
  return json.translations.map((t) => t.text);
}

// ---------------------------------------------------------------------------------------------
// Run
// ---------------------------------------------------------------------------------------------

const english = JSON.parse(readFileSync(SOURCE, "utf8"));
const glossaryDoc = JSON.parse(readFileSync(GLOSSARY, "utf8"));

/** Key patterns copied from English rather than translated. Supports a leading/trailing "*". */
const verbatimKeyPatterns = glossaryDoc.doNotTranslate.keys ?? [];
function isVerbatimKey(key) {
  return verbatimKeyPatterns.some((pattern) => {
    if (pattern.startsWith("*")) return key.endsWith(pattern.slice(1));
    if (pattern.endsWith("*")) return key.startsWith(pattern.slice(0, -1));
    return key === pattern;
  });
}

const protectedTerms = [
  ...glossaryDoc.doNotTranslate.identity,
  ...glossaryDoc.doNotTranslate.interface,
  ...glossaryDoc.doNotTranslate.proper,
].sort((a, b) => b.length - a.length);

const preferred = glossaryDoc.preferred[locale] ?? {};
if (Object.keys(preferred).length === 0 && !DRY_RUN) {
  console.warn(
    `  warn  i18n/glossary.json has no preferred terms for "${locale}".\n` +
      `        The draft will use the engine's own chess vocabulary, which is the single\n` +
      `        biggest source of error here. Consider filling it in first.`,
  );
}

const flatEnglish = flatten(english);
let existing = {};
if (existsSync(OUT)) {
  if (!FILL && !FORCE && !DRY_RUN) {
    console.error(
      `${rel(OUT)} already exists.\n` +
        `Use --fill to translate only the keys it is missing (keeps reviewed strings),\n` +
        `or --force to regenerate it from scratch (discards them).`,
    );
    process.exit(1);
  }
  existing = flatten(JSON.parse(readFileSync(OUT, "utf8")));
}

const keep = FILL ? existing : {};
const todo = Object.keys(flatEnglish).filter((k) => keep[k] === undefined);
const dropped = Object.keys(existing).filter(
  (k) => flatEnglish[k] === undefined,
);

// Deconstruct everything first: it is free, and it makes --dry-run report the real request size.
const plans = new Map();
let runs = 0;
let chars = 0;
let verbatim = 0;
for (const key of todo) {
  // A verbatim key is not "translated to itself": it never reaches the engine, so it cannot come
  // back subtly altered, and it costs no quota.
  const isVerbatim = isVerbatimKey(key);
  if (isVerbatim) verbatim++;
  const segments = isVerbatim
    ? [{ verbatim: flatEnglish[key] }]
    : deconstruct(flatEnglish[key], protectedTerms);
  plans.set(key, segments);
  for (const seg of segments) {
    if (seg.translate !== undefined) {
      runs++;
      chars += seg.translate.length;
    }
  }
}

console.log(`  locale        ${locale}`);
console.log(
  `  source        ${rel(SOURCE)} (${Object.keys(flatEnglish).length} keys)`,
);
console.log(`  mode          ${FILL ? "fill" : FORCE ? "force" : "create"}`);
console.log(
  `  to translate  ${todo.length} keys, ${runs} runs, ${chars} characters`,
);
console.log(`  verbatim      ${verbatim} keys copied from English, never sent`);
if (FILL)
  console.log(`  keeping       ${Object.keys(keep).length} existing strings`);
if (dropped.length)
  console.log(`  dropping      ${dropped.length} keys English no longer has`);

if (DRY_RUN) {
  const sample = todo.slice(0, 3);
  for (const key of sample) {
    console.log(`\n  ${key}`);
    console.log(`    en   ${flatEnglish[key]}`);
    for (const seg of plans.get(key)) {
      if (seg.translate !== undefined) console.log(`    send ${seg.translate}`);
    }
  }
  console.log("\n  --dry-run: nothing was sent and nothing was written.");
  process.exit(0);
}

if (todo.length === 0) {
  console.log("\n  nothing to translate.");
  process.exit(0);
}

const key = process.env.DEEPL_API_KEY;
if (!key) {
  console.error(
    "\nDEEPL_API_KEY is not set. Get a key at https://www.deepl.com/pro-api (the free tier\n" +
      "covers this catalogue many times over) and export it, or re-run with --dry-run.",
  );
  process.exit(1);
}

const glossaryId = await createGlossary(key, locale, preferred);
try {
  // One flat queue across all keys, so batches are full regardless of how runs fall per message.
  const queue = [];
  for (const [k, segments] of plans) {
    segments.forEach((seg, i) => {
      if (seg.translate !== undefined)
        queue.push({ k, i, text: seg.translate });
    });
  }

  const results = new Array(queue.length);
  const BATCH = 50; // DeepL's documented per-request maximum.
  for (let i = 0; i < queue.length; i += BATCH) {
    const slice = queue.slice(i, i + BATCH);
    const out = await translateBatch(
      slice.map((s) => s.text),
      key,
      locale,
      glossaryId,
    );
    out.forEach((text, j) => (results[i + j] = text));
    process.stdout.write(
      `\r  translating   ${Math.min(i + BATCH, queue.length)}/${queue.length} runs`,
    );
  }
  process.stdout.write("\n");

  const byKey = new Map();
  queue.forEach((item, i) => {
    if (!byKey.has(item.k)) byKey.set(item.k, []);
    byKey.get(item.k).push(results[i]);
  });

  const flatOut = { ...keep };
  const suspect = [];
  for (const [k, segments] of plans) {
    const value = reconstruct(segments, byKey.get(k) ?? []);
    flatOut[k] = value;
    const a = structuralTokens(flatEnglish[k]);
    const b = structuralTokens(value);
    if (
      a.vars.join(",") !== b.vars.join(",") ||
      a.tags.join(",") !== b.tags.join(",")
    )
      suspect.push({ k, a, b });
  }

  if (suspect.length) {
    console.error(
      `\n  ${suspect.length} strings lost or gained a placeholder:\n`,
    );
    for (const { k, a, b } of suspect.slice(0, 20)) {
      console.error(`    ${k}`);
      console.error(
        `      en vars[${a.vars.join(",")}] tags[${a.tags.join(",")}]`,
      );
      console.error(
        `      ${locale} vars[${b.vars.join(",")}] tags[${b.tags.join(",")}]`,
      );
    }
    console.error(
      "\nNothing was written. This is the failure mode gen-locale.mjs exists to catch:\n" +
        "a draft with a mangled placeholder renders as a crash, not as a bad sentence.",
    );
    process.exit(1);
  }

  writeFileSync(
    OUT,
    JSON.stringify(nest(english, flatOut), null, 2) + "\n",
    "utf8",
  );
  console.log(`  wrote         ${rel(OUT)}`);
  console.log(
    `\n  This is a DRAFT. Per ROADMAP 9f.4 it does not ship until a human who plays the\n` +
      `  variant has read it, and the rules namespace is where that reading matters most.\n` +
      `  Adding "${locale}" to i18n/routing.ts is a separate, deliberate step.`,
  );
} finally {
  if (glossaryId) {
    await deepl(`/v2/glossaries/${glossaryId}`, key, undefined, "DELETE").catch(
      () => {},
    );
  }
}
