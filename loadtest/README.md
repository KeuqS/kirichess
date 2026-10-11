# KiriChess load harness (Phase 9b.3)

A purpose-built .NET SignalR client that drives the real game server through matchmaking and play.
Raw per-rung JSON lives in `loadtest/results/`.

**Measured ceilings (local Docker, 1.0 CPU / 512 MB, 2026-08):**

**I-6-1: `loadtest/results/*.json` was captured 2026-08-08, against a build from before commit
`8574a55` (2026-08-17, Phase 10a.2/10a.3 - the per-IP hub connection budget and the aligned
`kill_timeout`) and after `e8883fa` (2026-08-14, Phase 9c.7 - the `hard_limit = 1000` / `soft_limit
= 800` connection numbers below). None of those three touches the write-behind persistence path, so
the persister and CPU rows are unaffected; the connections row is a static derivation from
`fly.backend.toml`, not something the local harness itself exercises (there is no Fly-style
connection-concurrency limiter in the compose stack), so it stays correct independent of the
harness build. What the persister and CPU rows do NOT reflect is `HubConnectionBudget`, which the
harness cannot drive from one source IP (see "Running against Fly" below) - re-running the ladder
(9e.8) is what actually supersedes this table.**

| Binding constraint                                                      | Bites at                                                                        | Nature                     |
| ----------------------------------------------------------------------- | ------------------------------------------------------------------------------- | -------------------------- |
| `fly.backend.toml` `hard_limit = 600` (`soft_limit = 400`) connections | **~150 games** on one machine (~100 starts the second)                         | config; 4 sockets per game |
| `LiveGamePersister` write-behind saturation                             | **~275–400 snapshot writes/s** (≈820–1,190 games locally, **≈200–600 on Neon**) | algorithmic, and silent    |
| Backend CPU on one core                                                 | ~800 games (76.6% of 1 vCPU)                                                    | resource                   |

`FlagFallMonitor` and `LatencyProbe` are not what breaks. The whole ladder is an **upper bound**
against a full core; a Fly `shared-cpu-1x` is a slice of a shared core, so production derates from
these numbers. Memory was never close (372 MB at 800 games).

## Running against Fly

This harness and its `run-ladder.*` scripts only ever `docker compose up` a local stack - they
cannot target `kirichess-api`/`kirichess-api-staging` directly. A production or staging run means
invoking `dotnet run --project loadtest/KiriChess.LoadTest` by hand, once per rung, and requires all
four of the following (I-6, action list items 2-9c/I-6-3/I-6-5/I-6-7/I-6-9); skipping any one
produces a void rung, not a failure that reads as one:

1. **Raise `Hub__ConnectionBudget__MaxConnectionsPerIp`** (Fly secret, temporarily) well above
   `4 × topRungGames`, or the budget refuses at connection 21 - about 10 bots / 5 games from one
   source IP - before the ramp even reaches its first real rung (I-6-3).
2. **Disable outbound mail, or target staging.** `loadbot*@load.invalid` addresses hard-bounce
   through Resend at 2N per run, which risks the sending domain's reputation (I-6-5).
3. **`docker stats`-based CPU/memory headroom reads as zero, and the criterion reports `Pass` on
   that zero, not `Void`** - there is no local Docker daemon to sample against a Fly target. Read
   Fly's own `fly_instance_cpu`/`fly_instance_memory` or Better Stack/Grafana during the window
   instead, and do not trust the harness's own headroom verdict (I-6-7).
4. **Pass the real `Metrics__ScrapeToken`**, not the local placeholder default - every scrape 401s
   against the placeholder, `kirichess_games_live` reads 0, and pairing looks like it "never
   converges" when it is actually the scrape that is failing (I-6-9).

The full owner runbook (target decision, per-rung secret changes, what to watch, the shutdown-flush
measurement, cleanup) lives in `docs/audits/I.md`'s "Owner runbook" section - this is the launch
window, and it is a Human step, not something this harness runs unattended.

## Prerequisites

- Docker Desktop, with nothing else named `kirichess-*` running (`docker ps`).
- The backend image built against the load overlay:
  `docker compose -p kirichess-load -f docker-compose.prod.yml -f docker-compose.load.yml build backend`

## Running one rung by hand

```bash
docker compose -p kirichess-load -f docker-compose.prod.yml -f docker-compose.load.yml up -d db redis backend
docker compose -p kirichess-load -f docker-compose.prod.yml -f docker-compose.load.yml --profile migrate run --rm migrate

dotnet run -c Release --project loadtest/KiriChess.LoadTest -- \
  --games 100 --pool steal3 --api http://localhost:8080 \
  --scrape-token load-only-not-a-secret-scrape-token \
  --think-ms 3000 --jitter-ms 1000 \
  --warmup-seconds 90 --window-seconds 180 \
  --presence true --seed 20260808 --pinned-cpus 1.0 \
  --label "A-cpu1-mem512" --out loadtest/results/A-n100.json

docker compose -p kirichess-load -f docker-compose.prod.yml -f docker-compose.load.yml down -v
```

Or run the whole ladder with `run-ladder.ps1` / `run-ladder.sh` (see below).

## CLI reference

| Flag                                     | Default                              | Meaning                                                                                                                                                                                                                                                                                                                                                |
| ---------------------------------------- | ------------------------------------ | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| `--games N`                              | required                             | Target concurrent live games. The harness registers `2N` accounts.                                                                                                                                                                                                                                                                                     |
| `--pool`                                 | `steal3`                             | Matchmaking pool (`docs/RULES.md` presets). Eclipse (900s) is the only pool with a long enough clock to survive the window - see README § Decision 2.                                                                                                                                                                                                  |
| `--api`                                  | `http://localhost:8080`              | Base URL of the backend.                                                                                                                                                                                                                                                                                                                               |
| `--scrape-token`                         | (empty)                              | Bearer token for `GET /metrics` - must match `Metrics__ScrapeToken`.                                                                                                                                                                                                                                                                                   |
| `--think-ms` / `--jitter-ms`             | `3000` / `1000`                      | Per-side think time, uniform jitter `±jitter-ms`.                                                                                                                                                                                                                                                                                                      |
| `--warmup-seconds`                       | `90`                                 | Played but not measured, so the 2N-account registration/PBKDF2 cost drains before the window opens.                                                                                                                                                                                                                                                    |
| `--window-seconds`                       | `180`                                | The measured interval. Metrics are scraped at both ends; gauges and container stats are polled every `--sample-interval-seconds` across it.                                                                                                                                                                                                            |
| `--presence`                             | `true`                               | Also open `/hubs/presence` per account. `false` isolates `PresenceBroadcaster` (suspect #4) for the attribution runs.                                                                                                                                                                                                                                  |
| `--seed`                                 | `20260808`                           | Base seed. Each bot's each game gets its own derived seed (`HashCode.Combine(seed, botIndex, gameCounter)`), so a run is reproducible but no two games share an RNG stream.                                                                                                                                                                            |
| `--label`                                | `run`                                | Free-text tag stamped into the JSON output and printed summary.                                                                                                                                                                                                                                                                                        |
| `--out`                                  | `loadtest/results/run.json`          | Where the machine-readable result is written.                                                                                                                                                                                                                                                                                                          |
| `--ramp-batch-size`                      | `50`                                 | Accounts registered+connected per batch during the ramp.                                                                                                                                                                                                                                                                                               |
| `--backend-container` / `--db-container` | `kirichess-backend` / `kirichess-db` | Container names `docker stats` samples.                                                                                                                                                                                                                                                                                                                |
| `--sample-interval-seconds`              | `5`                                  | Gauge and `docker stats` poll interval during the window.                                                                                                                                                                                                                                                                                              |
| `--ready-timeout-seconds`                | `120`                                | How long to poll `/health/ready` before giving up.                                                                                                                                                                                                                                                                                                     |
| `--pair-timeout-seconds`                 | `180`                                | How long to wait for `kirichess.games.live` to reach 95% of `--games` after every client calls `JoinQueue`.                                                                                                                                                                                                                                            |
| `--pinned-cpus`                          | `1.0`                                | The backend's `deploy.resources.limits.cpus` for _this_ run - **not** in the task file's CLI example, added because `docker stats`' CPU% is relative to one full host core (not the pinned budget), so the harness needs to know the budget to compute "% of budget" for the headroom criterion. Must match `LOAD_CPUS` passed to the compose overlay. |
| `--load-memory`                          | `512m`                               | Cosmetic only - stamped into the JSON output so it is self-describing without the compose invocation next to it. Must match `LOAD_MEMORY`.                                                                                                                                                                                                             |

## Prometheus metric names - discovered, not guessed

Guessing the OpenTelemetry Prometheus exporter's name mangling is the classic way to produce a
report full of zeroes. Before writing
`MetricsScraper.cs`, a live instance was brought up (`docker compose ... up -d db redis backend` +
migrate) and `/metrics` was scraped with `curl -H "Authorization: Bearer ..."`, first at rest and then
after deliberately triggering one of each event (a solo bot game via `POST /api/games`, a move via
`POST /api/games/{id}/moves`, a resign, and 14 rapid `POST /api/games` calls to trip the 12/minute
`game-create` HTTP rate limit). The raw scrapes are not committed (they were throwaway `curl` output
in the harness's scratch directory, not repo artifacts), but the transformation they proved is
mechanical and applies uniformly to every instrument in `KiriChessMetrics.cs`:

| Server-side name (`KiriChessMetrics.cs`)  | Kind             | Unit        | Confirmed Prometheus series                                                                                                                                                                                                                                                                               |
| ----------------------------------------- | ---------------- | ----------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `kirichess.games.started`                 | counter          | `{game}`    | `kirichess_games_started_total{pool,rated}` - **confirmed by scrape**                                                                                                                                                                                                                                     |
| `kirichess.games.ended`                   | counter          | `{game}`    | `kirichess_games_ended_total{pool,rated,termination}` - **confirmed by scrape**                                                                                                                                                                                                                           |
| `kirichess.moves.applied`                 | counter          | `{move}`    | `kirichess_moves_applied_total{pool}` - **confirmed by scrape**                                                                                                                                                                                                                                           |
| `kirichess.ratelimit.rejected`            | counter          | `{request}` | `kirichess_ratelimit_rejected_total{surface,policy}` - **confirmed by scrape** (tripped the `game-create` HTTP policy)                                                                                                                                                                                    |
| `kirichess.flagfall.detected`             | counter          | `{game}`    | `kirichess_flagfall_detected_total` - pattern-inferred (see below), not directly triggered pre-flight (requires a real flag-fall, which takes minutes)                                                                                                                                                    |
| `kirichess.persistence.snapshot_failures` | counter          | `{write}`   | `kirichess_persistence_snapshot_failures_total` - pattern-inferred, not directly triggered (requires a write failure)                                                                                                                                                                                     |
| `kirichess.hub.errors`                    | counter          | `{error}`   | `kirichess_hub_errors_total{method}` - pattern-inferred, not directly triggered (requires an unhandled hub exception)                                                                                                                                                                                     |
| `kirichess.move.apply.duration`           | histogram        | `ms`        | `kirichess_move_apply_duration_milliseconds{_bucket,_sum,_count}` tagged `pool` - **confirmed by scrape**                                                                                                                                                                                                 |
| `kirichess.flagfall.sweep.duration`       | histogram        | `ms`        | `kirichess_flagfall_sweep_duration_milliseconds{_bucket,_sum,_count}` - **confirmed by scrape** (the sweep runs unconditionally, so this appeared even with zero games)                                                                                                                                   |
| `kirichess.games.live`                    | observable gauge | `{game}`    | `kirichess_games_live` - **confirmed by scrape**                                                                                                                                                                                                                                                          |
| `kirichess.persistence.pending_snapshots` | observable gauge | `{game}`    | `kirichess_persistence_pending_snapshots` - **confirmed by scrape**                                                                                                                                                                                                                                       |
| `kirichess.presence.online_users`         | observable gauge | `{user}`    | `kirichess_presence_online_users` - **confirmed by scrape**                                                                                                                                                                                                                                               |
| `kirichess.matchmaking.queue.depth`       | observable gauge | `{player}`  | `kirichess_matchmaking_queue_depth{pool}` - pattern-inferred; the pre-flight smoke test never had a client sit in the queue (`GameMetricsObserver.ObserveQueueDepth` emits _no_ series at all when every pool's queue is empty - only shows up once a real client joins a queue with nobody to pair with) |
| `kirichess.game.ownership.owned`          | observable gauge | `{game}`    | `kirichess_game_ownership_owned` - **confirmed by scrape** (unused by the harness; listed for completeness)                                                                                                                                                                                               |

The transformation rule, read directly off the four counter/histogram pairs and five gauges that
_were_ triggered: dots become underscores; a `{unit}` annotation (a bounded-cardinality tag, not a
real unit) is **dropped** from counters entirely, which then get a `_total` suffix; a real unit
(`ms`) is spelled out (`milliseconds`) and the histogram gets the standard `_bucket`/`_sum`/`_count`
triplet; gauges keep the dotted name verbatim with no suffix at all. Every series also carries
`otel_scope_name="KiriChess.Server"`. `MetricsScraper.cs`'s doc comment repeats this table; the
constants are hard-coded there, not derived at runtime.

## Bucket boundaries observed

Both histograms use the same explicit buckets (milliseconds): `0, 5, 10, 25, 50, 75, 100, 250, 500,
750, 1000, 2500, 5000, 7500, 10000, +Inf`. The parser does not hard-code these - it reads whatever
`le` values are present in the scrape and finds the smallest one whose cumulative delta covers the
target percentile, so it would keep working if the buckets ever change.

## Layout

```
loadtest/
  README.md              - this file
  run-ladder.ps1          - Windows: runs the full ladder for one budget
  run-ladder.sh           - POSIX equivalent
  KiriChess.LoadTest/
    KiriChess.LoadTest.csproj
    Program.cs            - CLI parsing, orchestration, result JSON (top-level statements)
    LoadOptions.cs         - parsed run configuration
    JsonDefaults.cs        - shared System.Text.Json options (case-insensitive; see doc comment)
    WireDtos.cs             - the five minimal wire records (README § Decision 2)
    BotPlayer.cs            - one account: HTTP register + both hub connections + play loop
    GameCoordinator.cs      - shared aggregator for client-observed latency/counters
    MetricsScraper.cs       - GET /metrics, Prometheus text parser, delta + bucket quantiles
    ContainerStats.cs       - `docker stats` sampler + the harness's own CPU sampler
    RunResult.cs            - the JSON shape, the § Decision 6 criteria, the human summary table
  results/
    *.json                 - one file per rung, committed raw evidence
```

## Deviation: the generator-honesty "planned throughput" formula

An earlier draft used "planned throughput is `N × 2 / think_ms` moves per second." That formula was
not used as written. It assumes both sides of a game can move
independently and simultaneously - `2N` clients each producing a move every `think_ms` - but chess
turns strictly alternate: only the side on move can submit, so one game produces at most **one** ply
per mean think interval, not two. This is not a guess: an N=10 smoke rung (`loadtest/results/
smoke-n10.json`, 30s window) sustained 3.30 moves/s with harness CPU at ~0% of the host the entire
time - i.e. the generator was never remotely close to being the bottleneck - which matches
`N * 1000 / think_ms` (3.33) almost exactly and is roughly **half** of the literal formula's 6.67.
Using the literal formula would fail the generator-honesty criterion (§ Decision 6: "achieved moves/s
at least 90% of planned") on every single rung of the ladder, regardless of server health, purely
from an arithmetic mismatch between the formula and the alternating-turn traffic shape the same
plan's README § Decision 2 specifies. `Program.cs` uses `N * 1000.0 / think_ms` instead - the
physically achievable ceiling for N alternating-turn games at the configured cadence - and this
file's doc comment repeats the reasoning at the point of use.

## Deviations from the task file's exact layout

- `WireDtos.cs` and `JsonDefaults.cs` are two extra files not named in the task's file list. Both are
  small, single-purpose, and exist because the listed layout does not have an obvious home for
  "define only these five wire records" or "System.Net.Http.Json needs
  `PropertyNameCaseInsensitive` explicitly, unlike SignalR's own default." Splitting them out keeps
  `Program.cs` and `BotPlayer.cs` from absorbing unrelated concerns.
- `--pinned-cpus` is an added CLI flag - see the table above.
