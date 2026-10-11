/**
 * Phase 9f.4. Taking an ICU message apart so a translation engine cannot damage its structure, and
 * putting it back together afterwards. Split out of gen-locale.mjs so lib/icuProtect.test.ts can
 * prove the round-trip, which is this pipeline's one load-bearing correctness claim: whatever the
 * engine does to the words, every placeholder, rich-text tag and plural branch comes back.
 *
 * Node builtins only, and no imports at all, matching the other generators here.
 */

// ---------------------------------------------------------------------------------------------
// Message deconstruction
//
// Splits a message into runs that can be translated and spans that must survive byte-for-byte.
// ---------------------------------------------------------------------------------------------

/** Matches an ICU argument whose body has no comma, i.e. a plain `{name}` substitution. */
const SIMPLE_ARG = /\{\s*[A-Za-z0-9_]+\s*\}/g;
/**
 * Matches a rich-text tag, opening or closing: next-intl renders these through t.rich().
 *
 * `x` is excluded because it is the reserved sentinel name below. Without that exclusion this
 * pattern matches the markers the placeholder pass just created and stashes them a second time,
 * and the message comes back with raw `<x>0</x>` debris in it. lib/icuProtect.test.ts holds both
 * halves of that: the round-trip, and the assertion that no real message uses `<x>`.
 */
const RICH_TAG = /<\/?(?!x>)([a-zA-Z][a-zA-Z0-9]*)>/g;

/**
 * The opaque marker standing in for a protected span while the text is out at translation.
 *
 * Deliberately contains no whitespace. An earlier version used `<x id="0"/>`, which survives a
 * real XML-aware engine but is torn in half by anything that splits on spaces, and a sentinel that
 * only works when every downstream consumer is well behaved is not worth having. This shape is one
 * token, and `ignore_tags: ["x"]` names it in a single entry.
 */
const SENTINEL = /<x>(\d+)<\/x>/gi;

/** Strips sentinels, to ask whether what is left is worth sending to a translator at all. */
export function withoutSentinels(text) {
  return text.replace(SENTINEL, "");
}

/**
 * Finds the top-level `{name, plural|select|selectordinal, ...}` blocks in a message, by scanning
 * for brace depth rather than with a regex, since the branch bodies are themselves braced.
 */
function findComplexBlocks(message) {
  const blocks = [];
  for (let i = 0; i < message.length; i++) {
    if (message[i] !== "{") continue;
    let depth = 0;
    let end = -1;
    for (let j = i; j < message.length; j++) {
      if (message[j] === "{") depth++;
      else if (message[j] === "}") {
        depth--;
        if (depth === 0) {
          end = j;
          break;
        }
      }
    }
    if (end === -1) break;
    const body = message.slice(i + 1, end);
    if (/^[A-Za-z0-9_]+\s*,\s*(plural|select|selectordinal)\s*,/.test(body)) {
      blocks.push({ start: i, end: end + 1 });
      i = end;
    }
  }
  return blocks;
}

/**
 * Splits one complex block into the parts that stay verbatim (the header and each branch keyword)
 * and the branch bodies, which are ordinary sentences and get translated individually.
 */
function splitComplexBlock(block) {
  const inner = block.slice(1, -1);
  const headerEnd = inner.indexOf(",", inner.indexOf(",") + 1) + 1;
  const header = inner.slice(0, headerEnd);
  const rest = inner.slice(headerEnd);

  const parts = [];
  let i = 0;
  while (i < rest.length) {
    const open = rest.indexOf("{", i);
    if (open === -1) {
      parts.push({ verbatim: rest.slice(i) });
      break;
    }
    parts.push({ verbatim: rest.slice(i, open + 1) });
    let depth = 0;
    let close = -1;
    for (let j = open; j < rest.length; j++) {
      if (rest[j] === "{") depth++;
      else if (rest[j] === "}") {
        depth--;
        if (depth === 0) {
          close = j;
          break;
        }
      }
    }
    if (close === -1) {
      parts.push({ verbatim: rest.slice(open + 1) });
      break;
    }
    parts.push({ body: rest.slice(open + 1, close) });
    parts.push({ verbatim: "}" });
    i = close + 1;
  }
  return { header, parts };
}

/** Replaces every span that must not be translated with an opaque sentinel. */
export function protect(text, protectedTerms) {
  const spans = [];
  const stash = (value) => {
    spans.push(value);
    return `<x>${spans.length - 1}</x>`;
  };

  // Terms go first, while the text is still plain: stashing them afterwards would have to reason
  // about sentinels it might match inside.
  //
  // Longest first, so "Google Authenticator" wins over "Google" and "check chip" over "check".
  // Case-insensitive, because the catalogue writes the wordmark as "KIRICHESS" in the nav and auth
  // headings and as "KiriChess" in prose, and both have to survive. The matched text is stashed
  // exactly as it appeared, so the original casing comes back.
  let out = text;
  for (const term of protectedTerms) {
    const pattern = new RegExp(
      `(?<![A-Za-z0-9_])${term.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")}(?![A-Za-z0-9_])`,
      "gi",
    );
    out = out.replace(pattern, (m) => stash(m));
  }

  return {
    text: out
      .replace(RICH_TAG, (m) => stash(m))
      .replace(SIMPLE_ARG, (m) => stash(m))
      .replace(/#/g, (m) => stash(m)),
    spans,
  };
}

export function restore(text, spans) {
  // Case-insensitive and whitespace-tolerant on purpose: an engine has no business rewriting a tag
  // it was told to ignore, but if one ever does, a lost placeholder is a crash in production and
  // matching loosely costs nothing.
  return text.replace(SENTINEL, (whole, n) => {
    const span = spans[Number(n)];
    return span === undefined ? whole : span;
  });
}

/** Every translatable run in a message, in order, with the structure needed to rebuild it. */
export function deconstruct(message, protectedTerms) {
  const blocks = findComplexBlocks(message);
  const segments = [];
  let cursor = 0;

  const pushText = (raw) => {
    if (!raw) return;
    const { text, spans } = protect(raw, protectedTerms);
    // A run that is nothing but sentinels has no words to translate; sending it wastes quota and
    // invites the engine to "correct" what is pure structure ("{label}. {resultText}.").
    if (!/[A-Za-z]/.test(withoutSentinels(text))) {
      segments.push({ verbatim: restore(text, spans) });
    } else {
      segments.push({ translate: text, spans });
    }
  };

  for (const { start, end } of blocks) {
    pushText(message.slice(cursor, start));
    const { header, parts } = splitComplexBlock(message.slice(start, end));
    segments.push({ verbatim: `{${header}` });
    for (const part of parts) {
      if (part.verbatim !== undefined)
        segments.push({ verbatim: part.verbatim });
      else pushText(part.body);
    }
    segments.push({ verbatim: "}" });
    cursor = end;
  }
  pushText(message.slice(cursor));
  return segments;
}

export function reconstruct(segments, translations) {
  let out = "";
  let t = 0;
  for (const seg of segments) {
    if (seg.verbatim !== undefined) out += seg.verbatim;
    else out += restore(translations[t++], seg.spans);
  }
  return out;
}

/**
 * The placeholders and rich-text tags a message genuinely carries, for comparing a translation
 * against its source.
 *
 * The naive version of this, a regex for `{name`, is wrong in a way that matters: it cannot tell a
 * real `{code}` placeholder from the branch body of `{count, plural, one {code} other {codes}}`,
 * so it reports a false mismatch the moment a locale translates that branch, which every locale
 * must. Spanish `one {codigo}` would have been rejected as a lost placeholder. So this walks the
 * braces instead, counting the argument names and recursing into branch bodies (where a nested
 * placeholder is real) without counting the bodies themselves.
 */
export function structuralTokens(message) {
  const vars = [];
  const tags = [...message.matchAll(RICH_TAG)].map((m) => m[1]);

  const scan = (text) => {
    for (let i = 0; i < text.length; i++) {
      if (text[i] !== "{") continue;
      let depth = 0;
      let end = -1;
      for (let j = i; j < text.length; j++) {
        if (text[j] === "{") depth++;
        else if (text[j] === "}") {
          depth--;
          if (depth === 0) {
            end = j;
            break;
          }
        }
      }
      if (end === -1) return;
      const body = text.slice(i + 1, end);
      const complex = body.match(
        /^([A-Za-z0-9_]+)\s*,\s*(?:plural|select|selectordinal)\s*,/,
      );
      if (complex) {
        vars.push(complex[1]);
        for (const part of splitComplexBlock(text.slice(i, end + 1)).parts) {
          if (part.body !== undefined) scan(part.body);
        }
      } else {
        const simple = body.match(/^\s*([A-Za-z0-9_]+)\s*$/);
        if (simple) vars.push(simple[1]);
      }
      i = end;
    }
  };
  scan(message);
  return { vars: vars.sort(), tags: tags.sort() };
}
