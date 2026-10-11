# KiriChess PGN

`GET /api/games/{id}/pgn` downloads a finished game as a `.pgn` file. This document specifies that
file's format for two audiences: someone writing a parser for it who has never seen this repository,
and a future maintainer deciding whether a proposed change is allowed. The format was designed and
approved in Phase 9e.2 ("The format"); this file is the
permanent public copy, reproduced near-verbatim from that record.

## What a KiriChess PGN is

A KiriChess PGN is two files in one: a completely ordinary chess PGN that Lichess's importer, SCID,
ChessBase and every hobby parser load without complaint, and a lossless KiriChess record. The
positions and the moves in a KiriChess game are standard chess, unmodified; only the clock economy is
different. So every KiriChess-specific fact is carried in a place the PGN standard says to ignore
(a brace comment), and nothing about the standard part is bent to make room for it.

Concretely: strip every `{ … }` comment out of a KiriChess PGN and what remains is a normal, complete,
loadable chess game. A parser that has never heard of KiriChess sees exactly that.

## A complete worked example

Copied verbatim from `PgnBuilder`'s own test output (`tests/KiriChess.Server.Tests/PgnExportTests.cs`),
a five-ply steal1-preset game, Black resigning after 3. Bb5 (hence `[Result "1-0"]` with
`[KiriChessTermination "Resignation"]`):

```
[Event "KiriChess rated Anchor game"]
[Site "https://kirichess.app/replay/236ad93a-e276-4258-8dec-8d4055ada596"]
[Date "2026.08.15"]
[Round "-"]
[White "Ada"]
[Black "Bo"]
[Result "1-0"]
[UTCDate "2026.08.15"]
[UTCTime "19:04:11"]
[WhiteElo "1523"]
[BlackElo "1487"]
[TimeControl "360+0"]
[Termination "Normal"]
[KiriChessFormat "1"]
[KiriChessId "236ad93a-e276-4258-8dec-8d4055ada596"]
[KiriChessPool "steal1"]
[KiriChessRated "true"]
[KiriChessTermination "Resignation"]

{ [%kcfg CLOCK_START_SECONDS=360;BASE_INCREMENT=0;ECONOMY_SCALE=1;PIECE_TIME_VALUES=1,2,2,3,5;CAPTURE_MULT=5;CAPTURE_CAPTOR_GAIN_ENABLED=true;CAPTURE_VICTIM_LOSS_ENABLED=true;MOVE_COST_MODE=PerPiece;MOVE_COST_PER_PIECE=1,2,2,3,4,1;MOVE_COST_FLAT=1;MOVE_COST_PER_SQUARE=1;CASTLE_COST_FLAT=3;CHECK_CHECKER_GAIN=2;CHECK_VICTIM_LOSS=2;DOUBLE_CHECK_CHIP_MULTIPLIER=2;PROMOTION_COST_MULT=0;REPEATED_POSITION_NULLIFIES_TIME=true;THREEFOLD_REPETITION_IS_DRAW=true;FLAG_FALL_LOSES=true;FLAG_VS_INSUFFICIENT_MATERIAL_IS_DRAW=true] }
1. e4 { [%clk 0:05:59] [%kc 359,360,-1,0] } 1... e5
{ [%clk 0:05:59] [%kc 359,359,0,-1] } 2. Nf3
{ [%clk 0:05:57] [%kc 357,359,-2,0] } 2... Nc6
{ [%clk 0:05:57] [%kc 357,357,0,-2] } 3. Bb5
{ [%clk 0:05:55] [%kc 355,357,-2,0] } 1-0
```

Delete every `{ … }` comment from the movetext above and what is left, `1. e4 1... e5 2. Nf3 2... Nc6
3. Bb5 1-0`, is a completely ordinary, complete chess game.

## The header block

The Seven Tag Roster (`Event`, `Site`, `Date`, `Round`, `White`, `Black`, `Result`) is always present,
in that order, per PGN §8.1.1. Every tag after it is either always present or always absent, never
sometimes one and sometimes the other for the same reason:

| Tag | When present | Value |
| --- | --- | --- |
| `Event` | always | `"KiriChess rated {pool} game"` for a rated game with a known pool, else `"KiriChess casual game"`. `{pool}` is the pool's **display name** (`Anchor`), not its key: this tag is prose. |
| `Site` | always | The replay URL (`{baseUrl}/replay/{id}`) when the server has a configured base URL, else `"?"`, PGN's own "unknown" marker. |
| `Date` | always | The game's creation date, UTC, `yyyy.MM.dd`. |
| `Round` | always | `"-"`. KiriChess games are not organised into tournament rounds. |
| `White` / `Black` | always | The player's display name, or "Unknown player" if the account no longer exists. |
| `Result` | always | `1-0`, `0-1`, `1/2-1/2`, or `*` (aborted). See Result mapping below. |
| `UTCDate` / `UTCTime` | always | The same creation timestamp as `Date`, split into the two tags mainstream tools also emit. |
| `WhiteElo` / `BlackElo` | only when the rating is known | The player's pre-game rating, rounded to the nearest integer. **Omitted, not `"?"`**, for an unrated game or a player with no rating history: an absent tag is correct, a `"?"` would read as "rated player, rating unknown", which is a different fact. |
| `TimeControl` | always | `"{startSeconds}+{increment}"`, the base time control only. The KiriChess economy layered on top of it is what `%kcfg` describes; `TimeControl` never tries to express it. |
| `Termination` | always | The PGN standard's own closed vocabulary (`Normal`, `Time forfeit`, `Abandoned`, `Unterminated`). See Termination mapping below. |
| `FEN` / `SetUp` | only together, only for a non-standard start | The exact starting FEN, and `SetUp "1"` alongside it. Never one without the other. |
| `KiriChessFormat` | always | `"1"`. See Stability below. |
| `KiriChessId` | always | The game's id, so a file is identifiable even when `Site` is `"?"`. |
| `KiriChessPool` | only when the game has a rating pool | The pool's **storage key** (`"steal1"`, `"bounty1"`, …), which is stable across renames. Match on this, never on `Event`. |
| `KiriChessRated` | always | `"true"` or `"false"`. |
| `KiriChessTermination` | always | The exact KiriChess termination reason (`entity.Termination.ToString()`), e.g. `"NoShow"`, which has no equivalent in the standard `Termination` vocabulary. |

**Why there is no `[Variant]` tag.** This was the closest call in the header block. The moves and
positions in a KiriChess game *are* standard chess: declaring a variant would make some tools refuse a
file they can read perfectly, for no benefit. `[KiriChessFormat "1"]` is the unambiguous marker a
KiriChess-aware parser keys on instead. Only the clock is unusual, and the clock is exactly what the
`%kc*` comment commands describe, not the moves.

### Result mapping

| `GameResult` | `[Result]` and the movetext's closing token |
| --- | --- |
| `WhiteWins` | `1-0` |
| `BlackWins` | `0-1` |
| `Draw` | `1/2-1/2` |
| `Aborted` | `*` |
| `Ongoing` | `*` (unreachable in practice: the export endpoint 404s an ongoing game before `PgnBuilder` ever runs) |

### Termination mapping

`[Termination]` carries a standard value because that tag has a defined vocabulary in the PGN spec,
and putting a KiriChess-specific string there would be a lie to every parser that reads it.
`[KiriChessTermination]` always carries the exact reason alongside it.

| `TerminationReason` | `[Termination]` | `[KiriChessTermination]` |
| --- | --- | --- |
| `None` | `Unterminated` | `None` |
| `Checkmate` | `Normal` | `Checkmate` |
| `Flagfall` | `Time forfeit` | `Flagfall` |
| `Stalemate` | `Normal` | `Stalemate` |
| `Resignation` | `Normal` | `Resignation` |
| `DrawByRepetition` | `Normal` | `DrawByRepetition` |
| `DrawByInsufficientMaterial` | `Normal` | `DrawByInsufficientMaterial` |
| `DrawByFiftyMove` | `Normal` | `DrawByFiftyMove` |
| `Abandoned` | `Abandoned` | `Abandoned` |
| `DrawByAgreement` | `Normal` | `DrawByAgreement` |
| `ServerRestart` | `Unterminated` | `ServerRestart` |
| `NoShow` | `Abandoned` | `NoShow` |

## The three comment commands

PGN already has a convention for a value that needs to ride inside a comment without confusing a
parser: a brace comment containing one or more `[%name parameters]` commands. `[%clk 0:03:00]` and
`[%eval 0.17]` are the two every mainstream tool already emits and skips; KiriChess adds `%kc`-namespaced
commands in the same place.

### `[%clk H:MM:SS]`

Standard, not KiriChess's own. The mover's clock *after* their move. `H` is not zero-padded; `MM` and
`SS` are, always two digits. Emitted on every ply so a mainstream tool shows a clock at all, even one
that has never heard of `%kc`.

### `[%kc w,b,dw,db]`

KiriChess's own. Four integers, seconds, comma-separated, no spaces, emitted on every ply:

1. `w` - White's clock after this ply. Clamped to zero: a clock cannot go negative.
2. `b` - Black's clock after this ply. Clamped to zero, same as `w`.
3. `dw` - the *nominal* economy amount applied to White by this ply (can be negative): captures,
   movement toll, check chip and the base increment, computed from the move and the config.
4. `db` - the nominal economy amount applied to Black by this ply (can be negative), same basis as `dw`.

All four are read straight off the persisted `MoveRecord` the game actually played with, never
recomputed, because a recomputed replay charges no think time and would silently disagree with the
real game's clocks the moment flag-fall or lag credit was involved.

**`dw`/`db` are not clamped to what a clock could actually absorb; `w`/`b` are.** When a capture or
check chip would drive a side's clock below zero, the clock in that ply's `w`/`b` still floors at
zero, but `dw`/`db` reports the full nominal amount the rule computed, which can be a larger negative
number than the clock actually had left to give. A tool that tries to reconstruct a clock history by
summing `dw`/`db` across plies will disagree with the `w`/`b` values in the same file from that ply
onward. The clocks (`w`, `b`) are the authoritative record of what a side actually had; `dw`/`db` are
the itemised charge, not a second copy of the same number.

**Deliberately not emitted**: the itemised events behind those two deltas (`CaptureGain`,
`MovementToll`, a check chip, …). Given the `%kcfg` config and the position, they are recomputed
exactly, which is what the replay endpoint already does; emitting them here would roughly triple the
file's size to duplicate a derivation. The line the format draws is: **the file records what cannot be
recomputed (the clocks, including wall-clock think time, and the config) and does not duplicate what
can be.**

### `[%kcfg KEY=VALUE;KEY=VALUE;…]`

KiriChess's own. Emitted exactly once, as the first token of the movetext, before move 1. The exact
rule config the game was played under, as an ordered, semicolon-separated `KEY=VALUE` list. No value in
it ever needs PGN escaping: every one is an integer, a lowercase boolean (`true`/`false`), a `MoveCostMode`
enum name, or a comma-separated list of integers, never a quote or a backslash.

It lives in a comment, not a tag, because of PGN §7's 255-character cap on a tag's string value: the
full config is roughly 470 characters. Splitting it across `[KiriChessEconomy1 …]` / `[…2 …]` tags
would be a self-inflicted wound for no reader's benefit. Comment text has no such limit, and a comment
positioned before the first move is well-formed movetext (mainstream tools already emit one, for
`%kcfg`'s stablemate `%kcfg`-shaped opening-book annotations).

Cost, stated plainly: it is one long line and it does not wrap, because a brace comment is a single
unbreakable token (see Line wrapping below). Accepted.

#### The key table

Twenty keys. **The order is fixed and is part of the contract: a future key may be appended, but an
existing key is never reordered, renamed, or removed.** A file already on someone's disk has this exact
order baked into its text; `KiriChessConfig.ToKeyValues()` is the single source of truth for it, and
`KiriChessConfig.FromDictionary` is the reader, keyed by name rather than position, so appending is
always safe even though reordering is not.

| # | Key | Type | Meaning |
| --- | --- | --- | --- |
| 1 | `CLOCK_START_SECONDS` | integer | Starting clock, seconds. |
| 2 | `BASE_INCREMENT` | integer | Standard per-move increment, seconds. |
| 3 | `ECONOMY_SCALE` | integer | Multiplier applied to every economy amount below. |
| 4 | `PIECE_TIME_VALUES` | 5 integers, comma-separated (pawn, knight, bishop, rook, queen) | Capture value per piece type. |
| 5 | `CAPTURE_MULT` | integer | Multiplier on capture economy. |
| 6 | `CAPTURE_CAPTOR_GAIN_ENABLED` | boolean | Whether the capturing side gains time. |
| 7 | `CAPTURE_VICTIM_LOSS_ENABLED` | boolean | Whether the captured side loses time. |
| 8 | `MOVE_COST_MODE` | enum: `None`, `Flat`, `PerPiece`, `Distance`, `PerPieceXDistance` | How the movement toll is computed. |
| 9 | `MOVE_COST_PER_PIECE` | 6 integers, comma-separated (pawn, knight, bishop, rook, queen, king) | Per-piece movement toll (used by `PerPiece`/`PerPieceXDistance`). |
| 10 | `MOVE_COST_FLAT` | integer | Flat movement toll (used by `Flat`). |
| 11 | `MOVE_COST_PER_SQUARE` | integer | Per-square movement toll (used by `Distance`/`PerPieceXDistance`). |
| 12 | `CASTLE_COST_FLAT` | integer | Flat cost that replaces the king's toll when castling. |
| 13 | `CHECK_CHECKER_GAIN` | integer | Time the checking side gains for a non-mating check. |
| 14 | `CHECK_VICTIM_LOSS` | integer | Time the checked side loses for a non-mating check. |
| 15 | `DOUBLE_CHECK_CHIP_MULTIPLIER` | integer | Multiplier applied to both of the above on a double check. |
| 16 | `PROMOTION_COST_MULT` | integer | Multiplier on the promoted piece's value, charged on promotion. |
| 17 | `REPEATED_POSITION_NULLIFIES_TIME` | boolean | Whether a previously-seen position cancels that move's economy entirely. |
| 18 | `THREEFOLD_REPETITION_IS_DRAW` | boolean | Whether three-fold repetition ends the game as a draw. |
| 19 | `FLAG_FALL_LOSES` | boolean | Whether running out of clock loses the game. |
| 20 | `FLAG_VS_INSUFFICIENT_MATERIAL_IS_DRAW` | boolean | Whether flagging against insufficient mating material is a draw instead of a loss. |

## Line wrapping

Lines wrap at 80 columns, on token boundaries: a move number, a SAN move, and a `{ … }` comment are
each one unbreakable token. When appending the next token would push the current line past 80
columns, a newline is inserted before it instead. A token that is itself longer than 80 characters
(the `%kcfg` comment, always) is placed alone on its own line rather than split, since splitting it
would corrupt the brace-comment grammar. Line endings are `\n` throughout, never `\r\n`, so the file's
bytes are identical whether it was generated on a CI runner or a Windows dev machine.

## Move numbering from a non-standard start

Most KiriChess games start from the standard position, and the first move is numbered `1.` for White,
`1...` for Black. A game whose `[FEN]`/`[SetUp]` tags are present instead takes its first move number
and its first side to move from that FEN's own fields (the fullmove number and the active-colour
field), not from move 1. Getting this wrong produces a file that fails to parse at all in a strict
reader, which is why it is pinned by a test
(`PgnExportTests.FEN_and_SetUp_appear_together_and_the_first_move_numbers_from_the_FEN`).

Black's move number is always repeated in the `N...` form after an intervening comment, because every
ply in a KiriChess PGN carries one (the `%clk`/`%kc` comment), which is what PGN's own export format
calls for when a comment interrupts the usual `N. white_move black_move` run.

## Compatibility notes

A standard PGN reader that has never heard of KiriChess:

- Loads the game correctly: the moves, the result, and the Seven Tag Roster are all standard.
- Silently drops every `{ … }` comment it does not recognise, which is all of `%kc` and `%kcfg`, and
  most tools will still render the `%clk` clock, since that command is not KiriChess's own.
- Sees no `[Variant]` tag and no reason to refuse the file.
- Ignores `KiriChessFormat`, `KiriChessId`, `KiriChessPool`, `KiriChessRated`, and
  `KiriChessTermination`, five ordinary unrecognised tags, exactly as the PGN standard says an
  unrecognised tag should be handled.

A KiriChess-aware reader keys on `[KiriChessFormat "1"]` to know the `%kc*` commands are present and
parses `%kcfg` with the same `KEY=VALUE` split `KiriChessConfig.FromDictionary` uses, since that is
the actual reader implementation this format is built against (there is no separate parser to drift
from it).

## Stability

`[KiriChessFormat "1"]` exists so that a future incompatible change is **detectable**, not so that
version 1 is provisional. It is not versioned by negotiation and has no back-compat machinery. The bet
this format makes is that everything anyone actually wants to add fits one of two safe extension
points:

- **A new `%kc*` comment command.** Any parser that predates it skips the whole brace group as an
  unrecognised comment, per the PGN standard, so adding one is not a breaking change.
- **An appended `%kcfg` key.** `KiriChessConfig.FromDictionary` reads by key name, not position, so an
  older reader that does not know a new key simply never asks for it, and a file written before the
  key existed still parses (the key is absent, and the reader falls back to its own default for it).

**Reserved but not implemented**: `[%kce …]`, itemised clock events per ply (`CaptureGain`,
`MovementToll`, …). Deliberately left out of version 1 for the size reason stated in `%kc`'s section
above; the name is reserved so that if it is ever added, it slots into the same extension point rather
than colliding with a name already in use.

**Explicitly out of scope for this format, not merely unimplemented**: PGN import or paste-a-PGN
analysis (nothing in this codebase parses a KiriChess PGN back into a game; the reader half is
`KiriChessConfig.FromDictionary` plus the round-trip test that pins it, not a shipped importer),
`[%eval …]` or other engine annotations, NAGs, variations, and comments authored by a player.
