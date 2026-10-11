# AD.3 - the chessvariants.com catalogue entry, drafted

> **Drafted 2026-08-18. Numbers rechecked 2026-10-09** against live `/rules` and
> `npm --prefix ui run gen:rules:check` (clean). [CLAIMS.md](../../game/CLAIMS.md) calls this
> "probably the single highest-leverage move available". The public development repo
> ([github.com/MichielGekiere/kirichess](https://github.com/MichielGekiere/kirichess)) is a clean
> import with no private history, so it does **not** prove a 2026-07-21 invention date; this
> catalogue entry remains the earliest dated, third-party-hosted record of the invention that can
> establish priority. Numbers below are taken from [RULES.md](../../RULES.md), generated
> from `config/kirichess.rules.json`, the same document the engine loads at runtime - if a rule is
> retuned before this is sent, regenerate RULES.md and re-check the numbers here.
>
> **What this draft does not confirm.** chessvariants.com and its `.org` mirror both returned
> HTTP 403 (most likely bot protection rather than the site being down), and a web search did not
> surface a current "submit a variant" page with a specific address or form. The submission route
> still has to be found on the site. Historically the site has taken new-variant pages by email to
> its editor for posting, and separately runs a moderated "Invention/Design Competitions" and
> reader-submission track, but neither is confirmed current fact from this draft; check the site's
> own contact/submit page. What follows is the **content** to send once that route is found: a
> page-ready description in the site's usual register (rules stated plainly, not marketing copy)
> plus a shorter cover-note version for an email or form with a body-length limit.
>
> **Inventor name:** Michiel Gekiere (filled below). Every later citation points back to whatever
> name is on this entry.

## Before you send

- [x] Confirm the numbers below still match a freshly generated [RULES.md](../../RULES.md)
      (`npm --prefix ui run gen:rules:check` ok 2026-10-09 against live `/rules`). Re-check if a
      rule is retuned before sending.
- [x] Inventor name: Michiel Gekiere (filled below).
- [x] Cloudflare Access is off (roadmap **#12**, 2026-10-08) so `kirichess.com` and `/rules` load
      for the editor. Link the public repo
      [github.com/MichielGekiere/kirichess](https://github.com/MichielGekiere/kirichess) if a
      source URL helps; never link `kirichess-archive`.
- [ ] Find the current submission route on chessvariants.com itself and send the entry below
      through it.
- [ ] Once posted, save the resulting page's URL and record it in [CLAIMS.md](../../game/CLAIMS.md)'s
      recognition checklist, next to this action.

---

## The catalogue entry

> Copy from here down for the full page-style submission.

**Name:** KiriChess

**Inventor:** Michiel Gekiere

**Date invented:** February 2026 (first commit in the working repo was 2026-07-21 under the old
name "Clockstarved"; later renamed KiriChess)

**Board and pieces:** Normal 8×8 board, normal pieces, no new pieces and no changes to how pieces
move. Normal chess still applies (movement, check, mate, stalemate, castling, en passant,
promotion), unless a rule below says something else. If you can read normal chess notation, you can
read a KiriChess game.

**The idea in one line:** the clock is not only a timer. It is a resource that moves between the
two players during the game.

**Four rules, always in the same order:**

1. **Captures move time.** Taking a piece is worth time based on how strong it is: pawn 5s, knight
   or bishop 10s, rook 15s, queen 25s (base numbers). A game can use a *capture multiplier* (C1–C4)
   that scales these values. Rated pools pick the multiplier separately from the time control. What
   happens with the time depends on the *economy*:
   - **Steal**: you gain it, the opponent loses it. Time moves across; only winning material changes
     the gap between the clocks.
   - **Bounty**: you gain it, nobody loses. Captures add time to the game.
   - **Burn**: the opponent loses it, you gain nothing. Even equal trades take time from both
     clocks.
2. **Every move costs time from your own clock**, based on the piece you moved. These costs do not
   change with the economy: pawn or king 1s (king is cheap so endgame king moves stay okay), knight
   or bishop 2s, rook 3s, queen 4s, castling a flat 3s (not 4s).
3. **Giving check moves 2s from the checked player to the checker**, if the position is new. You
   still pay the move cost from rule 2. A queen check basically pays for itself (the gap does not
   change). A pawn check is good for you (+3s on the gap). Double check pays the check time twice.
4. **If the same position already happened, there are no time effects** — no capture time, no move
   cost, no check time. Without this, perpetual check can farm a clock down to zero without
   improving the board. Once a position has been seen, it stays "off" for the rest of the game.
   Threefold repetition is an automatic draw (no claim needed).

**Why this is interesting to me:** mate needs moves, and every move has a cost under rule 2 (and
that cost never changes between rated pools). So a won position has a price: king and rook vs a lone
king is about 70s of move costs, king and queen about 48s, bishop and knight about 88s. That number
is the same in every rated pool. You can be totally winning on the board and still not have enough
time to finish. So you have to plan the endgame *before* you trade into it.

**Rated pools:** six pools with the three economies above, different time controls, and different
capture multipliers, from 1-minute Bounty (Winder) up to 15-minute Steal (Eclipse). Custom games can
change these settings, including the move-cost and check tables, if people want to try their own
version.

**Play it:** free, no ads, at https://kirichess.com. Full rules (numbers from the same config the
server uses): https://kirichess.com/rules.

**Licence:** the rules text / manual is under
[CC BY 4.0](https://creativecommons.org/licenses/by/4.0/) — you can reuse and adapt it if you give
credit. Game rules themselves are not copyrighted; the licence is for the wording. Platform source
is Apache-2.0. The working repo is not public. Play / read rules at https://kirichess.com and
https://kirichess.com/rules.

---

## Cover note (short form, for an email or a form with a length limit)

> Copy from here down if the submission route turns out to be a plain message rather than a
> page-style entry.

Hi, I would like to submit an original variant, **KiriChess**, to the catalogue.

I am a student and I built this as a hobby. The short idea: it is normal chess (same board, same
pieces, same moves), but the clock is a resource that moves between the players, not only a timer
that goes down. Captures are worth time by piece strength (pawn 5s up to queen 25s at base), every
move costs time from your own clock based on the piece you moved, giving check moves 2s from the
checked player to the checker, and if the same position already happened there are no time effects
(so perpetual check cannot farm a clock to zero). Something I did not fully expect: mate needs
moves, moves cost time, so a winning position has a price (about 70s for king and rook vs a lone
king, about 88s for bishop and knight). You can be totally winning on the board and still not have
enough time to finish.

Invented by Michiel Gekiere in February 2026 (first commit 2026-07-21 as Clockstarved, later
renamed KiriChess). Free to play at https://kirichess.com. Full rules (from the same config the
server uses): https://kirichess.com/rules.

Happy to send more if you need it for a listing — a diagram, a longer text, whatever format you
use.

Thanks,
Michiel Gekiere
