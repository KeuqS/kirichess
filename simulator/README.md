# KiriChess game-mode simulator

A console harness that plays the **real engine** against itself so you can see how a pool actually
behaves: who flags, how long games last, how much think time the clock policy spends. Move quality
comes from Stockfish (or a seeded random-legal picker). Think time is the same budget the live bots
use (`ui/lib/botClock.ts`), charged as whole seconds the way `GameSession` floors wall-clock waits.

It is not a load test (that is `loadtest/`) and it is not the generative random self-play in
`tests/KiriChess.Engine.Tests/Generative/`. Those prove invariants. This one is for **playtesting
the six rated pools** with competent (or chaotic) play.

Stockfish is a separate process, same GPL isolation as the site's Web Worker. This repo does not
vendor a Stockfish binary.

## Prerequisites

- .NET 10 SDK
- **Optional, for `--picker stockfish` (the default):** a Stockfish binary
  - On PATH as `stockfish` / `stockfish.exe`, or
  - `$STOCKFISH` pointing at the file, or
  - `--stockfish <path>`
  - Windows: `winget install --id Stockfish.Stockfish`, or [stockfishchess.org/download](https://stockfishchess.org/download/)
- No database, Redis, or running server. The engine is in-process.

`--picker random` needs no Stockfish and is enough to smoke the clock economy.

## Running

From the repo root:

```bash
dotnet run -c Release --project simulator/KiriChess.Simulator -- --help
```

One pool, ten games, master-tier clock and Stockfish:

```bash
dotnet run -c Release --project simulator/KiriChess.Simulator -- \
  --pool steal1 --games 10 --elo 2100 --out simulator/results/steal1.json --pgn simulator/results/pgn
```

Every rated pool, random legal (no Stockfish), useful as a first pass:

```bash
dotnet run -c Release --project simulator/KiriChess.Simulator -- \
  --pool all --games 20 --picker random --elo 1500 --out simulator/results/random.json
```

Mismatched strengths, actually waiting the think delay (a live bot game, watched in the terminal):

```bash
dotnet run -c Release --project simulator/KiriChess.Simulator -- \
  --pool burn1 --games 1 --white-elo 2100 --black-elo 900 --realtime --verbose
```

## What "think time" means here

Live bots search Stockfish and `sleep(simulatedThinkMs)` concurrently, then submit. The server
charges `floor(elapsed seconds)`. Fast simulation does not wait: it asks Stockfish (or picks a
random legal move), then passes `floor(thinkMs / 1000)` into `KiriChessEngine.Apply` as
`thinkTimeSeconds`. Sub-second replies still cost nothing, the same as a human blitzing a recapture.

`--think-ms N` replaces the bot clock policy with a constant delay. `--realtime` waits that delay
for real, so a steal3 game takes minutes of wall time. Both still charge the *budgeted* seconds, not
measured wall time: if Stockfish ever searches longer than the budget, a live game would charge the
overrun and this harness will not.

`--elo` drives **both** axes: Stockfish skill/depth (from `ui/lib/bot.ts`) and the clock-policy tier
(novice burns 12% per move; master spends 3% and keeps a 15s reserve).

## CLI

| Flag | Default | Meaning |
| --- | --- | --- |
| `--pool` | `all` | Pool key, comma list, or `all`. Keys are the storage ids (`steal1`, `bounty1`, …). |
| `--games` | `1` | Games per pool. |
| `--elo` | `2100` | Both sides. Master clock + Stockfish depth 8. |
| `--white-elo` / `--black-elo` | (elo) | Per-side override. |
| `--picker` | `stockfish` | `stockfish` or `random`. |
| `--stockfish` | PATH / `$STOCKFISH` | Binary path. |
| `--think-ms` | (bot clock) | Constant think delay in milliseconds. |
| `--realtime` | off | Actually wait the think delay. |
| `--seed` | `20260824` | Base seed. Each (pool, game index) pair derives its own. |
| `--max-plies` | `2000` | Abort and report `Ongoing` if a game never ends. |
| `--concurrency` | `1` | Parallel games. Each Stockfish worker is its own process. |
| `--verbose` | off | Print every ply. |
| `--out` | (none) | JSON summary path. |
| `--pgn` | (none) | Directory for one PGN per game. Simulator dumps, not the download contract in `docs/game/PGN.md`. |

## Reading the table

```
pool       games  mate  flag  draw  open  plies  think/mv  duration  flag%
steal1        10     4     5     1     0   62.3      2.10s      4.3m    50%
```

- **think/mv** - mean charged think seconds per ply. Compare to the thinking-budget table on `/rules`
  (generated from `config/kirichess.rules.json`'s `docModel`).
- **duration** - mean sum of charged think time, both sides. That is how long the game would have
  lasted if both players spent exactly the bot budget; it is the number to hold against the
  duration table on `/rules`.
- **flag%** - share of games that ended on the clock. Burn pools should show more of this than Steal
  at the same Elo; if they do not, the pool is not doing what the rules page claims.

JSON `--out` repeats the table plus per-game UCI, final clocks, and economy totals (tolls, capture
gain, checks).

## Layout

```
simulator/
  README.md
  KiriChess.Simulator/     console app (not a test project)
tests/KiriChess.Simulator.Tests/
```

Clock-policy and Elo→skill numbers are ports of `ui/lib/botClock.ts` and `ui/lib/bot.ts`. The unit
tests pin the same cases as the Vitest file; if you retune a bot tier, change both.
