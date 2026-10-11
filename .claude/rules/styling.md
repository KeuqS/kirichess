# Styling rules - `ui/`

The visual standard for every page, panel and control in the app. Read this before styling anything
under `ui/`; [frontend.md](frontend.md) covers the Next.js/App Router mechanics, this file covers how
it looks.

**The standard is what `/`, `/rules`, `/leaderboards` and `/about` already do.** Those four pages are
the reference. When this file and a reference page disagree, the reference page wins and this file is
wrong: say so rather than editing the page.

Recipes live in [`ui/app/globals.css`](../../ui/app/globals.css) as plain classes, not a Tailwind
config. There is no `tailwind.config.*`: this is Tailwind v4 and the token layer is the
`@theme inline` block at `globals.css:47`.

## 1. Foundations

- **Dark theme only.** There is no light or high-contrast switch and no `dark:` variant anywhere.
  `html { color-scheme: dark }` is set globally.
- **One font family, and one exception.** Montserrat via `next/font/google` → `--font-montserrat` →
  `--font-sans`. There is no display face. Never introduce a `font-*` family of your own.
  **`font-mono` (Tailwind's default stack) is permitted in exactly two places** and nowhere else:
  - the clock (`Clock.tsx`, `learn/TutorialClocks.tsx`), where digits must not reflow as they change;
  - inline `<code>`, for a literal the reader may have to type or match character for character:
    a file path, a table name, an API scope, a token prefix.

  Inline code is `font-mono text-[var(--bounty)]` at the surrounding text size. It carries no
  background and no border: the family and the hue are enough, and a boxed literal inside a
  paragraph fights the line height. `<code>` is for a literal, not for emphasis; if the word is not
  something a reader could type, it is `font-semibold text-white`.

- **The app is square-cornered.** `globals.css` sets `* { border-radius: 0 !important }` globally, so
  every `rounded-sm` … `rounded-full` in the codebase is inert. **Do not write `rounded-*`
  classes**. They read as intent to a future author and deliver nothing. Strip them from any file
  you touch.
- **Motion is opt-out globally.** `@media (prefers-reduced-motion: reduce)` flattens every animation
  and transition app-wide, so you never need to guard an individual component.
- **Focus is handled globally.** `:focus-visible { outline: 2px solid var(--bounty) }` is deliberately
  unlayered so it beats Tailwind's `focus:outline-none`. Text inputs opt out and show their own gold
  border instead. **Never add `focus:ring-*`** (see §8).

## 2. Colour grammar

Tokens are defined in `:root` at `globals.css:3` and exposed as Tailwind utilities at
`globals.css:47`, so `text-muted`, `text-loss`, `bg-surface` all work.

| Token                                   | Value                 | Use for                                                                                            |
| --------------------------------------- | --------------------- | -------------------------------------------------------------------------------------------------- |
| `--background`                          | `#282c34`             | page background; also the text colour _on_ a gold or red fill                                      |
| `--surface`                             | `#21252b`             | cards, nav, footer, table headers                                                                  |
| `--surface-sunken`                      | `#1b1e24`             | recessed: zebra rows, an unfilled meter track. Replaces the `bg-black/NN` washes                   |
| `--border`                              | `#3e4451`             | every border and divider; `/50` for an interior divider, see below                                 |
| `--foreground`                          | `#abb2bf`             | body text                                                                                          |
| `--muted`                               | `#9197a3`             | secondary text, table column labels, footnotes                                                     |
| `--bounty`                              | `#e5c07b`             | **the brand accent**: links, section numbers, active state, primary CTA, inline `<code>`           |
| `--gain` / `--loss`                     | `#98c379` / `#e8838a` | signed values, success/error                                                                       |
| `--economy-steal` / `-burn` / `-bounty` | yellow / red / green  | capture-economy identity only                                                                      |
| `--accent`, `--cyan`, `--blue`          | purple / cyan / blue  | **categorical only** (see below): chart series and status keys. Never a state, a value, or a delta |

**The colour rule**, quoted from `rules/page.tsx:64`, is the one worth internalising:

> Colour is not a fourth tier - it layers onto a tier, and only for the two things on this page that
> a hue actually means: economy identity, and a signed delta. Nothing else gets a hue. `--bounty`
> stays the brand accent (section numbers, links); a plain number is white, not gold.

**Three text tiers, and only three:**

| Tier      | Class                      | For                                          |
| --------- | -------------------------- | -------------------------------------------- |
| Emphasis  | `text-white`               | headings, the one value a row exists to show |
| Body      | `text-[var(--foreground)]` | prose                                        |
| Secondary | `text-muted`               | labels, footnotes, breakdowns                |

**The categorical palette.** `--accent`, `--cyan` and `--blue` exist for the one job the rule above
cannot do: telling six things apart when none of them is better or worse than the others. A chart's
series, a sanction-type key. Three constraints, and they are what keep this from
becoming a licence to colour anything:

- **Categories only, never a judgement.** The moment a hue means good, bad, warning, active or
  changed, it is `--gain` / `--loss` / `--bounty` and the strict rule applies. A "worth watching"
  amber is a judgement wearing a category's clothes.
- **Define the mapping once, in a `Record`, at the top of the file** (the way
  `analytics/page.tsx` and `SanctionBanner.tsx` already do), so the key is greppable and one
  category cannot pick up two colours in two places.
- **Never in prose or a table cell.** These three are for a swatch, a bar, a line or a badge that
  sits next to its own legend. Body text has three tiers and none of them is purple.

Nothing outside data visualisation and categorical status keys may reach for them.

**`--surface-sunken` is the recessed shade**, for a surface that must read as _below_ the card it
sits on: a table's zebra striping, a meter's unfilled track, a move list's alternating rows. It is
the one legitimate use the `bg-black/NN` washes were reaching for, and having it named once stops
three call sites picking three different alphas.

**Black is still correct for a scrim over an image.** Dimming a user's uploaded photo behind a
cropper or a hover affordance is not theming a surface: the thing underneath is arbitrary pixels,
not a token, and a theme colour at low alpha would tint it. `bg-black/40` over a photo is right;
`bg-black/20` as the background of a table row is the thing to stop. The test allows the former
only in the avatar components.

**A token at reduced alpha is not the same thing as a raw wash.** `border-[var(--border)]/50` on the
row dividers inside a `DataTable` is correct and deliberate: the container edge and the header rule
carry the full weight, the dividers between rows sit under them, and that hierarchy is what stops a
dense table reading as a grid of equal lines. `bg-black/20` is the thing to avoid, because black is
not a token and three call sites will pick three different alphas. Tint a token to express a
hierarchy; never invent a colour to express one.

- **Never** `text-[var(--foreground)]/70` or lower. `globals.css:37` explains why: blending foreground
  that far reads as 2.8–4.0:1 against the background, under the 4.5:1 AA floor for the `text-sm` size
  it was always used at. `--muted` is the picked-once replacement that clears AA.
- **Never a raw hex in a Tailwind class.** `#282c34` is `var(--background)`, including as the text
  colour on a gold or red fill (`text-[var(--background)]`, not `text-[#282c34]`). Four exceptions,
  all cases where a CSS custom property genuinely cannot resolve:
  - the `selection:` pair in the page shell (§3), which is quoted verbatim everywhere;
  - `ui/app/global-error.tsx`, which renders when the stylesheet may not have loaded;
  - `themeColor` in route metadata (`layout.tsx`), which is a browser hint, not CSS;
  - `backgroundColor` in the `opengraph-image.tsx` files, rendered by Satori at build time with no
    stylesheet and therefore no tokens.

  Do not "fix" the last three. Replacing them with `var(...)` produces a transparent OG image or a
  missing theme colour, and neither failure is visible in the app.

## 3. Page shell

Every chrome-bearing page opens the same way. This string is verbatim identical on all four
reference pages, so copy it, don't retype it:

```tsx
<div className="min-h-screen flex flex-col bg-[var(--background)] text-[var(--foreground)] font-sans selection:bg-[var(--bounty)] selection:text-[#282c34]">
  <NavBar />
  <main id="main-content" className="flex-1 page-prose pt-10 pb-32">
    …
  </main>
  <Footer />
</div>
```

- **`id="main-content"` and `scroll-mt-24` on the `<main>` are both required.** The id is the skip
  link's target (`NavBar.tsx`), and a page that omits it silently breaks keyboard navigation. The
  `scroll-mt-24` is what stops the skip link landing the reader underneath the sticky nav, which is
  the same reason §9 puts it on every anchor target. A `<main>` with the id and without the offset
  is half a skip link.
- **This applies to every branch, not just the happy path.** A loading, error or not-found early
  return is a rendered page: if it draws a `NavBar`, it owes a `<main id="main-content">` and an
  `<h1>`. Early returns are where this has gone wrong every single time, because the shell lives at
  the bottom of the file and the early return is at the top.
- `<Footer />` is omitted only on `/game/[id]`, `/watch/[id]` and the auth screens, all deliberate.
  The auth screens have no `NavBar` either, but they still owe the `<main>` landmark: no chrome is
  not the same as no document structure.

**What in that string is load-bearing.** `body` already sets the background, the foreground and the
family globally, so copying the whole string is belt and braces rather than magic. Two parts do real
work and must not be dropped:

- `min-h-screen flex flex-col` on the outer div plus `flex-1` on the `<main>`. Without the pair the
  footer floats up under short content instead of sitting at the bottom.
- the `selection:` pair, which is the only thing making dragged text gold rather than browser blue.

Copy the whole string anyway. Knowing which half matters is for reviewing someone else's page, not
for licence to type a shorter one.

**Four page widths** (`globals.css:241`). Do not invent a fifth. Vertical padding stays on the element:

| Recipe       | Max width | Padding                     | Routes                                                |
| ------------ | --------- | --------------------------- | ----------------------------------------------------- |
| `page-width` | 80rem     | `pt-10 pb-24`               | lobby, lists, profile, custom, donate, staff          |
| `page-prose` | 56rem     | `pt-10 pb-32`               | about, developers, privacy, terms, rules              |
| `page-board` | 96rem     | `py-6`                      | live game, spectate, replay, learn                    |
| `page-card`  | 28rem     | none (the card's own `p-8`) | auth, mail links, challenge, offline, 404, 403, error |

`/` is the one documented exception: it puts `page-width pt-8 pb-16` on a `<section>` inside a bare
`<main id="main-content" className="flex-1">`, because the lobby's rows manage their own rhythm.

## 4. Type scale

| Role                        | Class                                                                          |
| --------------------------- | ------------------------------------------------------------------------------ |
| **h1**                      | `text-3xl font-bold leading-tight tracking-tight mb-4 text-white text-balance` |
| **h1, card page**           | `text-2xl font-bold tracking-tight text-white text-balance`                    |
| **h1, already-named page**  | `sr-only`, when the nav, a clock, or the controls already name the page (`/leaderboards`, `/login`) |
| **h2, chapter**             | `text-2xl font-bold mb-4 tracking-tight text-white`                            |
| **h2, sub-heading**         | `text-xl font-bold text-white mb-4 tracking-tight`                             |
| **h3**                      | `text-xl font-bold m-0 tracking-tight text-white`                              |
| **Card heading**            | `text-lg font-bold text-white`                                                 |
| **Body prose**              | `text-[15px] text-[var(--foreground)] leading-relaxed`                         |
| **Card body**               | `text-sm text-[var(--foreground)] leading-relaxed`                             |
| **Secondary / footnote**    | `text-sm text-muted leading-relaxed`                                           |
| **Eyebrow / divider label** | `text-sm tracking-widest text-muted font-semibold`                             |

Bottom margin stays on the element (`mb-4` / `mb-6` / `mb-8`), not in the recipe.

**Three forms, chosen by where the h1 sits and what already names the page, not by page width.**
Width was the old wording and it left `page-board` mapped to nothing, which is how four board
screens ended up with three different answers. Ask these in order:

1. **Is the page already named for the reader, by the nav, a running clock, or the controls on the card?** Then `sr-only`.
   `/leaderboards` and `/watch` because the nav names them; `/game/[id]` and `/watch/[id]` because a
   visible title competing with a live clock is noise, and the clock is the page. `/login` because
   the Log in / Register tabs already name the mode; the h1 stays in the document and switches with
   the tab. The other auth cards have no such control and stay on the card-page form below.
2. **Does the h1 sit inside a card?** Then the card-page h1. Every `page-card` screen (404, 403,
   `error.tsx`, offline, the auth cards other than `/login`, challenge) and any h1 inside a
   `<Card>` narrower than about 32rem, `/learn`'s tutorial step card included. `text-3xl mb-4` is
   too large in a 28rem card, and each of
   those screens picking its own size is how they ended up at four different ones.
3. **Otherwise the full h1.** The h1 sits directly in the page body and names a subject the nav does
   not: `/`, `/about`, `/profile`, `/u/[id]`, `/replay/[id]`.

**A page gets one h1**, in one of the three forms above, **on every branch**. `text-2xl`, `text-xl`
and `font-semibold` h1s are drift, not variants. An early return that renders before the page's own
h1 needs its own: `EmptyState` takes `headingLevel={1}` for exactly this, and a loading branch takes
the `sr-only` form.

`/` uses the full h1 for the product tagline. The nav names the site, not the sentence the lobby
exists to say, so this is the full form rather than `sr-only`.

**Section labels inside a page use `.profile-category`** (`globals.css:389`), not a hand-sized
heading: 14px, `font-semibold`, uppercase, `tracking-widest`, `--muted`, and `margin: 0` so spacing
stays on the element. It is the `<h2>` that divides a long page into named regions where a full
`text-xl` sub-heading would shout, and it is already the pattern on `/profile`, `/u/[id]` and every
account card. Reach for it before inventing a small bold heading; `text-sm font-bold text-white` is
not a heading recipe.

**14px is the floor.** Nothing carries **readable** type below `text-sm`. Two exceptions, both
because the glyphs are chrome rather than content:

- `.table-header`, at 12px precisely so uppercase + `tracking-widest` does not overpower 14px row
  content;
- a label sized to a cell of a decorative diagram, currently only the square label in
  `NotFoundScene`. That scene is `aria-hidden`, its board is ten columns inside a 28rem card, and a
  column is about 27px on a narrow viewport, so 14px plus `tracking-widest` overflows the cell it
  is captioning. A decorative glyph that no reader is asked to read is not held to a reading size.

The exception is the diagram, not the decoration. Anything a reader is meant to read is `text-sm`
or larger however small its container, and the answer to a cramped container is a bigger container.

**Inline links in prose:** `text-[var(--bounty)] font-medium hover:underline`. Gold carries the
signal; the underline is the hover affordance. (`/rules` uses an always-underlined variant; it is a
reference page and stays as it is, but new prose follows `/about`.)

## 5. Cards and elevation

[`ui/components/Card.tsx`](../../ui/components/Card.tsx) is the only card. It takes
`{ className, padding = "p-5", accentColorVar, style, onClick }` and renders
`border border-[var(--border)] bg-[var(--surface)]`.

**Do not hand-roll a card.** If you are writing `border border-[var(--border)] bg-[var(--surface)] p-5`,
you are writing `<Card>`.

Three elevation tiers, and nothing else:

| Tier    | Class        | For                                                 |
| ------- | ------------ | --------------------------------------------------- |
| Flat    | _(none)_     | in-flow content cards, the default                  |
| Raised  | `shadow-lg`  | a full-width panel that owns its region of the page |
| Overlay | `shadow-2xl` | modals, dialogs, tooltips, the auth card            |

**Raised is about the panel, not about what is inside it.** The phrase used to read "a `DataTable`
panel, an empty/error state", and both readings shipped: `/watch` and the staff pages shadow the
panel that holds the empty state, `error.tsx` and `MatchHistoryCard` leave it flat. The panel is
what earns the shadow. So a `DataTable` container, and the same container when it is showing a
skeleton, an empty state or an error instead of rows, are all `shadow-lg`; a tier card in a grid, a
step card, an account card are all flat, whatever they contain. If swapping the contents changes the
shadow, you have it backwards.

**A glow is not an elevation tier.** `shadow-[0_0_30px_-5px_…]` on an active clock and
`shadow-[0_0_16px_-4px_var(--bounty)]` on a hovered tile are an active/hover affordance in the brand
accent, not a fourth height. They are allowed, they do not go in the table above, and they take
`var(--bounty)` rather than a literal colour. `shadow-sm` and `shadow-inner` are neither, and are
not used.

`accentColorVar` tints the border for a semantic card (`<Card accentColorVar="--loss">` for a
disclaimer). Use it instead of writing a border colour.

**Padding follows what kind of thing the card is, not its layout.** Neither "does it share a grid
with siblings" nor "how wide is its column" predicts this - `PresetTile` and `TierCard` both sit in
a grid of same-shaped siblings, and `/custom`'s Opponent card is wider than `PoolRatingCard`'s
minimum column, so cell width doesn't split them either. What actually splits them is role:

- **`padding="p-8"`: the card is a complete, self-contained entity**, the thing itself
  rather than a piece of getting there. A pool tile (`PresetTile`), a pool rating (`PoolRatingCard`),
  a donation tier (`TierCard`). `PresetTile`, `PoolRatingCard`
  and `TierCard` sit in a grid of siblings and are still `p-8`, because each tile is still one
  complete option, not a step.
- **Default `p-5`: the card is a supporting panel** - a form, a workflow step, a HUD widget beside
  the board, a banner, a "type your own value" CTA, or a `/profile` settings section
  (`SecurityCard`, `ApiTokensCard`, `AccountDataCard`, `NotificationPreferencesCard`). The push
  row lives inside that notifications card. Rated pool cards on that page stay `p-8`. `ChallengeFriendPanel` and
  `BotPlayPanel` in `/`'s Others column, `/custom`'s `SectionCard` list and its Opponent/Live Preview
  pair (a tab row, a strength slider and a challenge-link form, and still `p-5`, because building a
  ruleset is a workflow, not an entity you own), `GapMeter`/`CapturedMaterialPanel`/`GameOverOverlay` beside the board,
  the dashed Custom game / Custom amount boxes.

**If a new card is one of several identical options a reader picks between, it is `p-8`.** If it is
part of assembling or supporting something else, it is the default `p-5`. Smaller explicit values
(`p-4`, `p-6`) exist for a denser metric tile or a modal's own recipe (`Dialog.tsx`) and are fine;
they are not a third row in this rule, just a smaller amount of the same idea.

## 6. Buttons

Four recipes, defined at `globals.css:302`. Use them on both
`<button>` and `<Link>`/`<a>`.

- `btn-primary`: filled gold. The one action on the surface.
- `btn-secondary`: outline, gold on hover. Related action or cancel.
- `btn-danger`: red outline. Destructive, not yet confirming.
- `btn-danger-fill`: filled red. Confirm the destructive action.

Padding and text size stay on the element so a Join chip and a form submit share a look without
sharing a size, but **pick from this scale**, don't invent a size:

| Size        | Class                   | For                                          |
| ----------- | ----------------------- | -------------------------------------------- |
| Compact     | `px-3 py-1.5 text-sm`   | inline in a row or card header               |
| Default     | `px-5 py-2 text-sm`     | the normal CTA                               |
| Hero        | `px-6 py-3 text-sm`     | an empty state's single call to action       |
| Form submit | `w-full py-2.5 text-sm` | the bottom of a form                         |
| Split row   | `flex-1 py-2.5 text-sm` | two or three buttons sharing one row         |
| Icon only   | `p-2`                   | a square button whose whole label is a glyph |

The last two were missing, and their absence is what produced most of the invented sizes in the
tree. **Split row** is Form submit's two-up form: same height, same text size, width from `flex-1`
instead of `w-full`, for a Confirm/Cancel or Draw/Resign pair. **Icon only** takes no horizontal
padding on purpose, because every other row in this table specifies horizontal padding a square
button must not have; it needs an `aria-label`, since it has no text to name it.

**Split row applies to the whole row, not to one button in it.** If one button in a
`flex items-center` row takes `flex-1 py-2.5 text-sm`, every button beside it takes the same thing.
Pairing a Split-row button with a Compact one leaves two controls of visibly different heights
sitting next to each other, which is worse than the invented size either of them started from. The
matching pair to reach for when neither button should grow is Compact beside Compact, the way
`ReportDialog`'s footer already does it. Pick one row height and give it to the row.

`px-4 py-2 text-sm` is the tab and filter chip size (below). It is not a CTA size: a button that is
not a tab does not take it.

**Tabs and filter chips are buttons**, not a fifth look. `/leaderboards` sets the pattern:

```tsx
className={`px-4 py-2 text-sm ${active ? "btn-primary" : "btn-secondary"}`}
```

Do not invent a fifth CTA. Ghost (nav links, underlined text) stays a local
pattern. Board Resign is `btn-danger` (idle) and `btn-danger-fill` (confirm).

## 7. Tables

Every table is [`ui/components/DataTable.tsx`](../../ui/components/DataTable.tsx), a CSS-grid-with-subgrid
component, not a `<table>`. It owns the container, the `.table-header` row, dividers and row hover;
you supply `gridTemplateColumns` as a string constant and the cells.

Three cell tiers, named at `rules/page.tsx:75`:

```ts
const CELL_LABEL = "font-semibold text-white"; // the row's identity, always first
const CELL_VALUE = "text-white font-medium"; // the one value the table exists to show
const CELL_SUPPORT = "text-sm text-muted"; // breakdowns, secondary counts
```

Right-aligned numeric columns take `text-right tabular-nums`. Loading is
[`SkeletonRows`](../../ui/components/SkeletonRows.tsx); empty and error are
[`EmptyState`](../../ui/components/EmptyState.tsx). Don't hand-build either.

**One exception: a prose table inside `page-prose`.** `DataTable` is built for rows of values, so it
brings a scroll container, a 12px uppercase header and cell tiers that assume a row has one thing to
show. A reference table in a document, three columns of sentences in `/privacy` or `/terms`, is a
different object, and forcing it through `DataTable` reads worse, not better. Those stay a real
`<table>`, and take this treatment so they still look like the rest of the app:

```tsx
<div className="overflow-x-auto">
  <table className="w-full text-left border-collapse">
    <thead>
      <tr className="border-b border-[var(--border)]">
        <th className="table-header py-2 pr-4">…</th>
      </tr>
    </thead>
    <tbody>
      <tr className="border-b border-[var(--border)]/50 align-top">
        <td className="py-3 pr-4 font-semibold text-white">…</td>
        <td className="py-3 text-sm text-[var(--foreground)] leading-relaxed">
          …
        </td>
      </tr>
    </tbody>
  </table>
</div>
```

The header uses `.table-header`, the same class `DataTable` uses, so the two kinds of table share a
column-label register even though they share nothing else. This exception is for a document. A table
of data on a `page-width` page is a `DataTable`, however few rows it has.

## 8. Forms

| Control          | Recipe                                                                                                                               |
| ---------------- | ------------------------------------------------------------------------------------------------------------------------------------ |
| Text input       | `w-full border border-[var(--border)] bg-[var(--background)] px-4 py-2.5 text-white focus:border-[var(--bounty)] focus:outline-none` |
| Compact input    | same, `px-3 py-2 text-sm`                                                                                                            |
| Select           | the same box plus `input-select pr-10`                                                                                               |
| Checkbox         | `checkbox-bounty`                                                                                                                    |
| Form / API error | `error-banner`                                                                                                                       |

- **Never add `focus:ring-*` to a text field.** `globals.css:175` spells out why: the field already
  has a 1px box, the global offset outline sits 2px outside it, and adding a ring on top renders as a
  triple gold frame. Inputs get `border-color: var(--bounty)` on focus and nothing else.
- `.error-banner` already exists: red text on surface with a neutral border. Don't hand-roll a
  red-on-red banner; `globals.css:401` notes the red text is enough signal on its own.

## 9. Spacing rhythm

- **Prose stacks:** `flex flex-col gap-5`. Not `space-y-*`; that is reserved for
  `list-disc pl-5 space-y-2` inside a list.
- **Between sections:** `mb-14` between articles; `mb-16 pt-16 border-t border-[var(--border)]` where
  a rule separates chapters.
- **Heading → body:** heading carries `mb-4`; an intro paragraph before a table carries `mb-6`.
- **Table → footnote:** `mt-5`.
- **Card grids:** `grid grid-cols-1 md:grid-cols-2 gap-6` / `md:grid-cols-3 gap-6`.
- **Inside a card:** `flex flex-col gap-4`.
- **Button rows:** `flex items-center gap-3 flex-wrap`.
- **Anchors:** `scroll-mt-24` on every element with an `id` a link targets; it clears the sticky nav.
  `<main id="main-content">` is one of these: the skip link is a link and the nav is sticky (§3).

## 10. Don't

- Don't invent a fifth page width, a fifth CTA look, or a second card.
- Don't hand-roll a card, modal shell, tab chip, error banner, empty state or skeleton; each already
  exists as a component or a class.
- Don't write a raw hex. Don't write `rounded-*`. Don't write `focus:ring-*`.
- Don't write a raw `rgba()` in an inline `style` or a template literal either. It is the same ban,
  and it is the one place the test cannot see you: that is how a colour retired for failing AA
  survived in `GapLadder`, and how an off-palette amber survived in `Board`.
- Don't use `text-[var(--foreground)]/70` or lower for secondary text; that's `text-muted`.
- Don't reach for `bg-black/NN` or `text-white/NN` as a shade. A wash over the background is not a
  token, and three call sites will pick three different alphas. Use `--surface` or `--muted`.
- Don't ship a new page without an `<h1>` and without `id="main-content" scroll-mt-24` on its
  `<main>` **on every branch**, early returns included.
- Don't add a `dark:` variant; there is only one theme.

## Verify

```bash
cd ui && npm run lint && npx tsc --noEmit && npm test && npm run build
cd e2e && npm test   # accessibility.spec.ts, axe WCAG 2.0/2.1 A+AA across the anonymous pages
```

[`ui/lib/styling.test.ts`](../../ui/lib/styling.test.ts) enforces the mechanical half of this file
and runs as part of `npm test`, so CI gates it. It checks the bans that can be decided from a class
string: no `rounded-*`, no raw hex for a colour that has a token, no raw `rgba()`, no `--foreground`
blended to 70% or below, no `focus:ring-*` on a field, no hand-rolled `Card`, no hand-rolled modal
backdrop, no `bg-black/NN`, no `space-y-*` outside a `list-disc`, no `shadow-*` outside the three
tiers, no button padding outside the size scale, and, for any file that renders a `NavBar`, a
`<main id="main-content" … scroll-mt-24>` and the full shell string. Each failure names the file,
the line and the section above.

It cannot judge the rest. Whether an h1 is the right size, a card the right elevation, or a hue
carries a meaning it is allowed to carry still needs a human. **A failure there means fix the code**;
loosening a rule or adding to an allowlist is only right when this file changed too, in which case
update the `.cursor/rules/styling.mdc` mirror in the same commit.

[`docs/ops/UI-CHECKLIST.md`](../../docs/ops/UI-CHECKLIST.md) is the manual walk: every page, overlay
and control with the states to check. It is a QA log, not a spec; this file is the spec.
