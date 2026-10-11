<!-- GENERATED FILE, DO NOT EDIT. Wording: the /rules page (`ui/messages/en.json`). Numbers: `config/kirichess.rules.json`. Run `npm --prefix ui run gen:rules`. -->

# Chess, but time is a resource.

## How it changes the way you play

The first thing that changes is that no move is free anymore. Every move costs time, so every move comes with a question: is this worth x seconds?

The second thing is that a piece stops having only one price. A knight is worth 3 points in chess and that number never moves all game. Here the same knight has three more prices: what your opponent gets if they take it, what it costs you every time you move it, and what it can earn you if it starts giving check.

The third one takes the longest to learn: time stops being just how you lose, and starts being something you can take from each other. Your opponent can go after your clock with checks, with trades, with a piece you hang. You have to defend it, and you can also attack theirs.

KiriChess adds a second question to every move: what does this do to the clocks? Sometimes those two answers disagree: the objectively correct move is a trade you can't afford, or a "bad" move turns out to be right because it eats your opponent's time.

Which is why games here don't just end the two ways they do in normal chess. You still get mates, and you still get the last-second scramble where someone's flag falls. There's a third ending: a forced flagfall. A plan that might not threaten mate or win a single pawn, but drains your opponent's time until their flag falls in a position that looks completely fine on the board.

## What hasn't changed

Everything from chess still applies unless this manual says otherwise: how pieces move, check, checkmate, stalemate, castling, en passant, promotion, threefold repetition, the fifty-move rule, and a draw when neither side can mate. If you can play chess, you already know most of KiriChess. This manual is the time economy layered on top that changes the decisions you make.

The rules stack in a fixed order: capture economy at the base, movement toll on top of it, the check chip on top of that, and a repetition guard wrapping all three so none of them can be farmed.

## The Four Rules of KiriChess

Three rules move the clocks: captures, moves, and checks. The fourth is the protection rule, repetition, which shuts those three off if the position has already been seen, so you can't farm free time by looping.

### 1. Captures Are Time

Capturing a piece moves the clock. How much, and who it moves it for, depends on the pool's economy. Bigger pieces are worth more seconds.

**Steal.** Captor **gains**, victim **loses**. Time transfers from victim to captor, only winning material moves the gap.

**Bounty.** Captor **gains**, nobody loses. Capturing adds time; games run longer.

**Burn.** Victim **loses**, captor gains nothing. Even equal trades drain both clocks.

| Piece captured | Steal | Bounty | Burn |
| --- | ---: | ---: | ---: |
| ♟ Pawn | ±5s | +5s | −5s |
| ♞ Knight | ±10s | +10s | −10s |
| ♝ Bishop | ±10s | +10s | −10s |
| ♜ Rook | ±15s | +15s | −15s |
| ♛ Queen | ±25s | +25s | −25s |

Values shown at base scale (C1). Rated pools apply a capture multiplier on top of these numbers, see [The 6 pools](#the-six-rated-pools). En passant counts as a normal pawn capture.

### 2. Tolls on Power

Every move costs time off your own clock, priced by the piece you move. A queen move is expensive; the king is cheap so king activity in the endgame stays affordable.

| Piece | Toll per move |
| --- | ---: |
| ♟ Pawn | 1s |
| ♞ Knight | 2s |
| ♝ Bishop | 2s |
| ♜ Rook | 3s |
| ♛ Queen | 4s |
| ♚ King | 1s |
| ⟲ Castling (either side) | 3s |

**Tolls never scale.** The toll table above is what you pay in every rated pool, regardless of its capture multiplier.

**Castling is a flat discount.** Priced individually, castling would move a king (1s) and a rook (3s) for 4s total. Instead it's a single flat 3s fee, the same for either side, since castling is almost always a good move.

**Zugzwang has a price tag.** In chess, being forced to move is sometimes only positionally painful. In KiriChess, you also pay for every forced move.

**The average move costs about 2 seconds.** Weighted by real-game move frequency, that's about 81 seconds of toll per player over a 40-move game. The queen and rook together make only ~31% of all moves but pay ~52% of all toll.

### 3. The Check Chip

Delivering check in a new, non-repeated position chips 2 seconds off the checked player and onto the checker. A check that is mate pays no chip. Double check pays 2×, a discovered attack that checks with two pieces at once steals 4s at base. The checking move still bills its own toll, so not every check turns a profit.

> your clock = −toll + chip, their clock = −2s

| Check by | Gap swing |
| --- | ---: |
| Queen | ±0 |
| Rook | +1 |
| Knight / Bishop | +2 |
| Pawn | +3 |

The cheaper the piece delivering the check, the more profitable the attack. Because tolls and chips never scale, this table is true in every rated pool.

### 4. The Repetition Guard

If the position on the board has occurred earlier in the game, the move triggers no time effects at all: no capture value, no toll, and checks stop chipping. Without this guard, perpetual check would farm your opponent's clock to zero without ever improving the position.

- A "position" follows the official chess definition: piece placement + side to move + castling rights + en passant availability.
- Once a position has been seen, it is nullified for the rest of the game, you can't dodge the guard by alternating between two different repetition patterns.
- Threefold repetition is automatic: the instant a position occurs for the third time, the game is drawn immediately, no player action required.

In a healthy game you'll rarely notice this rule.

### Other events

**Promotion is free.** The promoting move pays only the ordinary pawn toll (**1s**), nothing extra for the new piece. The pawn already paid the march tolls, and the new queen is priced by Rule 1 like any other queen.

**En passant.** Treated exactly as a normal pawn capture: same economy, same **±5s** at base, scaled by the pool's capture multiplier like any other capture.

### How games end

**Checkmate** - WIN. Immediate win, even if the mating move's toll would have flagged you. The toll still comes off your clock. Mate is decided before the flag.

**Opponent's flag falls** - WIN*. Win, unless you hold only a king, or a king and a single bishop or knight, in which case it's a draw.

**Flag vs insufficient material** - DRAW. The flag falls, and the player who would have won holds only a king, or a king and a single bishop or knight. Draw.

**Stalemate** - DRAW. No legal move, not in check, same as classical chess. It is still a draw if that move would have flagged you.

**Threefold repetition** - DRAW. Draw, no claim needed, the game ends the instant the third occurrence is reached. It outranks the flag, the same way mate does.

**Fifty-move rule** - DRAW. Fifty moves by each side with no capture and no pawn move. Draw, as in classical chess. It outranks the flag.

**Insufficient material** - DRAW. Neither side can mate. Draw, the moment the position is dead. It outranks the flag.

**Draw by agreement** - DRAW. Both players agree. Draw.

**Resignation** - LOSS. You lose.

**Abandonment** - LOSS. You leave the game and don't return. You lose.

**You run out of time** - LOSS. Your clock hits zero. You lose, unless the opponent holds only a king, or a king and a single bishop or knight, in which case it's a draw.

Steal and Burn fight draws in different ways. In Steal, the player behind has to win material (or chip) to get time back, which forces contact. In Burn, the player ahead can force trades the short clock cannot afford. Bounty is the exception: captures refill instead of draining. Tolls still run in every pool, so a totally quiet game still ends on the clock.

## The economy, deep dive

How the three economies price the same events differently, and how that changes the way you play.

### The price of victory: mate costs money

Delivering checkmate takes moves, and moves cost time. Approximate time costs for the basic mates:

| Endgame | Moves | Bill |
| --- | ---: | ---: |
| K+Q+R vs K (Ladder, heavy pieces) | ~6 | ~21s |
| K+2R vs K (Pure rook ladder, king never moves) | ~8 | ~24s |
| K+Q vs K (Queen boxes, king walks) | ~15 | ~48s |
| K+2B vs K (Bishops herd, king supports) | ~28 | ~48s |
| K+P vs K (Escort the pawn, then the queen bill) | ~25 | ~58s |
| K+R vs K (Rook boxes, king shoulders) | ~28 | ~70s |
| K+B+N vs K (The hardest mate in chess) | ~50 | ~88s |
| K+Q vs K+R (Queen-heavy grind) | ~30 | ~105s |

Practical move counts vary with piece placement and defender resistance; bills are the toll cost of that many moves at base rates, not a hard guarantee.

Because tolls never scale, the conversion bill is the same in every rated pool. So while playing that means you can be completely winning on the board and unable to afford the win. If you reach K+R vs K with less than 70 seconds on your clock, you will flag mid-conversion if your opponent doesn't slip up.

1. Budget your endgame before you enter it. Before liquidating into a winning ending, check whether your clock can cover the conversion bill.
2. Mate in the middlegame is cheaper than mate in the endgame. A sharp, clean mating attack costs a handful of tolls; a technical grind costs a fortune.
3. Know the technique, don't improvise it. The costs above assume the textbook method, driving the king to the edge with no wasted moves. If you don't convert efficiently you'll run out of time.

### Trade math per economy

The three main economies price the same knight trade completely differently:

| Economy (gain, loss / pt) | You after trading knights | Them | Gap |
| --- | ---: | ---: | --- |
| Steal (5, 5) | −10 +10 = 0 | 0 | unchanged |
| Bounty (5, 0) | +10 | +10 | both profit |
| Burn (0, 5) | −10 | −10 | both drain |

Under Burn, an equal trade drains 10 seconds from both players, and "can I even afford to trade?" becomes a question. Under Bounty, trades inflate both clocks and games run longer the more material is captured. But your opponent might be low on time, making an equal trade disadvantageous. Under Steal, only genuine material wins matter.

### Why the queen costs 4 and not 5

By raw material logic, the queen (worth a rook + bishop) "should" cost 5s to move if the rook costs 3 and the bishop 2. She's discounted for two reasons. First, tolls are paid per move, and the queen is the most-moved piece in most attacks: linear pricing would tax attacking chess into the ground; a ten-move queen attack at Q=5 costs 50 seconds on top of the double price sacrifices already pay. Second, the check table above. At Q=4, queen checks are exactly self-financing. At Q=5 they'd be gap-negative: attacking with your queen would lose you time even while giving checks.

## The six rated pools

Each pool locks in a time control, an economy, and a capture multiplier (C). The multiplier scales capture transfers/burns only, tolls and chips stay at base in every pool, and promotion is free everywhere. The time control decides how much you get to think; the multiplier decides how hard the material war hits.

| Pool | Time | Multiplier | Economy |
| --- | ---: | ---: | ---: |
| Winder | 1+0 | C1 | Bounty |
| Ratchet | 4+0 | C1 | Burn |
| Fulcrum | 5+0 | C2 | Steal |
| Anchor | 6+0 | C1 | Steal |
| Mainspring | 15+0 | C2 | Burn |
| Eclipse | 15+0 | C4 | Steal |

Custom configurations are always available for unrated games.

### Winder

*1+0 - Bounty - C1*

Captures pay both players and nobody loses time to them. You initially start with a low time, but your time grows, blundering a piece is survivable, and passive play isn't punished by the economy. A decent on-ramp, though not all its habits carry over to the other pools.

Two capture-happy players can stretch a "1-minute" game to around 1.8 real minutes.

**Rewards:** Activity, capture-hunting, king harassment.

**Punishes:** Nothing: it forgives everything else.

### Ratchet

*4+0 - Burn - C1*

The clearest version of the Burn economy. Nobody gains time from captures; every point of material that dies takes 5 seconds off its owner's clock. Equal trades are gap-neutral but drain both players: a full queen trade costs each side 25 seconds, so total time on the board only ever goes down.

The player with the more time can afford a war of attrition; the player behind faces the Burn dilemma on every capture: can I even afford to trade? Because tolls stay at base, Ratchet's 4-minute clock keeps pressure focused entirely in the trades.

**Rewards:** Clock-aware trade selection, knowing when not to resolve tension.

**Punishes:** Reflexive recapturing.

### Fulcrum

*5+0 - Steal - C2*

Fulcrum plays exactly like Anchor until the first capture. Tolls and chips are identical, so the pace of quiet play is indistinguishable. But this gamemode has a C2 capture economy, a hung bishop is 40 seconds of gap, a lost queen is 100, in a 5-minute game.

Fulcrum is the more deterministic pool: the material war decides more, and harassment rescues less.

**Rewards:** Deep calculation before contact.

**Punishes:** Optimism.

### Anchor

*6+0 - Steal - C1*

The reference game, and the pool the whole economy was tuned around. Material converts directly into time at the base rate; equal trades are gap-neutral, so only real material wins move the war.

The check table and the conversion bill describe Anchor exactly as written, and every other rated pool as well, because tolls and chips stay at base. The capture table is the C1 table, which is Anchor's multiplier. The other pools change the clock and at least one more dial. Mainspring changes the clock, the economy and the multiplier.

**Rewards:** Balanced two-board thinking.

**Punishes:** Hanging pieces, aimless queen play, unbudgeted endgames.

### Mainspring

*15+0 - Burn - C2*

Mainspring plays with a 15-minute clock and base tolls, so the opening is slow and you have time to think. But this gamemode has a C2 Burn economy: nobody gains time from captures, every point of material that dies takes 10 seconds off its owner's clock, and a queen trade costs each side 50 seconds that neither player gets back.

The player with more time can afford to trade; the player behind has to leave the tension. Once pieces start coming off, both clocks only go down.

**Rewards:** Building a clock lead before trading, leaving tension when you're behind.

**Punishes:** Opening a position you can't afford to recapture in.

### Eclipse

*15+0 - Steal - C4*

Eclipse plays with a 15-minute clock and base tolls, so you get around 20 seconds to think about each quiet move. But this gamemode has a C4 capture economy: a pawn swings the gap 40 seconds, a rook 120, a queen 200, which is 22% of a starting clock.

Checks still pay 2s, so king harassment barely moves a gap this size. A material deficit here is close to final.

**Rewards:** Calculating captures before they happen, keeping pieces defended.

**Punishes:** Hanging a piece and expecting checks to pay it back.

## Tempo and duration: how each pool feels

### The thinking budget

The clock number on the pool label is not how long you get to think. Tolls drain it, Burn drains it further, Bounty refills it. Subtract the economy from the pool and divide by a typical game (~40 moves per player) and you get each pool's real thinking budget, here is how I currently calculate it:

| Pool | Pool / player | Economy drain | Thinking / move | Feels like |
| --- | ---: | --- | ---: | ---: |
| Winder | 60s | ~81s tolls − ~75s bounty | ~1.4s | 1m bullet |
| Ratchet | 240s | ~81s tolls + ~75s burn | ~2.1s | 2m bullet |
| Fulcrum | 300s | ~81s tolls | ~5.5s | 3m blitz |
| Anchor | 360s | ~81s tolls | ~7s | 5m blitz |
| Mainspring | 900s | ~81s tolls + ~150s burn | ~16.7s | 10m rapid |
| Eclipse | 900s | ~81s tolls | ~20.5s | 15m rapid |

- The toll column is a constant (~81s per player), because tolls never scale, the only variable drain is Burn.
- The ladder is monotonic: 1.4 → 2.1 → 5.5 → 7 → 16.7 → 20.5 seconds per move. Thinking time rises with the time control.
- Averages hide divergence in high-multiplier Steal pools, Fulcrum's "5.5s" and Eclipse's "20.5s" describe the game, not either player. Once material falls unevenly, the winner has more thinking time than the loser.
- Burn drains are estimates of typical behavior, good Burn players trade less because trades cost them, so real Ratchet and Mainspring thinking budgets drift slightly above the table.

### Real-world game duration

How long a game actually lasts depends on the total time in the system, and on the level of play, beginners usually play shorter games (~25–35 moves) with heavier material flow and rarely use their full time; advanced players (~40–50 moves) push games toward the flag or a paid-for conversion.

| Pool | Nominal max | Effective ceiling | Beginner | Intermediate | Advanced |
| --- | ---: | ---: | ---: | ---: | ---: |
| Winder | 1 min | ~1.8 min | 1–1.5 min | 1–2 min | 1–2.5 min |
| Ratchet | 4 min | ~2.8 min | 1–2 min | 1.5–2.5 min | 2–2.8 min |
| Fulcrum | 5 min | ~7.3 min | 2–3 min | 3–5 min | 4–7 min |
| Anchor | 6 min | ~9.3 min | 4–7 min | 6–9 min | 8–10 min |
| Mainspring | 15 min | ~22 min | 9–13 min | 13–17 min | 16–21 min |
| Eclipse | 15 min | ~27 min | 10–15 min | 14–20 min | 18–26 min |

- Steal is zero-sum, so multipliers don't shorten the system clock. Fulcrum and Anchor have different ceilings because their time controls differ. What C2 changes is decisiveness: one blunder produces an unrecoverable gap and a fast flag.
- Ratchet runs well under the time on its label. Mainspring runs past it: advanced players who hoard material can stretch it near twenty real minutes. The beginner–advanced spread is widest in Burn, because beginners trade recklessly and collapse both clocks early.
- Winder is the only pool where better play makes longer games, strong players farming bounty push past the nominal ceiling.
- Eclipse has almost no system drain, so the ceiling is nearly the full half hour, though in practice a single C4 swing usually decides matters well before the clocks run down.

## Design notes

### Why capture-only scaling (Second Edition change)

The First Edition scaled the entire economy (F-mode: captures, tolls, and chips all multiplied) in the longer pools. Playtesting exposed a flaw: the toll multiplier silently taxes thinking time. An F2 pool didn't only double the stakes, it also doubled the toll cost of every move, draining minutes of pure system time. First Edition Fulcrum, marked as the careful-calculation pool, actually left less thinking time per move than Anchor; First Edition Eclipse destroyed over five minutes of its own pool in tolls and priced a basic rook mate at four and a half minutes, so winning players routinely flagged mid-conversion.

Capture-only scaling (C-mode) separates the two dials: the time control sets how much you think; the capture multiplier sets how hard the material war hits. Two properties follow: the check profitability table is universal (tolls and chips at base everywhere means "pawn check = +3, queen check = self-financing" is true in every rated game), and the conversion bill is universal (K+Q ≈ 48s, K+R ≈ 70s, in every pool).

F-mode still exists as a custom-game option for players who want tolls and chips scaled too.

### Why promotion is free (Second Edition change)

The First Edition charged time-value × 2 for the promoted piece. It was removed because the pawn already pays its way to the eighth rank in march tolls, and the new queen is instantly priced by Rule 1. A fee at the finish line taxed the same achievement a third time, and in low-clock endgames it punished the player for winning. Removing it also collapses the priced-event list to exactly three things: moves, captures, checks. The fee survives as a custom-game dial for players who miss promoting to a knight because it's all they could afford.

### Open dials (known trade-offs, watched in playtesting)

**Slow pools have weak toll pressure.** With tolls fixed at base, Eclipse's toll cost is ~9% of the pool (vs ~36% under old F4). Zugzwang-with-a-price-tag is a faint theme in the slow pools. If slow-pool players start shuffling for free, a hybrid setting (tolls ×2, captures ×4) is the escape hatch.

**Chip scaling is deliberately off, for now.** Because chips don't scale, high-multiplier pools have proportionally weaker comeback economies, making them more deterministic. If Fulcrum proves to feel like merely "Anchor but careful," scaling the chip with the capture multiplier (4s checks in C2) would restore comeback power proportional to stakes. That dial stays available.

**C4 concentrates variance into single moves.** A 200-second Eclipse queen swing can be strictly unrecoverable in a way the old distributed drain never was. It's the setting most likely to produce rage-quits, and the first place to soften (C3) if it does.

None of these are settled. Every dial above is exposed in custom games: different piece values, different tolls, F-mode, alternate capture personalities, so if you play one and it feels off, or you try a custom config that fixes something, that's the kind of feedback that's wanted.

## Glossary

**Clock gap.** Your time minus your opponent's.

**Flagfall.** Your clock reaching zero. You lose, unless the opponent holds only a king, or a king and a single bishop or knight.

**Toll.** The per-move fee for moving a piece (Rule 2). Never scales.

**Chip.** The 2-second check payment (Rule 3). Never scales.

**Steal / Burn / Bounty.** The three capture-economy personalities (Rule 1).

**The Guard.** The repetition rule (Rule 4) that stops time-farming.

**Capture multiplier (C-n).** The pool dial that scales capture transfers/burns.

**F-mode.** Full economy scaling (captures, tolls, and chips all multiplied). Custom games only.

**Thinking budget.** A pool's real per-move thinking time after economy drain: (pool − tolls ± economy) ÷ moves.

**Self-financing check.** A queen check whose gap stays at zero. Her toll is higher than the chip, and the checked player loses the chip, so both clocks fall together. True in every pool.

**Conversion bill.** The total tolls required to deliver mate from a winning position. ~48s for K+Q, ~70s for K+R, in every pool.

**Dead capital.** A repeated position; its checks and captures no longer pay.

## Licence

This rules text is licensed under a Creative Commons Attribution 4.0 licence. You are free to share and adapt it, including commercially, provided you give credit. “KiriChess” by Michiel Gekiere, licensed under CC BY 4.0. [View the full licence](https://creativecommons.org/licenses/by/4.0/).
