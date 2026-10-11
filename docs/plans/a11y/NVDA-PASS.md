# 9f.2 - the NVDA pass, scripted

> **This is the part of 9f.2 that has to be heard.** The code is built, typechecked, unit-tested
> and keyboard-verified; what has never happened is anyone *hearing* it. This document turns that
> from an open-ended "try a screen reader" into about 40 minutes of numbered steps with an expected
> utterance next to each, so you are confirming a checklist rather than exploring.
>
> Prepared 2026-08-17 by reading [lib/a11y.ts](../../../ui/lib/a11y.ts) and
> [components/AccessibleBoard.tsx](../../../ui/components/AccessibleBoard.tsx). Refreshed 2026-09-04
> for P-5-4 (selection is spoken), P-5-5 (`c`/`m` in the panel text), P-5-6 (promotion prompt), and
> P-5-8 (draw / rematch / presence / game-over overlay). Every "expected" string below is what the
> code actually builds, not what the design document hoped for.

## Setup, once

1. Install **NVDA** (free): [nvaccess.org/download](https://www.nvaccess.org/download/). Take the
   portable copy if you would rather not install.
2. Turn on the **Speech Viewer**: NVDA menu (`NVDA` key + `N`) → Tools → Speech Viewer. It prints
   every utterance as text in a window. **Do not skip this.** It is what lets you paste an exact
   wrong string into a bug report instead of describing a sound, and it makes the whole pass
   reviewable afterwards.
3. Browser: **Chrome or Edge**. Firefox is also fine. Test one, not all three.
4. Run the app against a real game. You need two clients to play both sides: a normal window and a
   private window signed in as a second account, or a bot game if that is quicker.
5. Useful NVDA keys, and this is all you need:
   - `NVDA` key is `Insert` (or `CapsLock` if you chose that at install).
   - `NVDA + Space` toggles **browse mode** and **focus mode**. This matters, see step 4 below.
   - `Ctrl` silences speech immediately. Use it constantly.
   - `NVDA + Q` quits.

**Record as you go.** Fill in the results table at the bottom. A step you skipped is not a pass.

---

## A. Finding the board at all

| # | Do this | Expect to hear | P/F |
| - | ------- | -------------- | --- |
| 1 | Open a live game page | Page title and content read normally, nothing about a second board yet | |
| 2 | Press `Tab` repeatedly until focus reaches the accessible board | "Accessible chess board. You are playing White." (or Black), then "Your move." or "Waiting for opponent.", then "Arrow keys to navigate, Enter to select, C for clocks, M for last move, question mark for help." | |
| 3 | Look at the screen at that moment | The board **becomes visible** as a bordered panel. It was `sr-only` until now. This is deliberate: it serves a sighted keyboard user too | |

**Step 4 is the one most likely to fail, and it is not a wording problem.**

| # | Do this | Expect to hear | P/F |
| - | ------- | -------------- | --- |
| 4 | With a square focused, press `ArrowRight` | The next square to the right, for example "f1, empty" | |

If instead NVDA reads the *next line of the page*, or says nothing useful, the arrow keys are being
eaten by **browse mode** before the app ever sees them. Press `NVDA + Space` to switch to focus mode
and try again.

- Works in focus mode only → **record it as a finding, not a pass.** A blind player should not have
  to know to do that. The fix is a real decision (`role="application"` on the grid, or an explicit
  instruction in the panel text), and it belongs to whoever acts on this pass.
- Works straight away in browse mode → pass, and note which browser, because this behaviour differs
  between them.

## B. Moving around

| # | Do this | Expect to hear | P/F |
| - | ------- | -------------- | --- |
| 5 | Arrow up, down, left, right a few times | Each new square named, for example "e2, white pawn" | |
| 6 | Arrow left repeatedly past the a-file | Focus **stops** at the a-file. It must not wrap to the h-file. Silence or a repeat of the same square is correct here | |
| 7 | Press `Home`, then `End` | File a, then file h of the current rank | |
| 8 | Press `PageUp`, then `PageDown` | Rank 8, then rank 1 | |
| 9 | Navigate to an empty square | "d4, empty" | |
| 10 | Navigate to an occupied square | "d8, black queen". Colour is named here on purpose: this is describing the board, not the listener | |

## C. The cost preview, which is the whole point of the feature

This is the part with no prior art. Take it slowly.

| # | Do this | Expect to hear | P/F |
| - | ------- | -------------- | --- |
| 11 | Focus one of your own pieces that has legal moves, press `Enter` | "e2 selected, 2 legal targets." (count is unique destination squares). Legal targets now carry their price | |
| 12 | Arrow onto a legal **empty** target | "e4, empty" plus a cost clause if the move is priced, for example "costs you 1 second" | |
| 13 | Arrow onto a legal **capture** target | "e5, black pawn, capture, costs you 2 seconds" or "...gains you 5 seconds", depending on the pool's economy | |
| 14 | Check the number against the visual board's own cost preview | They must agree. Both come from `costFor`, so a mismatch is a wiring bug, not a wording one | |
| 15 | Press `Escape` | "Selection cleared." | |

**What you are judging here** is whether the price arrives early enough to be a decision rather than
a narration. If you find yourself committing a move and only then learning what it cost, that is a
failure of the feature even if every string is correct.

## D. Playing a move, and the one-utterance rule

| # | Do this | Expect to hear | P/F |
| - | ------- | -------------- | --- |
| 16 | Select a piece, arrow to a legal target, press `Enter` | The move plays | |
| 17 | Listen to the announcement of your own move as **one** utterance | "Knight takes e5 pawn. You minus 2, opponent plus 4. Check." Move, then price, then consequence, in that order | |
| 18 | Have the opponent play a capture | The same shape, from your side: "you" and "opponent", never "White" and "Black" | |
| 19 | Play a quiet move that changes nobody's clock | The economy clause is **absent** entirely. It must never say "plus 0" | |
| 20 | Play a capture where only one side's clock moves | Only that side's clause is spoken | |
| 21 | Castle | "Castles kingside." (or queenside) | |
| 22 | Promote a pawn | First: "Promote on e8: press Q for queen, N for knight, B for bishop, or R for rook." Press `Q` or `Enter` for queen, `N`/`B`/`R` to under-promote. Then the move utterance, for example "Pawn to e8, promotes to queen." | |

**The failure to listen for in 17:** the move and its price arriving as two separate announcements,
with a gap you could interrupt. If you can `Ctrl` between them and lose the price, that breaks the
design's central rule and is a genuine defect.

## E. Refusals, which is where these features usually fail silently

| # | Do this | Expect to hear | P/F |
| - | ------- | -------------- | --- |
| 23 | Select a piece, arrow to an **illegal** square, press `Enter` | "No legal move from e2 to e5." Silence here is the single most common accessibility failure in this pattern | |
| 24 | `Tab` to the move-entry input, type `e2e4`, press `Enter` | The move plays. `e2-e4` and uppercase must work too | |
| 25 | Type `hello`, press `Enter` | "\"hello\" isn't a move in the form e2e4." | |
| 26 | Type a well-formed but illegal move such as `a1a8`, press `Enter` | "No legal move a1a8." | |

## F. Clocks, on demand and interrupting

| # | Do this | Expect to hear | P/F |
| - | ------- | -------------- | --- |
| 27 | Press `c` | "You 1 minute 32 seconds, opponent 2 minutes 5 seconds. You are behind by 33 seconds." Never "1:32", which reads as a date | |
| 28 | Press `c` when level | "You are level." | |
| 29 | Press `m` | The last move, repeated in the same one-utterance form | |
| 30 | Press `?` | The key summary | |
| 31 | Press `Ctrl+T` (or any browser shortcut) while the grid has focus | The browser acts normally. The letter keys above must not swallow modifier combinations | |

**The urgency test needs a deliberately short game.** Use the **Zeitnot** pool (1+0), or spend your
clock down on purpose.

| # | Do this | Expect to hear | P/F |
| - | ------- | -------------- | --- |
| 32 | Let your own clock fall through 60, 30, 15 and 10 seconds | An announcement at each, **interrupting** whatever is being said | |
| 33 | Under 5 seconds | Every second: "5 seconds", "4 seconds"... | |
| 34 | Win time back above a threshold, then fall through it again | It announces **again**. This variant hands time back, and a second crossing is a real crossing | |
| 35 | Let the **opponent's** clock fall through the same thresholds | **Nothing.** Announcing their flagfall risk at the same urgency makes the channel useless | |

Step 32 is the highest-stakes judgement in this pass: an announcement that arrives late, or that
gets cut off by the next one, is worse than no announcement, because it costs you the seconds it was
warning about.

## G. Read-only surfaces

| # | Do this | Expect to hear | P/F |
| - | ------- | -------------- | --- |
| 36 | Open a replay page, `Tab` to the board | "Accessible chess board, read-only." then "Read-only board. Arrow keys to navigate." | |
| 37 | Arrow around, press `Enter` on a piece | Navigation works, selection does **not** happen, no move input exists | |
| 38 | Same on a watch/spectate page | Same as 36 and 37 | |

## H. Live events the original script missed

These fire from the game page, not from the board grid. Keep the board focused so you hear the live region. Two clients, as in setup step 4.

| # | Do this | Expect to hear | P/F |
| - | ------- | -------------- | --- |
| 39 | From the other client, offer a draw | "Your opponent offers a draw." Assertive - it should interrupt. The visual banner is not enough | |
| 40 | Disconnect the other client (close the private window), then reopen it | Disconnect: "Opponent disconnected. They forfeit in 60s if they don't return." (grace is `Games:ResumeGraceSeconds`, default 60). Reconnect: "Opponent reconnected." | |
| 41 | Play to a rated result (checkmate is fastest) | One assertive utterance in the form "Checkmate. White wins." Unrated: no rating clause. Rated: "Checkmate. White wins. Rating: 1500 to 1508, +8." Numbers will differ; the shape must not | |
| 42 | After the result, do not Tab the page | Focus is on the overlay heading (the termination label, e.g. "Checkmate"). The overlay is a dialog. Tab cycles Offer rematch / New game / View & share replay / Report opponent, and wraps. Escape does **not** dismiss it | |
| 43 | Offer a rematch from this overlay; listen on the other client | The other client hears "Your opponent wants a rematch." and the overlay there shows Accept rematch | |
| 44 | Accept the rematch | Both clients navigate to the new game. No leftover overlay | |

Step 42 is the post-game half of step 4: if the result is spoken but focus stays on a now-inert board cell, record it as a finding. The overlay was given dialog semantics and a focus move-in specifically so this step can pass.

---

## How to judge what you hear

Three different verdicts, and keeping them apart is what makes this pass useful:

1. **Broken** - nothing spoken, the wrong square, a number that disagrees with the visual board, an
   announcement that never arrives. File it, it is a bug.
2. **Correct but unusable** - everything is spoken, in the right order, and it is still too slow, too
   long, or too late to act on. **This is the outcome the whole item exists to catch**, and it is the
   only one that no automated check could have found. Say so plainly, even if you cannot say what
   would be better.
3. **Wording you would phrase differently** - real feedback, lowest priority. The four design rules
   (one utterance per move, deltas pushed and absolutes on demand, urgency interrupts on your clock
   only, price spoken before commitment) are the part worth arguing with; the exact adjectives are not.

If you get someone who uses a screen reader daily to sit with this instead, their verdict outranks
every line above, and steps 4, 17, 32 and 42 are the four to put in front of them first.

## Results

| Section | Steps | Result | Notes |
| --- | --- | --- | --- |
| A. Finding the board | 1-4 | | |
| B. Moving around | 5-10 | | |
| C. Cost preview | 11-15 | | |
| D. Playing a move | 16-22 | | |
| E. Refusals | 23-26 | | |
| F. Clocks and urgency | 27-35 | | |
| G. Read-only | 36-38 | | |
| H. Live events and overlay | 39-44 | | |

**Date of pass:**
**Browser and NVDA version:**
**Overall verdict:**
