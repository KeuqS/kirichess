# Testing rules

Four distinct layers exist in this repo. Know which one a change needs - adding the wrong kind (or none) is the most common miss `pr-reviewer` flags.

## Backend unit/integration - `tests/KiriChess.Server.Tests/`

Reuse existing fixtures rather than inventing a new setup pattern:

- [RedisFixture.cs](../../tests/KiriChess.Server.Tests/RedisFixture.cs) - real Redis via Testcontainers for anything whose behavior depends on actual Redis semantics (rate limiting, distributed live-game state, caching). **Don't mock Redis where the real thing's behavior matters** - a test that mocks around this fixture for correctness-sensitive logic is a false-confidence pass, not a real one.
- [DockerAvailability.cs](../../tests/KiriChess.Server.Tests/DockerAvailability.cs) - gates Testcontainers-backed tests; CI's `ubuntu-latest` runner has Docker available by default specifically so these run for real instead of silently skipping. If a test using this starts skipping locally, that's expected without Docker Desktop running - it should never silently skip in CI.
- [FakeGameClock.cs](../../tests/KiriChess.Server.Tests/FakeGameClock.cs) - deterministic control of `IGameClock`'s wall and monotonic readings (see [realtime.md](realtime.md)) for anything clock-sensitive. Don't reach for `Thread.Sleep`/`Task.Delay` to test timing behavior.
- [CapturingLoggerProvider.cs](../../tests/KiriChess.Server.Tests/CapturingLoggerProvider.cs) - asserting on log output (e.g. the retention sweep's structured log line).

New backend code should have a same-named or clearly-adjacent test file. Existing coverage worth mirroring by area: auth lifecycle (`AuthLifecycleTests.cs`, `TokenServiceTests.cs`, `TwoFactorTests.cs`), matchmaking (`MatchQueueTests.cs`, `PairingNotificationTests.cs`), live-game durability (`DistributedLiveStateTests.cs`, `TwoInstanceGameTests.cs`, `GameSessionRestoreTests.cs`), ratings (`RatingCalculatorTests.cs`, `Glicko2Tests.cs`), moderation (`ModerationEndpointTests.cs`, `SanctionPolicyTests.cs`), retention (`RetentionSweepTests.cs`, `RetentionScheduleTests.cs`).

## Frontend unit - `ui/**/*.test.ts(x)`

Run via `npm test` (Vitest, per `vitest.config.mts`). Anything in `ui/lib/` encoding variant rules client-side (`costPreview.ts`, `botClock.ts`) needs coverage here - nothing else checks that the client-side economy mirror still agrees with the engine (see [engine.md](engine.md) on why that mirror exists and how it can drift).

## Browser E2E - `e2e/` (Playwright)

Runs against the real production Docker Compose images (`docker-compose.prod.yml` + `docker-compose.e2e.yml`) - a real browser against a real API and a real Postgres, not a mocked stack. Existing specs:

- `critical-path.spec.ts` - the core signup-through-game flow.
- `auth.spec.ts` - login/registration paths.
- `accessibility.spec.ts` - axe-core WCAG 2.0/2.1 A+AA scan across every anonymous-reachable page (content pages, both mail-link-only forms with a dummy token, the 404 catch-all) plus the signed-in custom-game builder. **Deliberately excludes the live game board** (`game/[id]`, `watch/[id]`, `replay/[id]`) - axe can't evaluate "can a screen-reader user actually play"; that's covered by the scripted manual pass in `docs/plans/a11y/NVDA-PASS.md` instead. Also excludes the staff-403 render inside `/moderation`/`/analytics`, since that state requires an authenticated non-admin session and so isn't anonymous-reachable in the first place.
- `seo.spec.ts` - metadata/SEO checks on public pages.

A UI change needs an E2E spec when it changes a cross-page flow or something a component test can't see (real auth, real navigation, real hub connection). A change scoped to one component's rendering or props is a Vitest test, not a new E2E spec.

## Load testing - `loadtest/` (SignalR-driving harness)

Not a toy script - a purpose-built .NET SignalR client that plays real games through matchmaking against a real containerized backend, documented in [loadtest/README.md](../../loadtest/README.md). Reach for it when the question is **throughput or capacity** (can N concurrent games hold up, what does CPU/latency look like at that load), never for correctness - correctness is the job of the layers above. Before hand-deriving a Prometheus metric name or a throughput formula, read the README's own gotchas: metric-name mangling was discovered by live scraping, not guessed, and the planned-throughput formula accounts for chess's strictly-alternating turns (one ply per game per think-interval, not two).

## Game-mode simulator - `simulator/` (engine self-play harness)

Not CI, not capacity. A console app that plays the real engine against itself with Stockfish (or random legal) and the live-bot clock policy, documented in [simulator/README.md](../../simulator/README.md). Reach for it when the question is **how a pool actually plays** (flag rate, duration, think seconds per move vs the `/rules` thinking-budget table). Unit tests live in `tests/KiriChess.Simulator.Tests/` and pin the C# port of `ui/lib/botClock.ts`; they do not need a Stockfish binary.

## Contract/drift tests - the guard for generated files

These exist specifically to catch the class of bug [rules/project.md](project.md) warns about (a hand-written file drifting from the generated source it mirrors). Breaking one means **regenerate the source**, not delete or loosen the test:

- `OpenApiContractTests.cs` - the committed `docs/api/openapi.json` matches what the C# endpoints actually produce.
- `MetricsContractTests.cs` - the metric names/shapes documented for `loadtest/` and Better Stack dashboards match what `KiriChessMetrics` actually emits.
- `BetterStackAlertingDriftTests.cs` - 10c.5's Terraform still declares the edge monitors and heartbeats, and the two probe scripts still assert a clock move and Neon storage.
- `LegalDocumentDriftTests.cs` - `docs/legal/*.md` content matches what the code (versioned terms, consent flow) actually implements.
- `CiWorkflowDriftTests.cs` - claimed GitHub Actions workflows still exist and still fire on the triggers the roadmap says they do (Phase 10c.2). A deleted workflow is how 9a.2's tick went stale; this is the thing that would have caught it. Breaking one means restore the workflow, or remove the claim in the same commit.

CI (`.github/workflows/ci.yml`) also gates `npm run gen:rules:check` (engine config vs. `docs/RULES.md`, see [engine.md](engine.md)) and `npm run gen:api:check` (OpenAPI vs. `ui/lib/api.generated.ts`) at the frontend-job level - these are the same drift-guard pattern applied outside the C# test suite.

## Verify

```bash
dotnet test KiriChess.slnx
cd ui && npm run lint && npx tsc --noEmit && npm test && npm run build
cd ui && npm run gen:api:check && npm run gen:rules:check
cd e2e && npm test   # only when the change touches a cross-page flow
```
