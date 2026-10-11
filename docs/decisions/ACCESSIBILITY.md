# Accessibility

Why this project does not owe a public conformance programme, recorded once so the next audit
does not re-derive it from scratch, and what the automated test suite actually proves so
nobody mistakes a green CI badge for a legal claim it was never designed to support. Written in the
named-trigger format of [docs/decisions/AUTH.md](AUTH.md) section 1.7 - read that section first if
this format is unfamiliar.

---

## 1. Why this is exempt, twice over (L-8-2)

**The Web Accessibility Directive (2016/2102) does not apply.** It binds public sector bodies. A
private individual running a free chess site is not one, and no reading stretches it.

**The European Accessibility Act (Directive (EU) 2019/882, applicable since 28 June 2025 and
transposed in Belgium) is the one that reaches private operators, and it misses twice:**

1. Its service scope is a closed list: electronic communications, access to audiovisual media
   services, defined elements of passenger transport, consumer banking, e-books, and e-commerce
   services. A free-to-play chess site is none of these, including e-commerce, which is defined
   around concluding a consumer contract for remuneration.
2. Even if a reading got past scope, Article 4(5) exempts microenterprises from the service
   obligations outright, and a one-person project with no turnover is a microenterprise by a wide
   margin.

Two independent misses is not a close call.

**What is left, and it does not need counsel:** Belgium's anti-discrimination act of 10 May 2007
covers access to and supply of goods and services available to the public, free ones included, and
carries a reasonable-accommodation duty scaled to the operator's means. "Reasonable" for a volunteer
running a free service is met by effort of exactly the kind already spent here: an `AccessibleBoard`
with roving tabindex and spoken move entry, a screen-reader announcement layer, a reduced-motion
block, and an axe WCAG A+AA gate that blocks CI on a set of public pages
(`e2e/tests/accessibility.spec.ts`). The gaps that remain are not engineering - that work is tracked
under packet P-5 and the NVDA pass at `docs/plans/a11y/NVDA-PASS.md` - they are that none of this is
*said* anywhere a user can find, and that nobody who hits a barrier has a route that names
accessibility. Both are product/documentation gaps, not legal exposure, and are tracked separately
(see "What this document does not do," below).

## 2. What the axe suite actually tests, and what it does not (L-8-3)

**Today the exposure from this gap is zero**, because no page on the site claims WCAG conformance and
no user-facing copy contains the string "WCAG." That is the correct state and this document exists
partly to keep it that way once someone eventually writes a public accessibility statement.

**The risk is not today's state, it is tomorrow's shortcut:** whoever writes that statement might
read the green CI badge from `e2e/tests/accessibility.spec.ts` as "the site is WCAG 2.1 AA
conformant" and say so. That would not be true, and saying it would be a worse legal position than
saying nothing - an unfounded conformance claim is an affirmative misstatement, not merely a
shortfall.

**What the suite tests:** `e2e/tests/accessibility.spec.ts` runs axe-core against the WCAG 2.0 A,
2.0 AA, 2.1 A and 2.1 AA rule sets (`wcag2a`, `wcag2aa`, `wcag21a`, `wcag21aa`) across a fixed list of
anonymous pages plus the signed-in custom-game builder.

**What it does not test, and by design:** `game/[id]`, `watch/[id]` and `replay/[id]` - the live
board, spectator view, and replay view - are deliberately excluded
(`.claude/rules/testing.md`). The stated reason is that axe cannot evaluate whether a screen-reader
user can actually *play*; an automated DOM scan proves the markup is not malformed, not that the
experience is usable in real time. The live board is the single most important surface for this
project's most accessibility-sensitive user, and it is the one surface the automated floor says
nothing about. Coverage of that surface is the scripted manual NVDA pass at
`docs/plans/a11y/NVDA-PASS.md`, which is written but has not yet been run (tracked as 9f.2,
not launch-gated, after 9f.1).

**The rule for any future statement:** describe what is tested and what is not, in these terms, and
attach no conformance level - "WCAG 2.1 AA" or any other level - to anything until the NVDA pass has
actually been run and its result recorded. A statement that says "we test the following pages against
the following axe rule sets in CI; the live game board is not covered by that automated test and its
manual review has not yet happened" is honest. A statement that says "this site is WCAG 2.1 AA" is
not, on the evidence in this repository as of this writing.

## 3. What reopens this

Any one of these, on its own:

1. **The project acquires employees, or turnover approaches the microenterprise ceiling.** Section 1's
   exemption is a statement about a one-person, no-revenue project; both conditions changing removes
   the premise, not just weakens it.
2. **KiriChess starts selling anything.** Today's Stripe donations are one-off patronage with no
   subscription and no paid tier (`docs/legal/TERMS.md` section 12; the standing "Free forever"
   position in `docs/audits/INDEX.md`). A recurring paid tier or any transactional sale moves the
   e-commerce question in section 1 from "clearly out of scope" to "worth rereading," even though the
   microenterprise exemption alone still likely disposes of it. `docs/audits/L.md`'s own verdict
   already flags this as the nearest trigger, cross-referencing packet L-7's finding that recurring
   Stripe tiers are live sooner than the "free forever" framing implies - reread this file, not just
   L-7, before concluding anything has actually changed.
3. **A conformance claim is drafted.** The moment anyone writes an accessibility statement or a WCAG
   claim for the site, section 2's rule applies and this file is the one to check before publishing,
   not after.

## What this document does not do

- **It is not the accessibility statement.** That is a separate, player-facing page or section (see
  `docs/audits/L.md` finding L-8-1) naming what has been built, what is known not to be verified yet,
  and an address to report a barrier to. Writing that page needs one sentence only the site owner can
  supply - how quickly a report gets answered - and is not done by this document.
- **It does not run the NVDA pass.** That remains an owner action, not launch-gated, after 9f.1.
- **It does not evaluate engineering quality.** Whether the keyboard board, the announcement layer, or
  reduced-motion handling actually work is packet P-5's question, not this one. This document only
  answers whether a legal duty exists and what the automated test's scope actually is.
