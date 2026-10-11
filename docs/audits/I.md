# I — Infrastructure, ops, scale

Merged packet reports for category **I**. Individual packets were `docs/audits/I-N.md`; they now live as sections below. Index: [INDEX.md](INDEX.md).

---

## I-1 — Architecture and speed
Date: 2026-08-30
Packet: docs/audits/INDEX.md

### Verdict

The architecture holds for a donation-funded hobby service in the Lichess shape, and the parts that
would be expensive to get wrong are done carefully: write-behind move persistence off the hot path,
a coarse 1-second flag-fall sweep instead of per-second DB ticks, a derived-not-authoritative
leaderboard cache, `QueueLimit = 0` throughout the rate limiter because a queued move still burns the
clock, and a connection budget that reserves capacity for players over spectators. The capacity
ceiling (~250 concurrent games) and its binding constraint (`LiveGamePersister` write-behind
saturation against the Neon write RTT, not CPU) are measured, argued and instrumented, not guessed.
The hobby bill stays coffee-money at that ceiling: one `shared-cpu-1x` backend, one Neon branch,
Cloudflare in front. The gap to genuine Lichess-scale concurrency is the Redis multi-instance story,
which is code-complete and un-run, and is already documented as such.

The full analysis (findings AS-1 through AS-11) follows the findings below, under "Architecture and speed report". This section's own findings are the ones worth acting on.

### Findings

#### I-1-1 - Lobby opens two `/hubs/game` WebSockets per visitor (three with a pending challenge), plus presence
- Severity: Medium (trigger: a busy lobby during the 8.7 launch window)
- Where: `ui/components/RatedPoolsGrid.tsx:66-79`, `ui/components/OpenSeekList.tsx:56-69`,
  `ui/components/ChallengeFriendPanel.tsx:136-139`, `ui/components/PresenceProvider.tsx:30`; capacity
  model `docs/ops/DEPLOYMENT.md` §4.1. Detail: `I.md (I-1)` AS-4.
- Impact: an idle signed-in lobby visitor holds 3 hub connections (2 game + 1 presence), an
  anonymous one holds 3 against the anonymous reserve. `DEPLOYMENT.md` §4.1 sizes the ~250-game
  ceiling assuming a lobby visitor is cheap; at 3 connections each, `MaxConnectionsPerIp = 20` is
  spent by ~7 tabs and the anonymous reserve drains ~2× faster than the budget math. This is the
  client-side re-creation of the exact "onlookers hold the capacity, players get refused" failure
  that `HubConnectionBudget` (10a.2) was added to prevent.
- Fix: hoist a single game-hub connection into a shared provider (mirror `PresenceProvider`), or do
  not open a socket until the visitor clicks a queue/seek/challenge action - the tiles render fine
  without one and already send anonymous clicks to `/login`.

#### I-1-2 - Flag-fall sweep serialises all persist + rating + leaderboard + broadcast work in one 1-second tick
- Severity: Medium (trigger: many games in a fast pool flagging within the same second)
- Where: `src/KiriChess.Server/Games/FlagFallMonitor.cs:29-47` →
  `src/KiriChess.Server/Games/GameRegistry.cs:97-146`. Detail: `I.md (I-1)` AS-5.
- Impact: for each flagged game the sweep `await`s a `SELECT` + `UPDATE` + two `RatingHistory`
  inserts + two `Rating` updates + 2-3 serially-awaited leaderboard-cache Redis calls per player +
  the `GameEnded` broadcast - one game at a time. A wave of idle flag-falls delays game-end
  notifications for every later game in the loop and can overrun the tick. Instrumented
  (`kirichess.flagfall.sweep.duration`), so visible as a symptom after the fact.
- Fix: bounded-parallel per-game work within a sweep (`Parallel.ForEachAsync` with a small degree,
  or gather + `Task.WhenAll`), and move the leaderboard write-through off the persist path onto its
  own fire-and-forget queue.

#### I-1-3 - `LiveGameIndex`: per-move set re-population, whole-set reads, incomplete self-heal
- Severity: Medium (dormant until Redis is provisioned - Phase 8.4)
- Where: `src/KiriChess.Server/Games/Distributed/LiveGameIndex.cs:45-86`, `:122-166`, `:169-185`;
  consumers `src/KiriChess.Server/Games/PresenceBroadcaster.cs:52-53`, `:65`. Detail: `I.md (I-1)`
  AS-6.
- Impact: `PublishAsync` re-`SADD`s both player ids to `{p}players:ingame` on every move though
  membership only changes at game start/end; `PlayersInGameAsync` does `SMEMBERS` over every player
  in every live game cluster-wide every 3 s and on every presence connect, when only a count and a
  union are needed; `{p}games:live` sorted-set entries past the read `limit` from a crashed owner
  never get removed (no TTL on the set, only on the per-game state keys).
- Fix: add/remove `players:ingame` only in `OnGameCreatedAsync` / `RemoveAsync`; add an `SCARD`-based
  count path; give `games:live` a periodic sweep or an expiry score with `ZREMRANGEBYSCORE` on read.
  Best done in the same batch as the deep audit's batch 3 (same "wrong on day one of 8.4" character).

#### I-1-4 - `GET /api/games/mine`: no composite index for the sort, non-sargable ORDER BY, offset pagination
- Severity: Medium (trigger: an account with thousands of finished games, at scale)
- Where: `src/KiriChess.Server/Games/GameHistoryQuery.cs:20-26`. Detail: `I.md (I-1)` AS-1.
- Impact: `WHERE Result != Ongoing AND (WhitePlayerId = @me OR BlackPlayerId = @me) ORDER BY
  COALESCE(EndedAt, CreatedAt) DESC OFFSET @skip LIMIT @take` - `WhitePlayerId`/`BlackPlayerId` are
  indexed only separately (FK indexes), nothing covers the sort, `COALESCE(...)` cannot use an
  index, and deep `OFFSET` re-scans. Invisible at hobby volume; a slow profile query for a heavy
  account at scale.
- Fix: order by `EndedAt` alone (finished games always have it) with composite indexes
  `Game(WhitePlayerId, EndedAt)` / `Game(BlackPlayerId, EndedAt)` - a migration, generate but do not
  auto-apply - and use keyset pagination for "load more".

#### I-1-5 - `/api/v1/account/playing` caps the live scan at 200 and filters client-side
- Severity: Low (trigger: >200 concurrent live games - inside the stated ~250 launch ceiling)
- Where: `src/KiriChess.Server/Games/PublicApiEndpoints.cs:58-65`. Detail: `I.md (I-1)` AS-9.
- Impact: past 200 concurrent games a bot's own game can fall outside the freshest-200 window and
  the endpoint reports "not playing" for a running game - a correctness bug that appears exactly at
  the capacity the deployment is sized for. The Redis-disabled path is fine.
- Fix: maintain a per-player live-game index (`{p}player:games:{userId}`), or route to the owning
  instance; until then raise the cap well above the game ceiling and document it.

#### I-1-6 - Hot in-memory paths allocate a full array / LINQ pass over all live sessions
- Severity: Low (trigger: tens of thousands of concurrent games per instance)
- Where: `src/KiriChess.Server/Games/GameRegistry.cs:83`, `:86-89`. Detail: `I.md (I-1)` AS-8.
- Impact: `ActiveSessions` (`_sessions.Values.ToArray()`) is allocated every `FlagFallMonitor` tick
  (1 s) and every Redis-disabled `GET /api/games/live`; `PlayersInGame` runs a LINQ chain with a
  per-session array allocation every `PresenceBroadcaster` tick (3 s) and per presence connect.
  Negligible at 250 games, steady GC pressure at Lichess scale on the highest-frequency paths. The
  load harness found the persister - not GC - as the ceiling, hence Low.
- Fix: incremental players-in-game count/set; iterate the `ConcurrentDictionary` directly in the
  sweep.

#### I-1-7 - Levers, not bugs: SignalR JSON-only, `AddDbContext` not pooled, no `loading.tsx`, unthrottled seek poll
- Severity: Low (trigger-based; see each)
- Where / detail: `I.md (I-1)` AS-7 (`Program.cs:470` - add `.AddMessagePackProtocol()` when move
  throughput is being tuned), AS-2 (`Program.cs:149` - `AddDbContextPool` cuts per-request
  allocation), AS-10 (no route-level `loading.tsx` anywhere under `ui/app/[locale]`), AS-11
  (`ui/components/OpenSeekList.tsx:38-54` - 5 s poll with no `document.hidden` gate).
- Fix: as noted per item; none is urgent.

### Already covered

- **`LiveGamePersister` write-behind saturation is the true ceiling; Neon write RTT vs local**  - 
  `loadtest/README.md`, `docs/ops/DEPLOYMENT.md` §4.1. Not re-derived; `I.md (I-1)` §3.1 / §4.3
  cross-reference only.
- **A deploy interrupts in-progress games (single always-on machine, `strategy = "immediate"`)**  - 
  `DEPLOYMENT.md` §12, `docs/audits/C.md (section INF-1)`.
- **REST move/resign/get bypass `GameCommandRouter`** - `docs/audits/C.md (C-1 improvement plan)` batch
  3 item 1, `BE-2` finding 1.
- **`MatchNoShowWatcher` uses process-local `IsConnected`** - `IMPROVEMENT_PLAN.md` batch 3 item 2.
- **Redis `ConnectionMultiplexer` never disposed on shutdown** - `IMPROVEMENT_PLAN.md` batch 3 item
  3, `BE-5` finding 3.
- **`LeaderboardWarmup` never re-warms hourly as documented** - `IMPROVEMENT_PLAN.md` batch 3 item 4.
- **`RatingHistory` has no unique `(GameId, UserId)` - a restart can double-count**  - 
  `IMPROVEMENT_PLAN.md` batch 4.
- **Reconnect wipes a player's move list / clock-economy graph** - `IMPROVEMENT_PLAN.md` batch 2
  item 1, `FE-4`.
- **`PresenceProvider` reconnects on every token refresh** - `IMPROVEMENT_PLAN.md` batch 6 item 2.
- **Stockfish worker leaked singleton** - `IMPROVEMENT_PLAN.md` batch 6 item 1.
- **CI `permissions:` / `timeout-minutes` / `concurrency`; dev Postgres 15 vs prod 16** - `INF-1`,
  `IMPROVEMENT_PLAN.md` batch 9.
- **Redis unprovisioned; ~250-game capacity decision; Cloudflare Access until launch; offsite backup
  never green** - standing positions in `ROADMAP.md` / `DEPLOYMENT.md`.
- **Load ladder authorised and unrun; shutdown flush unmeasured** - `I-6`, `9e.8`;
  `DEPLOYMENT.md` §4.1.

### Action list

Ordered. No standing-position reversals (Redis stays unprovisioned; ~250-game ceiling is a decision;
correspondence stays out).

1. **I-1-1 - collapse lobby hub connections to one shared game-hub connection (or connect lazily).**
   Frontend-only, biggest real-world capacity lever. Before 8.7.
2. **I-1-2 - bounded-parallel per-game work in the flag-fall sweep; leaderboard write-through off
   the persist path.** Before 8.7.
3. **I-1-3 - fix `LiveGameIndex` (`players:ingame` add/remove lifecycle, `SCARD` count path,
   `games:live` sweep).** Bundle with deep-audit batch 3, before Redis is provisioned.
4. **I-1-4 - `GET /api/games/mine` index + `ORDER BY EndedAt` + keyset pagination.** Draft +
   generates the migration; **owner applies** it (`docs/ops/MIGRATIONS.md`, expand/contract).
5. **I-1-5 - per-player live-game index for `/api/v1/account/playing`** (or raise + document the
   cap), with I-1-3.
6. **I-1-6 - incremental players-in-game count/set.** Low.
7. **I-1-7 - MessagePack when move throughput is tuned; `AddDbContextPool`; `loading.tsx` on
   data-heavy routes; pause the seek poll on `document.hidden`.** opportunistic.
8. **Owner decision** - whether items 3-5 land now or are booked against Phase 8.4 (same call the
   deep audit left open for its batch 3).
9. **Owner** - re-run `loadtest/` after items 1-2 land and update the capacity number in
   `DEPLOYMENT.md` §4.1 (rides `I-6` / `9e.8`, already authorised).

---


### Architecture and speed report

Findings AS-1 through AS-11. The packet summary and the I-1 findings are above.

### Architecture & Speed Audit

Date: 2026-08-30
Packet: `docs/audits/INDEX.md` table 5, **I-1**
No production code was changed.

---

#### 1. Executive summary

KiriChess is architecturally sound for what it is: a real-time chess-variant server whose
authoritative live state lives in-process on a single always-on Fly machine, with Postgres (Neon) as
the durable store and a fully-built-but-unprovisioned Redis layer (Phase 8.4) waiting behind a
fail-closed `Enabled` gate. The disciplined parts are genuinely disciplined - write-behind move
persistence off the hot path, a coarse 1-second flag-fall sweep instead of per-second DB ticks, a
derived-not-authoritative leaderboard cache, `QueueLimit = 0` everywhere in the rate limiter because
a queued move still burns the clock, and a connection budget that reserves capacity for players over
spectators. The capacity ceiling (~250 concurrent games) is a **written, argued decision**
(`docs/ops/DEPLOYMENT.md` §4.1) rather than an accident, and the binding constraint
(`LiveGamePersister` write-behind saturation against the Neon write RTT, not CPU) is measured and
instrumented (`loadtest/README.md`).

For a donation-funded hobby service in the Lichess shape, the current design comfortably covers
realistic launch traffic and the bill stays coffee-money: one `shared-cpu-1x` backend, one Neon
branch, Cloudflare in front. The gap between "handles launch" and "handles Lichess-scale concurrent
play" is real but is mostly **already named** in `DEPLOYMENT.md` §7 and the deep audit - it is the
Redis-multi-instance story, which is code-complete and un-run.

**Top three risks (new, not already tracked):**

1. **The lobby opens two independent `/hubs/game` WebSockets per visitor** (three with a pending
   challenge link), plus the presence socket. A visitor doing nothing costs almost as much
   connection budget as a live game, which erodes the ~250-game ceiling from the client side and
   re-creates the exact "onlookers hold the capacity, players get refused" failure mode that
   `HubConnectionBudget` (10a.2) was added to prevent.
2. **The flag-fall sweep is a single sequential loop of DB + Redis round trips inside a 1-second
   tick.** A burst of simultaneous flag-falls in a fast pool serialises every persist, every rating
   write, every leaderboard cache write and every `GameEnded` broadcast, delaying game-end
   notifications for unrelated games and potentially overrunning the tick.
3. **The Redis live-state read-model does per-move writes and whole-set reads that do not scale.**
   `players:ingame` is re-populated on every move and `SMEMBERS`-read every 3 seconds and on every
   presence connect; the `games:live` sorted set only self-heals within the read window. Dormant
   until Redis is provisioned, wrong the day it is.

None of the three loses data or hurts a stranger today. All three are cheap to fix now and expensive
to chase later.

---

#### 2. Database & data access

##### 2.1 What is right

- **Connection string guidance is explicit and correct** (`DEPLOYMENT.md` §2): Neon *pooled*
  endpoint, `SSL Mode=Require`, long-lived app holding its own Npgsql pool.
- **`EnableRetryOnFailure`** is configured (`Program.cs:149-157`) with an explicit command timeout,
  and the one place that needs an explicit transaction (`AccountDeletionService`) wraps it in
  `CreateExecutionStrategy().ExecuteAsync(...)` as the retrying strategy requires. The Phase 5
  game/rating write rides `SaveChangesAsync`'s implicit transaction, which the strategy covers.
- **Indexes match the actual query shapes** and the reasons are in comments:
  `Game.Result` for boot recovery (`WHERE Result = 'Ongoing'`), `Game(RatingPool, EndedAt)` for the
  balance query, FK indexes on `WhitePlayerId` / `BlackPlayerId`, `UserSanction(UserId, LiftedAt,
  ExpiresAt)` for the per-request ban check, `Challenge.Token` unique for O(1) link lookup,
  `DailyCounter` composite PK that *is* the data (`KiriChessDbContext.cs:90-294`).
- **Write-behind move persistence** (`LiveGamePersister.cs`): a move never waits on Postgres; snaps
  coalesce per game; batched 64-per-round-trip since 9c.7; the batch reads the live session at write
  time so a coalesced write can never regress the move list; shutdown flushes every live session.
  This is the right shape.
- **`GET /api/games/mine` is `AsNoTracking`, batches opponent-name and rating-delta lookups** into
  two follow-up `IN (...)` queries rather than N+1 (`GameHistoryQuery.cs`).
- **List endpoints are bounded**: `Math.Clamp` on `limit`/`take` for leaderboards (200), history
  (100), rating history (1000), live games (50).

##### 2.2 Findings

**AS-1 - `GET /api/games/mine`: no composite index for the sort, non-sargable ORDER BY, offset
pagination.**
- Severity: Medium (trigger: a single account with thousands of finished games, at scale)
- Where: `src/KiriChess.Server/Games/GameHistoryQuery.cs:20-26`
- Detail: the query is
  `WHERE Result != Ongoing AND (WhitePlayerId = @me OR BlackPlayerId = @me)
  ORDER BY COALESCE(EndedAt, CreatedAt) DESC OFFSET @skip LIMIT @take`.
  `WhitePlayerId` and `BlackPlayerId` are indexed **separately** (FK indexes,
  `KiriChessDbContext.cs:106-109`), so Postgres bitmap-ORs them, but nothing covers the sort key and
  `COALESCE(EndedAt, CreatedAt)` cannot use an index at all. Every page therefore materialises and
  sorts the player's entire finished-game set, and a deep `OFFSET` re-scans everything it skips. At
  hobby volume this is invisible; for a heavy account at Lichess scale it is a multi-hundred-ms
  profile query.
- Fix: finished games always have `EndedAt`, so order by `EndedAt` alone and add composite indexes
  `Game(WhitePlayerId, EndedAt)` and `Game(BlackPlayerId, EndedAt)` (a migration - generate, do not
  auto-apply). Prefer keyset (`WHERE EndedAt < @cursor`) over `OFFSET` for the "load more" path.

**AS-2 - `AddDbContext`, not `AddDbContextPool`.**
- Severity: Low (trigger: sustained high request rate)
- Where: `src/KiriChess.Server/Program.cs:149`
- Detail: a fresh `KiriChessDbContext` is constructed per scope/request. `AddDbContextPool` reuses
  context instances and removes most of the per-request allocation and change-tracker setup cost.
  The expensive resource (the Npgsql connection) is pooled independently and is unaffected, which is
  why this is Low.
- Fix: switch to `AddDbContextPool<KiriChessDbContext>(...)` with a `poolSize` matching expected
  concurrency. Verify no code stashes request state on the context instance (none seen).

**AS-3 - Read-replica / partitioning: not needed, and worth stating so.**
- The game-write volume that would justify a read replica or table partitioning is far beyond the
  connection ceiling this deployment can serve from one machine. Neon's branch-based scaling and the
  documented capacity re-check process (`DEPLOYMENT.md` §4.1, §7) are the right level of planning.
  No finding - recorded so a future reader does not re-derive it.

---

#### 3. Architecture, queues & real-time

##### 3.1 Synchronous vs asynchronous

The move path is correctly asynchronous end to end: engine `Apply` (pure, deterministic, no I/O) →
in-memory `GameSession` mutation → `LiveGamePersister.EnqueueSnapshot` (returns immediately) →
SignalR broadcast. Only the *final* write (`GameRegistry.PersistIfOverAsync`) is synchronous with the
move that ends the game, and it has to be (ratings, leaderboard write-through, ownership release all
belong in that transaction).

There is **no message broker and none is needed** - the write-behind `Channel<Guid>` in
`LiveGamePersister` is the one internal queue, it is single-consumer by design (so two writes for one
game can never race), and its ceiling is documented. A durable queue here would buy nothing:
the crash-loss window is already "at most the last move or two, which restart recovery replays
around."

##### 3.2 SignalR & backplane

- **Backplane is wired** (`RedisRegistration.AddKiriChessBackplane`, `AddStackExchangeRedis` with a
  namespaced channel prefix) and correctly no-ops when Redis is disabled. Without it,
  `Clients.Group(gameId)` is single-process - which is today's reality and the documented
  single-instance ceiling.
- **JSON protocol only** (`Program.cs:470`, `.AddJsonProtocol(...)`). See AS-7.
- **KeepAlive is default 15s**, deliberately under Cloudflare's ~100s proxied-WS idle timeout
  (`DEPLOYMENT.md` §4). Correct, but it means every idle connection costs a ping every 15s; at
  Lichess-scale connection counts that is non-trivial server work. Not a finding - the constraint is
  real and documented - but a lever if CPU ever binds before the persister does.

##### 3.3 Findings

**AS-4 - The lobby holds two independent `/hubs/game` connections per visitor (three with a pending
challenge), plus presence.**
- Severity: Medium (trigger: a busy lobby during the 8.7 launch window)
- Where: `ui/components/RatedPoolsGrid.tsx:66-79`, `ui/components/OpenSeekList.tsx:56-69`,
  `ui/components/ChallengeFriendPanel.tsx:136-139`, `ui/components/PresenceProvider.tsx:30`; capacity
  model in `docs/ops/DEPLOYMENT.md` §4.1
- Detail: on the home page, `RatedPoolsGrid` and `OpenSeekList` each call `createGameConnection()`
  and `conn.start()` in their own `useEffect` on mount - unconditionally, including for anonymous
  visitors (the token factory just returns `""`). `ChallengeFriendPanel` adds a third when the
  visitor has an open challenge link. `PresenceProvider` adds the presence socket. So an idle
  signed-in lobby visitor holds **3 hub connections** (2 game + 1 presence), an anonymous one holds
  3 against the anonymous reserve, and both `MatchFound` handlers fire the same `router.push` on a
  pairing. `DEPLOYMENT.md` §4.1 sizes capacity as "one live game is four connections, plus every
  lobby visitor who is connected and not playing" - the implicit assumption is that a lobby visitor
  is cheap. At 3 connections each they are not, `MaxConnectionsPerIp = 20` is exhausted by ~7 tabs
  instead of 20, and the anonymous reserve (`MaxAnonymousConnections = 600`, protecting 100
  players-only games) drains ~2× faster than the budget math assumes. This is the client-side
  re-creation of the precise failure 10a.2 exists to stop.
- Fix: hoist a single game-hub connection into a shared provider (mirror `PresenceProvider`), or
  don't connect until the visitor actually clicks a queue/seek/challenge action - the tiles render
  fine without a live socket and already route anonymous clicks to `/login`.

**AS-5 - The flag-fall sweep serialises all persist + rating + leaderboard + broadcast work inside
one 1-second tick.**
- Severity: Medium (trigger: many games in a fast pool flagging within the same second)
- Where: `src/KiriChess.Server/Games/FlagFallMonitor.cs:29-47`, calling
  `GameRegistry.PersistIfOverAsync` (`src/KiriChess.Server/Games/GameRegistry.cs:97-146`)
- Detail: the sweep iterates `ActiveSessions` and, for each flagged game, `await`s
  `PersistIfOverAsync` - a `SELECT` + `UPDATE` + two `RatingHistory` inserts + two `Rating` updates
  + (per player) 2-3 serially-awaited `LeaderboardCache.UpsertAsync` Redis calls - and then `await`s
  the `GameEnded` group broadcast, one game at a time. In a bullet-style pool a wave of idle
  flag-falls can land together; the last game in the loop waits for every earlier game's full
  round-trip chain before its players are told the game is over, and a long-enough wave overruns the
  `PeriodicTimer` tick. The sweep duration *is* instrumented
  (`kirichess.flagfall.sweep.duration`), so this would be visible - but as a symptom, after the
  fact.
- Fix: within a sweep, run the per-game persist/broadcast with bounded parallelism
  (`Parallel.ForEachAsync` with a small `MaxDegreeOfParallelism`, or gather tasks and
  `Task.WhenAll`), and/or move the leaderboard cache write-through off the persist path onto its own
  fire-and-forget queue (it already degrades safely and is rebuilt hourly when Redis is on).

**AS-6 - `LiveGameIndex`: per-move set re-population and whole-set reads.**
- Severity: Medium (dormant until Redis is provisioned - Phase 8.4)
- Where: `src/KiriChess.Server/Games/Distributed/LiveGameIndex.cs:45-86` (`PublishAsync`, called
  from `GameDistribution.OnGameChangedAsync` on every move), `:169-185` (`PlayersInGameAsync`),
  consumed by `src/KiriChess.Server/Games/PresenceBroadcaster.cs:52-53` (3s tick) and `:65` (per
  presence connect via `CurrentAsync`)
- Detail: three issues in the same file:
  1. `PublishAsync` re-adds both player ids to `{p}players:ingame` **on every move**
     (`:76-78`), though set membership only changes when a game starts or ends. Wasted `SADD` per
     ply, cluster-wide.
  2. `PlayersInGameAsync` does `SMEMBERS` on `{p}players:ingame` - the full set of every player in
     every live game on every instance - every 3 seconds (the `PresenceBroadcaster` tick) *and* on
     every new presence connection. At scale that is a large payload transferred from Redis on a
     fixed cadence plus a per-connect spike. `PresenceBroadcaster` only needs the *count* for the
     "playing" number and the *union* with online users for the "online" number.
  3. `LiveGamesAsync` only `ZREM`s stale ids it encountered **within `limit`** (`:156-157`), so a
     crashed owner's finished-game ids that sit past the read window never leave `{p}games:live`
     (the sorted set has no TTL - only the per-game state *keys* do). Slow unbounded growth.
- Fix: (1) move the `SADD`/`SREM` for `players:ingame` to `OnGameCreatedAsync` / `RemoveAsync`
  only; (2) expose `PlayersInGameCountAsync` using `SCARD` and use it where only the count is
  needed, keep `SMEMBERS` for the union path but consider caching it for the tick window; (3) give
  `{p}games:live` entries a periodic sweep or store a score = expiry timestamp and `ZREMRANGEBYSCORE`
  on read.

**AS-7 - SignalR carries JSON only; no MessagePack.**
- Severity: Low (trigger: sustained high move throughput)
- Where: `src/KiriChess.Server/Program.cs:470`
- Detail: every `MovePlayed` payload (full board FEN + legal-move list + clock events + state) goes
  out as JSON to each group member and, once Redis is on, through the backplane as JSON. MessagePack
  roughly halves both wire size and serialisation cost. The tradeoff is a client bundle addition
  (`@microsoft/signalr-protocol-msgpack`) and it is genuinely optional at this scale.
- Fix: add `.AddMessagePackProtocol()` server-side and the matching client protocol when move
  throughput becomes the thing being tuned; leave it for now and note it in `DEPLOYMENT.md` §4.1's
  "what to re-check" list.

##### 3.4 Caching - already the right shape

- `LeaderboardCache` is derived, never authoritative; `TopAsync` returns `null` (distinct from
  empty) on any miss/outage/cold state and the endpoint falls straight through to a byte-identical
  Postgres query. Rebuild uses a `:building` key + atomic `RENAME`. This is a model implementation
  of an optional cache.
- Rate-limit counters, presence TTLs, ownership leases, the DP key ring - all namespaced with
  `RedisOptions.KeyPrefix`, all carry explicit expiries. Matches `.claude/rules/backend.md`.
- **Gap already tracked:** `LeaderboardWarmup` documents an hourly re-warm it never performs (runs
  once per process, no timer). See `docs/audits/C.md (C-1 improvement plan)` batch 3 item 4. Not
  re-reported.

---

#### 4. Resilience & scale

##### 4.1 What is right

- **Connection budget** (`HubConnectionBudget`, 10a.2): counts the right unit (sockets → games),
  reserves a players-only share against anonymous spectators, refuses at negotiate so the client's
  `start()` rejects cleanly, and counts refusals on `kirichess.ratelimit.rejected` with
  `surface="hub-connect"`.
- **Rate limiting** covers every mutating HTTP surface with a named policy (see the table in
  `.claude/rules/security.md`), `QueueLimit = 0` everywhere, and an IP-partitioned global backstop
  with `/health` `/metrics` `/api/v1` `/openapi` exempted. The hub side has its own
  `HubRateLimitFilter`. The move policy is deliberately the loosest because a rejected move burns
  the clock - the reasoning is in the file header and it is correct.
- **Load shedding**: four DB-backed kill switches (`KillSwitchService`, singleton, dictionary lookup
  not a round trip) can disable matchmaking / signups / seek list / API without a deploy.
- **Graceful shutdown**: `kill_timeout = 30` aligned with the persister flush budget; the flush is
  batched specifically so it fits.
- **CDN**: Cloudflare in front, TLS at the edge, static assets and DDoS handled there. Fly proxies
  WSS straight through.
- **Backpressure visibility**: `kirichess_persistence_pending_snapshots` is the documented
  early-warning gauge for durability debt - the failure mode is silent otherwise, and they knew it.

##### 4.2 Findings

**AS-8 - Hot in-memory scans allocate a full array / LINQ pass over all live sessions.**
- Severity: Low (trigger: tens of thousands of concurrent games per instance)
- Where: `src/KiriChess.Server/Games/GameRegistry.cs:83` (`ActiveSessions => _sessions.Values.ToArray()`),
  `:86-89` (`PlayersInGame` → `.Where().SelectMany(new[]{...}).ToHashSet()`)
- Detail: `ActiveSessions` is allocated fresh on every `FlagFallMonitor` tick (1s) and every
  Redis-disabled `GET /api/games/live`. `PlayersInGame` runs its LINQ chain - including a 2-element
  array allocation per session - on every `PresenceBroadcaster` tick (3s) and every presence
  connect. At 250 games this is nothing; at Lichess scale it is steady GC pressure on the
  highest-frequency paths. The load harness found the persister, not CPU/GC, as the ceiling - so
  this is Low - but it is exactly the "allocations on the hot path at high throughput" the audit
  brief asks about.
- Fix: maintain an incremental `players-in-game` count/set updated on create/end, and let the
  flag-fall sweep iterate the `ConcurrentDictionary` directly rather than snapshotting to an array.

**AS-9 - `/api/v1/account/playing` caps the live scan at 200 and filters client-side.**
- Severity: Low (trigger: >200 concurrent live games - inside the stated ~250 launch ceiling)
- Where: `src/KiriChess.Server/Games/PublicApiEndpoints.cs:58-65`
- Detail: the Redis path calls `liveGames.LiveGamesAsync(200, ct)` then `.Where(s => WhitePlayerId
  == me || BlackPlayerId == me)`. Past 200 concurrent live games a bot's own game can fall outside
  the freshest-200 window and the endpoint reports "not playing" for a game that is very much
  running - a correctness bug that appears exactly at the capacity the deployment is sized for. The
  Redis-disabled path scans the local `ActiveSessions` and is fine.
- Fix: maintain a per-player index (`{p}player:games:{userId}` set) written alongside
  `players:ingame`, or route the query to the owning instance. Until then, raise the cap well above
  the game ceiling and document it.

##### 4.3 Already tracked elsewhere - not re-reported

| Area | Where it lives |
| --- | --- |
| `LiveGamePersister` write-behind saturation is the real ceiling; Neon write RTT (5-15 ms) vs local (<4 ms) | `loadtest/README.md`, `docs/ops/DEPLOYMENT.md` §4.1 |
| A deploy interrupts in-progress games (single always-on machine, `strategy = "immediate"`) | `DEPLOYMENT.md` §12, `docs/audits/C.md (section INF-1)` |
| REST move/resign/get bypass `GameCommandRouter` - silently 404s the day a 2nd instance exists | `IMPROVEMENT_PLAN.md` batch 3 item 1, `BE-2` finding 1 |
| `MatchNoShowWatcher` uses process-local `IsConnected` | `IMPROVEMENT_PLAN.md` batch 3 item 2 |
| Redis `ConnectionMultiplexer` never disposed on shutdown | `IMPROVEMENT_PLAN.md` batch 3 item 3, `BE-5` finding 3 |
| `LeaderboardWarmup` never re-warms hourly as documented | `IMPROVEMENT_PLAN.md` batch 3 item 4 |
| `RatingHistory` has no unique `(GameId, UserId)` - a restart can double-count a rating | `IMPROVEMENT_PLAN.md` batch 4 |
| Reconnect wipes a player's move list / clock-economy graph | `IMPROVEMENT_PLAN.md` batch 2 item 1, `FE-4` |
| `PresenceProvider` reconnects on every token refresh (count blinks) | `IMPROVEMENT_PLAN.md` batch 6 item 2 |
| Stockfish worker is a leaked singleton with one global resolver | `IMPROVEMENT_PLAN.md` batch 6 item 1 |
| CI: no `permissions:` / `timeout-minutes` / `concurrency`; dev Postgres 15 vs prod 16 | `INF-1`, `IMPROVEMENT_PLAN.md` batch 9 |
| Redis unprovisioned; ~250-game capacity decision; Cloudflare Access until launch | standing positions, `DEPLOYMENT.md` |

---

#### 5. Frontend performance

##### 5.1 What is right

- **The heavy board component is dynamically imported** with `ssr: false` on all three routes that
  carry it (`ui/app/[locale]/game/[id]/page.tsx:59`, `watch/[id]/page.tsx:40`,
  `replay/[id]/page.tsx:50`), so it is not in the initial payload of any other route.
- **The home page is a server component** (`ui/app/[locale]/page.tsx`) - `getTranslations` runs on
  the server, only the interactive tiles below it are `"use client"`.
- **Navigation goes through `@/i18n/navigation`** (locale-aware `Link`, prefetch on by default).
- **The live clock does not re-render the page per tick** - `game/[id]/page.tsx` keeps the ticker
  sync point in a `useRef` (`syncRef`, `resetTicker`) and the countdown lives in the `Clock`
  component; hub messages update colocated `useState`, not a global store.
- The Stockfish worker (client-side bot) is a separate program, off the main bundle.

##### 5.2 Findings

**AS-10 - No `loading.tsx` boundary on any route.**
- Severity: Low (trigger: a slow first data fetch on `/leaderboards`, `/analytics`, `/replay`,
  `/u/[id]`)
- Where: `ui/app/[locale]/**` - `Glob` for `loading.tsx` returns nothing
- Detail: most pages are `"use client"` and render their own inline spinner/skeleton while they
  fetch, so this is mostly cosmetic - but the routes that do server-side work in a layout
  (`replay/[id]/layout.tsx` fetches metadata) have no streaming fallback, and a client page that
  suspends on a dynamic import shows a blank frame rather than a route-level skeleton.
- Fix: add `loading.tsx` (reusing `SkeletonRows` / `EmptyState`) for the data-heavy routes. Low
  priority; the inline spinners already cover the common case.

**AS-11 - Duplicate lobby fetch/poll work.** *(See AS-4 for the connection half.)*
- Severity: Low
- Where: `ui/components/OpenSeekList.tsx:38-54` (5 s `setInterval` poll of `GET /api/seeks`),
  `ui/components/RatedPoolsGrid.tsx:43-64` (`getMyRatings` on mount)
- Detail: `getOpenSeeks` polls every 5 s with no `visibilitychange` gate, so a backgrounded lobby
  tab keeps polling. Not expensive per request, but combined with AS-4's connections it is a
  backgrounded tab doing steady work for nothing. No request dedupe/caching layer
  (`ui/lib/api.ts` is thin wrappers over `fetch`), so two components wanting the same data each
  fetch it - acceptable today given how little overlaps.
- Fix: pause the poll when `document.hidden`; consider a tiny SWR-style dedupe cache if polled
  endpoints multiply.

##### 5.3 Client bundle

`@microsoft/signalr` and `@tabler/icons-react` are the notable client deps. Icons are tree-shaken
via named imports. `next/font` is used for Montserrat (no layout-shift, self-hosted). No obvious
bloat. `P-7` (frontend performance / Core Web Vitals on a volunteer's phone) owns the deep pass here;
this audit only confirms the architecture-level choices are right.

---

#### 6. Action plan

##### High priority (do before 8.7 launch)

1. **AS-4 - collapse the lobby's hub connections to one shared game-hub connection** (or connect
   lazily on first action). Biggest single lever on the real-world capacity ceiling, and it is a
   frontend-only change. 
2. **AS-5 - parallelise per-game work within the flag-fall sweep** with bounded concurrency, and
   move leaderboard write-through off the persist path. 

##### Medium priority (before Redis is provisioned - Phase 8.4)

3. **AS-6 - fix `LiveGameIndex`**: `players:ingame` add/remove only at game start/end; `SCARD` for
   counts; periodic sweep for `games:live`. Do this in the same batch as the deep audit's batch 3
   (REST router bypass, `MatchNoShowWatcher`, multiplexer disposal) - same "dormant now, wrong on
   day one of 8.4" character. 
4. **AS-1 - `GET /api/games/mine` index + ORDER BY + keyset pagination.** Needs a migration
   generated (not applied). Owner applies the migration.
5. **AS-9 - per-player live-game index for `/api/v1/account/playing`**, or raise the scan cap well
   above the game ceiling and document it. 

##### Low priority (levers, not bugs)

6. **AS-7 - MessagePack** for SignalR when move throughput is the thing being tuned; add to
   `DEPLOYMENT.md` §4.1's re-check list now.
7. **AS-2 - `AddDbContextPool`.** Verify there is no per-request state on the context.
8. **AS-8 - incremental players-in-game count/set** instead of LINQ-over-all-sessions on the
   presence and flag-fall paths.
9. **AS-10 / AS-11 - `loading.tsx` for data-heavy routes; pause the seek poll on `document.hidden`.**

##### Owner

- Applying any migration from AS-1 (`docs/ops/MIGRATIONS.md` - expand/contract, owner confirms).
- Deciding whether items 3-5 are done now or booked against Phase 8.4 (the same call the deep audit
  left to the owner for its batch 3).
- Re-running `loadtest/` after AS-4 / AS-5 land to move the capacity number in `DEPLOYMENT.md` §4.1.

---

#### Appendix: files inspected

**Backend:** `Program.cs`, `Data/KiriChessDbContext.cs`, `Data/Migrations/` (names + snapshot index
list), `Games/LiveGamePersister.cs`, `Games/GameRegistry.cs`, `Games/GameEndpoints.cs`,
`Games/PublicApiEndpoints.cs`, `Games/GameHistoryQuery.cs`, `Games/SeekEndpoints.cs`,
`Games/FlagFallMonitor.cs`, `Games/PresenceBroadcaster.cs`, `Games/Distributed/LiveGameIndex.cs`,
`Infrastructure/RateLimiting.cs`, `Infrastructure/Redis/RedisRegistration.cs`,
`Ratings/LeaderboardCache.cs`, `Ratings/LeaderboardWarmup.cs`, `Ratings/RatingEndpoints.cs`.

**Frontend:** `ui/app/[locale]/page.tsx`, `ui/app/[locale]/game/[id]/page.tsx` (partial),
`ui/lib/realtime.ts`, `ui/components/RatedPoolsGrid.tsx`, `ui/components/OpenSeekList.tsx`,
`ui/components/ChallengeFriendPanel.tsx` (partial), route inventory via `Glob`
(`page.tsx`, `loading.tsx`, `next/dynamic` usage).

**Docs:** `docs/ops/DEPLOYMENT.md`, `loadtest/README.md`, `.claude/rules/{project,backend,frontend,realtime,engine}.md`,
`docs/audits/C.md` (C-1 improvement plan; sections BE-2, BE-5, INF-1).
## I-2 — Topology and IaC
Date: 2026-08-31
Packet: docs/audits/INDEX.md

### Verdict

The parts of this topology that are written down are written down unusually well — the capacity
derivation, the shutdown budget, the migration gate, the staging divergences and the Redis
preconditions all carry their reasoning in the file that implements them, and `INF-1` was right that
the four `fly.*.toml` files hold no unexplained drift. The theme of this packet is one layer up:
**the repo describes a two-hop request path (Cloudflare → Fly) that nothing in the repo configures,
tests, monitors, or even records the existence of.** Three separate code comments assert that
`RemoteIpAddress` is the real player behind that path; with the framework default this audit reads
in `Program.cs`, on a proxied path it is Cloudflare's edge address instead, which would cap
concurrent play at a small multiple of the number of Cloudflare PoPs serving the audience and
present as "Busy right now, try again shortly." Whether that is happening today cannot be
determined from this repository, because whether the orange cloud is on is the one topology fact
nobody wrote down — DEPLOYMENT.md calls it "optional", `I.md (I-1)` states it as fact, and the
Terraform monitors it as if it were real. The same shape recurs: the single-machine invariant the
entire no-Redis design rests on is asserted in a comment and verified nowhere; the Cloudflare-proxied
WebSocket leg is exercised by no staging environment, no E2E run, and no synthetic probe; and the
one flow E2E exists to prove is built with two of the five build args production uses. Redis is
genuinely, correctly unprovisioned — `redis.Enabled && options.Distributed` degrades everywhere it
matters, and that standing position is safe. The rest is a topology whose riskiest leg has never
been looked at.

### Findings

#### I-2-1 — Behind Cloudflare, the per-IP hub budget counts Cloudflare's edge, not the player
- Severity: **Critical** if `api.kirichess.com` is orange-clouded; inert if it is grey-clouded, and
  I-2-3 is that nothing in the repo says which. Named precondition, not a hedge — settle I-2-3 first.
- Where: `src/KiriChess.Server/Program.cs:436-441` (`ForwardedHeadersOptions` sets
  `ForwardedHeaders` and clears `KnownProxies`/`KnownIPNetworks`, and does **not** set
  `ForwardLimit`, whose framework default is 1), `:700` (`app.UseForwardedHeaders()`);
  `src/KiriChess.Server/Infrastructure/HubConnectionBudget.cs:38`
  (`MaxConnectionsPerIp` default 20), `:59-61` ("so `RemoteIpAddress` is the caller rather than
  Fly's or Cloudflare's edge"), `:254`;
  `src/KiriChess.Server/Infrastructure/RateLimiting.cs:436-439`, `:450`;
  `docs/ops/DEPLOYMENT.md:7-13` (the two-hop diagram), `:245-247`.
- Impact: On the proxied path the request reaches Fly *from Cloudflare*, so the hop Fly contributes
  to `X-Forwarded-For` is a Cloudflare edge address, and `ForwardLimit = 1` takes exactly that one
  hop. `CF-Connecting-IP` is never read anywhere in `src/`. Every IP-partitioned control then
  partitions by PoP rather than by player: `MaxConnectionsPerIp = 20` is four sockets per live game
  (DEPLOYMENT.md § 4.1), so **~5 concurrent games per Cloudflare edge address**, against a
  deployment sized and derated for 250. `RateLimitPolicies.Auth` (30/min), `Replay` and the 600/min
  `Global` backstop collapse the same way — one shared bucket for everyone arriving through the same
  PoP, which is both a denial of service to real players and the loss of the per-attacker property
  those limiters exist for. The refusal is worded `Busy right now, try again shortly.`, so it reads
  as the server being at capacity rather than as a misconfiguration.
  `HubConnectionBudget.cs:180-190` already reasons about the "cap the whole site at twenty" failure
  and fails open for a *null* address; a handful of known-but-wrong addresses walks past that guard.
- Fix: Determine the proxy status (I-2-3), then make the hop count explicit rather than default —
  read `CF-Connecting-IP` when the request arrives from Cloudflare, or set `ForwardLimit` to the
  real number of trusted hops. Raising `ForwardLimit` alone is not safe until I-2-2 is closed, since
  the origin is directly reachable; the two must be fixed together. Add one test that asserts
  `IpKey` returns the client, not the last hop, for a two-entry `X-Forwarded-For`.

#### I-2-2 — The Fly origins are publicly reachable, and the comment that makes trusting `X-Forwarded-For` safe says otherwise
- Severity: High
- Where: `src/KiriChess.Server/Program.cs:432-441` — "The proxy IPs are not knowable ahead of time,
  so the known-network allowlist is cleared; **this is only safe because the container is never
  reachable except through that proxy**"; contradicted by
  `infra/betterstack/variables.tf:13-17` and `:37-41` (the Fly hostname is kept as a *separate*
  monitor target from the edge precisely so "edge down, origin up" is distinguishable),
  `infra/betterstack/main.tf:65-84`, and `docs/ops/DEPLOYMENT.md:693-699` (a `curl` to
  `https://kirichess-api.fly.dev/metrics` from anywhere, expecting 401).
- Impact: The stated safety condition is not merely unmet, it is deliberately inverted — the origin
  is public by design so the edge can be monitored independently. With `KnownProxies` cleared and
  `X-Forwarded-For` enabled, a caller reaching `kirichess-api.fly.dev` directly supplies its own
  value for every IP-partitioned control listed in I-2-1, and `X-Forwarded-Proto` likewise decides
  `Request.IsHttps` (`Program.cs:349`). It also means Cloudflare's WAF, the Access wall, and the
  edge half of the Turnstile story are all optional from the caller's side. `"AllowedHosts": "*"`
  (`src/KiriChess.Server/appsettings.json:6`) adds no Host-based backstop.
- Fix: Lock the origin to the edge — have Cloudflare add a secret header via a Transform Rule and
  reject requests to the public port that lack it, exempting only `/health` and `/health/ready` so
  the origin monitor keeps working. Then the trusted-hop count in I-2-1 becomes safe to raise, and
  the `Program.cs:432` comment becomes true instead of aspirational.

#### I-2-3 — Whether the orange cloud is on, for which hostnames, and at what SSL mode is recorded nowhere, and the repo's own documents disagree
- Severity: High
- Where: `docs/ops/DEPLOYMENT.md:15` ("an **optional** orange-cloud proxy"), `:209` ("**If**
  Cloudflare proxying (orange cloud) is enabled") vs `docs/audits/I.md (I-1)` ("**CDN**:
  Cloudflare in front, TLS at the edge") and `infra/betterstack/main.tf:341-364` +
  `variables.tf:37-47`, which provision monitors whose entire purpose is to catch "a Cloudflare
  5xx". No DNS record, proxy status, or Cloudflare SSL mode for `kirichess.com` or
  `api.kirichess.com` appears anywhere in `docs/`; the only records written down are the four
  Resend mail records (`docs/ops/PROVIDER-ACCOUNTS.md:96-101`), each with the command that confirms
  it.
- Impact: This is the fact I-2-1 turns on, and it is also what decides whether § 4's ~100 s idle
  timeout applies to `/hubs/game` at all, whether the Access wall is in the path, and whether the
  DDoS/WAF protection the security posture assumes exists. It cannot be answered from the repo. A
  fork, a successor, or the maintainer six months from now reconstructs the front door from memory;
  a mistyped record or a proxy toggle flipped during debugging changes the behaviour of the rate
  limiter and the connection budget with no diff anywhere.
- Fix: Add a short table to DEPLOYMENT.md § 1: each hostname, record type, target, proxy status and
  Cloudflare SSL/TLS mode, each with the `dig` / `Resolve-DnsName` command that confirms it — the
  same format PROVIDER-ACCOUNTS.md § 2 already uses for the mail records.

#### I-2-4 — The single-machine invariant the whole no-Redis design rests on is asserted in comments and verified nowhere
- Severity: High
- Where: `fly.backend.toml:41-49` (`auto_stop_machines = "off"`, `auto_start_machines = false`,
  `min_machines_running = 1` — a floor, with no ceiling anywhere),
  `src/KiriChess.Server/Infrastructure/HealthEndpoints.cs:23-28` (`/health/ready` reports
  `redis: "disabled"` and nothing about instance count), `infra/betterstack/main.tf:65-84` (the
  monitor reads a status code only), `docs/ops/DEPLOYMENT.md:766-769` — the log shipper "created
  the app with no secrets and **two HA Machines**", i.e. this exact trap has already fired once in
  this org.
- Impact: Every "safe because there is one machine" claim in the tree — in-process `GameRegistry`,
  the local `HubConnectionBudget` counters, `strategy = "immediate"`, `MatchNoShowWatcher`'s
  process-local view, the REST move/resign paths that skip `GameCommandRouter` — depends on
  `kirichess-api` running exactly one machine. Nothing enforces or observes that. A `fly deploy`
  that creates an HA pair, an orphan left by a failed deploy, or a stray `fly scale count` produces
  two machines that `auto_stop_machines = "off"` will never reclaim, and Fly will route the two
  players of one game to different instances. With Redis disabled the symptom is not an error: it is
  moves that never arrive and clocks that disagree, which reads as a client bug.
- Fix: Add `--ha=false` to both `flyctl deploy` invocations in `.github/workflows/deploy.yml` (the
  same flag `fly.logshipper.toml:17-18` already calls load-bearing), and add
  `fly machines list --app kirichess-api` — expect exactly one — to DEPLOYMENT.md § 3 as a
  post-deploy check and to § 7 step 3 as a precondition. Longer term, have the app log its
  `InstanceIdentity` at boot so two machines are at least visible in the shipped logs.

#### I-2-5 — Nothing anywhere exercises the Cloudflare-proxied WebSocket leg
- Severity: Medium
- Where: `fly.ui.staging.toml:11-12` (staging UI is built against
  `https://kirichess-api-staging.fly.dev`, the raw Fly hostname — no custom domain is provisioned,
  `docs/ops/DEPLOYMENT.md:67-68`); `docker-compose.e2e.yml:38-45` (E2E's browser talks to
  `http://localhost:8080`); `scripts/synthetic-play.sh:20`, `:25`, `:65` (hits
  `https://api.kirichess.com` — the edge — but over REST `/api/v1/...` only, never a hub
  connection); `docs/ops/DEPLOYMENT.md:204-213` (the ~100 s idle-timeout hazard, stated as
  reasoning against SignalR's 15 s keep-alive, not as a measurement).
- Impact: The staging pair is a faithful twin of the *Fly* half and no twin at all of the *edge*
  half, and the packet's question — does staging truly mirror prod — turns on that. The leg with the
  most moving parts (a long-lived WSS upgrade through a CDN with an idle timeout, on a game whose
  win condition is a clock) is the one leg no automated thing has ever traversed. The first traffic
  through it will be real players in rated games.
- Fix: Extend `scripts/synthetic-play.sh` to open `/hubs/game` through `api.kirichess.com` and hold
  it past the keep-alive interval before resigning — it already has a `play` token and a heartbeat.
  That is cheaper than fronting staging with a Cloudflare hostname, and it covers the specific
  hazard § 4 names.

#### I-2-6 — The E2E frontend is built with two of the five build args production uses, so the registration flow it proves is not the one that ships
- Severity: Medium
- Where: `fly.ui.toml:14-26` (five `[build.args]`: API base, site URL, Sentry DSN, Turnstile site
  key, Turnstile Worker URL) vs `docker-compose.prod.yml:92-101` (two) and
  `docker-compose.e2e.yml:38-45` (overrides one); `Dockerfile.frontend:24-34` accepts all five and
  defaults the missing three to empty; `e2e/tests/critical-path.spec.ts:30,40-41` and
  `e2e/tests/auth.spec.ts:10` register through the UI.
- Impact: With `NEXT_PUBLIC_TURNSTILE_SITE_KEY` empty the widget never renders, so the E2E suite
  registers through a form that structurally cannot exist in production. The one flow the suite was
  built to prove end to end is the one flow it does not actually cover. This is not hypothetical:
  `docs/ops/PROVIDER-ACCOUNTS.md:104-105` records a production registration failing on exactly this
  difference ("The 19 Aug attempt died on Turnstile double-redeem; the UI deploy unblocked it") —
  a bug that reached production because no environment below it renders the widget.
- Fix: Pass Cloudflare's published always-pass Turnstile test site key (and the Worker URL, or a
  stub) through `docker-compose.e2e.yml`'s frontend build args, so the widget renders and the
  token round-trip is exercised. Add a one-line comment in `docker-compose.prod.yml` naming the
  three args a self-hoster must supply to match production.

#### I-2-7 — Two ops documents disagree about whether `Turnstile__SecretKey` is set in production
- Severity: Medium
- Where: `docs/ops/PROVIDER-ACCOUNTS.md:21` — "`Turnstile__SecretKey` | **Deployed**" (in a table
  whose header says every line was "confirmed against the deployed apps") vs
  `docs/ops/DEPLOYMENT.md:864-866` — "`Turnstile__SecretKey` **has never been set** on
  `kirichess-api`, so `TurnstileVerifier` is still disabled and `/api/auth/register` accepts a
  direct POST with no token", repeated at `:617-635`.
- Impact: One of these is wrong and the repo cannot say which, so the state of the only server-side
  anti-automation control on account creation is unknown to anyone reading it. The two documents
  are the two places an on-call reader or a successor would look. Both are dated after the other's
  claim, so recency does not settle it either.
- Fix: Run `fly secrets list --app kirichess-api`, correct the losing document in place, and date
  the correction the way DEPLOYMENT.md § 11.1 dates the log-shipper state. If it genuinely is unset,
  § 12's known-gaps entry stays and PROVIDER-ACCOUNTS' table row changes; the reverse if it is set.

#### I-2-8 — The documented Neon connection string turns off certificate validation on the one link that crosses providers
- Severity: Medium
- Where: `docs/ops/DEPLOYMENT.md:43` ("Neon's connection string **needs**
  `SSL Mode=Require;Trust Server Certificate=true` appended"), `:101`, `:117`;
  `scripts/restore-drill.sh:146`; `scripts/offsite-backup.sh:160`. Contrast the Neon-issued URI
  those blocks parse, which carries `sslmode=require&channel_binding=require` — the conversion to
  Npgsql key/value form drops the channel binding and adds the trust override.
- Impact: `Trust Server Certificate=true` tells Npgsql to accept whatever certificate the far end
  presents without validating the chain or the name. The connection is then encrypted but not
  authenticated, on the one hop that leaves a provider's network: Fly `ams` to Neon
  `aws-eu-central-1` (DEPLOYMENT.md:271-273), carrying every account row, every game and every
  refresh token. Neon presents a publicly trusted certificate, so the flag buys nothing that
  ordinary validation would not — it is presented in the doc as a requirement rather than as a
  choice, which is how it propagated into both backup scripts unquestioned.
- Fix: Change the documented string to `SSL Mode=VerifyFull` with no `Trust Server Certificate`, in
  DEPLOYMENT.md § 2 / § 2.1 and both scripts, and confirm against staging before production.
  `Trust Server Certificate=true` remains correct only for the local restore target inside
  `offsite-backup.sh`, which has no public certificate.

#### I-2-9 — The log shipper is the one image in the repo pinned to a moving tag, and it holds an org-scoped token
- Severity: Low (trigger: the next `fly deploy --config fly.logshipper.toml`, which
  DEPLOYMENT.md § 11.2 documents as the recreate path)
- Where: `fly.logshipper.toml:22-23` (`image = "ghcr.io/superfly/fly-log-shipper:latest"`) against
  `.github/workflows/deploy.yml:27-31` (Actions SHA-pinned, with an explicit rationale naming the
  `trivy-action` and `kics-github-action` compromises) and both Dockerfiles' digest-pinned bases
  (10c.3, confirmed by `docs/audits/C.md (section INF-1)`).
- Impact: The repo's stated supply-chain rule is applied to every artefact it builds and to every
  Action it runs, and not to the one third-party binary it deploys. That binary holds
  `ACCESS_TOKEN`, a readonly token scoped to the whole Fly org (DEPLOYMENT.md:771-773), and reads
  every app's log stream — which includes EF command logs on `/api/auth/register`
  (DEPLOYMENT.md:763-765). A redeploy silently takes whatever `latest` resolves to that day.
- Fix: Pin `ghcr.io/superfly/fly-log-shipper` by digest in `fly.logshipper.toml`, with the same
  Dependabot-style comment the workflows use, and note in § 11.2 that recreating means updating the
  digest deliberately rather than inheriting a new one.

#### I-2-10 — The tokenless metrics port is private to the whole Fly org, not to Fly's scraper
- Severity: Low (trigger: any other app in the `personal` org being compromised or misbehaving —
  today that includes the unpinned image in I-2-9)
- Where: `fly.backend.toml:85-95` — "only 8080 is published by `[http_service]`, so **this port is
  private** and the scraper … reaches it without a token" — with `ASPNETCORE_HTTP_PORTS = "8080;9091"`
  at `:34` binding 9091 on all interfaces; `src/KiriChess.Server/Infrastructure/MetricsEndpoint.cs`
  serves that port tokenless by design.
- Impact: Fly's private network is org-wide, so "private" here means reachable from
  `kirichess-ui`, `kirichess-api-staging`, `kirichess-ui-staging` and `kirichess-log-shipper` over
  `.internal`, not reachable only from Fly's scraper. The exposure is operational metrics rather
  than player data, which is why this is Low — but the comment states a narrower boundary than the
  one that exists, and that is what a future reader will reason from.
- Fix: One sentence in the comment: the boundary is the org's private network, and the tokenless
  port is acceptable because everything on it is ours. Revisit if a non-first-party app is ever
  deployed into this org.

#### I-2-11 — Terraform state is local-only, with no remote backend and no backup
- Severity: Low (trigger: the maintainer's machine is lost, reinstalled, or the directory is
  deleted)
- Where: `infra/betterstack/versions.tf:1-16` (no `backend` block), `.gitignore:70-74`
  (`*.tfstate`, `*.tfstate.*`, `terraform.tfvars` all ignored — correctly),
  `infra/betterstack/README.md:79-81` ("nothing here is CI-triggered").
- Impact: The mapping from the 12+ Better Stack resources to Terraform lives in one untracked file
  on one machine. Losing it does not lose the monitors, but it loses the ability to change them
  through this directory — a re-apply against an empty state creates duplicates, and recovery means
  `terraform import` for each resource, including the two heartbeat URLs that are already GitHub
  secrets and the log source token `kirichess-log-shipper` authenticates with. This is IaC's own
  bus-factor edge; B-1 owns the wider question.
- Fix: Note in `infra/betterstack/README.md` that `terraform.tfstate` and `terraform.tfvars` are
  recovery-critical and belong in whatever personal backup B-1 settles on, or move state to a free
  remote backend. Either is a ten-minute job; the current state is that neither has been decided.

#### I-2-12 — Production UI "scales to zero when idle" never happens, because the edge monitors poll it every 180 s
- Severity: Low (trigger: reasoning about cost or first-visit latency from the comment)
- Where: `fly.ui.toml:35-38` ("The frontend is stateless, so it is fine to scale to zero when idle",
  `auto_stop_machines = "stop"`, `auto_start_machines = true`, `min_machines_running = 0`) against
  `infra/betterstack/main.tf:349-364` (the `edge_site` monitor on `https://kirichess.com`,
  `check_frequency = 180`, `regions = ["us", "eu"]`).
- Impact: A request every three minutes from two regions restarts the machine before it can idle, so
  the machine is effectively always on: the cost saving the setting was chosen for does not
  materialise (`docs/audits/I.md:43` prices it as scale-to-zero), and — the more useful half —
  the cold-start latency the setting trades away is never visible to the monitoring, because the
  monitor is the thing preventing it. Nobody will learn the real first-visit number from a chart.
- Fix: One line in DEPLOYMENT.md § 1 noting that 10c.5's edge monitors keep the UI machine warm, so
  `min_machines_running = 0` is a fallback rather than the operating state. If a cold-start figure
  is ever wanted, stop the machine by hand and time one request.

#### I-2-13 — The compose file that represents production hard-depends on a Redis production has never had
- Severity: Low (trigger: a fork or self-hoster reading `docker-compose.prod.yml` as the production
  shape, or removing the unused service)
- Where: `docker-compose.prod.yml:19-31` (the `redis` service), `:74`
  (`Redis__Configuration=${Redis__Configuration:-}`, empty by default), `:81-85`
  (`depends_on: redis: condition: service_healthy`); `docker-compose.e2e.yml` sets no
  `Redis__Configuration` either, so the E2E stack also runs a Redis nothing connects to;
  `docs/ops/DEPLOYMENT.md:490-518` (§ 7: no Redis has ever been provisioned against the Fly app).
- Impact: The file DEPLOYMENT.md § 6 and O-5 both point a stranger at presents Redis as a hard
  prerequisite of the backend starting, while the actual production deployment has never had one and
  the app treats it as optional throughout (`RedisRegistration.cs:40`,
  `RateLimiting.cs:201` — `redis.Enabled && options.Distributed`). A fork that reasonably deletes
  the unused service finds the backend refusing to start; a fork that keeps it concludes Redis is
  required. Neither reading matches production.
- Fix: A two-line comment in `docker-compose.prod.yml` stating that the Fly deployment runs without
  Redis and that this service exists so the Phase 8.4 multi-instance path can be exercised locally,
  and relax the dependency to `condition: service_started` (or drop it) so removing the service
  works.

### Already covered

- **`fly.*.toml` key-level drift, `[metrics]` vs `[[metrics]]`, and the absence of a parity guard** —
  `docs/audits/C.md:116-127` (C-9-6) and `docs/audits/C.md (section INF-1)`. Not
  re-reported; I-2-6 is the *build-arg* divergence between compose and `fly.ui.toml`, which neither
  covers.
- **`strategy = "immediate"`, `kill_timeout = 30`, and a deploy interrupting live games** — INF-1's
  "already covered" section, `I.md (I-1)`, `DEPLOYMENT.md:852-856`.
- **REST move/resign bypassing `GameCommandRouter` the day a second instance exists** —
  `docs/audits/S.md` (S-1-15), `I.md (I-1)`. I-2-4 is the complementary gap: nothing
  detects that a second instance exists.
- **Redis unprovisioned as a standing position, and Upstash cost the day it is provisioned** —
  INDEX standing positions, `docs/audits/I.md:125-139` (I-9-4). This audit confirms the position
  is safe in prod config: no Fly config, secret, or code path assumes Redis exists.
- **The Turnstile siteverify Worker having no source or config in the repo** —
  `docs/audits/I.md:82-99` (I-10-3).
- **Neon plan, autosuspend, and the always-on pool** — `docs/audits/I.md:86-103` (I-9-2).
- **Cloudflare holding domain, DNS, front door and the backup bucket in one account** —
  `docs/audits/I.md:79-95` (I-4-2).
- **Unfiltered `logs.>` shipping all four apps into one Better Stack source** — stated in
  `fly.logshipper.toml:25-31`; cost angle at `docs/audits/I.md:140-155` (I-9-5).
- **Nothing gates CI green before a deploy; `FLY_API_TOKEN` scope; no image scan** —
  `docs/audits/I.md` (I-3-1, I-3-2, I-3-3).
- **No release-confirmation step after a deploy; `/health/ready` reporting ready on an unusable
  database** — `docs/audits/I.md` (I-5-3, I-5-4).
- **Dev compose on Postgres 15 vs prod-shaped compose on 16; missing `API_BASE_INTERNAL` in
  `docker-compose.prod.yml`** — INF-1-1, INF-1-5.
- **Cloudflare Access still fronting the apex until DH.3, and the `302` allowance that follows from
  it** — `DEPLOYMENT.md`, `docs/audits/I.md:118-132` (I-5-5).

### Action list

1. **Owner, first, ten minutes.** Answer I-2-3: is `api.kirichess.com` proxied, is `kirichess.com`,
   and at what SSL mode. Everything below branches on it. Write the answer into DEPLOYMENT.md § 1 as
   a table with the confirming command.
2. **Owner.** Answer I-2-7 with `fly secrets list --app kirichess-api` and correct whichever of the
   two documents is wrong.
3. I-2-1: make the trusted-hop count explicit and add a
   two-entry `X-Forwarded-For` test over `IpKey` and `HubConnectionBudget`. Do not ship this before
   4.
4. **Owner.** I-2-2: put a Cloudflare-set secret header in front of the Fly origin, exempting
   `/health` and `/health/ready`; then the change in 3 is safe.
5. I-2-4: add `--ha=false` to both `flyctl deploy` steps in `deploy.yml`, and the
   `fly machines list` check to DEPLOYMENT.md § 3 and § 7.
6. I-2-6: pass a Turnstile test site key through `docker-compose.e2e.yml` so the E2E
   registration exercises the widget.
7. **Owner.** I-2-8: switch the documented Neon string to `SSL Mode=VerifyFull` without the trust
   override, verify on staging, then production and both backup scripts.
8. I-2-5: add a hub leg to `scripts/synthetic-play.sh` through `api.kirichess.com`.
9. I-2-9 pin the log-shipper image by digest; I-2-10, I-2-12, I-2-13 are one comment each.
10. **Owner.** I-2-11: decide where `terraform.tfstate` and `terraform.tfvars` are backed up, and
    write it into `infra/betterstack/README.md`. Feeds B-1.

---

## I-3 — CI/CD and release

Date: 2026-08-30
Packet: docs/audits/INDEX.md, table 5

### Verdict

The release machinery is small, legible and mostly right: production deploys only on a `v*` tag or a
manual production dispatch, a `verify-main` job refuses any tag or dispatch whose commit is not an
ancestor of `origin/main`, staging deploys itself off every push to `main`, the schema migration runs
as a Fly `release_command` in a throwaway machine that aborts the deploy on a non-zero exit
(`RunMigrationsOnStartup=false` everywhere), rollback is documented as roll-forward-only with a
`fly deploy --image` app-level fallback, every third-party action is SHA-pinned with a Dependabot
comment, and secrets are passed through `env:` to a CLI, never interpolated into a `run:` string.
`CiWorkflowDriftTests` accurately reflects reality — all eight claimed workflow files exist, the
trigger assertions it makes all pass, and the SHA-pin sweep passes.

The gaps are at the seam between the two independent workflows. `ci.yml` and `deploy.yml` do not know
about each other: `ci.yml` never runs on a `v*` tag, and nothing requires it to be green on the
tagged commit before `deploy.yml` ships it — the owner-in-the-loop tagging habit is the only gate.
The image the deploy actually ships is built fresh on Fly's remote builder at tag time and is never
scanned; Trivy only ever sees a different, locally-built image inside `ci.yml`'s e2e job. `FLY_API_TOKEN`
is one repository-scoped secret that deploys all four apps including production, with no environment
protection rule visible on it. And the drift test that exists precisely to stop a CI claim from going
stale does not pin any of `deploy.yml`'s release-safety properties, so the `verify-main` gate could be
removed without failing a single test. None of this is a live incident; each is a place where one
forgotten step or one refactor ships something unverified.

### Findings

#### I-3-1 — Nothing requires CI to be green before a deploy; `ci.yml` never runs on a tag
- Severity: Medium
- Where: `.github/workflows/ci.yml:3-6` (`on: pull_request` + `push: branches: [main, dev]` — no
  `tags:`), `.github/workflows/deploy.yml:3-8` (`on: push: branches: [main], tags: ["v*"]`),
  `.github/workflows/deploy.yml:47-64` (`production-backend` has `needs: verify-main` but no status
  check on CI), `docs/ops/DEPLOYMENT.md:142-166`.
- Impact: `git tag v1.2.3 && git push origin v1.2.3` starts `deploy.yml` immediately. `ci.yml` does
  **not** trigger on a tag push (its `push` filter is `branches: [main, dev]` only), so the only CI
  signal on the tagged commit is whatever ran earlier when that commit landed on `main` — which may
  be red, may still be running, or (if `main` was updated by a direct push rather than a PR) may
  be a race. `verify-main` only proves the
  commit is *on* `main`, not that it *passed*. Staging has the same shape: every push to `main`
  deploys with no gate. Because rollback is roll-forward-only and every deploy already interrupts
  in-progress games (`DEPLOYMENT.md:853-857`), a broken deploy is a real outage to extend, not a
  quick revert.
- Fix: add a `gate` job at the top of `deploy.yml` that `needs`-blocks `production-backend` /
  `staging-backend` and asserts CI succeeded on `github.sha` — cheapest form is one
  `gh api repos/${{ github.repository }}/commits/${{ github.sha }}/check-runs` call that fails unless
  the `CI` runs concluded `success`. If a tag can be pushed to a commit CI never ran on, have the
  gate dispatch `ci.yml` (or just re-run `dotnet test` + `npm test` + both builds inline) and wait.
  Complements I-5-3, which adds the *post*-deploy smoke check; this one is the *pre*-deploy gate.

#### I-3-2 — The image the deploy ships is never scanned
- Severity: Medium
- Where: image scan lives only in `.github/workflows/ci.yml:168-186` (Trivy, in the `e2e` job,
  against `docker compose ... --build` images resolved by id at `:152-156`); `deploy.yml:62`
  (`flyctl deploy --config fly.backend.toml --remote-only`) and `:76` build a **fresh** image on
  Fly's remote builder from `fly.backend.toml:21-22` / `fly.ui.toml` at tag time. No scan step in
  `deploy.yml`.
- Impact: Trivy in `ci.yml` scans a locally-compose-built image at PR/push time. The artifact that
  actually reaches production is a separate build, on a different builder, at a later moment, and is
  never scanned. A CRITICAL/HIGH CVE disclosed between the last `ci.yml` e2e run on the tagged SHA
  and the tag push ships unremarked; so does any tag pushed to a commit whose e2e job was skipped,
  flaked, or predates a `.trivyignore` expiry (`.trivyignore` entries expire as early as
  `2026-09-01`). Base images are digest-pinned and Dependabot-bumped (`.github/dependabot.yml:46-51`,
  INF-1 verified), which bounds but does not close the window.
- Fix: in `deploy.yml`, split the deploy into `flyctl deploy --build-only --push` (produces
  `registry.fly.io/kirichess-api:deployment-<id>`), run `aquasecurity/trivy-action` against that
  ref with the same `severity: CRITICAL,HIGH`, `ignore-unfixed: true`, `trivyignores: .trivyignore`
  settings `ci.yml` uses, then `flyctl deploy --image <that ref>` only if it passes. Or accept the
  proxy and add a one-line note to `DEPLOYMENT.md §3` that image-scan currency depends on `ci.yml`
  having run on the tagged commit.

#### I-3-3 — `FLY_API_TOKEN` is one repo-scoped secret for all four apps, with no environment protection
- Severity: Medium
- Where: `.github/workflows/deploy.yml:64, 78, 94, 108` — every deploy job reads
  `${{ secrets.FLY_API_TOKEN }}`; jobs declare `environment: production` / `environment: staging`
  (`:50, 69, 85, 99`) but the token is a repository secret, not an environment secret, and no wait
  timer or required reviewer is visible on the `production` environment. `docs/audits/INDEX.md:228`
  ("`FLY_API_TOKEN` has already burned once").
- Impact: after the burn, the residual exposure is blast radius. One leaked token is deploy access
  to `kirichess-api`, `kirichess-ui`, and both staging apps — including the ability to push an
  arbitrary image to production. The `environment:` keys are currently cosmetic: they scope nothing
  because the secret is repo-wide, and (as far as any repo artifact shows) the `production`
  environment has no protection rule, so a compromised Actions run or a malicious PR workflow change
  that reaches `main` can deploy to production unattended.
- Fix: mint per-app deploy tokens (`fly tokens create deploy -a kirichess-api`, one per app), store
  them as **environment** secrets under the GitHub `production` and `staging` environments (so a
  staging-scoped leak cannot touch production), and enable "Required reviewers" on the `production`
  environment so a prod deploy is one approval click. Cross-references B-4-2 (rotation cadence and
  the operational-secret inventory) — this finding is the scoping/isolation half B-4 did not cover.

#### I-3-4 — `setup-flyctl` is pinned to an untagged `master` commit with no update path, and the comment says otherwise
- Severity: Low (trigger: a bug, needed feature, or security fix in `flyctl-actions`)
- Where: `.github/workflows/deploy.yml:59, 75, 91, 105` —
  `superfly/flyctl-actions/setup-flyctl@ed8efb33836e8b2096c7fd3ba1c8afe303ebbff1 # master @ 2026-08-19`;
  the comment at `:57-58` claims "Dependabot's github-actions ecosystem entry still bumps the SHA on
  its own schedule even without a version string to update."
- Impact: that claim is wrong. Dependabot's `github-actions` updater needs the action to publish
  releases or semver tags to compute an update; `superfly/flyctl-actions` has none (the comment
  itself says "No release tag exists upstream"). So this pin is frozen at a 2026-08-19 `master`
  commit indefinitely — no bumps, no security fixes — on the one action that runs inside the
  `FLY_API_TOKEN` jobs. `CiWorkflowDriftTests.Every_workflow_pins_third_party_actions_to_a_commit_sha`
  passes because it is a 40-hex SHA; nothing notices it never moves.
- Fix: drop the action and install flyctl directly in a `run:` step
  (`curl -L https://fly.io/install.sh | sh` then prepend `~/.fly/bin` to `$GITHUB_PATH`), removing a
  third-party action from the credentialed deploy path entirely; or add "check `superfly/flyctl-actions`
  for a first tagged release / newer `master`" to the B-4 maintenance calendar. Either way, correct
  the misleading comment.

#### I-3-5 — `CiWorkflowDriftTests` does not pin any of `deploy.yml`'s release-safety properties
- Severity: Low (trigger: a refactor of `deploy.yml` that drops a guard)
- Where: `tests/KiriChess.Server.Tests/CiWorkflowDriftTests.cs:44-59` (`deploy.yml` only asserted to
  *exist*), `:155-174` (SHA-pin sweep covers it). No test asserts `verify-main`, the
  `git merge-base --is-ancestor` check (`deploy.yml:37-42`), the
  `startsWith(github.ref, 'refs/tags/v')` production condition (`:24`), or the staging
  `github.ref == 'refs/heads/main'` condition (`:83`). Contrast the detailed trigger assertions the
  same class makes for `codeql.yml` (`:66-77`), `semgrep.yml` (`:85-94`), `offsite-backup.yml`
  (`:181-190`) and `ops-probes.yml` (`:211-221`).
- Impact: `DEPLOYMENT.md:154` states "Only commits reachable from `main` can deploy" as a load-bearing
  property. A future edit that removes the `if:` on `verify-main`, or broadens `deploy.yml`'s tag
  filter, or lets staging deploy from an arbitrary branch, would pass every test in the suite whose
  stated purpose is "a claimed control is missing should fail the build, not a roadmap tick nobody
  re-reads" (`CiWorkflowDriftTests.cs:7-19`). The release workflow's own safety net is the one CI
  claim the drift test does not hold.
- Fix: add facts to `CiWorkflowDriftTests` in the existing idiom: `deploy.yml` contains
  `merge-base --is-ancestor` and a `verify-main` job; production jobs are gated on
  `refs/tags/v` or a `workflow_dispatch` with `environment == 'production'`; `staging-backend` is
  gated on `refs/heads/main`.

#### I-3-6 — A half-completed deploy leaves backend and UI on different wire contracts with nothing flagging it
- Severity: Low (trigger: `production-backend` succeeds and `production-ui` fails, or the run is
  cancelled between the two)
- Where: `.github/workflows/deploy.yml:66-78` (`production-ui` has `needs: production-backend`);
  `fly.backend.toml:30` (`strategy = "immediate"`); the generated client `ui/lib/api.generated.ts`
  is regenerated from the backend's OpenAPI document and gated in `ci.yml:100-101` but only at build
  time.
- Impact: if the UI deploy fails after the backend deploy lands, production serves a new backend
  behind an old UI whose generated API types may not match a renamed route or changed DTO. Roll-
  forward-only is the correct recovery, but nothing confirms the pair landed together, and
  `deploy.yml` has no post-run assertion (see I-5-3). The window is short and self-inflicted rather
  than stranger-facing, hence Low.
- Fix: acceptable at this scale; fold into I-3-1's gate job (assert both Fly apps report the tagged
  release after the run) or add one line to `DEPLOYMENT.md §3` that a partially-applied deploy is
  finished by re-running the workflow, never left.

### Already covered

- **`ci.yml` / `deploy.yml` / `restore-drill.yml` missing `permissions:`, `timeout-minutes`,
  `concurrency`; `restore-drill.yml` stale `setup-dotnet` v4 pin** — `docs/audits/C.md (section INF-1)`
  (INF-1-2, INF-1-3, INF-1-4). Not re-reported.
- **`strategy = "immediate"` (no health-gated rollout, no auto-rollback) is a deliberate consequence
  of Redis being unprovisioned, not a gap; manual rollback path documented** —
  `docs/audits/C.md (section INF-1)` (Already covered §), `DEPLOYMENT.md §3 / §3.1`.
- **Migration gate (`release_command`, `RunMigrationsOnStartup=false`) consistent across both Fly
  tomls, all four compose files, and `.env.example`; workflow secrets always passed via `env:`** —
  `docs/audits/C.md (section INF-1)` (Already covered §).
- **Staging/prod `fly.backend*.toml` parity is unguarded (`[metrics]` vs `[[metrics]]` is the tell)** —
  `docs/audits/C.md` (C-9-6). Not re-reported.
- **A release serves traffic before anything confirms it can (post-deploy smoke step missing from
  `deploy.yml`); nothing notices when `/metrics` goes dark** — `docs/audits/I.md` (I-5-2, I-5-3).
  I-3-1 is the complementary *pre*-deploy CI gate.
- **GitHub disables every scheduled workflow after 60 days of no commits; no rotation cadence or
  expiry tracking for `FLY_API_TOKEN`, `NEON_API_KEY`, R2 keys, `SYNTHETIC_API_TOKEN`** —
  `docs/audits/B.md` (B-4-1, B-4-2). I-3-3 is the token-*scoping* half, not the rotation half.
- **Roll-forward-only rollback policy and the per-migration app-rollback cost table** — standing
  position; detail in `docs/ops/MIGRATIONS.md` and `DEPLOYMENT.md §3.1`. Schema/migration invariants
  are packet **C-12** (not yet run).
- **CodeQL / Semgrep / gitleaks coverage, `--error` gating, digest pinning** — `CiWorkflowDriftTests`
  and `docs/audits/C.md (C-1) ` INF/BE sections; SBOM and transitive-copyleft are **O-6** / **I-11**.
- **`docker-compose.prod.yml` self-host gaps (Postgres 15↔16, `API_BASE_INTERNAL`)** —
  `docs/audits/C.md (section INF-1)` (INF-1-1, INF-1-5).

### Action list

Ordered. Owner items are marked.

1. I-3-1: add a `gate` job to `deploy.yml` that blocks the backend deploy jobs until CI
   concluded `success` on `github.sha` (a single `gh api .../check-runs` assertion). Highest value:
   closes the "tag ships a red or unverified commit" hole for both environments.
2. **Owner** — I-3-3: mint per-app `fly tokens create deploy` tokens, move them to GitHub
   **environment** secrets under `production` / `staging`, and turn on Required reviewers for the
   `production` environment. Pair with B-4-2's first rotation.
3. I-3-2: add a Trivy scan of the actual deploy artifact to `deploy.yml`
   (`--build-only --push` → scan `registry.fly.io/...` → `--image`), reusing `.trivyignore`.
4. I-3-5: extend `CiWorkflowDriftTests` to pin `verify-main`, the ancestor check, and the
   tag/branch conditionals in `deploy.yml`.
5. I-3-4: replace `setup-flyctl` with a direct flyctl install in `deploy.yml`, or add a
   calendar line to track it; fix the incorrect Dependabot comment either way.
6. I-3-6: fold a "both apps on the tagged release" assertion into item 1's gate job, or
   add the one-line roll-forward note to `DEPLOYMENT.md §3`.

No production code changed in this packet. No standing-position reversals. Only `docs/audits/I.md`
was created.

---

## I-4 — Backup and DR
Date: 2026-08-31
Packet: docs/audits/INDEX.md

### Verdict

The design here is better than the state. Neon PITR is genuinely proven rather than assumed — one
script drives both the by-hand and the scheduled drill (`scripts/restore-drill.sh`), it asserts four
things in order of how much each one hurts, and it passed for real on 2026-08-17
(`docs/ops/DEPLOYMENT.md:402-403`). The off-site path is written to the same standard and in one
place it is better: it verifies *the object that landed in R2*, not the file that was sent, and it
restores into a local Postgres rather than back into Neon, which is the only way to prove the copy
survives the provider. Restoring the Data Protection key ring is a solved problem since 10a.1
(`src/KiriChess.Server/Program.cs:177-179`), and `Jwt__Key` — the other key a restore might have
needed — turns out not to matter, because refresh tokens are opaque randoms compared against a
SHA-256 hash rather than signed (`src/KiriChess.Server/Data/Entities/RefreshToken.cs:8`).

What the state costs is concentrated in three places, and none of them is the one already on every
list. **That the first dump never ran is stated, tracked and cross-referenced below; this report does
not re-litigate it.** The three things nobody has looked at are: the off-site copy was placed in the
same provider account that holds the domain, DNS, the front door and Turnstile, so a Cloudflare loss
takes the site and its backup together — the exact correlated failure §5.3 exists to break; the R2
object is a plaintext copy of every account, password hash and the Data Protection ring, written by a
credential that can also delete all eight retained copies, with no bucket versioning anywhere; and
the weekly job advances the `latest` pointer and prunes the older dumps *before* it verifies the new
one, so a bad dump can become the pointer the disaster path reads while evicting a known-good copy.
Underneath all of it sits a number nobody has written down: the Neon history-retention window, which
is the actual RPO of the production path, and which the monthly drill never tests because it always
restores one hour back. The compose `pg_dump` snippet is confirmed *not* relied on in production —
but it is not dead text either, and it is subtly wrong.

### Findings

#### I-4-1 — The R2 copy is a plaintext player database written by a credential that can also delete every copy
- Severity: High (trigger: the first successful dump, which `DEPLOYMENT.md:472-475` says is imminent)
- Where: `scripts/offsite-backup.sh:410-412` (`pg_dump --format=custom --compress=9 --no-owner
  --no-acl`, no encryption step anywhere), `:421-422` (upload), `:337-351` (`prune_old_dumps` issues
  `aws s3 rm`, `:348`); token scope at `docs/ops/DEPLOYMENT.md:443-445` ("Object Read & Write");
  credentials at `:446-449`; `src/KiriChess.Server/Program.cs:177-179` —
  `AddDataProtection().SetApplicationName("KiriChess").PersistKeysToDbContext<KiriChessDbContext>()`
  with **no `ProtectKeysWith*`**, and `src/KiriChess.Server/Data/KiriChessDbContext.cs:13,46`
  (`IDataProtectionKeyContext`, `DbSet<DataProtectionKey>`); no bucket versioning, object-lock or
  lifecycle configuration exists in the tree — `infra/` holds only `betterstack`
- Impact: from the first green run, one credential pair is simultaneously (a) read access to every
  account email, password hash, Identity authenticator secret, API-token hash, report and sanction in
  the database, plus the Data Protection key ring in the clear — the ring being in Postgres is what
  makes the restore work, and it is also what puts it in the dump — and (b) delete access to all
  eight retained copies and the `latest` pointer. The whole boundary is two GitHub repository secrets
  on a now-public repo. Neither half is wrong on its own; together, and with no versioning, a single
  leaked or mis-scoped token turns the thing that exists to survive a disaster into a second one.
- Fix: two dashboard settings, no code — enable object versioning (or a bucket lock retention rule)
  on `kirichess-backups` so a delete is recoverable, and record in `DEPLOYMENT.md` §5.3 that the
  object is unencrypted at rest beyond Cloudflare's own. Client-side encryption (`age -p` before the
  `s3 cp`) is one line if wanted later, but only alongside writing the passphrase into the B-1
  succession note: an unrecoverable backup is a worse outcome than an unencrypted one.

#### I-4-2 — The off-site copy lives in the same provider account as the domain, DNS and the front door
- Severity: High (trigger: Cloudflare account suspension, lockout, or billing lapse)
- Where: `docs/ops/DEPLOYMENT.md:15-17` (Cloudflare is DNS + proxy "and after 10c.1 it also holds
  the off-site Postgres dumps in R2"), `:439-450` (bucket and token created inside that same
  account), `:617-635` (§9.1 Turnstile), `docs/audits/B.md:117-123` (Cloudflare Registrar holds
  `kirichess.com`), `docs/audits/I.md:37` (the Turnstile siteverify Worker exists only as a
  deployed artifact in that account)
- Impact: §5.3's stated purpose is "the copy that still exists if Neon is the thing that failed", and
  against Neon it works. But the copy was placed in the account that also holds the domain, the DNS
  zone, the CDN/WAF, Turnstile and the Turnstile Worker — so a Cloudflare loss takes the front door,
  the name every email link and the OAuth redirect are built from, *and* the backup that recovery
  depends on, in one event. The other three accounts are fine by comparison: Fly-loss redeploys two
  stock images elsewhere (I-10), GitHub-loss stops every safety net at once but loses no data
  (`B.md:14-16`), Neon-loss is the case actually designed for. Cloudflare is the one correlated
  failure, and it is the provider whose account this project depends on most broadly.
- Fix: smallest version, and it needs no new account: add a line to the B-4 quarterly checklist to
  download the newest dump to the maintainer's own disk (or any storage outside both providers) and
  keep the last one, and state the correlation explicitly in `DEPLOYMENT.md` §5.3 so it is an
  accepted risk rather than an unnoticed one. If a second destination is ever wanted, note that
  `scripts/offsite-backup.sh:58-79` is pure S3 — a second `--endpoint-url` is a few lines.

#### I-4-3 — `latest` is advanced and old dumps pruned before the new dump is verified
- Severity: Medium
- Where: `scripts/offsite-backup.sh:421-422` (upload, then write `kirichess/latest`), `:433`
  (`prune_old_dumps`), `:434` (`verify_dump_locally` — the schema / boot / replay assertions, running
  last); the pointer is what the disaster path reads by default at `:325-335`, specifically `:329`
- Impact: the ordering means R2 is mutated on the strength of a successful `put` and a byte-count
  match, before anything has proved the object is *restorable*. Two consequences. The `latest`
  pointer — the one `bash scripts/offsite-backup.sh restore` follows when `DUMP_KEY` is unset — can
  be left naming an object that failed assertion 3 or 4, and the job's failure email does not undo
  it, so a later disaster restore silently picks up the bad dump. And `prune_old_dumps` counts that
  same unverified object toward `KEEP_DUMPS`, evicting the oldest known-good copy to make room for
  one that did not pass. The byte-count round trip at `:427-431` is genuinely good and catches a
  truncated upload; it just runs before the checks that catch a bad *dump*.
- Fix: move the `latest` write and the `prune_old_dumps` call to after `verify_dump_locally` returns,
  so both only ever act on an object that passed all three assertions. A failed run then leaves the
  new object in the bucket, harmless and unreferenced, with the previous good pointer intact.

#### I-4-4 — The disaster-restore path hardcodes Postgres 16 and never reads the archive header
- Severity: Medium (trigger: running the documented disaster command against a dump written by
  Postgres 17)
- Where: `scripts/offsite-backup.sh:373` (`PG_MAJOR="${PG_MAJOR:-16}"`), against `:123-136`
  (`detect_pg_major_from_uri`) and `:406`, which dump mode uses to pick a matching image; the comment
  at `:369-372` describes reading the custom-format header via `pg_restore --list` and then does not
  do it; the three documented commands at `docs/ops/DEPLOYMENT.md:414-427` mention no `PG_MAJOR`
- Impact: dump mode is correct — it detects the major Neon actually runs and dumps and verifies with
  that image, so the weekly job is safe. The standalone `restore` mode is the one used at exactly the
  moment Neon is unavailable and there is no server to interrogate, and it defaults to
  `postgres:16-alpine`. `pg_restore` 16 cannot read a custom-format archive written by `pg_dump` 17;
  the failure is a header-version error, mid-incident, against the only remaining copy. The escape
  hatch (`PG_MAJOR=17`) exists but appears only in a source comment, which is not where a person
  restoring under pressure will look.
- Fix: derive the major from the archive itself — `pg_restore --list` on the downloaded file prints
  the version it was dumped from — or fall back from 17 to 16 on failure. Either way add `PG_MAJOR=`
  to `DEPLOYMENT.md` §5.3's disaster block so the manual override is documented where it is needed.

#### I-4-5 — The PITR horizon is the RPO of the production path, and it is neither recorded nor tested
- Severity: Medium
- Where: `scripts/restore-drill.sh:101-109` (restore point defaults to one hour ago),
  `.github/workflows/restore-drill.yml:15-18` (the schedule passes no `restore_point`, so every
  scheduled run takes that default; the input at `:20-24` is dispatch-only),
  `docs/ops/DEPLOYMENT.md:375` (assertion 1's failure text says PITR may not "reach back that far on
  the current plan" — the plan and the window are never named), `docs/decisions/RETENTION.md:135-143`
  (§6 describes both paths and states no window)
- Impact: `DEPLOYMENT.md` §5 and `RETENTION.md` §6 both present Neon PITR as the production backup,
  and the drill proves it works — one hour back. Nothing anywhere states how far back it actually
  reaches, and Neon's history-retention window is plan-dependent (24 hours on Free). So the
  recoverable age of a *slow* failure — a bad migration noticed the next morning, a bug corrupting
  rows over a week, an abusive script — is an unknown number, and if it is 24 hours then the entire
  recovery story for that class of incident is the weekly R2 dump, which has never run. This is the
  DR half of I-9-2's "the Neon plan is unrecorded"; that finding is about the bill, this one is about
  what is recoverable.
- Fix: read the project's history-retention setting in the Neon console once and write the number
  into `DEPLOYMENT.md` §5 beside the drill, as the stated RPO for the Neon path. Then dispatch the
  drill once with `RESTORE_POINT` near the far edge of that window, so the number is tested rather
  than quoted — that is the single dispatch that turns assertion 1 from "PITR works" into "PITR
  reaches N days".

#### I-4-6 — A restore un-anonymises erased accounts and unbans sanctioned users, with no reconciliation step
- Severity: Medium
- Where: `docs/decisions/RETENTION.md:135-143` (§6 covers backups and says nothing about their
  interaction with erasure); the string "backup" does not appear in `docs/legal/PRIVACY.md`,
  `docs/legal/ROPA.md` or `docs/legal/TERMS.md` at all; retention of eight weekly dumps at
  `scripts/offsite-backup.sh:36` (`KEEP_DUMPS=8`) and `docs/ops/DEPLOYMENT.md:463-465`;
  `src/KiriChess.Server/Data/Entities/UserSanction.cs`
- Impact: two standing positions — erasure anonymises, and sanctions survive deletion — are both
  silently reversed by a restore for anything that happened after the restore point. With an R2 dump
  up to eight weeks old, a Neon-loss recovery can bring back personal data for accounts erased in
  that window and lift bans issued in it, and nothing in the runbook says to re-apply either. Worse
  for the erasure half: because erasure anonymises in place, the database itself no longer records
  who asked, so after a restore there is nothing *in* the system to reconcile against. The evidence
  that does survive is the `privacy@kirichess.com` mailbox, and only if someone thinks to use it.
- Fix: three lines in `DEPLOYMENT.md` §5.3's disaster block — after restoring from a dump, re-run
  erasure for every request in the `privacy@` mailbox dated after the dump's timestamp, re-apply any
  sanction issued after it (the moderation log and the mailbox are the record), and note the restore
  and its point in the incident record. Cross-ref `RETENTION.md` §6 and L-2.

#### I-4-7 — The compose backup snippet is confirmed not production, but it is the one untested restore path and it is wrong
- Severity: Low (trigger: a fork, or the maintainer's own VPS exit per I-10-1, following §5's
  compose commands)
- Where: `docs/ops/DEPLOYMENT.md:322-327`; the production path it sits under is `:311-320`;
  `docker-compose.prod.yml:2-9` (the `db` service is a real `postgres:16-alpine` with a volume)
- Impact: **Confirmed not relied on in production.** Production is Neon; no workflow, script or test
  touches the compose `db` service for backup, and §5 labels the snippet "(local / VPS)". But it is
  not dead text: I-10-1's proposed exit runbook and O-5's fork path both land on
  `docker-compose.prod.yml`, so this is the backup procedure a self-hoster actually meets. As written
  it is plain-SQL `pg_dump` with no `--clean`/`--if-exists`, restored with `psql` into a database
  that already carries the schema — which produces a wall of duplicate-key errors and a half-restored
  database, and `psql` without `ON_ERROR_STOP` exits 0 while doing it. It is the only backup snippet
  in the repo with nothing asserting it, sitting two sections away from two scripts that assert three
  things each.
- Fix: replace the pair with the shape the real script already uses — `pg_dump --format=custom
  --no-owner --no-acl` and `pg_restore --clean --if-exists` — or delete the snippet and point at
  `bash scripts/offsite-backup.sh restore` with `RESTORE_DATABASE_URL`, which is the tested path and
  is already documented six lines further down at `:424-426`.

#### I-4-8 — Nothing in the recovery sections says what a restored database still needs to become a running site
- Severity: Low
- Where: `docs/ops/DEPLOYMENT.md` §5 / §5.2 / §5.3 name no secret at all; `Jwt__Key` is minted once
  into `fly secrets` at `:35-38`; `docs/audits/B.md:216-224` has the secrets table but it is a
  rotation calendar, not a recovery list; `docs/audits/I.md:60-62` assembles the must-set list as
  part of a runbook that does not exist yet
- Impact: the two key-material questions this packet asks both have good answers, and neither is
  written where a person restoring at 3 a.m. would find it. The Data Protection ring travels *inside*
  both backup paths since 10a.1 (`src/KiriChess.Server/Program.cs:177-179`), so confirmation and
  password-reset links already in inboxes survive a restore — a real change from the pre-10a.1
  behaviour that section documents. `Jwt__Key` travels in neither, and does not need to: refresh
  tokens are opaque random values compared against a SHA-256 hash rather than signed
  (`src/KiriChess.Server/Data/Entities/RefreshToken.cs:8`, pattern stated at
  `src/KiriChess.Server/Data/Entities/ExternalLoginCode.cs:13`), so a fresh signing key costs only
  the access tokens currently in flight and clients recover on their next refresh. Unwritten, both
  facts are re-derived under pressure, and the second one reads like a mass-logout risk until
  somebody opens `TokenService.cs`.
- Fix: fold I-10-1's six-secret minimum into `DEPLOYMENT.md` §5.3's disaster block and add those two
  sentences beside it — the key ring comes back with the data, `Jwt__Key` does not and does not need
  to. Two paragraphs, and it closes the key-material half of this packet permanently.

### Already covered

- **The first off-site dump has never run (private-repo Actions quota, parked 2026-08-20)** —
  `docs/ops/DEPLOYMENT.md:472-475`, `docs/audits/B.md` (B-4-4), `docs/audits/I.md` (I-10-2),
  `docs/decisions/RETENTION.md:141-143`, ROADMAP 10c.1. Stated, tracked and owned; not re-reported.
  Every finding above is written to survive that run going green.
- **R2 and GitHub become processors of a full production dump on the first success; PRIVACY §4 /
  ROPA §3-§4 rows and a bucket lifecycle rule needed** — `docs/audits/L.md` (L-3-3). I-4-1 is the
  custody-and-deletion exposure, not the paperwork.
- **No "move off Fly / Neon / Cloudflare" runbook; the exit is mechanical but unwritten** —
  `docs/audits/I.md` (I-10-1). I-4-8 only asks that its secret list land in §5.3 too.
- **The Turnstile siteverify Worker has no source in the repo** — `docs/audits/I.md` (I-10-3);
  cited in I-4-2 only as one more thing inside the Cloudflare account.
- **A GitHub dormancy pause stops the restore drill, the off-site backup and the ops probes together;
  no rotation cadence for `NEON_API_KEY` / the R2 keys** — `docs/audits/B.md` (B-4-1, B-4-2).
- **`restore-drill.yml` has no `permissions:` block and pins a stale `setup-dotnet` major** —
  `docs/audits/C.md (section INF-1)` (INF-1-2, INF-1-4), `docs/audits/I.md`.
- **Failure notification is GitHub's email only; a disabled schedule is indistinguishable from a
  passing one; no on-call** — `docs/ops/DEPLOYMENT.md:397-400,467-470`, `docs/audits/I.md`,
  `docs/audits/I.md`.
- **The Neon plan is unrecorded (cost angle) and the always-on pool defeats autosuspend** —
  `docs/audits/I.md` (I-9-2). I-4-5 is the recoverable-age consequence of the same missing fact.
- **`docs/audits/I.md:24,202` calls the `pg_dump` path "proven"** — it is not; it has never run.
  Worth correcting there when that file is next touched, not a defect of its own.

### Action list

1. **Owner.** Dispatch **Off-site backup** and watch it finish. Everything below assumes the copy
   exists; this is still the single highest-value action in the packet, and it is already owned by
   B-4-4 / I-10-2 / ROADMAP 10c.1.
2. **Owner.** In the Cloudflare dashboard, turn on object versioning (or a lock retention rule) for
   `kirichess-backups` before that first dump lands (I-4-1). One setting, and it is the difference
   between a leaked token being an exposure and it being an extinction event.
3. Reorder `scripts/offsite-backup.sh` so the `latest` write and `prune_old_dumps` run
   after `verify_dump_locally` (I-4-3). Smallest real code change in this report and the one with a
   silent failure mode.
4. **Owner.** Read the Neon history-retention window, write it into `DEPLOYMENT.md` §5 as the stated
   RPO, then dispatch the restore drill once with `RESTORE_POINT` near its far edge (I-4-5).
5. `DEPLOYMENT.md` §5.3 disaster block: name Cloudflare as the accepted correlated
   failure (I-4-2), add the `PG_MAJOR` override (I-4-4), add the erasure/sanction reconciliation
   lines (I-4-6), and fold in I-10-1's must-set secret list plus the two key-material sentences
   (I-4-8).
6. Fix `DEPLOYMENT.md` §5's compose snippet to custom-format `pg_dump` / `pg_restore
   --clean --if-exists`, or replace it with the tested `offsite-backup.sh restore` invocation
   (I-4-7).
7. Make `scripts/offsite-backup.sh restore` detect the archive's Postgres major instead of
   defaulting to 16 (I-4-4).
8. **Owner + B-4.** Add "keep one dump outside both Cloudflare and Neon" to the quarterly checklist
   (I-4-2).

No standing-position reversals. No production code change is required by any item above; items 3 and
7 touch `scripts/`, not `src/`.

---

## I-5 — Observability

Date: 2026-08-30
Packet: docs/audits/INDEX.md, table 5

### Verdict

The observability *plumbing* is complete and honest about its own state: structured JSON logs shipped
to Better Stack (9c.6, live), Sentry wired as far as it can go without a DSN and failing closed when
there is none (6d.2), a bounded-cardinality `Meter` instrument set (6d.3), a token-gated `/metrics`
that fails closed on an unconfigured deploy, an external `/health/ready` monitor polling every three
minutes, and two out-of-app probes (synthetic play, Neon storage) behind heartbeats. Cardinality
discipline is real — no user id, game id or IP is ever a tag or a metric label — and the drift guards
(`MetricsContractTests`, `BetterStackAlertingDriftTests`, `CiWorkflowDriftTests`) mean a renamed
instrument or a deleted probe fails the build.

The gaps are in *what is watched*, not in the machinery. The alert set covers three signals —
matchmaking queue depth, hub error rate, flag-fall sweep duration — and none of them answers the
question this deployment shape actually raises: a single always-on machine holding game state in
memory with a write-behind persister. The persister's own durability instruments
(`snapshot_failures`, `pending_snapshots`) have no chart and no alert. Nothing notices when `/metrics`
stops producing data, which is the exact failure that shipped and sat unremarked for roughly a month.
A deploy is serving traffic before anything confirms it can serve. And `/health/ready` treats "TCP
connected" as "ready". None of these is a live outage today; each is a place where the next incident
is invisible until a human notices a blank chart.

The privacy and data-transfer side of the observability stack (Better Stack DPA and region, the
Sentry OTLP trace claim) is thoroughly covered by L-3 and L-6 and is not re-opened here. Readiness
**OBS** has not been run (`docs/audits/readiness/` does not exist); this packet stands alone, merge it
when that domain runs.

### Findings

#### I-5-1 — The alert set does not watch the write-behind persister, the one thing that loses games
- Severity: High
- Where: `infra/betterstack/main.tf:127-291` — four charts, three `logtail_dashboard_alert`
  resources (`queue_stuck`, `hub_error_rate_high`, `flagfall_sweep_slow`). The durability instruments
  with no chart and no alert:
  - `kirichess.persistence.snapshot_failures` — `src/KiriChess.Server/Infrastructure/KiriChessMetrics.cs:58`
    ("Non-zero means durability is degraded"), incremented per game at
    `src/KiriChess.Server/Games/LiveGamePersister.cs:182` when even the one-at-a-time retry fails.
  - `kirichess.persistence.pending_snapshots` — `src/KiriChess.Server/Infrastructure/GameMetricsObserver.cs:48`
    ("Sustained growth means Postgres is not keeping up and a crash would lose more than the tail move").
  - `kirichess.persistence.snapshot_batch.duration` — `KiriChessMetrics.cs:64`, the Neon write RTT
    that sets the persister's throughput ceiling (9b.3 / 9c.7).
- Impact: `fly.backend.toml` runs one machine (`min_machines_running = 1`, `auto_start_machines =
  false`, `strategy = "immediate"`) with live clocks and `GameRegistry` in process memory. The only
  durability guarantee is the write-behind persister flushing move snapshots to Neon. A single
  snapshot failure self-heals — the next snapshot writes the whole move list, not a delta
  (`LiveGamePersister.cs:174`) — but a *sustained* failure (Neon unwell, a schema problem, connection
  exhaustion) means every live game's crash-loss window grows without bound, and the machine is
  always-on so a crash is the realistic loss path. The three signals that are watched all answer
  "can players start and finish a game"; none answers "are the games that finished actually saved".
  Also unwatched: `kirichess.game.ownership.takeovers` (`KiriChessMetrics.cs:85`, "instances dying
  unexpectedly") and `kirichess.ratelimit.rejected` (`:70`).
- Fix: add a `logtail_dashboard_chart` + `logtail_dashboard_alert` for the exported name of
  `snapshot_failures` (threshold on a *rate* over a few minutes, not `> 0` once, since a lone
  transient failure is benign; `on_missing_data = "treat_as_zero"`), and a threshold alert on
  `pending_snapshots` sustained above a small ceiling. Put the metric names in `main.tf`'s `locals`
  block the way `metric_games_live` etc. already are, so `MetricsContractTests` proves the exporter
  emits them. `game.ownership.takeovers` can wait until a second instance exists; `ratelimit.rejected`
  is a chart, not necessarily an alert, at this scale.

#### I-5-2 — Nothing notices when `/metrics` stops producing data
- Severity: Medium
- Where: `infra/betterstack/main.tf:219-291` — `queue_stuck` and `flagfall_sweep_slow` are both
  `on_missing_data = "dont_fire"` (the reasoning is spelled out at `:276-281`); `logtail_source.metrics`
  (`:90-112`) has no freshness or heartbeat check. `docs/ops/DEPLOYMENT.md:664-701` and `§12`.
- Impact: `GET /metrics` returned a bare `404` in production from the first Terraform apply
  (2026-08-15) until a backend redeploy that, per `DEPLOYMENT.md §10.1` and `§12`, is still the
  outstanding proof for the three threshold alerts. Through that whole window the scrape source had
  nothing, all four charts drew "no data", and no alert fired — the regression was found by a person
  noticing an empty chart. The same state recurs on a token rotation, a bad deploy, or the
  `MetricsEndpoint` guard regressing. `/health/ready` does not catch it: the app is up, only its
  telemetry is dark, and two of the three app alerts are explicitly configured to stay quiet in that
  state.
- Fix: add one absence/freshness alert on a series that is always present and is a real `0` rather
  than missing on a healthy idle server — `kirichess_games_live` is exactly that. "No data for
  `games_live` in 10 minutes" (a Better Stack absence monitor on the source, or an alert with
  fire-on-missing semantics) turns "the scrape broke" into a page. This is the check that would have
  caught the 404 on day one instead of a month later.

#### I-5-3 — A release is serving traffic before anything confirms it can serve
- Severity: Medium
- Where: `.github/workflows/deploy.yml:60-64` (`production-backend` ends at `flyctl deploy`, no
  post-deploy step); `fly.backend.toml:29` (`strategy = "immediate"`); `fly.backend.toml:71-76`
  (`[[http_service.checks]]` polls `/health`, the liveness probe that "deliberately touches nothing
  else" — `src/KiriChess.Server/Infrastructure/HealthEndpoints.cs:12-16`).
- Impact: `deploy.yml` stops at `flyctl deploy`. With `strategy = "immediate"` and a Fly health check
  that only hits `/health`, a release that boots but cannot reach Neon, or that reintroduces the
  `/metrics` 404, or that fails any readiness condition, takes traffic the instant the machine
  starts. The backstops are the external `/health/ready` monitor (3-minute poll + 60s confirmation,
  `main.tf:74-78`) and the synthetic every 15 minutes — which is not running until the September
  `ops-probes` dispatch. The `/metrics` outage is the worked example of how long that gap can stay
  open.
- Fix: add a smoke step to `deploy.yml` after `production-backend` (and before `production-ui`) that
  curls `https://kirichess-api.fly.dev/health/ready` expecting `200` and `"status":"ready"`, and
  `/metrics` expecting `200` with the scrape token and `401` without — the same three-line check
  `DEPLOYMENT.md:693-699` already documents for a human — and fails the job on a mismatch. Whether
  Fly's own check should move to `/health/ready` is an INF-2 (`fly.*.toml`) call, not this packet's.

#### I-5-4 — `/health/ready` reports ready on a connectable-but-unusable database
- Severity: Low (trigger: a Neon compute that is up with an incomplete schema — an aborted
  `release_command`, a connection string pointed at the wrong branch/database, the manual rollback
  path in `DEPLOYMENT.md §3.1`)
- Where: `src/KiriChess.Server/Infrastructure/HealthEndpoints.cs:26` — `db.Database.CanConnectAsync(ct)`.
- Impact: `CanConnectAsync` opens a connection and returns; it runs no query and never looks at
  `__EFMigrationsHistory`. `release_command` aborting on a failed migration keeps the common case
  narrow, but a rollback or a mispointed `ConnectionStrings__Default` passes this check, so the app
  takes traffic and 500s on the first real query while both the Fly check and the external monitor
  stay green. The synthetic would eventually catch the symptom; readiness would still say "ready".
- Fix: make the readiness probe touch a real table cheaply — `await db.Users.AnyAsync(ct)`, or a raw
  `SELECT 1 FROM "__EFMigrationsHistory" LIMIT 1` — so "ready" means "the schema this build expects is
  present", not just "TCP connected". Keep the existing behaviour that Redis state never changes the
  status code.

#### I-5-5 — The edge-site monitor's `302` allowance has no removal trigger after Access comes off
- Severity: Low (trigger: DH.3 / Todo #12)
- Where: `infra/betterstack/main.tf:349-364` — `expected_status_codes = [200, 302]`,
  `follow_redirects = false`, `remember_cookies = false`, comment "302 is the Cloudflare Access login
  wall, still in front of the apex until DH.3". `tests/KiriChess.Server.Tests/BetterStackAlertingDriftTests.cs:31`
  asserts `expected_status_codes = [200, 302]` is present, which pins the launch-time value in place.
- Impact: After Access is removed, `kirichess.com` should only ever answer `200`. Leaving `302` in
  the accepted set means a redirect loop, an accidental Access re-enable, or a Cloudflare rule that
  302s every visitor reads as healthy and never pages. Nothing prompts the cleanup, and the drift
  test actively locks the current value.
- Fix: add a bullet to the Todo #12 / DH.3 checklist (`ROADMAP.md`, `DEPLOYMENT.md §10.2`): drop
  `302` from `expected_status_codes`, restore `follow_redirects` / `remember_cookies` to defaults,
  and update `BetterStackAlertingDriftTests` to expect `[200]`. Cheapest marker now: a
  `# DH.3: drop 302` comment on the line plus the checklist bullet.

### Already covered

- **Better Stack as a processor — DPA not filed, contracting entity and US data region unrecorded,
  no log retention set** — `docs/audits/L.md` (L-3-1 and table row 6). Shipping account-id-scoped
  logs (`logs.>`, all four apps) to `sNNNN.us-west-2a.betterstackdata.com` is L-3's finding, not
  re-reported here.
- **"Sentry: errors only, no tracing" contradicted by the OTLP trace exporter** — `docs/audits/L.md`
  (L-6-1). Dormant until a `Sentry:Dsn` is configured; the fix is two sentences in PRIVACY/ROPA.
- **Frontend Sentry config verified clean** (`tracesSampleRate: 0`, `sendDefaultPii: false`,
  `ApiError` dropped in `beforeSend`) — `docs/audits/C.md (section FE-1)`, `docs/audits/L.md`.
  Server-side `SendDefaultPii = false` and the deliberate no-double-report in `UnhandledExceptionHandler`
  are correct; `EnableSensitiveDataLogging` is not set anywhere, so shipped logs carry no EF
  parameter values.
- **Workflow `permissions:` / `timeout-minutes` / `concurrency` hygiene** — `docs/audits/C.md (section INF-1)`
  (INF-1-2, INF-1-3). `ops-probes.yml` already carries all three; the gap is in `ci.yml` / `deploy.yml`.
- **Drift guards and their blind spots** — `docs/audits/C.md`, `docs/audits/C.md` cover what
  `MetricsContractTests` / `BetterStackAlertingDriftTests` / `CiWorkflowDriftTests` do and do not
  enforce.
- **Stated positions, not re-filed:** `/metrics` needs one backend redeploy to stop 404ing
  (`DEPLOYMENT.md §10.1`, `§12`); the GitHub `ops-probes` dispatch waits for 1 Sep, so the synthetic
  and Neon heartbeats are silent until then (`ROADMAP.md` 10c.5); no on-call rotation, every alert
  emails the whole team (`DEPLOYMENT.md:659`, ROADMAP 8.7); the metric-alert SQL has never run
  against a live scrape (`infra/betterstack/main.tf:12-16`, `infra/betterstack/README.md`).

### Action list

1. I-5-1: add a persister-health chart + alert to `infra/betterstack/main.tf`
   (`snapshot_failures` rate, `pending_snapshots` sustained growth), with the metric names in the
   `locals` block so `MetricsContractTests` covers them.
2. I-5-2: add a data-freshness / absence alert on `kirichess_games_live` (or a Better
   Stack absence monitor on `logtail_source.metrics`) so a dark `/metrics` pages instead of silently
   blanking three charts.
3. I-5-3: add a post-deploy smoke step to `deploy.yml` asserting `/health/ready` = 200 /
   `"ready"` and `/metrics` = 200-with-token / 401-without; fail the deploy on a mismatch.
4. I-5-4: deepen `/health/ready` to touch a real table (`Users.AnyAsync` or a
   `__EFMigrationsHistory` probe).
5. I-5-5: fold "drop the 302 allowance" into the DH.3 / Todo #12 checklist and
   flip `BetterStackAlertingDriftTests` to `[200]` at the same time.
6. **Owner** — after the next production backend deploy, run `DEPLOYMENT.md §10.1`'s two curls and
   confirm one Better Stack chart shows a real number. That closes the last of 9c.5 and is the
   prerequisite for trusting the alerts added in items 1-2.

No production code changed in this packet. No standing-position reversals.

---

## I-6 — Capacity and load
Date: 2026-08-30
Packet: docs/audits/INDEX.md

### Verdict

The load harness is real, well-argued and already ran a full ladder — but every committed rung
(`loadtest/results/*.json`, dated 2026-08-08) was measured against a build that predates the
connection budget (10a.2, 2026-08-17), the negotiate probe, and `kill_timeout = 30` (10a.3,
2026-08-18). The harness itself was built for a throwaway local compose stack that gets `down -v`
between rungs, and it has **no production story**: run as-built against `kirichess-api` it aborts in
the first ramp batch on three independent production guards (the per-IP connection budget, the
Auth-endpoint rate limit, and — silently — a bounce storm of confirmation emails to `.invalid`
addresses), and its CPU-headroom criterion reads `docker stats` which returns nothing against Fly,
so the run self-reports "Pass" on headroom it never measured. The 9e.8 ladder is authorized
(2026-08-16) and the `/metrics` 401 gate is met, so the block is not permission — it is that nobody
has written down what the owner must change on the deployed side to make the run measure the right
thing without polluting the real database or the real mail reputation. The shutdown-flush number
(10a.3) has no first-class instrument: `LiveGamePersister.StopAsync` flushes but logs no summary
line, so the measurement is a timestamp-diff by eye. This packet supplies the owner runbook and the
DEPLOYMENT.md target line; the load run stays an owner step.

What is *not* bitrotted: the harness's wire surface still matches `HubContract.cs` — it invokes
`JoinQueue` / `LeaveQueue` / `JoinGame` / `SubmitMove` and handles `Ping` / `MatchFound` /
`MovePlayed` / `GameEnded` (plus no-op handlers for `DrawOffered`, `SpectatorsChanged`,
`OpponentDisconnected`, `OpponentReconnected`, `PresenceChanged`), all declared in
`HubContract.cs:32-65`; the Prometheus metric names in `MetricsScraper` match `KiriChessMetrics`;
`POST /api/auth/register` and `/health/ready` and `/metrics` are current. The only name drift is the
dead `sundial` pool default in `run-ladder.ps1` (I-6-2).

Connection-budget and ~250-game-ceiling consistency: the budget arithmetic is internally coherent
(`hard_limit = 1000` → ~250 games; `MaxAnonymousConnections = 600` reserves 400 sockets / 100 games
for signed-in players) and the binding constraint is the persister, not connections or CPU — all
already established in `I.md (I-1)` (AS-4, AS-8, §4) and `I.md`. The one reconciliation this
packet adds: the harness cannot exercise that budget from one source IP, so a production ladder run
measures capacity **with the budget disabled**, which means the budget-vs-capacity interaction that
I-6 is meant to check can only be verified by a separate, deliberate test (see I-6-3).

### Findings

#### I-6-1 — The committed ladder measured a build without the connection budget, the negotiate probe, or the aligned kill_timeout
- Severity: Medium (trigger: quoting `loadtest/results/` or `DEPLOYMENT.md` §4.1's "400 games at 31.6% CPU" as current)
- Where: `loadtest/results/*.json` (git mtime 2026-08-08) vs `src/KiriChess.Server/Infrastructure/HubConnectionBudget.cs` (added 2026-08-17, Phase 10a.2), `fly.backend.toml:19` `kill_timeout = 30` (2026-08-18, Phase 10a.3); `loadtest/README.md:6` "Measured ceilings (local Docker … 2026-08)"
- Impact: the README's ceiling table and `DEPLOYMENT.md` §4.1's headroom figures come from a binary that is now three launch-gating features behind. The persister ceiling (the number that actually matters) is unaffected because none of those three touches the write-behind path, so the numbers are not *wrong* — but they are undated against the build, and the README table still cites `hard_limit = 500` / "~125 games" (line 11) which `fly.backend.toml` moved to `1000` / `800` in 9c.7.
- Fix: add a "measured against build vX, DATE" line to `loadtest/README.md`'s ceilings table and to `DEPLOYMENT.md` §4.1; correct the stale `hard_limit = 500` row to `1000 / soft 800`. Re-running the ladder (9e.8) supersedes all of it with a production number.

#### I-6-2 — `run-ladder.ps1` defaults `-Pool "sundial"`, a pool key deleted on 2026-08-22
- Severity: Medium (trigger: owner runs `run-ladder.ps1` on Windows without `-Pool steal3`)
- Where: `loadtest/run-ladder.ps1:6` `[string]$Pool = "sundial"`; renamed by `src/KiriChess.Server/Data/Migrations/20260822234147_RenamePoolKeysToNeutralIds.cs:35` `("sundial", "steal3")`; current keys in `config/kirichess.rules.json:163` (`steal3` = label "Eclipse"). `run-ladder.sh:21` and the harness default (`LoadOptions.cs:51`) and `README.md:48` are all correctly `steal3`.
- Impact: this is the M-2 / M-6 pool-name drift class, live in a runnable script. On the owner's stated platform (Windows), the PS1 ladder passes `--pool sundial` to every bot; `MatchmakingService.TryJoinAsync` rejects the unknown pool with `ArgumentException` ("'sundial' is not a matchmaking pool.", `MatchmakingService.cs:80-83` via `RatingConstants.IsOfficialPool`), `JoinQueue` fails for all 2N clients, pairing never converges, and the run produces a void rung — wasting a scheduled production load window.
- Fix: change `run-ladder.ps1:6` default to `"steal3"` to match `run-ladder.sh` and the harness. One line.

#### I-6-3 — The harness cannot drive load past ~10 games against the current build: one source IP vs `MaxConnectionsPerIp = 20`
- Severity: High (trigger: running the ladder against `kirichess-api` or any budget-enabled stack)
- Where: `src/KiriChess.Server/Infrastructure/HubConnectionBudget.cs:38` (`MaxConnectionsPerIp` default 20), `:122-126` (refuse), `HubConnectionBudgetProbe` (refuse at negotiate); `Program.cs:436-440,700` (`ForwardedHeaders` with `KnownProxies.Clear()`, so `RemoteIpAddress` is the real caller); harness opens 2 sockets/bot from one machine (`loadtest/KiriChess.LoadTest/BotPlayer.cs:84,121`)
- Impact: all `2N` harness clients share the owner's egress IP. Each bot holds `/hubs/game` + `/hubs/presence` = 2 connections, so the budget refuses at connection 21 — roughly 10 bots, i.e. **5 concurrent games** before `StartAsync` throws and `Program.cs:46-55`'s ramp batch aborts the whole run. The local `docker-compose.load.yml` overlay only overrides `RateLimiting__Auth` and `__Global`; it does **not** touch `Hub__ConnectionBudget`, and the committed runs pre-date the budget, so this has never been exercised. Consequence: a production ladder can only run with `Hub__ConnectionBudget__MaxConnectionsPerIp` raised to well above `4 × topRungGames` (or `Enabled=false`) via `fly secrets` for the window — which means the run measures capacity **with the budget off**, and the budget-vs-ceiling question I-6 exists to answer needs its own check.
- Fix (run): raise `Hub__ConnectionBudget__MaxConnectionsPerIp` for the run window (runbook below), restore after. Fix (harness, optional): add a `--budget-note` or document in `README.md` that a production run requires this secret change and why. Fix (the actual I-6 check): verify the budget separately with a tiny scripted test — N bots from N distinct IPs is not feasible for a hobbyist, so instead assert by reading: 20/IP × realistic distinct-IP count vs `hard_limit = 1000` is already the derivation in `HubConnectionBudget.cs:15-30` and `DEPLOYMENT.md` §4.1 "Who gets the budget" — record that the budget is verified by construction, not by load, and move on.

#### I-6-4 — A production ladder run registers 2N accounts into the indefinite-retention database with no teardown
- Severity: High (trigger: running the ladder against `kirichess-api`)
- Where: `loadtest/KiriChess.LoadTest/BotPlayer.cs:48,56-78` (`loadbot{n}@load.invalid`, real `POST /api/auth/register`); `run-ladder.ps1`/`.sh` teardown is `docker compose … down -v` (compose-only, no production equivalent); standing position "Games and ratings kept — indefinite retention" (`INDEX.md`, `RETENTION.md`)
- Impact: a 5-rung ladder to N=400 registers up to ~2,400 `loadbot*` users plus every game they play, permanently, in the production Postgres that the project's own retention policy says is never pruned. These rows carry email-shaped PII (`@load.invalid`), land in `DailyCounter` signup/'`games.started`' analytics, and pollute leaderboards for the `steal3` pool. There is no `down -v` against Neon.
- Fix: decide the target first (I-6-6). If production: pre-agree a cleanup SQL run (`DELETE` cascade on users where `Email LIKE '%@load.invalid'`) immediately after the ladder, executed by the owner, and note it in `DEPLOYMENT.md` §5 / the run log; or accept the rows and document why. If staging: its Neon branch is disposable, this finding disappears — which is the strongest argument for I-6-6.

#### I-6-5 — A production run fires 2N confirmation emails to reserved-TLD addresses through Resend
- Severity: High (trigger: running the ladder against `kirichess-api` with `Email__*` configured)
- Where: `src/KiriChess.Server/Auth/AuthEndpoints.cs:132-133` (`register` always calls `email.SendAsync(Confirmation(...))`); harness emails are `loadbot{n}@load.invalid` (`BotPlayer.cs:48`) — `.invalid` is RFC 2606 reserved and hard-bounces; local overlay sets no `Email__*` so this is inert locally only
- Impact: ~2,400 hard bounces in minutes against the Resend account real strangers depend on for confirmation and password-reset mail. Resend's free tier is ~100/day, ~3,000/month (see `I.md`), and a bounce spike is exactly what gets a transactional sender rate-limited or suspended. This is the load run degrading a shared production dependency for real users.
- Fix: for the run window, disable outbound mail on `kirichess-api` (unset the Resend key or set the app's email-disable flag — see runbook), or target staging (I-6-6). Harness-side: skip registration email by using a domain the server treats as non-sending is not currently possible, so this stays an ops step.

#### I-6-6 — Neither the harness nor the docs resolve production vs staging as the 9e.8 target
- Severity: Medium (trigger: scheduling the run)
- Where: `ROADMAP.md` 9e.8 ("sustained load against your single paid production instance") vs `DEPLOYMENT.md` §4.1 point 3 ("re-runs against a compose stack") vs 10a.3 ("during #10's top rung: watch graceful shutdown"); staging twin exists (`fly.backend.staging.toml`, `kirichess-api-staging`, its own Neon branch)
- Impact: I-6-3, I-6-4 and I-6-5 all vanish or shrink against staging (disposable Neon branch, no real mail reputation, budget secret can be set freely), and staging runs the same image on the same `shared-cpu-1x` class in the same `ams` region against Frankfurt Neon — the 5–15 ms RTT band that `DEPLOYMENT.md` §4.1 and the 10a.3 budget both depend on is preserved. The one thing only production gives is the exact production Neon compute tier's write RTT. Recommendation: run the full ladder against **staging** for the capacity curve and the shutdown-flush number, then optionally one single mid-rung confirmation run against production during a quiet window with mail disabled and a cleanup SQL ready.
- Fix: owner picks; this packet's runbook is written for staging with a production-delta note.

#### I-6-7 — `backend-cpu-headroom` criterion silently passes on missing data against any non-Docker target
- Severity: Medium (trigger: running against Fly — production or staging)
- Where: `loadtest/KiriChess.LoadTest/Program.cs:113-123` (`ContainerStats.SampleOnceAsync` shells out to `docker stats`), `:180-181` (`backendStats` defaults to all-zero when no samples), `RunResult.cs:172-176` (`Headroom(0,0)` → `0 < 80` → `Pass`); already visible in `loadtest/results/A-innet-n800.json` (`"mean=0% max=0% … Pass"`)
- Impact: there is no Docker daemon reachable from a run targeting `api.kirichess.com`, so backend CPU/mem is never sampled and the criterion returns `Pass` on zero evidence. The `Overall` verdict then reads `Pass` while the single most important resource question — does the machine have headroom at rung N — went unmeasured. `--pinned-cpus 1.0` / `--load-memory 512m` are also meaningless against a shared vCPU.
- Fix: for a Fly-targeted run, treat the harness verdict as covering only latency / persister-backlog / correctness / generator-honesty, and read CPU, memory and machine load from Fly's own metrics (`fly_instance_cpu`, `fly_instance_memory`) or Better Stack / Grafana **during** the window, plus the `kirichess_*` gauges from `/metrics`. Document this in the runbook (done below) and in `README.md`. Optionally make `Headroom` return `Void`, not `Pass`, when it has zero samples.

#### I-6-8 — The shutdown flush (10a.3) has no summary instrument; the number is a timestamp-diff by eye
- Severity: Medium (trigger: 10a.3 measurement, which rides the 9e.8 window)
- Where: `src/KiriChess.Server/Games/LiveGamePersister.cs:98-117` (`StopAsync` flushes in batches, logs nothing on success — only `LogWarning`/`LogError` on failure at `:159,181`); `DEPLOYMENT.md` §4.1 "The shutdown budget" says "watch the shutdown at the top rung and write the number here"
- Impact: "watch graceful shutdown" has nothing to watch. During a deploy/restart the owner must diff the SIGTERM / "Application is shutting down" log timestamp against the last `SnapshotBatchWritten` metric emission or the process-exit line to estimate the flush duration — imprecise, and impossible to do confidently while also watching everything else at the top rung.
- Fix (one line, not applied in this packet): wrap the `StopAsync` loop in a `Stopwatch` and emit `_logger.LogInformation("Shutdown flush complete: {Sessions} sessions, {Batches} batches, {ElapsedMs} ms", …)` after `:116`. Then 10a.3's number is `grep "Shutdown flush complete"` on the deploy logs. Until then, the runbook below gives the manual procedure.

#### I-6-9 — `--scrape-token` default is the local placeholder; a production run needs the real secret
- Severity: Low (operational note)
- Where: `loadtest/README.md:31,50` and `run-ladder.*` default `load-only-not-a-secret-scrape-token`; production `Metrics__ScrapeToken` is a Fly secret on `kirichess-api` (`DEPLOYMENT.md:129`, §9); `/metrics` returns 401 without it (confirmed met, `ROADMAP.md` 9e.8 note 2026-08-17)
- Impact: with the placeholder token every `MetricsScraper.ScrapeAsync` call 401s, so `kirichess_games_live` reads 0, pairing "never converges", and the run bails or produces an all-zero rung.
- Fix: pass `--scrape-token "$(fly ssh console -C 'printenv Metrics__ScrapeToken' --app kirichess-api)"` (or read it from the Fly dashboard / `fly secrets` records) — see runbook.

### Already covered

- **Lobby opens 2–3 `/hubs/game` sockets per visitor, eroding the ceiling from the client side** — `I.md (I-1)` AS-4, `I.md` I-1-1. Not re-reported; I-6-3 reconciles the budget-vs-harness angle only.
- **`LiveGamePersister` write-behind saturation is the true ceiling (Neon write RTT, not CPU); ~250-game decision** — `loadtest/README.md`, `DEPLOYMENT.md` §4.1, `I.md (I-1)` §4.3, `I.md`.
- **A deploy / restart interrupts in-progress games (single machine, `strategy = "immediate"`, roll-forward-only)** — `DEPLOYMENT.md` §12, `docs/audits/C.md (section INF-1)`, `I.md (I-1)` §4.3.
- **Neon plan / autosuspend defeated by the always-on pool; compute-hours untracked** — `I.md` I-9-2. A load run pins Neon compute at its ceiling for the ladder duration (~30–90 min across rungs); that cost rides I-9-2, not this packet.
- **Hot in-memory scans / LINQ over all live sessions at high N; SignalR JSON-only; `AddDbContext` not pooled** — `I.md (I-1)` AS-8/AS-7/AS-2, `I.md` I-1-6/I-1-7.
- **`/api/v1/account/playing` caps the live scan at 200, wrong inside the launch ceiling** — `I.md (I-1)` AS-9, `I.md` I-1-5.
- **Redis unprovisioned; single Fly machine; Cloudflare Access on until launch** — standing positions, `DEPLOYMENT.md`.

### Action list

Ordered. No standing-position reversals (Redis stays unprovisioned; ~250-game ceiling is a decision; indefinite retention stays).

1. Fix `run-ladder.ps1:6` default `-Pool` to `"steal3"` (I-6-2). One line, no behaviour change to the passing `.sh` path.
2. In `loadtest/README.md`, date the ceilings table against its build, correct the stale `hard_limit = 500` row, and add a "running against Fly" section noting (a) the connection-budget secret change required (I-6-3), (b) mail must be disabled or staging used (I-6-5), (c) `docker stats` / headroom is inert and CPU comes from Fly metrics (I-6-7), (d) the real scrape token (I-6-9).
3. Add the `StopAsync` flush summary log line in `LiveGamePersister.cs` (I-6-8), flagged and not applied here, so 10a.3's number is a grep. Owner confirms the one-line prod change.
4. **Owner — decide the target: staging (recommended) vs production** (I-6-6). Everything below assumes staging; the production delta is noted inline.
5. **Owner — run the ladder** per the runbook below. This is the owner step of 9e.8.
6. **Owner — write the numbers back**: capacity curve and the top passing rung into `DEPLOYMENT.md` §4.1 (the "Why 250 and not 400" paragraph and "What to re-check" list); the shutdown-flush milliseconds into `DEPLOYMENT.md` §4.1 "The shutdown budget" block, replacing the "Unmeasured, and it should not stay that way" blockquote. Write the numbers in from the pasted run output.
7. **Owner — if production was used: run the agreed `loadbot*@load.invalid` cleanup SQL** (I-6-4) and record it in the run log.

---

### Owner runbook — the 9e.8 / 10a.3 load run

**This part of packet I-6 is the owner's. Do not start sustained load against a
live server.** Everything above this line is done.

#### 0. Target decision

Run against **staging** (`kirichess-api-staging`) unless you specifically need the production Neon
compute tier's write RTT. Staging is the same image, same `shared-cpu-1x` class, same `ams` region,
same Frankfurt Neon band — and its DB branch is disposable, its mail path has no reputation to
burn, and you can set the budget secret freely. If you run production, do it in a quiet window and
do steps 1c and 7.

#### 1. Prep the deployed side (per rung target N of your top rung; example assumes top rung N=400)

```bash
APP=kirichess-api-staging          # or kirichess-api for the production confirmation run
API=https://kirichess-api-staging.fly.dev   # staging has no custom domain; production is https://api.kirichess.com
# confirm the staging hostname: fly info --app kirichess-api-staging

# a. Lift the per-IP connection budget for the window — the harness drives from one IP (I-6-3).
#    Set it above 4 * topRungGames with margin. down = restore after.
fly secrets set --app $APP Hub__ConnectionBudget__MaxConnectionsPerIp=8000

# b. Lift the Auth + Global HTTP rate limits — 2N registrations from one IP (I-6-5 sibling).
fly secrets set --app $APP \
  RateLimiting__Auth__PermitsPerMinute=100000 \
  RateLimiting__Global__PermitsPerMinute=1000000

# c. PRODUCTION RUN ONLY: stop outbound confirmation mail (I-6-5). On staging, confirm no Resend
#    key is set (fly secrets list --app kirichess-api-staging | grep -i resend) and skip this.
#    Unset the sender key so TurnstileVerifier-style "empty config = off" applies:
fly secrets unset --app kirichess-api Resend__ApiKey     # note the exact key name from `fly secrets list`

# Each `fly secrets set` restarts the machine. Wait for health before the run:
curl -fsS $API/health/ready && echo OK

# d. Get the metrics scrape token (I-6-9):
TOKEN=$(fly ssh console -C 'printenv Metrics__ScrapeToken' --app $APP | tr -d '\r\n')
curl -s -o /dev/null -w '%{http_code}\n' -H "Authorization: Bearer $TOKEN" $API/metrics   # expect 200
```

#### 2. Run the ladder by hand, one rung at a time

The `run-ladder.*` scripts are compose-only (they `docker compose up` a local stack) and **cannot**
target Fly. Run the harness directly, once per rung, from the repo root:

```bash
for N in 25 50 100 200 400; do
  dotnet run -c Release --project loadtest/KiriChess.LoadTest -- \
    --games $N --pool steal3 --api $API \
    --scrape-token "$TOKEN" \
    --think-ms 3000 --jitter-ms 1000 \
    --warmup-seconds 90 --window-seconds 180 \
    --presence true --seed 20260808 \
    --label "prod-ladder-n$N" --out loadtest/results/prod-ladder-n$N.json
  echo "rung $N done — review before continuing"; sleep 30
done
```

Notes:
- `--pinned-cpus` / `--load-memory` are cosmetic here (I-6-7) — leave them default.
- The harness's `backend-cpu-headroom` criterion will say `Pass` on zero data. **Ignore it.** The
  real headroom reading is step 3.
- If a rung reports `PairingConverged: false` with `observed live` far below N, you hit a budget or
  rate limit — recheck step 1, do not just proceed.
- Stop climbing when latency p95/p99 degrades, `kirichess_persistence_pending_snapshots` shows a
  sustained upward trend (not just a spike), or `hub.errors` / `snapshot_failures` go non-zero.

#### 3. What to watch during each window (the parts the harness can't see)

- **Fly machine**: `fly metrics` / the Fly dashboard, or Better Stack / Grafana — `fly_instance_cpu`,
  `fly_instance_memory` for `$APP`. This is the headroom number.
- **`/metrics`** (scrape every ~10s): `kirichess_games_live` (should hold near N),
  `kirichess_persistence_pending_snapshots` (the durability-debt early warning —
  `DEPLOYMENT.md` §4.1 point 1), `kirichess_move_apply_duration_milliseconds` p95/p99,
  `kirichess_flagfall_sweep_duration_milliseconds`, `kirichess_hub_errors_total`,
  `kirichess_ratelimit_rejected_total{surface="hub-connect"}` (budget biting).
- **Neon**: the Neon dashboard's compute / connections graph — confirms the pool behaviour I-9-2
  describes and gives the write-RTT context for §4.1.

#### 4. The shutdown-flush measurement (10a.3) — do this once, at the top passing rung

While the top rung's measurement window is **still open** (games live, N near target):

1. Note the wall-clock time and tail the logs: `fly logs --app $APP`.
2. Trigger a graceful shutdown: `fly apps restart $APP` (or `fly deploy` with a trivial change).
   This sends SIGTERM, and `kill_timeout = 30` (`fly.backend.toml:19`) is the budget under test.
3. In the logs, mark the interval from the shutdown signal / "Application is shutting down" line to
   the last `SnapshotBatch` metric activity / the process-exit line. **If action item 3 (the
   `StopAsync` summary log line) has landed, this is just `grep "Shutdown flush complete"` — read
   the `ElapsedMs`.**
4. Confirm no move loss: after the machine is back, spot-check that games that were live are intact
   (move lists continue, no reset to an earlier ply).
5. Record the milliseconds. Expected order of magnitude (per `DEPLOYMENT.md` §4.1): ~60 ms of pure
   write time for 250 games at a 15 ms Neon RTT, plus whatever cold-compute latency Neon adds — the
   headroom in the 30 s budget is exactly for that unknown.

#### 5. Restore the deployed side

```bash
fly secrets unset --app $APP \
  Hub__ConnectionBudget__MaxConnectionsPerIp \
  RateLimiting__Auth__PermitsPerMinute \
  RateLimiting__Global__PermitsPerMinute
# PRODUCTION RUN ONLY:
fly secrets set --app kirichess-api Resend__ApiKey="<the value you unset>"
curl -fsS $API/health/ready && echo restored
```

#### 6. Write the numbers down

Paste the per-rung harness summaries (or the `loadtest/results/prod-ladder-n*.json` files) and the
Fly CPU/memory readings and the shutdown-flush milliseconds. Write them into
**`docs/ops/DEPLOYMENT.md` §4.1**, specifically:

- the capacity curve and top passing rung → the **"Why 250 and not 400"** paragraph and the
  **"What to re-check before changing this number"** list (replace "The exact RTT is still
  unmeasured and rides 9e.8" with the measured RTT / rung data);
- the shutdown-flush milliseconds → the **"The shutdown budget (10a.3)"** block, replacing the
  `> **Unmeasured, and it should not stay that way.**` blockquote with the measured figure and the
  date.

#### 7. Production run only — clean up

Run the pre-agreed cleanup (owner executes; example shape, confirm cascade behaviour against the
schema first):

```sql
DELETE FROM "AspNetUsers" WHERE "Email" LIKE '%@load.invalid';
-- verify Games / Ratings / RatingHistory / DailyCounter fallout and clean per docs/ops/MIGRATIONS.md
```

Record the row counts removed in the run log.

---

## I-7 — Secrets, DNS, TLS, mail auth
Date: 2026-08-31
Packet: docs/audits/INDEX.md

### Verdict

The accounts are real, the keys are deployed, and the DNS is genuinely correct: the DH.5 runbook's
claim that this item is a quarter of what it reads as holds up under a live check. What is wrong is
not the configuration but the **record of it**: the three documents that describe this stack disagree
with each other about the one setting that decides whether a stranger can create accounts without a
browser, and `.env.example`, which `DEPLOYMENT.md:32` calls "the full variable list", is two years of
features behind the code it claims to describe. That matters more than it would elsewhere, because
every integration here fails closed on an empty key, so *an unset secret and an unbuilt feature look
identical from the outside*, and the only thing standing between the two is a document. The
mail-auth half has one real defect underneath a correct-looking surface: SPF, DKIM and DMARC all
resolve and all pass today, but the DMARC records ask for **strict** SPF alignment against an
envelope domain that can never align, so the domain is riding on DKIM alone and the planned tightening
from `p=none` is the step that will expose it. The TLS posture is sound and asymmetric: the token-bearing
API host pins for a thirtieth of the time the site host does, and everything Cloudflare answers without
touching an origin (the Access wall, the `www` redirect) carries no HSTS at all. One thing checked
and clean, because the packet asks and getting it wrong is the classic failure: the Google redirect URI
is on the API host, in the code as well as the runbook.

### Findings

#### I-7-1 — Three ops documents disagree about whether `Turnstile__SecretKey` is set, and it is the only server-side bot gate on a register endpoint that is public *today*
- Severity: High (Critical if `fly secrets list` shows the key absent)
- Where: `docs/ops/PROVIDER-ACCOUNTS.md:21` says `Turnstile__SecretKey` is **Deployed**;
  `docs/ops/DEPLOYMENT.md:625` says "the secret key has never been set anywhere",
  `docs/ops/DEPLOYMENT.md:864-866` (§12 Known gaps) repeats it, and `docs/ROADMAP.md:1731-1733`
  lists setting it as still-open work. Gate itself:
  `src/KiriChess.Server/Auth/TurnstileVerifier.cs:62-63`, called at
  `src/KiriChess.Server/Auth/AuthEndpoints.cs:61`.
- Impact: `IsValidAsync` returns `true` unconditionally when the key is empty, so the deployed
  enforcement is client-side only and a direct POST creates accounts with no bot check. This is not
  a launch-day risk waiting on Todo #12: `api.kirichess.com` was pulled out of Access long ago
  (`docs/decisions/AUTH.md:150`), and `GET https://api.kirichess.com/api/auth/register` answers
  **405** today, so the route is already reachable by strangers. The blast radius is not just junk
  rows: every accepted registration sends a confirmation mail from `mail.kirichess.com`, addressed
  to whatever address was posted, on a domain whose bounces and complaints nobody consumes
  (`docs/ops/DEPLOYMENT.md:595-597`). A signup flood is therefore also a sending-reputation
  incident, and the free Resend tier is the ceiling it runs into.
- Fix: run `fly secrets list --app kirichess-api` before anything else on launch day; if the key is
  absent, set it per §9.1. Either way, correct whichever of the three documents is stale in the same
  sitting: a gate whose state has to be re-derived from a `fly` command is not a gate anyone can rely on.

#### I-7-2 — DMARC asks for strict SPF alignment that the sending setup can never satisfy, so the domain rides on DKIM alone
- Severity: Medium (becomes High at the moment `p=` is tightened, which §8.2 plans)
- Where: resolved live against 1.1.1.1 on 2026-08-31:
  `_dmarc.mail.kirichess.com` and `_dmarc.kirichess.com` both return
  `v=DMARC1; p=none; rua=mailto:dmarc@kirichess.com; pct=100; adkim=s; aspf=s`; SPF is published one
  label down at `send.mail.kirichess.com` (`v=spf1 include:amazonses.com ~all`) and
  `mail.kirichess.com` itself has no TXT record. The record text is prescribed at
  `docs/ops/DEPLOYMENT.md:566` and confirmed at `docs/ops/PROVIDER-ACCOUNTS.md:98`; the From address
  is `noreply@mail.<domain>` per `docs/ops/DEPLOYMENT.md:578`.
- Impact: `aspf=s` requires the SPF-authenticated envelope domain to equal the header-From domain
  **exactly**. Resend's envelope is on `send.mail.kirichess.com`, the From is on
  `mail.kirichess.com`, so SPF alignment never passes and DMARC has exactly one leg: DKIM (which does
  align exactly, so `adkim=s` is fine). Invisible at `p=none`. The moment §8.2's planned move to
  `p=quarantine` and then `p=reject` happens, any hop that alters the message enough to break the
  DKIM signature (a forwarder, a mailing list, some corporate gateways) has nothing to fall back
  on, and the confirmation mail is quarantined with no bounce the application can see. That is the
  failure §8.2 warns about in its own last sentence, arriving by a route the sentence does not cover.
- Fix: change `aspf=s` to `aspf=r` in both `_dmarc` TXT records **before** raising `p=`. Relaxed SPF
  alignment matches on the organizational domain, which `send.mail.kirichess.com` and
  `mail.kirichess.com` share, so SPF becomes a real second leg. Leave `adkim=s` alone.

#### I-7-3 — `.env.example` omits every security-gating key added since 8a.1, and still declares one that was deleted
- Severity: Medium
- Where: `.env.example` (whole file), which `docs/ops/DEPLOYMENT.md:32` calls "the full variable
  list". Missing against code that reads them: `Turnstile__SecretKey`
  (`src/KiriChess.Server/Auth/TurnstileVerifier.cs:19`), `Authentication__Google__ClientId` /
  `ClientSecret` (`src/KiriChess.Server/Auth/ExternalAuthOptions.cs:39-40`),
  `Auth__BreachedPasswordCheckEnabled` (`src/KiriChess.Server/Auth/BreachedPasswordChecker.cs:22`),
  `Auth__RequireConfirmedEmail` (`src/KiriChess.Server/Auth/AuthEndpoints.cs:166`),
  `Hub__ConnectionBudget__*` (`src/KiriChess.Server/Infrastructure/HubConnectionBudget.cs:37-39`),
  `Retention__*` (set as Fly secrets at `docs/ops/DEPLOYMENT.md:336-340`), and the two frontend
  Turnstile build args (`Dockerfile.frontend:29-34`). Stale in the other direction:
  `.env.example:25` still declares `Analytics__AccessToken`, which AD.6+11a.3 deleted outright
  (`docs/decisions/AUTH.md:186-190`: the whole `/api/analytics` surface is behind
  `ModerationPolicies.Admin` now), and `docs/ops/DEPLOYMENT.md:129` still instructs setting it.
- Impact: both directions cost something. A fork or a first-time contributor building from this file
  (**O-5**, **O-7**) gets a stack whose register endpoint has no server-side bot gate and whose
  Google button is inert, with nothing in the file hinting either knob exists, and because every
  one of these fails closed silently, nothing tells them. The stale line is worse in kind: it invites
  an operator to set a Fly secret that gates nothing while believing the analytics surface is behind
  it.
- Fix: add the seven missing keys with the same "empty = off" comments the file already uses for
  `Sentry__Dsn` and `Email__ApiKey`; delete the `Analytics__AccessToken` line and the sentence at
  `docs/ops/DEPLOYMENT.md:129` that names it.

#### I-7-4 — HSTS is 30 days on the token-bearing host, and absent entirely from everything Cloudflare answers itself
- Severity: Medium
- Where: `src/KiriChess.Server/Infrastructure/SecurityHeaders.cs:21` calls `app.UseHsts()` with no
  `AddHsts(...)` anywhere in the tree, so ASP.NET's 30-day default applies; `ui/next.config.ts:145-148`
  ships `max-age=63072000; includeSubDomains`. Measured 2026-08-31:
  `https://api.kirichess.com/health` → `strict-transport-security: max-age=2592000` (no
  `includeSubDomains`); `https://kirichess.com/` → 302 to the Access login with **no** HSTS header at
  all; `https://www.kirichess.com/` → 301 from the edge redirect rule, likewise none.
- Impact: `api.kirichess.com` is the host every bearer token is sent to
  (`docs/decisions/AUTH.md` §1), and it pins for a thirtieth as long as the site host and does not
  cover subdomains. More practically: any response Cloudflare serves without reaching an origin
  carries no HSTS, which today is *every* apex response, so the site name is unpinned for a
  first-time visitor until Access comes off and Next starts answering. The apex's own
  `includeSubDomains` would cover `api` once it ships, but only for a browser that visited the apex
  first, which a bot or API client never does.
- Fix: `builder.Services.AddHsts(o => { o.MaxAge = TimeSpan.FromDays(730); o.IncludeSubDomains = true; });`
  so the two hosts agree, and switch on Cloudflare's edge HSTS (SSL/TLS → Edge Certificates) so
  edge-served responses carry it too. Do **not** add `preload`: it is close to irreversible and buys
  a one-person host nothing the above does not.

#### I-7-5 — "`RemoteIpAddress` is the real caller" is asserted in four places and measured in none, on a two-proxy chain
- Severity: Medium (trigger: the first time a per-IP limit has to do real work, i.e. launch day)
- Where: `src/KiriChess.Server/Program.cs:436-441` sets
  `ForwardedHeaders.XForwardedFor | XForwardedProto` with `KnownIPNetworks.Clear()` and
  `KnownProxies.Clear()`, leaving `ForwardLimit` at its default of 1. The conclusion is then relied
  on at `src/KiriChess.Server/Infrastructure/RateLimiting.cs:436-439`,
  `src/KiriChess.Server/Infrastructure/HubConnectionBudget.cs:59-61`, and repeated as established
  fact in `docs/audits/I.md:56`.
- Impact: production is Cloudflare → Fly → app, so `X-Forwarded-For` arrives with more than one
  entry while `ForwardLimit = 1` consumes only the rightmost. If that rightmost entry is an edge
  address rather than the client's, every per-IP control silently collapses onto a handful of
  addresses: `RateLimitPolicies.Auth` (30/min, and the only limiter in front of `/register`,
  `/forgot-password` and `/resend-confirmation`, all three of which send mail) and
  `HubConnectionBudget.MaxConnectionsPerIp` (20, meant as "ten tabs or a household") become
  effectively global caps. Both failure directions are silent and opposite: too strict locks real
  players out of a lobby, too loose deletes the limit. Nothing in the repo records anyone having
  looked.
- Fix: one observation, not a change: make a single request through `https://api.kirichess.com` and
  read `HttpContext.Connection.RemoteIpAddress` off it (`fly logs`, or temporarily in a `/health`
  field). If it is not the caller's own address, set `ForwardLimit` to the real hop count or read
  Cloudflare's `CF-Connecting-IP`, and write the answer into `DEPLOYMENT.md` § 4.1 beside the
  capacity numbers so the next reader inherits a measurement instead of an assertion.

#### I-7-6 — The Turnstile siteverify Worker still holds a second copy of the secret key, is still allowed by CSP, and is called by nothing
- Severity: Low (trigger: rotating the Turnstile secret, or auditing the Cloudflare account)
- Where: `ui/app/[locale]/login/page.tsx:148-151` records that the token is single-use and that "the
  old Worker pre-check consumed" it: the client now sends the token straight to the API, which is
  the sole siteverify (`src/KiriChess.Server/Auth/TurnstileVerifier.cs:26-29`). Yet
  `fly.ui.toml:26` and `fly.ui.staging.toml:19` still bake `NEXT_PUBLIC_TURNSTILE_WORKER_URL`,
  `Dockerfile.frontend:33-34` still declares it, and `ui/next.config.ts:50-58` still adds its origin
  to `connect-src`.
- Impact: a publicly reachable Cloudflare Worker holding a copy of the widget's secret key
  (`fly.ui.toml:24` describes it as living there), with no caller, no source in the tree, and a
  standing CSP permission from the app that stopped using it. One secret in two places means
  rotation has two destinations and only one of them is written down anywhere.
- Fix: delete the Worker and its secret from Cloudflare, drop the build arg from both `fly.ui*.toml`
  and `Dockerfile.frontend`, and remove `turnstileWorkerOrigin()` from the CSP. Keep
  `NEXT_PUBLIC_TURNSTILE_SITE_KEY`, which is the half that is genuinely public and still used.

#### I-7-7 — `AllowedHosts` is `*`, so a spoofed Host header has to fail closed downstream rather than at the door
- Severity: Low (trigger: any future code that builds an absolute URL from the request)
- Where: `src/KiriChess.Server/appsettings.json:8`.
- Impact: bounded today, and worth stating why rather than filing it as alarming. No application code
  builds an absolute URL from the request (`Request.Host`, `Request.Scheme` and `GetDisplayUrl` do
  not appear anywhere in `src/KiriChess.Server`), and every mail link comes from `Frontend:BaseUrl`
  (`src/KiriChess.Server/Infrastructure/Email/EmailOptions.cs:45`), which is exactly the
  host-header-poisoning vector this setting usually matters for, and it is already closed. The one
  place the request host is still load-bearing is the Google handler's derived `redirect_uri`, and
  Google's own allowlist rejects anything that is not `https://api.kirichess.com/signin-google`, so
  that path fails closed rather than redirecting somewhere else. What is missing is only the outer
  guard.
- Fix: set `AllowedHosts` to the hosts each environment actually serves
  (`api.kirichess.com;kirichess-api.fly.dev;localhost`, staging its own) so a spoofed Host is a 400
  at the framework instead of something three downstream mechanisms have to independently survive.

### Checked and clean

Recorded because the packet asks and a silent pass reads the same as an unchecked box:

- **The Google redirect URI is on the API host, in the code as well as the runbook.**
  `src/KiriChess.Server/Program.cs:341` sets `CallbackPath = "/signin-google"` on `kirichess-api`,
  and the handler derives the origin from the request it received, which is the API host, since the
  start endpoint lives there. That matches `docs/ops/PROVIDER-ACCOUNTS.md:64`'s
  `https://api.kirichess.com/signin-google`. `Frontend:BaseUrl` is where the user is returned to
  afterwards (`src/KiriChess.Server/Auth/ExternalAuthOptions.cs:41`), which is the correct split.
  The ordering trap (consent-screen URLs are fetched by Google, and `/privacy` and `/terms` are
  behind Access until Todo #12) is already documented at `docs/ops/PROVIDER-ACCOUNTS.md:48-52`.
- **SPF, DKIM and DMARC all resolve as documented.** Verified 2026-08-31 against 1.1.1.1:
  `send.mail.kirichess.com` TXT (SPF), `resend._domainkey.mail.kirichess.com` TXT (DKIM, 1024-bit
  RSA), `_dmarc.mail.kirichess.com` and `_dmarc.kirichess.com` TXT. The apex carries
  `v=spf1 include:_spf.mx.cloudflare.net ~all` for Email Routing and sends no application mail.
  The only defect is the alignment mode in I-7-2.
- **Nothing in the application depends on Cloudflare Access staying.** There is no Access-derived
  authentication anywhere in the tree; `api.kirichess.com` came out of the gate already
  (`docs/decisions/AUTH.md:150`) and answers publicly today. The apex still 302s to
  `cloudflareaccess.com`, so the launch-day flip is genuinely still pending and genuinely still the
  only constraint that changes.
- **TLS itself is correct on both hosts**: Cloudflare edge in front of Fly-terminated TLS,
  `force_https = true` in `fly.backend.toml:40` and `fly.ui.toml:34`, and `www` 301s to the apex
  with path and query intact. The API's own CSP (`default-src 'none'`) is right for a JSON+hub origin.

### Already covered

- **The edge-site monitor's `302` allowance has no removal trigger once Access comes off**:
  `docs/audits/I.md` (I-5-5), re-listed at `docs/audits/B.md:142`. Belongs on the Todo #12
  checklist.
- **The Turnstile Worker has no source or config in the repo**: `docs/audits/I.md:78-94`
  (I-10-3). I-7-6 above is the different half: it is also now *unused*, and still holds a live secret.
- **`FLY_API_TOKEN` is one repo-scoped secret deploying all four apps, with no environment
  protection**: `docs/audits/I.md` (I-3-3). CI secret handling generally: I-3.
- **No full-history secret scan; the snapshot is what gets scanned**: `docs/audits/O.md` (O-4-3).
- **Turnstile's script loads on every route rather than the one that needs it**:
  `docs/audits/C.md (C-1 improvement plan)`, noted again at `docs/audits/S.md:278`.
- **CSP `'unsafe-inline'` and localStorage token storage**: `docs/decisions/AUTH.md` §1.6/§1.7 and
  `docs/audits/S.md`. Not re-argued here; no §1.7 trigger has fired that this packet can see.
- **Offsite backup never dispatched; `/metrics` dark; ops-probes waiting on the post-1-Sep quota
  reset**: `docs/audits/I.md`, `docs/audits/I.md`, `docs/audits/I.md`.

### Action list

**Owner, before removing Cloudflare Access (Todo #12):**

1. `fly secrets list --app kirichess-api`. Confirm `Turnstile__SecretKey` is present. If it is not,
   set it per `DEPLOYMENT.md` § 9.1 before the gate comes off, not after. (I-7-1)
2. Edit both `_dmarc` TXT records in Cloudflare DNS: `aspf=s` → `aspf=r`. One character each, and it
   has to happen before `p=` is ever raised. (I-7-2)
3. Cloudflare → SSL/TLS → Edge Certificates → enable HSTS, so the Access wall, the `www` redirect and
   any Cloudflare error page carry it. Leave preload off. (I-7-4)
4. After the gate is off: create the Google OAuth client with the fields already tabulated at
   `PROVIDER-ACCOUNTS.md:58-68`, redirect URI `https://api.kirichess.com/signin-google`. Unchanged
   by this audit: confirmed correct, listed so it stays sequenced after step 3's context.
5. Make one request to `https://api.kirichess.com` and read the `RemoteIpAddress` the app saw. (I-7-5)
6. Delete the `turnstile-siteverify-kirichess` Worker and its secret once the change in step 9
   has shipped. (I-7-6)


7. Fix the Turnstile documentation contradiction: whichever of `PROVIDER-ACCOUNTS.md:21`,
   `DEPLOYMENT.md:625`, `DEPLOYMENT.md:864-866` and `ROADMAP.md:1731-1733` is stale, in one commit,
   after step 1 settles the fact. (I-7-1)
8. Bring `.env.example` back in step with the code: add the seven missing keys, delete
   `Analytics__AccessToken` and the `DEPLOYMENT.md:129` sentence that instructs setting it. (I-7-3)
9. Remove `NEXT_PUBLIC_TURNSTILE_WORKER_URL` from `fly.ui.toml`, `fly.ui.staging.toml` and
   `Dockerfile.frontend`, and drop `turnstileWorkerOrigin()` from `ui/next.config.ts`. (I-7-6)
10. Add `AddHsts(...)` beside the existing `UseHsts()` so the API matches the UI's max-age and
    `includeSubDomains`. (I-7-4)
11. Set `AllowedHosts` per environment instead of `*`. (I-7-7)

---

## I-8 — Incidents and bus factor

Date: 2026-08-30
Packet: docs/audits/INDEX.md, table 5

### Verdict

The detection half of incident response is built and honest about its own unfinished state: an
external `/health/ready` monitor polling every 3 minutes, two edge monitors through Cloudflare, two
"can a game be played / is Neon healthy" heartbeats, and three app-signal threshold alerts. What is
missing is everything between *an alert fires* and *the maintainer is fixing it*. There is no
symptom-to-action runbook — `DEPLOYMENT.md` is 866 lines organised by setup task, not by "the page
just went off, now what", and the recovery procedures that do exist (rollback §3, migration
roll-forward §3.1, Neon restore §5, the kill switches — which per S-5-3 are not in the doc at all)
are scattered with no incident index. Every alert is delivered by **email only**: `email = true` on
every monitor and heartbeat, a single zero-wait escalation step to `entire_team`, no repeat, and
nothing in the repo provisions or documents an SMS / call / push channel that actually wakes a
sleeping solo maintainer at 3am. And the bus factor for incidents specifically is one: the Better
Stack "team" is one person, the alert goes to one inbox, and no second human can see that the site
is down, flip a kill switch (Admin + `mfa`), post a notice, or start a restore. None of this is a
live outage today — the site is pre-launch behind Cloudflare Access — but 8.7 turns each of these
from "acceptable while nobody is watching" into "the first real incident is discovered by players".

The full "if I disappear" succession doc (where every secret lives, who can freeze billing, whether
a fork can keep the variant playable) is B-1's packet (owner, not yet run) and is not duplicated
here; this packet covers only the narrower question of whether an incident can be *detected, routed
to a human, and acted on* by the one person available.

### Findings

#### I-8-1 — No incident runbook: nothing maps an alert to a first check and a response action
- Severity: Medium (trigger: 8.7 / Todo #16 — pre-launch, players do not yet depend on a fast response)
- Where: `docs/ops/DEPLOYMENT.md` has no "when an alert fires" section. The closest artefacts each
  stop short of an action: `docs/ops/DEPLOYMENT.md:703-716` (§10.2) maps each signal to *what a page
  means* but not to *what to do*; recovery procedures are scattered at `:168-176` (app rollback),
  `:178-202` (§3.1 migration roll-forward), `:314-327` (§5 Neon restore), `:405-427` (§5.3 offsite
  restore); the kill switches appear nowhere in the file (`docs/audits/S.md` S-5-3). The seven
  Better Stack alerts live in `infra/betterstack/main.tf:65-385`; the four GitHub-cron failure
  emails in `.github/workflows/{restore-drill,offsite-backup,ops-probes}.yml`.
- Impact: a maintainer woken by "KiriChess backend readiness" or "synthetic play" has to reconstruct
  the response from a 12-section setup document under stress. Which alert means "roll back the last
  deploy" versus "restore Neon" versus "flip Pairing and go back to sleep" is not written down
  anywhere as a lookup. The `ops-probes.yml` failure step already gestures at this
  (`echo "See docs/ops/DEPLOYMENT.md § 10.2"`) but §10.2 has no procedure to send them to.
- Fix: add one page — `docs/ops/INCIDENTS.md`, or a "When an alert fires" subsection in
  `DEPLOYMENT.md` §10 — as a table: {alert name → most likely cause → first check (a URL or a
  command) → response action → link to the detailed procedure}. One row per Better Stack alert and
  per cron-failure email. Fold in S-5-3's kill-switch `curl` block and a "leaked secret / bad
  deploy" row (see I-8-6). Keep it to a page; the detail already exists in DEPLOYMENT.md and just
  needs an index.

#### I-8-2 — Every alert is email-only; no channel that wakes a solo maintainer is provisioned or documented
- Severity: Medium (trigger: 8.7 makes "players tell you first" a real cost — ROADMAP.md:683)
- Where: `infra/betterstack/main.tf:83` and every monitor/heartbeat below it set `email = true`;
  the escalation policy is a single step with `wait_before = 0` and `step_members { type =
  "entire_team" }` (`:50-55`), no second step and no repeat; `variables.tf:31-35` documents
  `alert_policy_id` as deliberately `null` ("Leave null until an on-call policy exists"). GitHub
  Actions failure notifications for the restore drill, offsite backup and ops probes are also plain
  email (`docs/ops/DEPLOYMENT.md:397`, `:467`).
- Impact: whether a 3am page reaches a phone depends entirely on undocumented per-user Better Stack
  mobile-app settings that no repo artefact establishes or verifies. As provisioned, the escalation
  policy sends one email and never nags. A missed or dismissed first notification is a missed
  incident — for a service whose whole product is a running clock, "discovered at 9am" is an
  outage measured in hours.
- Fix: two cheap steps, both free-tier. (1) The maintainer installs the Better Stack mobile app and
  enables critical-alert push for the KiriChess policy; add a line to `DEPLOYMENT.md` §10 stating
  that this app is the actual paging channel and the escalation policy assumes it exists. (2) Add a
  second escalation step to `betteruptime_policy.kirichess` (`wait_before` ~600s, repeat to the same
  member) so a missed first alert re-fires rather than going silent. Neither is an on-call rotation
  and neither reverses the "no NOC" position.

#### I-8-3 — The alert a maintainer opens at 3am contains no link to the runbook
- Severity: Low (trigger: depends on I-8-1 existing)
- Where: `infra/betterstack/main.tf` — none of `betteruptime_monitor.health_ready` (`:65-84`),
  `edge_api` (`:327-339`), `edge_site` (`:349-364`), or the two `betteruptime_heartbeat` resources
  (`:369-385`) set a description / notes / runbook URL field. The Better Stack alert email therefore
  carries only the monitor name.
- Impact: even once I-8-1's page exists, nothing connects the notification to it. The responder has
  to remember the doc exists and where it is — the same failure mode S-5-3 found for the kill
  switches.
- Fix: once `INCIDENTS.md` (I-8-1) exists, put its URL in each monitor's and heartbeat's
  description/notes field in `main.tf`, and add the relevant row anchor per monitor. Update
  `BetterStackAlertingDriftTests` if it needs to tolerate the new field.

#### I-8-4 — Bus factor for incidents is one: no second human can see an alert or act during an outage
- Severity: Medium (trigger: the maintainer is unreachable — travel, illness, no signal — while an incident is open)
- Where: `infra/betterstack/main.tf:50-55` — `entire_team` on a single-member account is one inbox;
  `variables.tf:31-35` confirms no policy targets anyone else. The kill switches require Admin +
  `mfa` (`docs/audits/S.md` S-5-3), held by exactly one account (`.claude/rules/security.md`,
  "exactly one `Admin` account, owned by the deployment owner"). Fly / Neon / Cloudflare / Stripe
  are single-owner accounts (`docs/ops/PROVIDER-ACCOUNTS.md`). No shared or monitored ops inbox —
  alerts and cron-failure mail land in a personal inbox.
- Impact: if the maintainer is offline for the duration of an outage, no one else knows the site is
  down, can pause matchmaking, can restore from backup, or can tell players anything. For a
  one-person hobby project some of this is irreducible, but "nobody can even *see* the alert" is a
  gap a free-tier team member closes.
- Fix (incident-specific slice of B-1 — cross-reference, do not duplicate the succession doc): add
  one trusted second person as a member of the Better Stack account so the page is at least *seen*
  by two people; write down in `DEPLOYMENT.md` (or B-1's doc) whether that person has any provider
  access or is purely an "escalate to the maintainer, post a Discord notice" contact. B-1 owns the
  decision on secrets custody and billing-freeze authority.

#### I-8-5 — No way to tell players an outage is known; the one channel that survives a Fly/Neon outage is undocumented for it
- Severity: Low (trigger: 8.7 — a public playerbase with nowhere to check)
- Where: no status page, by decision (`infra/betterstack/README.md:152-153`, "No status page. Not
  asked for by 9c.5's text"), which is proportionate at this size and not challenged here. But
  nothing designates a fallback. The Discord server is live in the site footer
  (`docs/audits/INDEX.md:277`, `https://discord.gg/GRusewDw4`) and runs on infrastructure entirely
  separate from Fly/Neon/Cloudflare, so it stays up during a KiriChess outage.
- Impact: during an outage `kirichess.com` is dead and a player has no signal that anyone is aware.
  "Aware, investigating" posted somewhere reachable is the cheapest trust-preserving action in an
  incident and there is no documented place to post it.
- Fix: one line at the top of I-8-1's runbook — "step 0: post 'aware, investigating' to Discord
  #announcements" — and optionally a pinned Discord message stating that Discord is where status
  updates go during an outage. No status-page product, no new vendor.

#### I-8-6 — A leaked credential has no written revoke-and-rotate procedure
- Severity: Low
- Where: `docs/audits/B.md` B-4-2 tracks rotation cadence and inventory. The steps to take when a
  token leaks (which token, revoke where, redeploy) are not written as a procedure.
  `.claude/rules/security.md` records that a live Cloudflare token was once committed, so this is
  the incident class most likely to actually occur here.
- Impact: the most probable incident — a leaked credential — has no checklist.
- Fix: add a "leaked secret" row to the incident runbook pointing at B-4's operational-secret table
  (`docs/audits/B.md`) with the revoke-and-rotate command per secret.

### Already covered

- **No on-call rotation; every alert emails the whole team; nobody staffed to answer a page** —
  `docs/ops/DEPLOYMENT.md:659-660`, `infra/betterstack/README.md:145-147`, `docs/audits/I.md`
  (Already-covered §), `docs/audits/S.md` (S-5 Already-covered §), ROADMAP.md:683. I-8-2 is about
  the *delivery channel* (email cannot wake a sleeping person, no push provisioned), not about
  staffing a rotation.
- **`/metrics` dark since 2026-08-15; ops-probes not dispatched until post-1-Sep; no absence alert
  on a dead scrape; a release serves traffic before anything confirms it can** — `docs/audits/I.md`
  I-5-2, I-5-3; `docs/audits/B.md` Already-covered §.
- **GitHub disables every scheduled workflow after 60 days of no commits, silently — restore drill,
  offsite backup, ops probes and both scanners stop together** — `docs/audits/B.md` B-4-1.
- **No rotation cadence / expiry tracking for `FLY_API_TOKEN`, `NEON_API_KEY`, R2 keys, the
  synthetic token; `FLY_API_TOKEN` has burned once** — `docs/audits/B.md` B-4-2; `docs/audits/INDEX.md:228`.
- **Kill switches are undiscoverable during an incident — no `/moderation` panel, no line in
  DEPLOYMENT.md, no runbook `curl`; `Pairing` does not actually stop most game creation** —
  `docs/audits/S.md` S-5-1, S-5-3, S-5-5. I-8-1 folds the kill-switch runbook block into the
  broader incident index rather than re-specifying it.
- **`/health/ready` returns ready on a connectable-but-unusable database; edge-site `302` allowance
  has no removal trigger after Access comes off** — `docs/audits/I.md` I-5-4, I-5-5.
- **The full "if I disappear" succession doc — secrets custody, who can freeze billing, fork
  continuity** — `docs/audits/INDEX.md` B-1 (owner, not yet run). I-8-4 is only the "can a second
  person see and act on an alert" slice.
- **GDPR Art. 33 personal-data-breach notification (72h to the Belgian DPA)** — `docs/legal/PRIVACY.md`,
  `docs/legal/ROPA.md`, `docs/audits/L.md`, `docs/audits/L.md`. Out of scope for this
  operational packet; I-8-6 covers only the *technical* leaked-credential response.
- **Sentry DSN unset on `kirichess-ui` (server/edge exceptions unreported)** —
  `docs/ops/PROVIDER-ACCOUNTS.md`.

### Action list

Ordered. No standing-position reversals (no NOC, no on-call rotation, no status-page product, no paid tier).

1. I-8-1: write `docs/ops/INCIDENTS.md` (or a `DEPLOYMENT.md` §10 subsection) as a
   one-page alert → first-check → action → detailed-procedure table, one row per Better Stack alert
   and per cron-failure email. Fold in S-5-3's kill-switch `curl` block, I-8-5's "post to Discord
   first" step 0, and I-8-6's leaked-secret row. Link it from `docs/README.md` and from the
   `ops-probes.yml` / restore-drill failure-explanation steps.
2. **Owner, once** — I-8-2: install the Better Stack mobile app, enable critical-alert push for the
   KiriChess policy, and confirm a test alert reaches the phone. Then add the second
   escalation step (`wait_before` ~600s, repeat) to `betteruptime_policy.kirichess` and a sentence
   to `DEPLOYMENT.md` §10 naming the app as the paging channel.
3. **Owner** — I-8-4: add one trusted second person to the Better Stack account so an alert is seen
   by two people; record in B-1's doc whether they hold any provider access. Defer the rest of
   secrets-custody / billing-freeze to B-1.
4. I-8-3: once `INCIDENTS.md` exists, set its URL in each monitor and heartbeat
   description field in `infra/betterstack/main.tf`; adjust `BetterStackAlertingDriftTests` if
   needed.
5. **Owner, once** — I-8-6: add the leaked-secret row to the incident runbook. Set `SENTRY_DSN` on
   `kirichess-ui`.
6. **Owner** — I-8-5: pin a Discord #announcements message stating Discord is the outage
   status channel.

No production code changed in this packet. No standing-position reversals. Only `docs/audits/I.md`
was created.

---

## I-9 — Cost
Date: 2026-08-30
Packet: docs/audits/INDEX.md

### Verdict

The bill is genuinely coffee-money today and stays that way through a realistic launch: one
always-on Fly `shared-cpu-1x`, one Neon branch, a 256 MB log-shipper, and everything else on a free
tier. Best estimate of the current monthly spend is **~$25-35**, and it is dominated by Neon — which
is the single number nobody wrote down. The arch-speed audit already established that the ~250
concurrent-game ceiling does not move the Fly *compute* line (the binding constraint is the
write-behind persister, not CPU or RAM), so the ceiling cost story is not "the server falls over and
you pay for a bigger one" — it is a set of **free-tier cliffs that fail silently**, each on a
different provider, and none of them is watched. Resend's 100-emails-per-day cap breaks confirmation
mail on the exact day a launch spikes signups. GitHub's 2,000 private-repo Actions minutes are
already spent every month, and the working repo is private *permanently* by decision, so the
`ops-probes` 15-minute cron cannot be switched on as written without blowing the whole allowance.
Upstash's free command budget is exhausted in hours by the per-move Redis churn the day Phase 8.4
provisions Redis. None of these costs money at the $0 spending limits in place — they just stop the
service, quietly.

Lock-in is low across the board: every paid or free provider here is a weekend to leave (Fly is
plain containers with a working compose path, Neon is stock Postgres 16 with a proven `pg_dump`
restore, Resend/Sentry/Better Stack/Stripe are all behind thin abstractions or plain URLs). I-10
owns portability in depth; this packet only confirms nothing in the cost structure creates a deeper
hook. The one irreplaceable asset is the domain name itself, and the one thing that takes weeks
rather than a weekend to rebuild is the sending domain's mail reputation.

The smallest thing that protects the maintainer is a single page — provider, plan, ~$/mo, the
ceiling, what happens when you hit it — wired to B-4's quarterly pass. That page is the deliverable
this audit could not find and the action list below seeds it.

### Provider inventory

Rows are "what it is today". "Cliff" = the usage level or event that jumps the bill or breaks the
service. Prices are the providers' published rates. The repo records
none of them, which is finding I-9-7.

| Provider | Tier today | ~Cost today | Cliff | At the cliff |
| --- | --- | --- | --- | --- |
| **Fly.io — `kirichess-api`** | `shared-cpu-1x` / 512 MB, always-on (`fly.backend.toml:47-49,97-99`) | ~$3.19/mo compute | Second machine for Phase 8.4 multi-instance (`DEPLOYMENT.md:490-518`) doubles compute; outbound bandwidth (see I-9-6) | Compute ~2x; bandwidth is the real variable |
| **Fly.io — `kirichess-ui`** | `shared-cpu-1x` / 512 MB, scale-to-zero (`fly.ui.toml:36-38`) | ~$0-2/mo | Sustained traffic keeps it warm | Small, linear |
| **Fly.io — staging pair** | both scale-to-zero (`fly.*.staging.toml`) | ~$0-1/mo | A test session that pins `min_machines_running` and forgets to revert (`fly.backend.staging.toml:27-34`) | One machine's worth, silently, until noticed |
| **Fly.io — `kirichess-log-shipper`** | `shared-cpu-1x` / 256 MB, always-on (no `[http_service]` ⇒ no auto-stop, `fly.logshipper.toml:14-15,45-47`) | ~$1.94/mo | none realistic | — |
| **Fly.io — bandwidth** | pay-as-you-go, **no free allowance** since Oct 2024 | ~$0 pre-launch | Concurrent SignalR play (I-9-6) | ~$12-35/mo at the ~250-game ceiling (estimate) |
| **Neon** | **plan unrecorded** — `neon-watch.sh:27-28` assumes Launch; `PROVIDER-ACCOUNTS.md:31` says "paid for already" | **~$19/mo if Launch**, $0 if Free | Storage over the plan's included GB (indefinite game retention — standing position); compute-hours (I-9-2) | Storage overage $1.75/GB-mo (Launch); compute billed continuously because the app never lets the primary autosuspend |
| **Cloudflare — DNS/proxy/CDN/DDoS/Turnstile/Email Routing/Redirect Rules** | Free | $0 | >5 WAF/rate-limit rules; Zero Trust Access >50 seats (Access is removed at launch anyway, ROADMAP #12) | Would need a paid Cloudflare plan; very unlikely at this scale |
| **Cloudflare — Registrar (`kirichess.com`)** | at-cost auto-renew | ~$10.44/yr (~$0.87/mo) | Lapsed card at renewal (B-4-6) | Domain stops resolving — CORS, OAuth redirect, mail auth, every email link all break |
| **Cloudflare — R2 (backups)** | Free tier (10 GB, no egress fee), EU jurisdiction (`DEPLOYMENT.md:437-442`) | $0 | DB dump set > 10 GB (8 dumps retained, `DEPLOYMENT.md:463-465`) | $0.015/GB-mo — trivial for a long time |
| **Cloudflare — Workers (Turnstile siteverify)** | Free (100k req/day) | $0 | 100k register-page loads/day | $0.30/million — negligible |
| **Resend** | Free (3,000/mo, **100/day**) | $0 | **100 emails/day** (I-9-1) | 429s; non-durable in-process queue drops them (`DEPLOYMENT.md:596-599`); no bounce webhook. Pro is $20/mo / 50k |
| **Sentry** | Free "Developer" (~5k errors/mo, 1 seat), backend DSN + client DSN both live (`PROVIDER-ACCOUNTS.md:22`, `fly.ui.toml:21`) | $0 | ~5k errors/mo — one recurring hot-path exception burns it in hours | Events dropped for the rest of the month; the one month you have a bug is the month you are blind. `tracesSampleRate:0` already set (I-5, L-6) keeps performance units out of it |
| **Better Stack** | Free (10 monitors, small log volume/retention), all 4 apps shipped unfiltered (`fly.logshipper.toml:31`) | $0 | Log volume/retention overage (I-9-5); >10 monitors | Logs dropped or billed; same account holds the uptime alerting the project depends on |
| **Stripe** | pay-as-you-go, no monthly fee | $0 fixed | none (per-transaction 1.5%+€0.25 EU / 2.9%+$0.30) | Linear on donation volume; not a cliff |
| **GitHub Actions** | Free private-repo (**2,000 min/mo**), $0 spending limit | $0 | **2,000 min/mo** — already spent monthly (`ROADMAP:41-42`, `:1419-1422`); working repo private *permanently* (`ROADMAP:35`) | All workflows stop mid-month, CI on `main` included. `ops-probes` cron cannot be enabled as written (I-9-3) |
| **Upstash (Fly Redis)** — future | not provisioned (`DPA-CHECKLIST.md:42-46`, standing position) | $0 | Free ~500k commands/mo — the per-move churn (AS-6) blows it in hours at ceiling (I-9-4) | Pay-as-you-go ~$0.20/100k commands |

**Today's total: ~$25-35/mo**, ~$19 of it Neon (if Launch). **At the ~250-game engineering ceiling:
roughly $70-130/mo**, the growth being Fly bandwidth, a forced Resend Pro tier, Better Stack log
overage, and Upstash once Redis exists. Still not a business — but 3-4x, and worth the maintainer
knowing the shape before traffic gets there.

### Findings

#### I-9-1 — Resend's 100-emails-per-day cap breaks confirmation mail on launch day, and the queue that would absorb it is non-durable
- Severity: Medium
- Where: `docs/ops/DEPLOYMENT.md:534-540` (§8, email wired but the free tier's limits are recorded
  nowhere), `:596-599` (§8.4 "The queue is in-process and not durable. A restart drops whatever is
  queued" and "No bounce or complaint handling"), `PROVIDER-ACCOUNTS.md:31` ("Sentry and Resend are
  on free tiers that this traffic will not exceed at launch" — asserted, not sized).
- Impact: Resend's free tier is 3,000/month **and 100/day**; the daily cap binds first. Email
  verification and password reset are the only senders, so a launch that puts >100 new
  registrations + resets through in 24 hours (AD.4's founding cohort is ~10, but AD.7's public
  targets and a good launch thread can exceed that) starts getting 429s. The dispatch queue is
  in-process and not durable, and there is no bounce/complaint webhook, so the failed confirmations
  are neither retried nor visible — a stranger who signed up simply never gets the mail. The repo's
  own claim that launch traffic "will not exceed" the free tier is about the monthly number and does
  not address the daily one.
- Fix: before 8.7, pick one: (a) confirm from AD.7 that expected launch-day signups stay well under
  100/day, and record the cap + the manual recovery path (`/api/auth/resend-confirmation`,
  `/api/auth/forgot-password`) in `DEPLOYMENT.md` §8 as a known ceiling; (b) upgrade to Resend Pro
  ($20/mo) for the launch month only; (c) give the confirmation queue minimal persistence + retry.
  (a) is the smallest thing and is probably enough.

#### I-9-2 — The Neon plan and the expected monthly compute-hour draw are unrecorded, and the always-on connection pool defeats autosuspend
- Severity: Medium
- Where: `docs/ops/DEPLOYMENT.md:44-47` (§2: "long-lived app holding a connection pool of its own",
  use the pooled endpoint), `scripts/neon-watch.sh:27-28` (`LAUNCH_STORAGE_BYTES` — a Launch-plan
  assumption baked into the watch script), `:76` ("scale-to-zero is a plan setting"),
  `PROVIDER-ACCOUNTS.md:31` ("Neon is provisioned ... paid for already"), `DEPLOYMENT.md` §4.1
  (capacity derivation — no cost line).
- Impact: nothing in the repo states whether the project is on Neon Free or Launch (~$19/mo). It
  matters for the cost model: the app holds an Npgsql pool continuously by design, so the primary
  compute never autosuspends and runs ~720 h/month. On Launch that is continuous consumption against
  a bundled compute-hour allowance, with overage billed per compute-hour and nothing tracking it —
  `neon-watch.sh` watches storage percent, not compute-hours or the bill. On Free it means the
  deployment depends on undocumented always-on behaviour. Either way the largest line in the monthly
  bill is the one with no entry in any doc.
- Fix: record the active Neon plan, the autosuspend setting, and the observed monthly bill in
  `DEPLOYMENT.md` §4.1 next to the capacity number. Make B-4's generic "Neon / Fly / Sentry / Resend
  free-tier headroom" quarterly line specifically include "Neon compute-hours vs plan allowance".

#### I-9-3 — The `ops-probes` 15-minute cron cannot be enabled as written without exceeding the entire GitHub free Actions allowance, on a repo that is private permanently
- Severity: Medium
- Where: `.github/workflows/ops-probes.yml` (cron `3,18,33,48 * * * *` = 96 runs/day, per
  `docs/audits/B.md:29`), `docs/ROADMAP.md:63` ("The 15-minute cron will start spending minutes on
  its own if the workflow is on `main` — decide before 1 Sep whether you want that, or keep it
  dispatch-only"), `:35` ("The **working** repo stays private permanently"), `:41-42`, `:1419-1422`
  ($0 spending limit, August minutes spent, two backup dispatches died).
- Impact: 96 job-starts/day at GitHub's 1-minute-minimum billing plus runner spin-up is ≈2,900+
  minutes/month — more than the entire 2,000-minute free private-repo allowance on its own, before
  CI (heavy: `dotnet test` + Testcontainers, two Docker image builds, Trivy, Playwright, on every
  push to `main`/`dev` and every PR), weekly CodeQL + Semgrep, monthly restore-drill, weekly
  offsite-backup. The spending limit is $0, so crossing the line does not cost money — it stops
  every workflow mid-month, CI on `main` included. ROADMAP flags the *decision* but not that the
  arithmetic makes "keep it dispatch-only" the only viable answer while the repo stays private.
- Fix: keep `ops-probes` `workflow_dispatch`-only, or move the two probes off GitHub Actions
  entirely — a Cloudflare Worker Cron Trigger (free, 100k/day, Cloudflare is already in the stack
  and already a processor) can `curl` `/health/ready` and `synthetic-play` and ping the same Better
  Stack heartbeat URLs. Record the choice in `DEPLOYMENT.md` §10.2. Cross-ref: `docs/audits/I.md`
  (I-5-3 wants a post-deploy smoke check — same "external free scheduler" answer), deep
  `IMPROVEMENT_PLAN.md` batch 9 item 3, `B.md` B-4-1.

#### I-9-4 — Upstash's free command budget is exhausted almost immediately by the per-move Redis churn, the day Phase 8.4 provisions Redis
- Severity: Low (trigger: Redis provisioned)
- Where: `docs/audits/I.md (I-1)` AS-6 / `docs/audits/I.md` I-1-3
  (`src/KiriChess.Server/Games/Distributed/LiveGameIndex.cs` — `SADD` both player ids on *every
  move*, `SMEMBERS` the whole cluster-wide set every 3 s and per presence connect),
  `docs/ops/DEPLOYMENT.md:490-518` (§7), `docs/legal/DPA-CHECKLIST.md:42-46`.
- Impact: Fly Redis is Upstash; the free tier is ~500k commands/month. AS-6's churn (2 writes per
  ply, a cluster-wide `SMEMBERS` every 3 seconds, a read per presence connect) reaches that in hours
  at the ~250-game ceiling and in days at launch traffic. Pay-as-you-go is ~$0.20 per 100k commands,
  so the un-fixed churn is a *variable cost*, not only a scaling smell — a second reason to land the
  fix before the feature ships.
- Fix: land AS-6 / I-1-3 (already recommended on scaling grounds) *before* provisioning Redis; size
  the Upstash plan against the post-fix command rate; add an Upstash row to the cost page and to
  B-4's headroom line when its DPA row is filed (`docs/audits/L.md`, `B.md` B-4-5).

#### I-9-5 — Better Stack ingests unfiltered logs from all four apps with no retention set, so log-volume overage is uncapped — and it shares an account with the uptime alerting
- Severity: Low
- Where: `fly.logshipper.toml:28-31` (`SUBJECT = "logs.>"` — "Every app in the org, unfiltered"),
  `infra/betterstack/main.tf` (`logtail_source`, no retention — per `docs/audits/L.md` L-3-1
  `:308-310`), `docs/ops/DEPLOYMENT.md:844-849` ("No retention policy chosen beyond Better Stack's
  plan default").
- Impact: the free tier bundles a small monthly log volume and short retention. Shipping production
  + staging + UI + the shipper's own stdout unfiltered (including EF command lines) crosses that
  under real traffic; the result is dropped logs or a bill on the same account that holds the
  10-monitor uptime alerting the project depends on, so a billing lapse there also darkens the
  alerts. L-3-1 already asks for an explicit retention on *privacy* grounds — this is the same edit
  for a cost reason.
- Fix: set a short explicit retention on the source and narrow `SUBJECT` to production only (drop
  staging and the shipper), exactly as `docs/audits/L.md` L-3-1 recommends. One change closes both
  findings.

#### I-9-6 — Fly outbound bandwidth is the only usage-metered cost that scales with concurrent play, and it is unestimated and unwatched
- Severity: Low (trigger: sustained play toward the ~250-game ceiling)
- Where: `src/KiriChess.Server/Program.cs:470` (`.AddJsonProtocol()` — full FEN + legal-move list +
  clock events per move, per `docs/audits/I.md (I-1)` AS-7), `docs/ops/DEPLOYMENT.md` §4.1
  ("what to re-check" list).
- Impact: every move broadcasts a JSON payload to each group member (both players plus every
  spectator), and Fly bills outbound transfer at ~$0.02/GB (EU) with no free allowance since late
  2024. At the engineering ceiling this is plausibly $12-35/mo — several times today's compute line
  — and nothing estimates or monitors it. It is not a launch concern (launch traffic is a small
  fraction of the ceiling); it is a "know it before you get there" item.
- Fix: add "Fly outbound transfer" to `DEPLOYMENT.md` §4.1's re-check list beside the existing
  MessagePack note (AS-7 / I-1-7 already recommends `.AddMessagePackProtocol()`, which roughly
  halves this); read the transfer line off the Fly bill after 9e.8's load test.

#### I-9-7 — No document records the monthly bill, the plan per provider, or the free-tier ceilings
- Severity: Low (umbrella)
- Where: `docs/ops/PROVIDER-ACCOUNTS.md` (accounts and keys — no costs), `docs/ops/DEPLOYMENT.md`
  (topology, capacity — no costs), `docs/audits/B.md:211` (one generic "free-tier headroom"
  calendar line).
- Impact: the questions this packet asks — what does it cost, what breaks it — cannot be answered
  from the repo. Each silent free-tier ceiling (Resend 100/day, Sentry ~5k/mo, GitHub 2,000 min/mo,
  Upstash-future, Better Stack log volume) fails with no bill and no alert; the first signal is a
  dropped email, a dropped error, or a stopped workflow. A one-person project that can go quiet for
  months is exactly the case that needs this written down.
- Fix: add a short "Cost & tiers" section to `DEPLOYMENT.md` (or `docs/ops/COST.md`), seeded from
  the inventory table above: provider, plan, ~$/mo, the ceiling, what happens at the ceiling,
  upgrade path. Link it from B-4's `MAINTENANCE-CALENDAR.md` quarterly pass and make the headroom
  line name the specific caps.

### Already covered

- **Free-tier headroom quarterly check** — `docs/audits/B.md` (calendar line "Neon / Fly / Sentry
  / Resend free-tier headroom vs. actual usage — Quarterly", `B.md:211`). I-9 makes the caps
  specific and adds GitHub Actions, Upstash and Better Stack log volume to that line.
- **Missing `timeout-minutes` / `concurrency` on `ci.yml` / `codeql.yml` / `semgrep.yml` as a real
  cost risk on a minutes-constrained repo** — deep `IMPROVEMENT_PLAN.md` batch 9 item 3,
  `docs/audits/C.md (section INF-1)` INF-1-2 / INF-1-3. Not re-reported; I-9-3 is the `ops-probes`
  cron specifically, which those do not cover.
- **GitHub disables every scheduled workflow after 60 days of no commits; long-lived operational
  token rotation (`FLY_API_TOKEN` burned once); domain auto-renew / registrar card unmonitored** —
  `docs/audits/B.md` B-4-1, B-4-2, B-4-6.
- **Neon PITR proven, offsite-backup plumbing, the provider-account-deletion / billing-lapse DR
  scenario** — `docs/audits/INDEX.md` **I-4** (not yet run), `docs/decisions/RETENTION.md` §6.
- **Portability in depth — "leave Neon or Fly in a weekend", what is irreplaceable** —
  `docs/audits/INDEX.md` **I-10** (not yet written). I-9 confirms only that the cost
  structure adds no deeper hook: every provider here is thin-abstraction or plain-URL, the compose
  path boots, `pg_dump`/restore into stock Postgres 16 is proven (`scripts/offsite-backup.sh`).
- **The per-move Redis churn (`LiveGameIndex`), SignalR JSON vs MessagePack** —
  `docs/audits/I.md (I-1)` AS-6 / AS-7, `docs/audits/I.md` I-1-3 / I-1-7. I-9-4 and I-9-6 add
  the cost dimension only.
- **Better Stack retention unset (privacy), Better Stack / Stripe / R2 / GitHub as processors
  needing DPAs and ROPA rows** — `docs/audits/L.md` L-3-1, L-3-2, L-3-3; `docs/audits/I.md`.
- **The ~250 concurrent-game ceiling is a written decision, the binding constraint is the persister
  not CPU/RAM, and the bill stays coffee-money at that ceiling** — `docs/audits/I.md (I-1)` §1,
  `docs/audits/I.md` verdict. I-9 only refines "coffee-money" into the ~$70-130/mo shape and names
  which lines grow.
- **Staging scaling to zero as a deliberate cost/safety choice** — `docs/audits/C.md (section INF-1)`,
  `docs/ops/DEPLOYMENT.md` §2.1.
- **Recurring Stripe donation tiers, GitHub Sponsors / Ko-fi as additive** — `docs/audits/funding.md`,
  `docs/audits/L.md`. Not a profitability model, per the packet.

### Action list

Ordered. No standing-position reversals (Neon stays as provisioned; ~250-game ceiling is a decision;
free-forever holds).

1. create the "Cost & tiers" section / `docs/ops/COST.md` from the inventory table above
   (I-9-7). Link it from `docs/ops/DEPLOYMENT.md` §12 and from B-4's `MAINTENANCE-CALENDAR.md`.
2. **Owner** — record the active Neon plan, autosuspend setting and last month's actual bill into
   that page and `DEPLOYMENT.md` §4.1 (I-9-2). One dashboard glance.
3. **Owner** — decide the Resend launch-day posture from AD.7's numbers: accept the 100/day cap and
   document the recovery path, or buy Pro for the launch month (I-9-1). Before 8.7.
   **Decided 2026-09-04 with AD.7:** stay on free. Expected launch-day well under 100/day.
   Recovery is `/api/auth/resend-confirmation` and `/api/auth/forgot-password`. Cap recorded in
   `DEPLOYMENT.md` §8.4.
4. **Owner** — keep `ops-probes` dispatch-only, or move the two probes to a Cloudflare
   Worker Cron Trigger; record the choice in `DEPLOYMENT.md` §10.2 (I-9-3). Before the post-1-Sep
   dispatch decision.
5. set an explicit short retention on the Better Stack log source and narrow
   `fly.logshipper.toml`'s `SUBJECT` to production (I-9-5) — same edit as `L.md` L-3-1, do it once.
6. add "Fly outbound transfer" and "Upstash command rate (post-AS-6-fix)" to
   `DEPLOYMENT.md` §4.1's re-check list (I-9-6, I-9-4).
7. **Owner** — when Phase 8.4 provisions Redis: land AS-6 / I-1-3 first, then size the Upstash plan
   against the post-fix command rate, then add its cost + DPA rows (I-9-4).

No production code changed in this packet. Only `docs/audits/I.md` was created.

---

## I-10 — Portability
Date: 2026-08-30
Packet: docs/audits/INDEX.md

### Verdict

The maintainer can leave Fly, Neon, Cloudflare, Resend, Sentry or Stripe in a weekend, and for
most of them in an afternoon. This is not luck: the app is two stock containers
(`Dockerfile.backend`, `Dockerfile.frontend`) with no cloud-vendor base image or SDK, the database
layer is a bare `UseNpgsql` with `EnableRetryOnFailure` and zero Neon-specific SQL, extensions or
branching calls in `src/**`, email is a 95-line `POST /emails` client behind an already-abstracted
dispatcher, and the one automated off-provider dump (`scripts/offsite-backup.sh`) writes
`pg_dump --format=custom --no-owner --no-acl`, so a restore into a stock Postgres 16 is clean and
scripted. The only code path that reads a Fly-specific env var (`FLY_MACHINE_ID` in
`InstanceIdentity.cs:18`) has a hostname fallback one line down. Data Protection keys were moved
into Postgres in 10a.1 (`Program.cs:177-179`), so session/token-signing material now travels with
the dump instead of dying with the container.

**Nothing in code or infrastructure is irreplaceable.** What is irreplaceable is non-technical and
outside this packet: the domain, the reserved name, the git history as a timestamp, and the
maintainer (B-1). The real portability exposure is operational, not architectural: (1) there is no
written "move off" runbook — `docs/ops/DEPLOYMENT.md` documents only the happy path on the current
stack, so an eviction turns a weekend migration into an improvisation while the site is down; (2)
the off-Neon dump that every "you can leave" claim rests on has never had a green run (I-4 /
ROADMAP 10c.1); (3) one piece of running front-door infrastructure — the Turnstile siteverify
Worker — has no source or config anywhere in the repo. Better Stack is the only provider that is a
month-move rather than a weekend-move, and it is not in the serving path.

### Provider coupling and move-cost

| Provider | Provider-specific coupling (`file:line`) | Rating |
| --- | --- | --- |
| **Fly.io** (compute) | 5 `fly.*.toml` files; `fly.backend.toml:27` `release_command`, `:30` `strategy = "immediate"`, `:34,36,93-95` private metrics port 9091 (no `[http_service]` publish); `src/KiriChess.Server/Infrastructure/Redis/InstanceIdentity.cs:18` reads `FLY_MACHINE_ID` (clean `MachineName`+GUID fallback `:20-21`); `docs/ops/DEPLOYMENT.md` is entirely Fly-shaped. No Machines API, no Volumes, no `fly-replay`, no Fly Postgres. `Dockerfile.*` use only `mcr.microsoft.com` / `node:20-alpine`. | **weekend-move** |
| **Neon / Postgres** | `src/KiriChess.Server/Program.cs:149-157` stock `UseNpgsql` + `EnableRetryOnFailure`; `docs/ops/DEPLOYMENT.md:43-46` pooled `-pooler` endpoint + `SSL Mode=Require;Trust Server Certificate=true`; `:88-91` Neon provisions 4 roles (`neondb_owner` + `authenticator`/`anonymous`/`authenticated`) and 2 DBs (`kirichess`, `neondb`) — the app connects only as `neondb_owner` to `kirichess`, the rest is unused Neon Data-API scaffolding; `scripts/restore-drill.sh`, `scripts/offsite-backup.sh`, `scripts/neon-watch.sh` all shell out to `neonctl`. **No Neon SQL, extension, or `pg_*` feature in `src/**`.** EF migrations are plain. | **weekend-move** for the database engine; backup-out-of-Neon is I-4 |
| **Cloudflare — DNS + proxy** | `docs/ops/DEPLOYMENT.md:15-20`. TLS terminates at Fly (`force_https` in the tomls), not the edge, so the proxy is optional CDN/DDoS only. | **weekend-move** (repoint nameservers) |
| **Cloudflare — Turnstile** | `src/KiriChess.Server/Auth/TurnstileVerifier.cs:70` hits the standard `turnstile/v0/siteverify`; `Dockerfile.frontend:31-34` `NEXT_PUBLIC_TURNSTILE_SITE_KEY` / `_WORKER_URL`; `ui/next.config.ts:50-62` CSP allowances. Fails open (`TurnstileVerifier.cs:62,87`); disable = one unset secret. **Worker source not in repo** (I-10-3). | **weekend-move** |
| **Cloudflare — R2** | `scripts/offsite-backup.sh:58-79` S3 API via `aws --endpoint-url`; `:64-68` jurisdiction-host logic (`.eu.r2.cloudflarestorage.com`); `:73-77` `AWS_REQUEST_CHECKSUM_CALCULATION=WHEN_REQUIRED` workaround for R2 rejecting newer AWS-CLI checksums. Pure S3 API otherwise. | **weekend-move** (swap endpoint + creds for B2/S3/MinIO; the checksum env var is harmless elsewhere) |
| **Resend** (email) | `src/KiriChess.Server/Infrastructure/Email/ResendEmailSender.cs` — one `POST emails` with `{from,to,subject,html,text}`, the most generic transactional shape there is; sits behind `LoggingEmailSender` / `QueuedEmailSender` / `EmailDispatchService`. DNS is Resend-on-SES specific: SPF `v=spf1 include:amazonses.com` and MX `feedback-smtp.eu-west-1.amazonses.com` (`docs/ops/PROVIDER-ACCOUNTS.md:91-92`). | **weekend-move** — code is an afternoon (one new sender class); deliverability reputation rebuild is weeks (I-10-4) |
| **Sentry** | `src/KiriChess.Server/Program.cs:88-105` — DSN-gated, not registered at all when unset; `:103` `o.UseOtlp()` routes traces over vendor-neutral OTLP, not Sentry's proprietary span path; `ui/sentry.{server,edge}.config.ts`, `ui/instrumentation*.ts`. `Sentry.AspNetCore` 6.8.0 (`KiriChess.Server.csproj:63`). | **weekend-move** — traces move to any OTLP backend untouched; only error capture needs a different SDK |
| **Better Stack** | `infra/betterstack/main.tf` — uptime monitors, a Prometheus scrape source, a `logtail_source` (`platform = "fly_io"`), a dashboard with 4 charts and 3 alerts whose **SQL is written against Better Stack's schema**; `fly.logshipper.toml` deploys `superfly/fly-log-shipper` (Vector) pointed at Better Stack ingest; `BetterStackAlertingDriftTests` + `MetricsContractTests` pin the metric names. | **month-move** — rewrite the Terraform and re-author the dashboard SQL for another vendor. Not in the serving path: losing it loses alerting + log retention, not the site. Cross-ref I-5. |
| **Stripe** | `src/KiriChess.Server/Account/StripeWebhookEndpoints.cs`, `Data/Entities/DonationReference.cs`, `ui/lib/donationLink.ts` — Stripe-hosted **payment links** (not embedded Elements/Checkout SDK) plus one webhook endpoint. No player-facing dependency; donations do not unlock play (standing position). | **weekend-move** — swap link + webhook for GitHub Sponsors / Ko-fi / Liberapay. Cross-ref $1. |

### Findings

#### I-10-1 — No "move off Fly / move off Neon" runbook; the exit is mechanical but unwritten and unrehearsed
- Severity: Medium
- Where: `docs/ops/DEPLOYMENT.md` (no migration section anywhere); `docker-compose.prod.yml` is the
  latent target but surfaces only in §6 "Running the production images locally"
  (`docs/ops/DEPLOYMENT.md:477-488`)
- Impact: The packet's question — "leave in a weekend?" — is *yes on the merits*, but there is no
  written procedure, so a provider eviction (billing lapse, ToS action, account lockout, or a price
  change the hobby budget cannot absorb) turns a weekend migration into an improvisation performed
  while the site is down. **O-5** covers the *stranger-forks-the-repo* documentation gap; **I-4 /
  ROADMAP 10c.1** covers *backup out of Neon*; neither is the maintainer's own "the front-door
  provider just evicted me, here are the eight steps" note. The pieces to write it from already
  exist and are cited in this report.
- Fix: One page in `DEPLOYMENT.md` (or fold into B-1): target is `docker-compose.prod.yml` on any
  VPS or container host; steps are build + push the two images, `scripts/offsite-backup.sh restore`
  into the new Postgres, set the ~6 must-have secrets (`Jwt__Key`, `ConnectionStrings__Postgres`,
  `Frontend__BaseUrl`, `NEXT_PUBLIC_API_BASE`, `NEXT_PUBLIC_SITE_URL`, `Cors__AllowedOrigins__0` —
  same list as O-5-3), repoint DNS. Name what has to be re-expressed on the new host: the private
  metrics port (`fly.backend.toml:93-95`), the migration gate (`release_command` becomes
  `--profile migrate run --rm migrate`), and — if email also moves — the DMARC re-observation
  window.

#### I-10-2 — The off-Neon dump that every portability claim rests on has never completed a run
- Severity: Medium
- Where: `scripts/offsite-backup.sh`, `.github/workflows/offsite-backup.yml`,
  `docs/ops/DEPLOYMENT.md:472-475` ("Two dispatches on 2026-08-20 never got a runner … Parked
  until a green run")
- Impact: Largely **I-4 / ROADMAP 10c.1** territory — cross-reference, not a fresh defect. The
  portability-specific point: "you can leave Neon in a weekend" is only true if the R2 dump is
  restorable, and that path has been dispatched twice and never executed. Until one green run, an
  urgent Neon exit means restoring from a script nobody has watched finish. Neon PITR (proven,
  `docs/ops/DEPLOYMENT.md:402-403`) does not help here — it restores *into Neon*, the account that
  might be the thing that failed.
- Fix: (owner) `DEPLOYMENT.md:730` says dispatch after 1 Sep once private-repo Actions minutes
  reset — that is now imminent. Dispatch **Off-site backup**, read the log, confirm assertion 3
  (schema) and assertion 4 (replay) pass. Tracked by I-4.

#### I-10-3 — The Turnstile siteverify Worker is running infrastructure with no source or config in the repo
- Severity: Low (trigger: rebuilding the front door on a new Cloudflare account or a non-Cloudflare
  host, or the Worker being deleted)
- Where: `Dockerfile.frontend:29-34` (`NEXT_PUBLIC_TURNSTILE_WORKER_URL`, and the comment noting the
  widget's private secret "lives solely as a Cloudflare Worker secret"), `ui/next.config.ts:48-62`,
  `ui/lib/turnstile.ts`; no `wrangler.toml`, `wrangler.jsonc`, or worker source anywhere in the
  tree
- Impact: Every other piece of running infrastructure is in a tracked file — `fly.*.toml`,
  `infra/betterstack/*.tf`, `docker-compose*.yml`, `fly.logshipper.toml`. This one Cloudflare
  Worker exists only as a deployed artifact; recreating it depends on memory of the last deploy,
  not on version control. Low because Turnstile fails open, can be disabled with one
  unset secret, and the backend `TurnstileVerifier` calls `siteverify` directly regardless of the
  Worker — but it is the single component of the live front door with no file behind it, which is
  exactly the thing a portability audit exists to surface.
- Fix: Commit the Worker source and its `wrangler` config under `infra/turnstile-worker/`, or add a
  paragraph to `DEPLOYMENT.md` §9.1 recording how it was created, what its env holds, and the exact
  `siteverify` behaviour it implements, so it can be rebuilt deterministically. Cross-ref I-2 (IaC).

#### I-10-4 — Email portability is code-cheap and reputation-expensive, and DEPLOYMENT.md §8 does not say so
- Severity: Low (trigger: changing transactional-email providers)
- Where: `src/KiriChess.Server/Infrastructure/Email/ResendEmailSender.cs`;
  `docs/ops/DEPLOYMENT.md:544-592` (§8, reads as a one-time setup); `docs/ops/PROVIDER-ACCOUNTS.md:87-99`
- Impact: Swapping `ResendEmailSender` for an SES / Postmark / Mailgun / SMTP sender is one class
  behind the existing `EmailDispatchService` abstraction — genuinely an afternoon. But the DNS side
  (SPF, DKIM, and the DMARC `p=none` observation window that `DEPLOYMENT.md:568-572` itself says to
  run for "a couple of weeks" before tightening) is provider-shaped and rebuilds sending reputation
  from zero. A reader of §8 could reasonably conclude email is as portable as the code path.
- Fix: One line in `DEPLOYMENT.md` §8: a provider change is a DNS + deliverability exercise
  measured in weeks, not a code change, and the SPF/DKIM/DMARC pattern in §8.1–8.2 is what has to
  be re-derived for the next provider.

### Already covered

- **Self-host / fork documentation gaps** (README points at the dev compose stack, not
  `docker-compose.prod.yml`; `NEXT_PUBLIC_*` baked at image-build time; migrations behind a
  profile; `.env.example` does not separate must-set from optional) — **O-5** (O-5-1..O-5-4).
  I-10-1 is the maintainer's *own* exit runbook, which O-5 does not cover.
- **`docker-compose.yml` runs Postgres 15, `docker-compose.prod.yml` runs 16** —
  `docs/audits/C.md (section INF-1)` INF-1-1.
- **Self-host on `docker-compose.prod.yml` loses replay OG metadata (`API_BASE_INTERNAL` unset)** —
  INF-1-1..INF-1-5 (`INF-1-5`).
- **`permissions:` / `timeout-minutes` / `concurrency` missing on CI workflows; stale
  `setup-dotnet` pin** — INF-1-2, INF-1-3, INF-1-4.
- **Backup out of Neon never run; provider-account-deletion scenario; compose `pg_dump` is not
  production** — I-4 packet (unwritten as of this date) and ROADMAP 10c.1. I-10-2 restates only the
  portability consequence.
- **`strategy = "immediate"`, single always-on machine, deploy interrupts live games, Redis
  unprovisioned** — standing positions, `DEPLOYMENT.md` §7/§12, `docs/audits/I.md (I-1)` §4.3.
- **Better Stack dashboard SQL unverified against a live scrape; Sentry PII off; `/metrics` gate** —
  `docs/audits/I.md`, `infra/betterstack/README.md`.
- **Cost cliffs / free-tier traps / lock-in economics** — **I-9** (Cost). Not yet written as of
  2026-08-30; this packet is coupling and move-cost, not the bill.
- **`docs/audits/I.md (I-1)`** already states no read-replica / partitioning is needed (AS-3)
  and that Neon branch-based scaling is the right level — no portability finding there to repeat.

### Action list

1. Write the "Moving off Fly / Neon / Cloudflare" runbook (I-10-1) — one page in
   `DEPLOYMENT.md`, assembled from the file:line pointers in this report. Not launch-gated.
2. **Owner.** Dispatch **Off-site backup** once September Actions minutes are available and confirm
   a green run (I-10-2). This is the single highest-value portability action and it is already
   owned by I-4 / ROADMAP 10c.1.
3. Put the Turnstile siteverify Worker under version control
   (`infra/turnstile-worker/` with `wrangler` config) or document its exact construction in
   `DEPLOYMENT.md` §9.1 (I-10-3). Owner supplies the current Worker source and it gets committed.
4. One clarifying line in `DEPLOYMENT.md` §8 that an email-provider change is a
   weeks-long DNS/reputation exercise, not a code swap (I-10-4).
5. No standing-position reversals. No production code change is required by any item above.

---

## I-11 — Dependencies
Date: 2026-08-30
Packet: docs/audits/INDEX.md

### Verdict

The dependency-hygiene machinery is good for a one-person project: a NuGet `--vulnerable` gate,
a wrapped `npm audit` gate with dated waivers, Trivy image scans with dated `exp:` waivers, weekly
Dependabot across four ecosystems with a 7-day supply-chain cooldown, SHA-pinned Actions, and
digest-pinned base images. As of today the NuGet tree is clean (`dotnet list --vulnerable` reports
nothing across all seven slnx projects) and `npm audit` in `ui/` and `e2e/` both report zero
advisories.

The problem is freshness, not architecture, and it converges on one package. `next` is pinned
**exact** at `16.3.0`; `16.3.3` shipped on 2026-08-25 with fixes for **two Critical unauthenticated
RCE advisories** (GHSA-p293-qw3h-jr36, GHSA-2xp9-vwfh-vxw4). `npm audit` does not flag it yet only
because GitHub's advisory version-ranges have not propagated to the audit DB — and when they do, the
`npm-audit-gate.mjs` ALLOWLIST still carries a stale `next` entry (written before the 15→16 bump,
expiring 2026-11-07) that waives **by package name**, so CI will silently wave the incoming Critical
through. The Trivy waiver list has the same staleness: several entries justify themselves by "the
fix is the next@16.3.0 major bump" when the file was committed 2026-08-17, five days *after* that
bump already landed. Everything else is routine drift (a handful of NuGet minor bumps Dependabot
will pick up; a few frontend majors with no manual-bump tracking; `e2e/` outside Dependabot).

### Findings

#### I-11-1 — `next` is pinned exact at 16.3.0; 16.3.3 patches two Critical RCE advisories
- Severity: Critical
- Where: `ui/package.json:22` (`"next": "16.3.0"`, exact — no `^`), `ui/package-lock.json` (`node_modules/next` = 16.3.0); frontend shipped as an internet-facing Node server (`Dockerfile.frontend:57` `CMD ["node", "server.js"]`)
- Impact: `next@16.3.3` (published 2026-08-25) contains security fixes for GHSA-p293-qw3h-jr36
  ("Unauthenticated Remote Code Execution on windows-hosted servers", Critical) and
  GHSA-2xp9-vwfh-vxw4 ("Unauthenticated Remote Code Execution in Image Optimization API when AVIF
  files are used", Critical). Production runs Linux (`node:20-alpine`), which blunts the
  Windows-host advisory — but every contributor runs `next dev` on Windows 11 (repo env), and the
  Image-Optimization advisory is platform-independent: `/_next/image` is enabled by default and
  `ui/next.config.ts` sets no `images` restrictions. `npm audit` currently returns 0 because the
  advisory's affected-version data has not yet reached the npm/OSV feeds (confirmed: an OSV query
  for `next` 16.3.0 returns nothing today), so neither the CI gate nor Dependabot has reacted.
- Fix: Bump `ui/package.json` to `next@16.3.3` (a patch within the pinned minor — no regression pass
  beyond the normal `npm run build` + E2E), regenerate `ui/package-lock.json` with npm 10.8.2
  (CONTRIBUTING.md: a newer npm rewrites the lockfile differently), and re-run `cd ui && npm run build && cd ../e2e && npm test`. Do not
  wait for Dependabot's 7-day cooldown on this one.

#### I-11-2 — `npm-audit-gate.mjs` waives by package name, so the pending `next` Critical will be auto-waived
- Severity: High
- Where: `.github/scripts/npm-audit-gate.mjs:14-31` (ALLOWLIST keyed by package name), `:62-66` (`ALLOWLIST[name]` lookup against `npm audit --json`'s per-package `vulnerabilities` keys); `next` entry expires `2026-11-07`
- Impact: The gate keys waivers on the top-level package name, not on a CVE / GHSA id. The
  `next`, `postcss`, and `sharp` entries therefore waive *any* future high/critical advisory on
  those three packages until 2026-11-07. The moment GHSA-2xp9-vwfh-vxw4 / GHSA-p293-qw3h-jr36 land
  in the audit DB, CI will print `Waived: next (critical)` and stay green on a known unauthenticated
  RCE. `.trivyignore` does not share this flaw (keyed by CVE) but shares the staleness — see I-11-3.
- Fix: After I-11-1 lands, delete the `next`, `postcss`, and `sharp` ALLOWLIST entries entirely
  (they are already inert — see I-11-3). Longer term, key the ALLOWLIST on advisory id (npm audit's
  `vuln.via[].url` / `source`) rather than package name, so a waiver cannot silently absorb an
  unrelated future advisory on the same package.

#### I-11-3 — The `next`/`postcss`/`sharp` npm-audit and Trivy waivers are stale (fix already applied, `npm audit` now clean)
- Severity: Medium
- Where: `.github/scripts/npm-audit-gate.mjs:19-30`; `.trivyignore:11-23`
- Impact: Both waiver sets justify themselves by "the fix is `next@16.3.0`, a semver-major bump,
  out of scope for the automation gate." That bump landed on 2026-08-12 (`ui/package.json` PR #11,
  15.5.21 → 16.3.0). `npm audit` in `ui/` now reports **0 vulnerabilities** at every severity, and
  the top-level `postcss` (8.5.26), `sharp` (0.35.3) and `cross-spawn` (7.0.6) are all well past
  their fixed versions. The npm-audit-gate entries are dead weight that will never fire again on the
  advisories they name — but they are not harmless, because of I-11-2. The `.trivyignore` entries
  (lines 15-23) may still be live (Trivy filesystem-scans `next/dist/compiled/{tar,cross-spawn,postcss}`
  vendored copies that `npm audit`'s dependency-tree view never sees), but their stated remedy is
  already done.
- Fix: Remove the three `npm-audit-gate.mjs` ALLOWLIST entries. For `.trivyignore`, run a real
  scan against a fresh `Dockerfile.frontend` build (`trivy image --severity CRITICAL,HIGH
  --ignore-unfixed <frontend-image>` with the ignore file removed): drop any CVE Trivy no longer
  reports, and for any it still reports, rewrite the justification to name the actual fix
  (`next@16.3.3` if that refreshes the vendored copies, or "no upstream fix, vendored in next" if
  not) with a fresh `exp:` date.

#### I-11-4 — `.trivyignore` header claims a fix that was already applied when the file was written
- Severity: Medium
- Where: `.trivyignore:12-14` ("the fix is the same next@16.3.0 major bump"), committed `b6fbe0c` on 2026-08-17; `next` reached 16.3.0 on 2026-08-12
- Impact: Whoever authored the waiver on 2026-08-17 saw Trivy still flagging these CVEs on a tree
  that was *already* at `next@16.3.0`, and wrote down a remedy that demonstrably had not worked.
  A waiver whose justification is self-contradictory cannot be re-decided at expiry (2026-11-07) by
  anyone reading only the file — they will re-apply a fix that is already in place and re-date the
  entry for another 90 days. This is the exact "waiver that is re-decided forever for no reason"
  the file's own header warns against.
- Fix: Same as I-11-3's Trivy half — one real scan, then either delete the entries or write a
  justification that matches what Trivy actually reports against the current image.

#### I-11-5 — Trivy waiver `CVE-2026-45447` expires 2026-09-01 (in 2 days)
- Severity: Low (trigger: the E2E job's Trivy step fails on/after 2026-09-01 if the base-image digest has not moved)
- Where: `.trivyignore:41-45` (Alpine `libcrypto3`/`libssl3` inside the pinned `node:20-alpine` digest, `exp:2026-09-01`)
- Impact: This is the waiver-expiry mechanism working as designed — a deliberately short fuse on an
  OS-package CVE that should be picked up by Dependabot's `docker` ecosystem entry within days. But if
  Dependabot has not yet opened a digest-bump PR for `Dockerfile.frontend` / `Dockerfile.backend`
  by 2026-09-01, the `e2e` job (a blocking CI job) starts failing on every PR until someone either
  merges the digest bump or re-dates the line.
- Fix: Check whether Dependabot has a pending `node:20-alpine` digest bump; merge it, or (if the
  upstream Alpine rebuild genuinely has not happened) re-date the entry with a one-line note that
  3.5.7-r0 still has not reached the base image.

#### I-11-6 — `e2e/package.json` is not covered by Dependabot
- Severity: Low
- Where: `.github/dependabot.yml:24-32` (npm ecosystem, `directory: "/ui"` only); `e2e/package.json:13-18` (`@playwright/test`, `@axe-core/playwright`, `typescript`, `@types/node`)
- Impact: The E2E toolchain gets no automated bump PRs and no supply-chain-cooldown treatment.
  `npm outdated` in `e2e/` today shows `@axe-core/playwright` 4.12.1 → 4.13.0 and `@types/node`
  pinned a whole major behind `ui/` (20 vs 26). Low stakes (dev/CI-only, `npm audit` clean), but
  the packet asks about "e2e outdated" and this is why it drifts silently.
- Fix: Add a third npm block to `.github/dependabot.yml` with `directory: "/e2e"`, same `cooldown`
  and `groups` shape as the `/ui` block.

#### I-11-7 — `src/Checker/Checker.csproj` is outside `KiriChess.slnx`, so the CI vulnerable-package gate skips it
- Severity: Low
- Where: `KiriChess.slnx` (no `Checker` project entry); `src/Checker/Checker.csproj:11` (`Gera.Chess` 1.2.0); `.github/workflows/ci.yml:29` (`dotnet list KiriChess.slnx package --vulnerable`)
- Impact: The backend CI vulnerability gate is scoped to the solution file, which omits `Checker`
  (a dev-only tool, last touched 2026-07-21). Its one dependency is currently the latest available,
  so there is no live exposure, but a future CVE in anything `Checker` pulls would not fail CI.
  Dependabot's nuget scan (`directory: "/"`) does discover loose `.csproj` files, so bump PRs would
  still appear — the gap is only the CI fail-fast.
- Fix: Either add `src/Checker/Checker.csproj` to `KiriChess.slnx` (it is a real project in `src/`),
  or, if `Checker` is dead, delete it (O-4 / public-repo hygiene territory).

#### I-11-8 — Frontend major-version dependencies drift with no manual-bump tracking
- Severity: Low (trigger: a needed fix or a peer-dep conflict lands only in a major already skipped)
- Where: `ui/package.json` — `react-chessboard` 4.7.3 (latest 5.12.1), `typescript` `^5` (latest 7.0.2), `eslint` `^9` (latest 10.9.1), `eslint-config-next` `15.5.21` (latest 16.3.3, a full major behind `next` 16)
- Impact: Dependabot's `npm-minor-patch` group only proposes minor/patch, so majors accumulate
  invisibly. `eslint-config-next` behind `next` is already flagged as **FE-1-6** in the deep audit;
  the others are the same shape. None is a vulnerability today; the risk is that a security patch
  for one of these eventually ships only in a major line the project has silently fallen behind on,
  turning a routine bump into a migration under time pressure.
- Fix: No action beyond awareness. Optionally add a quarterly "review skipped majors" line to the
  B-4 calendar. Bump `eslint-config-next` to the `16.x` line alongside I-11-1 (per FE-1-6).

### Already covered

- `eslint-config-next` a major behind `next`: deep audit **FE-1-6** (`docs/audits/C.md (section FE-1)`). I-11-8 cross-references, does not re-report.
- `sharp` used by `ui/scripts/gen-icons.mjs` without being a declared dependency: deep audit **FE-1-5** (`docs/audits/C.md (section FE-1)`). Not a vuln/staleness issue; out of scope here.
- Transitive copyleft / license compatibility of the dependency trees (`@sentry/cli` FSL, `Xunit.SkippableFact` MS-PL, `@img/sharp` LGPL, `WebPush` unproven license): **O-6** (`docs/audits/O.md`) and **L-10**. This packet is vulns and staleness only, per the INDEX note — no overlap.
- Absence of a committed SBOM artifact: **O-6** "Already covered" (`docs/audits/O.md:80`) via **L-10-6**.
- Workflow `permissions:` scope and missing `timeout-minutes` on `ci.yml` jobs: deep audit **INF-1-2 / INF-1-3** (`docs/audits/C.md (section INF-1)`). Supply-chain adjacent but not this packet.
- SHA-pinning of GitHub Actions and digest-pinning of base images: **INF-1** (`docs/audits/C.md (section INF-1)`) confirms it is done consistently; I-11 relies on it.
- Readiness **DEPS** (`docs/audits/readiness/DEPS.md`): does not exist; the `docs/audits/readiness/` directory is empty. This packet did the dependency audit directly, as the packet brief instructed.

### Action list

1. Bump `next` to `16.3.3` in `ui/package.json`, regenerate the
   lockfile with npm 10.8.2, run `npm run build` + `e2e` tests. (I-11-1)
2. Delete the `next` / `postcss` / `sharp` entries from
   `.github/scripts/npm-audit-gate.mjs`'s ALLOWLIST — they are inert and dangerous (I-11-2, I-11-3).
   Bump `eslint-config-next` to `16.x` (I-11-8 / FE-1-6).
3. **Maintainer.** Run one real `trivy image` scan against a fresh `Dockerfile.frontend` build with
   `.trivyignore` disabled; delete entries Trivy no longer reports, rewrite the justification +
   `exp:` date for any it still does. (I-11-3, I-11-4)
4. **Maintainer, before 2026-09-01.** Merge the pending `node:20-alpine` digest bump, or re-date
   `.trivyignore:45`. (I-11-5)
5. Add a `/e2e` npm block to `.github/dependabot.yml`. (I-11-6)
6. **Maintainer.** Decide `src/Checker`'s fate — add to `KiriChess.slnx` or delete. (I-11-7)
7. **Maintainer, later.** Consider re-keying `npm-audit-gate.mjs` waivers on advisory id rather
   than package name. (I-11-2)
8. **Routine.** The clean NuGet minor bumps (`Sentry.AspNetCore` 6.8→6.9, `StackExchange.Redis`
   3.1.13→3.1.31, `Testcontainers.*` 4.13→4.14, `Stripe.net` 52.3→52.4) need no action — Dependabot's
   weekly nuget run covers them.

