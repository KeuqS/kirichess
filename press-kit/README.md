# Press kit

## Using the mark

`logo/kirichess-mark.svg` and `logo/kirichess-mark-512.png` are the reserved KiriChess mark (see
[NOTICE](../NOTICE)): not licensed under Apache-2.0 or CC BY 4.0. This kit exists so you do not
have to ask before covering the project. You may:

- reproduce the mark unmodified to illustrate coverage of KiriChess.

You may not:

- alter the mark, use it as your own or anyone else's mark, or use it in a way that implies
  endorsement or affiliation with KiriChess.

Everything else in this folder follows the licence of what it captures: the screenshots are stills
of the Apache-2.0-licensed site, and `pitch.md` draws on the CC BY 4.0 rules text plus the
project's own launch copy. Neither carries the mark restriction above.

> **AD.1** in [docs/ROADMAP.md](../docs/ROADMAP.md) (#17, after 8.7). Sequenced after the Reddit
> launch on purpose: first players are the thread, not coverage. The kit is the clip plus three
> screenshots, the logo, and a one-paragraph pitch, so a stranger who decides to cover this
> _later_ has something to take.
>
> **Clips are not in this folder yet.** A 430×1400 phone-framed take was recorded 2026-08-18 and
> converted 2026-08-19, then pulled: the game page below `lg` stacks both clocks above the board,
> and that layout is not designed. Recapture on the desktop `lg` grid
> (`240px` clocks | board | 320px sidebar) when we pick a clip from the list below.
> `convert.mjs` stays; it just needs a new `clip/queen-capture-raw.webm`.

## What's here

| Path                                                      | What it is                                                                                                                 |
| --------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------- |
| `screenshots/queen-capture.png`                           | Still the mobile stacked layout - replace on the next capture. Both clocks, the gap meter, queen on f6, −25s / +25s panel. |
| `screenshots/home-pools.png`                              | The six rated-pool tiles on the home page, before anyone queues.                                                           |
| `screenshots/rules-captures-table.png`                    | The manual's own capture-value table (Rule 1), screenshotted off `/rules` at 1280×900.                                     |
| `logo/kirichess-mark.svg` / `logo/kirichess-mark-512.png` | The site's existing mark (`ui/app/icon.svg` / `apple-icon.png`).                                                           |
| `pitch.md`                                                | One-paragraph pitch plus a caption-length version, from [POST-DRAFT.md](../docs/plans/launch/POST-DRAFT.md) and RULES.md.  |
| `convert.mjs`                                             | Trim last 12s of a raw Playwright `.webm` → MP4 + GIF. Needs `ffmpeg` on PATH.                                             |

## Clips to recapture (desktop)

All of these assume a viewport ≥1024px wide so the clocks sit beside the board, not above it.
Queen at base C1 is **25s**; Fulcrum is C2 so a queen is **50s**. Toll on the capturing queen is
always **4s** and does not scale.

1. **Clutch queen (the one to try first).** Trailing player has ~20s, leader ~30s, trailer
   takes the queen. Steal, so both clocks move. Two honest versions of "and wins":
   - **Anchor (C1, ±25s):** 20 − 4 + 25 = **41s**, 30 − 25 = **5s**. The Gap flips from −10
     to +36. They do **not** flag - a queen is 5s short of killing a 30s clock. Still the
     swing the product is about.
   - **Fulcrum (C2, ±50s):** 20 − 4 + 50 = **66s**, 30 − 50 → **flag**. Game-over overlay on
     the same move. This is the literal "captures the queen and wins" cut.
   - **Anchor, clocks 20 vs 24:** same C1 queen, victim ≤25s, also flags. Use this if the
     clip should stay in the default pool.
     Custom games start both clocks equal (`startTimeSeconds`); there is no public "FEN + 20s vs
     30s" seed. The capture script has to create the gap by waiting on one clock, then play the
     take. Worth a press-kit-only seed later if waiting looks fake.

2. **Opening swing (the original featured sequence).** Anchor, 1. e4 e5 2. Qf3 Qf6 3. Qxf6.
   Full clocks, ±25s, no recapture. Teaches the mechanic in four half-moves. Script already
   exists in [e2e/press-kit/capture.spec.ts](../e2e/press-kit/capture.spec.ts) - retarget the
   viewport to desktop `lg` and re-run.

3. **Check chip.** A pawn check (Rule 3): mover −1s toll +2s chip = **+1s**, opponent **−2s**.
   Shows that checks move time and that cheap pieces profit. Short, readable, different from
   a capture.

4. **Burn vs Steal.** Same capture twice: Anchor (both clocks move) then Ratchet (only the
   victim drops). Makes the three economies visible rather than explained.

5. **Unaffordable mate.** The pitch's consequence: a completely winning K+Q vs K where the
   queen bill (~48s in the rules doc-model) is more than the clock. They have the win on the
   board and flag converting it. Harder to stage; skip unless 1–4 are already in the folder.

Steal equal trades do **not** move The Gap (knight for knight nets 0). The finishes below are
the ones that do: an unrecaptured take, a Burn drain, a recapture you can't afford, or a flag
that isn't a capture at all.

6. **Mate on empty.** Checkmate wins even if the mating move's toll would have flagged you
   ("Mate ends the game before the bill arrives"). Player at **3s** mates with a queen (**4s**
   toll). Clock would hit 0; overlay says checkmate anyway. The one rule that breaks the
   economy, and the most chess-shaped finish.

7. **Spite pawn.** Leader has **2s**. Trailer delivers a pawn check: −1s toll +2s chip for you,
   **−2s** for them → flag. No capture. Cheapest possible win, and it only exists because
   checks move time.

8. **Burned queen trade.** Ratchet (Burn, C1). Clocks ~40 vs ~20. Equal queens come off: you
   pay 4s toll, they lose **25s**, you gain nothing. 40 → 36, 20 → **flag**. The board is even;
   the trade is the win. This is what "even equal trades drain both clocks" looks like when
   someone is already poor.

9. **Don't take it back.** Anchor. You take their hanging queen (20s → 41s, them 30s → 5s,
   clip 1's Anchor line). They recapture your queen on the next ply: they −4 +25 = **26s**,
   you −25 = **16s**. You were winning the economy for one move and lost the game by taking a
   queen that wasn't free. The recapture is the punchline, not the first take.

10. **Bounty refill.** Winder (Bounty, 1+0). Trailer on **8s** takes a knight with a pawn:
    +10s − 1s toll = **+9s**, 8 → 17, opponent's clock unchanged. They were dead, now they
    aren't, and nobody was stolen from. The casual-pool version of clip 1.

11. **The 100-second queen.** Eclipse (Steal, C4). A queen is **±100s**. Trailer 80s, leader
    90s, QxQ: 80 − 4 + 100 = **176s**, 90 − 100 → **flag**. Same story as Fulcrum clutch,
    one capture is a full minute. Use if 1's Fulcrum cut isn't loud enough.

12. **Rook ladder they can't afford.** K+2R vs K, conversion bill ~24s, defender already
    flagged-looking on ~15s. You have mate in the bag and not enough clock to ladder it -
    same moral as 5, but a rook ending reads faster than K+Q vs K (8 rook moves vs 15 queen
    moves). Pair with 5 or pick one.

13. **King hunt on chips.** No captures. Three pawn or knight checks in a row (each check is
    −2s on them, +2s gap for a knight, +3s for a pawn). Start them on ~6s, they flag from
    being checked, not taken. Rule 3 as a finish rather than a demo.

14. **Bare-king escape.** You are winning on the board, they shed to a lone king and race your
    flag. Your flag falls; insufficient material is a **draw**, not their win. The desperate
    defender's finish. Quiet, but it's the FIDE-shaped twist the rules page already sells.

## Regenerating (once a desktop take exists)

```sh
cd e2e
npm run stack:up
npx playwright test --config=playwright.presskit.config.ts
npm run stack:down
cd ../press-kit
node convert.mjs
```
