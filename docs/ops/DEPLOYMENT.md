# Deployment

Production runtime for KiriChess: Fly.io (compute) + Neon (Postgres) + Cloudflare (DNS, and a
proxy in front of the **site** only).

## 1. Topology

```
Client → kirichess.com / www     (Cloudflare orange-cloud) → kirichess-ui   (Next.js, port 3000)
Client → api.kirichess.com       (DNS only, grey-cloud)    → kirichess-api  (ASP.NET Core, port 8080)
                                                                  │
                                                                  └── Neon (managed Postgres)
```

Recorded 2026-09-04 from the Cloudflare DNS table for `kirichess.com`, after the owner set `api`
to DNS only. I-2-1 is inert on that choice: the hub budget and rate limiters see the player, not a
Cloudflare PoP. `https://api.kirichess.com/health/ready` returned `{"status":"ready","redis":"disabled"}`
the same sitting. Re-checked 2026-10-04 from this repo's machine: `Resolve-DnsName api.kirichess.com
-Server 1.1.1.1` answers `66.241.124.71` and `2a09:8280:1::167:f319:0` (Fly). `kirichess.com` answers
Cloudflare anycast (`104.21.89.190`, `172.67.191.8`). One hop, so `ForwardLimit` 1 leaves
`RemoteIpAddress` as the player (I-7-5).

| Hostname | Role | Proxy | How to re-check |
| --- | --- | --- | --- |
| `kirichess.com` | Site (Next.js) | **Proxied** (orange). A + AAAA | `Resolve-DnsName kirichess.com -Type A -Server 1.1.1.1` answers Cloudflare anycast, not the Fly UI IP |
| `www.kirichess.com` | Site alias | **Proxied**. A + AAAA, same target as the apex | same, for `www` |
| `api.kirichess.com` | API, SignalR hubs, Google redirect | **DNS only** (grey). A + AAAA | `Resolve-DnsName api.kirichess.com -Type A -Server 1.1.1.1` answers the Fly API IP. Browser TLS terminates on Fly |
| Mail (`MX` / `TXT` / DKIM / SPF / DMARC) | Resend | **DNS only** (required) | [PROVIDER-ACCOUNTS.md](PROVIDER-ACCOUNTS.md) §2 |

Cloudflare SSL/TLS encryption mode is zone-wide, not per record, and was **not** read this sitting.
Read it under SSL/TLS → Overview if a later change (Flexible vs Full vs Full strict) needs to be
recorded. Grey `api` does not use that setting: the browser talks HTTPS to Fly directly.

- **Cloudflare** is DNS for the zone, orange-cloud CDN/WAF/DDoS for the site, and R2 for off-site
  Postgres dumps after 10c.1 (§ 5.3). It is not in the path of `/hubs/game`. Better Stack's
  `api.kirichess.com/health/ready` monitor is therefore an origin check, same class as
  `kirichess-api.fly.dev`, not an edge-vs-origin split.
- **Fly terminates TLS** for both apps (`force_https = true` in `fly.backend.toml` /
  `fly.ui.toml`) and proxies WebSocket upgrades straight through to `internal_port`, there is no
  self-hosted Caddy/nginx/Traefik in this stack.
- **Neon** is the Postgres provider, see § 5 for why.
- `kirichess-api.fly.dev` does not serve the API. `OriginLock:BlockedHosts` 404s that Host except
  `/health` and `/metrics` (I-2-2). Better Stack's origin monitor and Prometheus scrape stay on the
  Fly hostname on purpose. Player traffic uses `api.kirichess.com`. This is not a Cloudflare secret
  header: `api` is DNS-only, so there is no edge that could stamp one. `ForwardLimit` stays 1.
- Non-probe traffic is also limited to `OriginLock:AllowedHosts` (`api.kirichess.com` in
  production, `kirichess-api-staging.fly.dev` on staging). Any other Host is 400 (I-7-7). ASP.NET
  `AllowedHosts` stays `*`: Fly's metrics scrape arrives on the machine's 6PN address, which cannot
  be listed, and a framework allow-list would 400 that scrape. `/health` and `/metrics` are exempt
  from `OriginHostLock`, so the scrape never reaches the allow-list.
- **`kirichess-ui`'s "scales to zero when idle" (`fly.ui.toml`) never actually happens in
  production (I-2-12).** `infra/betterstack/main.tf`'s `edge_site` monitor polls
  `https://kirichess.com` every 180 seconds from two regions, which restarts the machine before it
  idles out. `min_machines_running = 0` is the fallback for a monitoring outage, not the operating
  state, and the cold-start latency the setting was chosen to trade away is never observed for the
  same reason: the monitor keeping the machine warm is also the thing that would notice a cold start
  if there ever was one.

## 2. First-time setup

Create both apps without deploying yet:

```bash
fly launch --no-deploy --config fly.backend.toml
fly launch --no-deploy --config fly.ui.toml
```

Then set the backend's secrets (never committed, see `.env.example` for the full variable list):

```bash
fly secrets set --app kirichess-api \
  ConnectionStrings__Postgres="<neon pooled connection string>" \
  Jwt__Key="$(openssl rand -base64 48)" \
  Cors__AllowedOrigins__0="https://kirichess.example"
```

Notes:

- Npgsql needs `SSL Mode=VerifyFull` on Neon's connection string. That checks the certificate
  chain and the host name on the hop from Fly to Neon (I-2-8). Confirmed 2026-10-04: both
  `kirichess-api-staging` and `kirichess-api` came back `/health/ready` after the secret was set
  to that mode on the pooled endpoint.
- **Serving role (C-12-4).** `kirichess_app` exists on the `staging` and `production` branches. It
  can read and write rows and cannot `CREATE` in `public` (checked 2026-10-04). `neondb_owner`
  stays the migration role, in `ConnectionStrings__Migrator`. `ConnectionStrings__Postgres` on
  `kirichess-api` and `kirichess-api-staging` is the `kirichess_app` pooled string
  (`SSL Mode=VerifyFull`), switched 2026-10-06 after the `ServingConnection` build was the one Fly
  runs. Both apps answered `/health/ready` 200 after the restart. `--migrate` reads `Migrator`
  when that secret is set, so a later release still migrates as the owner.
- Use Neon's **pooled** endpoint (the `-pooler` host), not the direct one, this is a long-lived
  app holding a connection pool of its own, not a serverless function making one-shot connections.
- `Jwt__Key` must be at least 32 bytes; `openssl rand -base64 48` comfortably clears that.

The UI app needs no secrets, `NEXT_PUBLIC_API_BASE` is baked in at build time via
`fly.ui.toml`'s `[build.args]`, not set as a runtime secret.

## 2.1 Staging (9c.2)

A second, cheaper pair of apps that takes the first mistake instead of production. Same topology,
same migration gate, same CD pipeline, different Fly apps and a different Neon branch.

**Provisioned already, and reproduced here in case it's ever torn down and rebuilt:**

```bash
fly launch --no-deploy --config fly.backend.staging.toml --name kirichess-api-staging --org personal --region ams --yes
fly launch --no-deploy --config fly.ui.staging.toml --name kirichess-ui-staging --org personal --region ams --yes
npx neonctl branches create --project-id plain-salad-47730828 --name staging
```

`flyctl launch` rewrites the `fly.*.staging.toml` it's given into its own formatting (drops
comments, `[metrics]` → `[[metrics]]`); the committed files have the explanatory comments restored
on top of whatever it produces. No custom domain is provisioned, the default `*.fly.dev` hostnames
are enough for a staging environment.

Three of the four backend secrets are set the same way as production (§ 2), and already done:

```bash
fly secrets set --app kirichess-api-staging \
  Jwt__Key="$(openssl rand -base64 48)" \
  Cors__AllowedOrigins__0="https://kirichess-ui-staging.fly.dev" \
  Frontend__BaseUrl="https://kirichess-ui-staging.fly.dev"
```

**`ConnectionStrings__Postgres` is the one secret the owner sets in a local terminal, every time.**
It is a live database password. `NEON_API_KEY` is scoped to Neon's
management API (branch create/delete), not to reading a branch's role password. Run the command
below in that terminal; it never prints the password, it goes straight into the Fly secret:

**`--role-name` and `--database-name` are not optional here**, though `neonctl`'s help presents them
that way. This project carries `neondb_owner`, `kirichess_app`, and the `authenticator` /
`anonymous` / `authenticated` trio, and two databases (`kirichess`, and Neon's empty default
`neondb`). `neonctl` refuses to guess when there is more than one of either. Omitting them fails
with `Multiple roles found for the branch` before it ever reaches the parse. The application's
tables live in `kirichess`. The long-lived process connects as `kirichess_app`. `neondb_owner` is
only `ConnectionStrings__Migrator`.

**bash** (Git Bash on Windows, not PowerShell, see the note below):

```bash
uri="$(NEON_API_KEY=$(grep NEON_API_KEY .env.neon | cut -d= -f2) npx neonctl connection-string staging --project-id plain-salad-47730828 --role-name kirichess_app --database-name kirichess --pooled)"
rest="${uri#postgresql://}"; userpass="${rest%%@*}"; hostrest="${rest#*@}"
user="${userpass%%:*}"; pass="${userpass#*:}"
host="${hostrest%%/*}"; dbname="${hostrest#*/}"; dbname="${dbname%%\?*}"
fly secrets set --app kirichess-api-staging \
  ConnectionStrings__Postgres="Host=${host};Port=5432;Database=${dbname};Username=${user};Password=${pass};SSL Mode=VerifyFull"
```

**PowerShell.** Pasting the bash block into PowerShell does not half-work, it fails on every line and
then hands `fly` a literal `\`: `${var#pattern}` is not PowerShell syntax, and there is no inline
`VAR=x cmd` form. Note `$dbHost` rather than `$host`, which is a reserved automatic variable, and the
single quoted `NAME=VALUE` argument, without which PowerShell reads the connection string's
semicolons as statement separators:

```powershell
$env:NEON_API_KEY = ((Get-Content .env.neon | Where-Object { $_ -like 'NEON_API_KEY=*' }) -split '=',2)[1]
$uri = (npx neonctl connection-string staging --project-id plain-salad-47730828 --role-name kirichess_app --database-name kirichess --pooled | Select-Object -Last 1).Trim()
$m = [regex]::Match($uri, '^postgresql://([^:]+):([^@]+)@([^/]+)/([^?]+)')
if (-not $m.Success) { throw "Could not parse the Neon URI" }
$dbUser = $m.Groups[1].Value; $dbPass = $m.Groups[2].Value
$dbHost = $m.Groups[3].Value; $dbName = $m.Groups[4].Value
fly secrets set --app kirichess-api-staging "ConnectionStrings__Postgres=Host=$dbHost;Port=5432;Database=$dbName;Username=$dbUser;Password=$dbPass;SSL Mode=VerifyFull"
```

Neither version prints the password. **The key/value conversion is the whole point and is not
cosmetic:** Npgsql parses key/value pairs only and rejects a `postgresql://` URI at parse time, which
`CanConnectAsync` swallows into a bare `false`. Setting the secret to the raw URI produces a
`/health/ready` that answers 503 in about a millisecond and looks exactly like a dead database.

**Deliberately left unset on staging.** The app fails closed when these are empty, in every
environment. `Sentry__Dsn` is **Deployed** on production `kirichess-api` (reconfirmed 2026-10-04
with `fly secrets list --app kirichess-api`). Staging leaves it unset so staging errors stay out
of that project; give staging its own DSN only if that is ever wanted. Also left unset on staging:
`Email__ApiKey` (staging falls back to `LoggingEmailSender`, which is exactly right, nothing should
email a real inbox from a test environment), `Redis__Configuration` (staging is one machine, no
backplane needed), `Metrics__ScrapeToken` (set it the same way as
production, § 8, only if that endpoint needs to be reachable in staging too). `Authentication:Google:ClientId`
/ `ClientSecret` need their own OAuth client if Google sign-in is ever tested in staging, Google's
console ties a client to a fixed redirect URI, so production's client cannot be reused here.

**Sentry client SDK (P-7-4):** production's UI image bakes `NEXT_PUBLIC_SENTRY_DSN`
(`fly.ui.toml`). Staging leaves that build arg empty, so `withSentryConfig` and the browser SDK
stay off there. An empty DSN does not load `@sentry/nextjs`. The server `Sentry__Dsn` secret is a
separate toggle and does not pull the client SDK.

**Once `ConnectionStrings__Postgres` is set, staging deploys itself** on the next push to `main`
(or run the `Deploy` workflow manually with `environment: staging`). Production still only deploys
on a `v*` tag or a manual `environment: production` dispatch, unchanged from § 3.

Staging is intentionally cheap: both apps scale to zero when idle (`min_machines_running = 0`), the
opposite of production's always-on backend (see the comment in `fly.backend.staging.toml` for why
that divergence is safe here and wouldn't be in production).

## 3. Deploying

**Deploy remote:** [MichielGekiere/kirichess](https://github.com/MichielGekiere/kirichess)
(public source of truth). Do not push deploy tags to `kirichess-archive`.

Deploys are tag-triggered (`.github/workflows/deploy.yml`, on `v*` tags, or manual
`workflow_dispatch`):

```bash
git checkout main
git pull
git tag v1.2.3
git push origin v1.2.3
```

**Only commits reachable from `main` can deploy.** A `verify-main` job runs first and checks
`git merge-base --is-ancestor <tagged-sha> origin/main`; a tag pushed against `dev` (or any other
branch) fails that check and the workflow stops before `backend` or `ui` run. Same guard applies to
a manual `workflow_dispatch` run.

**One deploy token per app (I-3-3, 2026-10-04).** Each Fly app has its own `fly tokens create deploy -a <app>`
token, stored as a GitHub **environment** secret. `production` holds `FLY_API_TOKEN_API`
(`kirichess-api`) and `FLY_API_TOKEN_UI` (`kirichess-ui`). `staging` holds the same two names for
`kirichess-api-staging` and `kirichess-ui-staging`. The job's `environment:` key is what selects
which value a name resolves to, so a staging token cannot deploy production. Confirmed 2026-10-07:
`main` deploys with those secrets (staging backend and UI both green), the repository secret
`FLY_API_TOKEN` is deleted, and org tokens `github-actions-deploy`,
`github-actions-deploy-2026-10-04`, and `github-actions-deploy-2026-10-04-restore` are all revoked
(B-4-2). Annual rotation is now the four per-app tokens; see
[MAINTENANCE-CALENDAR.md](MAINTENANCE-CALENDAR.md) item 7.

Required reviewers on the `production` environment was refused by GitHub on 2026-10-04 (HTTP 422:
the then-private repository's plan did not include that protection rule). Re-check on the public
repo whether required reviewers are available; until then production deploys stay unattended.

What happens:

1. The backend job runs `flyctl deploy --config fly.backend.toml --remote-only --ha=false`. Fly's
   `release_command` (`dotnet KiriChess.Server.dll --migrate`) runs the EF Core migrations in a
   throwaway machine **before** the new release rolls out. A non-zero exit aborts the deploy, the
   old release keeps serving. `--ha=false` is load-bearing while Redis is off: Fly's default is an
   HA pair, and two machines with an in-process `GameRegistry` is silent split-brain.
2. The `ui` job runs after `backend` succeeds and deploys `fly.ui.toml`.

After the backend job, confirm the single-machine invariant:

```bash
fly machines list --app kirichess-api
```

Expect **exactly one** machine. Two machines with Redis still disabled is the silent split-brain
(moves never arrive, clocks disagree). Same check for `kirichess-api-staging` after a staging
deploy:

```bash
fly machines list --app kirichess-api-staging
```

**Rolling back:**

```bash
fly releases --app kirichess-api        # find the previous good image
fly deploy --app kirichess-api --image <previous-image-ref>
```

The same pattern applies to `kirichess-ui`. Rolling back the backend does **not** roll back a
migration that already ran, see § 3.1.

## 3.1 Rolling back a migration (9c.3)

`release_command` handles the easy failure: a migration that throws aborts the deploy and the old
release keeps serving. The case with no automatic answer is **the migration succeeded and the
application is broken**, and the answer to that one is deliberately *not* an EF `Down()` migration.

**The policy is roll-forward only.** Broken code with intact data → roll the application back with
the `fly deploy --image` above and **leave the schema at head**, subject to the rollback floor
below; EF ignores columns it does not know about, and every `NOT NULL` column in this chain carries
a database-level default, so an older release runs against a newer schema. Broken schema → write a
new forward migration and deploy it
normally. Damaged or destroyed **data** → restore a Neon branch from before the migration and
repoint (§ 5). A `Down()` is never run against production, for three reasons: the runtime image is
`dotnet/aspnet` with no SDK and `--migrate` only moves forward, so there is no way to invoke one
without a human editing `DesignTimeDbContextFactory` mid-incident; every `Down()` in this repo drops
tables or columns without consulting whether they have rows; and whether a given `Down()` runs *at
all* depends on production data, the head migration's fails outright on any database where two
accounts have been erased, which means it passes in a clean staging database and fails in the only
place it would ever be needed.

**The rollback floor, and it is the first thing to check.** **No `kirichess-api` image older than
`20260825204345_ReplaceAvatarKeyWithUpload` may be deployed against a head schema.** That migration
drops `AspNetUsers.AvatarKey`, and "EF ignores columns it does not know about" does not extend to a
column the older image's model *does* have: EF Core emits an explicit column list on every read, so
such an image issues `SELECT ... "AvatarKey" ... FROM "AspNetUsers"` and takes `42703 column does
not exist` on **every** query touching the users table, which is login, registration, token refresh,
profile and moderation. Rolling back below that image does not degrade a feature, it takes the site
down, and it does so as a result of the recovery procedure. If the image you want predates it, this
play is unavailable and the answer is a Neon restore (§ 5).

The same mechanism sets an **earlier floor at `20260819235137_DropChallengeRated`** (drops
`Challenges.Rated`), which breaks reads of `Challenges` and therefore the challenges feature, but
not login. **The floor moves forward on every `DropColumn`**; nothing else moves it.

**Then two further checks before rolling the app back**, both measured in the drill: rolling past
`AddDisplayNameModeration` (8a.4) breaks registration after the first new account, the older code
leaves `NormalizedDisplayName` at its `''` default and the unique index rejects the second row; and
rolling past `RenamePoolKeysToNeutralIds` leaves the older image reading pool keys it was not
compiled against, which is a data mismatch rather than an error. Both are repairable in place, and
both sit below the current floor anyway, so in practice the floor is the check that decides.
(`AddModeration` (8.5) used to be listed here as a third item. It is not a Play A concern:
Play A leaves the schema at head, so `UserSanctions` is not dropped and nobody is unbanned. Its cost
is a `Down()` cost, priced in MIGRATIONS.md § 2, and it applies only to a schema rollback this
policy never performs.)

The full per-migration cost table, the three recovery plays in detail, the rules that keep
app-rollback cheap, and the drill transcript are in [docs/ops/MIGRATIONS.md](MIGRATIONS.md).

## 4. WebSockets (6a.2)

Fly proxies WSS straight to `internal_port` with no extra configuration, so `/hubs/game` and
`/hubs/presence` (SignalR) work out of the box.

If the **site** is orange-clouded (it is), Cloudflare supports WebSockets with a **~100 second idle
timeout** on those proxied connections. That timeout does **not** apply to `/hubs/game` or
`/hubs/presence`: those go to `api.kirichess.com`, which is DNS only, so the connection is Fly's
idle policy, not Cloudflare's. The number stays here so nobody orange-clouds `api` again without
re-checking it. SignalR's default `KeepAliveInterval` is 15 seconds, under either limit.

## 4.1 Capacity: the connection limit (9c.7)

**One machine holds about 100 concurrent games, and refuses new connections around 150.** A second
machine, stopped until the first is past `soft_limit`, takes the next 100. It is a decision,
recorded here so that it is arguable rather than inherited. The 2026-10-06 staging rung is what
moved it: 100 games held on `shared-cpu-1x`, and 150 stopped answering HTTP. Before that the limit
was `hard_limit = 1000` (~250 games), which was past the point the shared core already failed.

**How connections turn into games.** A player holds **two** WebSockets, `/hubs/game` and
`/hubs/presence` (see [`ui/lib/realtime.ts`](../../ui/lib/realtime.ts)), so **one live game is four
connections**, plus every lobby visitor who is connected and not playing.

| `fly.backend.toml` | Value | Games |
| ------------------ | ----- | ----- |
| `soft_limit` | 400 | ~100, start the second machine |
| `hard_limit` | 600 | **~150, this machine stops accepting** |

`min_machines_running = 1` keeps one machine up. `auto_start_machines = true` starts a
stopped machine once every running machine is past `soft_limit`. That second machine is not
created until `/health/ready` reports `"redis": "up"` (§ 7). Connections past `hard_limit` on a
machine are refused.

### Who gets the budget (10a.2)

`hard_limit` counts the right unit and says nothing about **who** spends it. `JoinAsSpectator` is
anonymous on purpose (8.1) and a lobby visitor holds a presence socket, so on the day 8.7 sends a
wave of people to watch one game, the failure mode is not a server falling over: it is **players
being refused while onlookers hold the capacity**. The app therefore keeps its own connection budget
beside Fly's, in `HubConnectionBudget`, applied on hub connect and probed at negotiate.

| Setting                                       | Default | What it is                                                                                        |
| --------------------------------------------- | ------- | -------------------------------------------------------------------------------------------------- |
| `Hub__ConnectionBudget__MaxConnectionsPerIp`   | 20      | Live hub connections one address may hold. Ten tabs, or a household at once; 2% of `hard_limit`. |
| `Hub__ConnectionBudget__MaxAnonymousConnections` | 200   | Ceiling for callers with no user id. **The reserve is its complement:** 400 sockets, 100 concurrent games, that only signed-in players can occupy. |
| `Hub__ConnectionBudget__Enabled`               | true    | Off means no counting at all, not counting without enforcing.                                     |

The per-IP ceiling keys on the player. `api.kirichess.com` is DNS-only, re-checked 2026-10-04
(§ 1), so the one forwarded hop Fly adds is the caller.

A refused caller is told `Busy right now, try again shortly.` - at negotiate, so the client's own
`start()` rejects with that sentence rather than a socket opening and dying. Refusals are counted on
`kirichess.ratelimit.rejected` with `surface="hub-connect"`, so a budget that is actually biting is
visible on the instrument 9c.5 already watches rather than being invisible from outside.

**These are ceilings, not throttles**, and they are deliberately unrelated to 6e.1's rate limiter:
that one limits how fast a connection may *invoke* and a thousand idle spectator sockets violate no
rate limit at all. **Raising `hard_limit` is not the fix** if these start firing; see the next
paragraph for why that number is what it is.

**Why 250 and not 400.** 9b.3 measured 400 concurrent games as the comfortable rung (31.6% CPU,
286 MB RSS) and 800 as the edge (76.6% CPU, 372 MB of 512). Those rungs ran against a Docker `cpus:
1.0` quota, which is a **full** core; a Fly `shared-cpu-1x` vCPU is a slice of a shared core subject
to co-tenant steal, so
the 9b.3 ladder is an upper bound rather than an
estimate. See [loadtest/README.md](../../loadtest/README.md). 250 games spends about half of the comfortable figure and keeps the rest as derating
against that difference.

**I-6-1: those numbers were captured 2026-08-08, against a build predating commit `8574a55`
(2026-08-17, the per-IP `HubConnectionBudget` and the `kill_timeout` alignment) and predating none
of `LiveGamePersister`'s write-behind path or the CPU measurement itself, so the persister ceiling
and the CPU figures above are unaffected by the gap. What they do not reflect is the connection
budget added afterward, which the harness cannot exercise from one source IP (see
`loadtest/README.md`'s "Running against Fly" section) - re-running the ladder (9e.8) is what
actually closes this, not a document edit.**

**Do not raise `[[vm]]` to lift this.** 9b.3 doubled the CPU and quadrupled the memory and the
ceiling did not move, because the ceiling was `LiveGamePersister`, not the machine. Memory was never
close: 372 MB at 800 games, about 280 KB per live game. Keep 512 MB.

**Neon project region (10c.7, 2026-08-19):** `aws-eu-central-1` (Frankfurt). Fly is `ams`
(Amsterdam). Adjacent EU regions, so the 5–15 ms write-RTT band below is the right order of
magnitude rather than a transatlantic multiple. The exact RTT is still unmeasured and rides 9e.8.

**What to re-check before changing this number:**

1. **The durability ceiling, which is a different limit and can bind first.** `LiveGamePersister`
   writes a move snapshot per game per move. 9c.7 batches those writes, which lifts its ceiling by
   the batch size, but the ceiling still scales with the database write RTT and **Neon is 5-15 ms
   where the measured local bridge was under 4**. The instrument that shows this is
   `kirichess_persistence_pending_snapshots`: sustained growth means durability debt is
   accumulating **silently**, with no errors and no rejected moves. Watch it, not the CPU graph.
2. **Whether the measurement still describes the app.** Every number above comes from one pool
   (`steal3`), random-legal play at a 3 s cadence, 180 s rungs, and Redis off. Longer games, faster
   pools and real humans all move it.
3. **Re-measure rather than reason.** The 9b.3 harness is committed in [`loadtest/`](../../loadtest/)
   and re-runs against a compose stack; `loadtest/run-innetwork.sh` is the in-network runner that
   avoids the Docker Desktop port-proxy artefact described in RESULTS.md § 4.
4. **Fly outbound transfer (I-9-6) - the one cost line that scales with concurrent play, not with
   the server sitting idle, and Fly has billed it with no free allowance since late 2024.** Nothing
   in the repo measures it; the arithmetic below is derived from the actual DTOs
   (`Games/RealtimeDtos.cs`, `Games/GameDtos.cs`) rather than a real capture, so treat it as an
   order-of-magnitude floor, not a bill:
   - `MovePlayedDto` (state + uci + deltas + clock events + `legalMoves` + `EconomyDto`, echoed on
     every move) serializes to **~1.3 KB** compact JSON, measured by hand-building one from the real
     field list with a 35-entry `legalMoves` array (a commonly-cited average chess branching factor,
     not a KiriChess measurement) and one clock event.
   - At the ladder's own default cadence (`--think-ms 3000`, i.e. `1000 / think_ms` plies/s/game,
     the same formula `loadtest/README.md`'s throughput section derives), 250 games sustained at the
     launch ceiling produce `250 * (1000/3000) ≈ 83.3` plies/s, each broadcast to the **2 players**
     in that game (not spectators - AS-4 already flags spectator sockets as a separate, larger
     multiplier this estimate does not include): `166.7 sends/s * ~1.3 KB ≈ 217 KB/s ≈ 0.78 GB/hour`.
   - At Fly's own published EU egress rate (~$0.02/GB, the same figure I-9.md's cost inventory
     cites), that is **~$0.016/hour of MovePlayed traffic alone** at continuous, sustained,
     full-ceiling occupancy - roughly **$11/month** if the ceiling held 24/7 for a 30-day month.
   - **What is missing to turn this into a real monthly figure: an actual concurrent-games-over-time
     profile** (signups, session length, time-of-day pattern) - the repo has no launch-traffic
     estimate to multiply against, so "$11/month at the ceiling" is a ceiling-utilization number,
     not a forecast. It also excludes spectator fan-out (AS-4), SignalR's 15 s keep-alive pings on
     every idle connection (`I.md (I-1)` § 3.2), and non-JSON (TCP/TLS) overhead, so real traffic
     at any given occupancy is higher than this per-move-payload floor, not lower. `.AddMessagePackProtocol()`
     (AS-7/I-1-7, not yet landed) would roughly halve the JSON-payload half of this once it ships.
   - **The only way to replace this with a real number is to read the transfer line off the actual
     Fly bill after a 9e.8 load run** (I-6) sustains the ceiling for a known duration.

### The shutdown budget (10a.3)

`kill_timeout = 30` in both `fly.backend*.toml`. 6c.1's durability guarantee is that
`LiveGamePersister` flushes every live session on graceful shutdown, and that flush had **no stated
budget**: `kill_timeout` was unset in all five `fly.*.toml` files, so Fly's 5 second default applied,
while the flush itself runs under the host's shutdown token whose timeout is .NET's 30 second
default. The smaller of the two won, and a truncated flush is silent, because the process is exiting
anyway.

30 aligns the two rather than inventing a third number. The arithmetic says it is generous: 9c.7
writes 64 games per round trip, so 250 live games is four round trips, about 60 ms at a 15 ms Neon
RTT. The headroom exists for the case arithmetic does not cover, a cold or suspended Neon compute.

**Measured 2026-09-26** (during the #10 / 9e.8 production ladder): a graceful restart flushed
**100 live games in 550 ms** (2 batches). That is well inside the 30 s `kill_timeout`, so the
budget fits the observed ceiling. Neon is Frankfurt (`aws-eu-central-1`), adjacent to `ams`, so
this figure and the capacity numbers above share the same 5–15 ms RTT band.

## 5. Backup / restore (6a.7)

**Neon path (production):** Neon provides automated backups plus point-in-time recovery (PITR).
This is the roadmap's DH.2 rationale for choosing Neon over a self-managed Postgres container.

**PITR horizon (I-4-5) - fill once from the console:** Neon project → **Settings → Instant
restore** (history window). Write the configured value here (not the plan maximum):
**________** (checked ________). Plan limits for reference: Free max 6 hours; Launch default 1 day /
max 7 days; Scale default 1 day / max 30 days
([Neon history window](https://neon.com/docs/introduction/history-window)). That number is the RPO
of the in-Neon restore path in this section. After recording it, dispatch the restore drill once
with `RESTORE_POINT` near the far edge of that window.

The restore drill:

1. In the Neon console, create a **branch** from the desired timestamp (or just before an incident).
2. Point a staging `kirichess-api` deploy at the branch's connection string
   (`fly secrets set --app kirichess-api-staging ConnectionStrings__Postgres="..."`).
3. Verify the data looks correct against the staging app.
4. Repoint production (`fly secrets set --app kirichess-api ConnectionStrings__Postgres="<branch-or-promoted-connection-string>"`)
   once confirmed.

**`docker-compose.prod.yml` path (local / VPS, not relied on in production - production is Neon):**
back up and restore the `db` service directly, using the custom format and a `--clean` restore, not
plain SQL through `psql`. `scripts/offsite-backup.sh`'s own `dump_mode` uses the same
`pg_dump --format=custom --no-owner --no-acl`; `--clean --if-exists` is added here because this
restores into a database that may already carry the schema, which the script's own restore path
avoids only because it targets a freshly created, empty container:

```bash
docker compose -f docker-compose.prod.yml exec db pg_dump -U postgres --format=custom --no-owner --no-acl kirichess > backup.dump
docker compose -f docker-compose.prod.yml exec -T db pg_restore -U postgres --clean --if-exists --no-owner --no-acl -d kirichess < backup.dump
```

A plain-SQL `pg_dump`/`psql` pair (the previous version of this snippet) restored into a database
that already has the schema produces a wall of duplicate-key errors, and `psql` without
`ON_ERROR_STOP` exits 0 while doing it - a false-green restore. If this path is ever actually needed
for a self-hosted instance with no Neon and no R2 backup of its own, prefer this pair over
hand-rolling a fresh one.

## 5.1 Data retention (8a.3)

The daily retention sweep is configured with `Retention:*` keys, set as Fly secrets the same
way as § 2 and § 8.3:

```bash
fly secrets set --app kirichess-api \
  Retention__Enabled=true \
  Retention__SweepIntervalHours=24 \
  Retention__InitialDelaySeconds=60 \
  Retention__RefreshTokenGraceDays=30 \
  Retention__ExternalCodeGraceDays=1 \
  Retention__BanEvasionMarkerGraceDays=30
```

Each sweep writes one `Information` log line with the per-rule deleted counts, and increments the
`kirichess.retention.rows_deleted` counter (tagged `rule`). Setting `Retention__Enabled=false`
stops the sweep and changes nothing else. See [docs/decisions/RETENTION.md](../decisions/RETENTION.md) for the policy
itself, the schedule, the config defaults, and the known limits.

## 5.2 The restore drill, scripted and scheduled (9c.4 / 9e.8)

The four steps above are the manual version and describe an incident. The drill is the same
restore performed when nothing is wrong, which is the only time it is safe to find out that it does
not work. It is one script, [scripts/restore-drill.sh](../../scripts/restore-drill.sh), used two ways:
by hand for 9c.4's "run it once", and monthly by
[.github/workflows/restore-drill.yml](../../.github/workflows/restore-drill.yml) for 9e.8. One file on
purpose, so the scheduled drill and the by-hand drill cannot drift into testing different things.

```bash
NEON_API_KEY=... ./scripts/restore-drill.sh                                  # restore to 1 hour ago
NEON_API_KEY=... RESTORE_POINT=2026-08-16T09:00:00Z ./scripts/restore-drill.sh
NEON_API_KEY=... KEEP_BRANCH=true ./scripts/restore-drill.sh                 # keep the branch to poke at
```

Requires `psql`, `jq`, `npx` and the .NET SDK. On Windows the easier route is the workflow's
`workflow_dispatch` button, which takes the same two inputs.

**It touches production only by reading it.** It branches the Neon project at a past timestamp,
works exclusively against that throwaway branch, and deletes it on the way out including on failure
(a leaked branch is a compute endpoint that keeps costing money, so cleanup failure is reported
loudly rather than swallowed). Nothing in it writes to the production branch.

**What it asserts, and what a failure of each one means:**

| # | Assertion | A failure means |
| --- | --- | --- |
| 1 | **Branch** from a timestamp in the past | PITR itself does not work, or does not reach back that far on the current plan. Every recovery plan in § 5 is fiction until this passes. |
| 2 | **Boot**: the app starts against the restored branch and `/health/ready` returns 200 | The restored database is not usable by this build at all. |
| 3 | **Schema**: `__EFMigrationsHistory`'s newest row equals the newest migration in the checkout | 9e.8's actual worry. Either a migration never ran in production, or the restore point predates a migration that has since shipped. Such a restore boots fine and then fails on the first query touching a new column, which is why boot alone is not enough. |
| 4 | **Replay**: the most recently finished game replays through the rule engine via `/api/games/{id}/replay` | Rows restored but the stored moves did not survive readable, the failure that a row count would never catch. |

Assertion 4 is **skipped with a loud warning, not a failure, when the restored database holds no
finished games**, which is production's state before 8.7. It is not skipped when games exist and
none replay. The warning disappears on its own once there are real games.

The drill runs with `RunMigrationsOnStartup=false` deliberately: a boot-time migration would
quietly repair the exact discrepancy assertion 3 exists to detect. `GameRecoveryService` does still
run and may write to the drill branch, recovering or aborting games that were live at the restore
point. That is intended, it exercises the recovery path against real restored state, and the branch
is thrown away minutes later.

**Setup, one repository secret:** `NEON_API_KEY`, a Neon **management** API key (branch
create/delete). It is not a database password and not the same secret as
`ConnectionStrings__Postgres`: the drill reads the drill branch's own credentials from the Neon API
at runtime and never needs production's. Optionally set the `NEON_PROJECT_ID` repository variable if
the project is ever not `plain-salad-47730828`.

**Notification is GitHub's own e-mail on a failed scheduled run**, which is the only path that
needs no additional account. Two known gaps, stated rather than half-solved: a *disabled* schedule
is silent in exactly the same way a passing one is (GitHub disables cron on repos with 60 days of no
activity), and a drill that never runs raises nothing. A heartbeat check against 9c.5's Better Stack
account is the upgrade once that is fully live.

**Ran and passed 2026-08-17** (GitHub Actions run 31996903275). All four assertions, including
replay of a finished game. The monthly schedule is what keeps that from becoming a hypothesis again.

## 5.3 Off-site backup, out of Neon (10c.1)

Neon PITR (§ 5 / § 5.2) proves restore *into Neon*. This is the copy that still exists if Neon is
the thing that failed: account lockout, an accidental project deletion, a billing lapse, a
provider-side incident. One script, [scripts/offsite-backup.sh](../../scripts/offsite-backup.sh),
used two ways: weekly (and `workflow_dispatch`) by
[.github/workflows/offsite-backup.yml](../../.github/workflows/offsite-backup.yml), and by hand for a
disaster restore. Same file on purpose, so the dump nobody has loaded is not a second hypothesis.

```bash
# Weekly path (also the workflow): dump, upload, re-download, restore into local Postgres, assert
NEON_API_KEY=... R2_ACCOUNT_ID=... R2_ACCESS_KEY_ID=... R2_SECRET_ACCESS_KEY=... \
  bash scripts/offsite-backup.sh

# Disaster: Neon is gone. Pull the latest object and restore it. No Neon credentials required.
R2_ACCOUNT_ID=... R2_ACCESS_KEY_ID=... R2_SECRET_ACCESS_KEY=... \
  bash scripts/offsite-backup.sh restore

# Disaster into a *new* Postgres (new Neon project, Fly Postgres, a laptop). The URL must be
# reachable from a Docker container; `localhost` on the host is not.
RESTORE_DATABASE_URL='postgresql://...' \
  bash scripts/offsite-backup.sh restore

# Restoring a specific object instead of trusting "newest" (see the retention note below) - for
# example when the newest dump is the one a failed run left behind.
DUMP_KEY='kirichess/20260830T054100Z.dump' R2_ACCOUNT_ID=... R2_ACCESS_KEY_ID=... \
  R2_SECRET_ACCESS_KEY=... bash scripts/offsite-backup.sh restore

# Overriding the Postgres major the local-Docker restore target uses (I-4-4). Detected
# automatically from the archive's own header; set this only if that detection is wrong or the
# archive predates the header format.
PG_MAJOR=17 R2_ACCOUNT_ID=... R2_ACCESS_KEY_ID=... R2_SECRET_ACCESS_KEY=... \
  bash scripts/offsite-backup.sh restore
```

**`PG_MAJOR` (I-4-4).** The two restore targets pick the Postgres major two different ways.
Against `RESTORE_DATABASE_URL` it is read off the *live target server* (`SHOW server_version`), so
it always matches whatever is already running there. The local Docker target has no server to ask,
so it is read from the *dump file's own header* instead: a custom-format archive opens with a block
of `;`-prefixed comment lines that `pg_restore --list` prints ahead of the table of contents,
including `Dumped from database version: X.Y`, and that header is plain text a pg_restore of either
major can read even from an archive whose TOC it cannot restore. If the header cannot be parsed
(empty, truncated, or the file is not a custom-format dump at all) detection falls back to 16,
matching `docker-compose.prod.yml`, rather than failing outright. Set `PG_MAJOR=` explicitly to
skip detection entirely - the thing to reach for if a restore fails with a header-version error, or
if the header read picked the wrong major.

**What a restored database still needs to become a running site.** Two secrets people worry about at
restore time, and neither is the problem it looks like. The Data Protection key ring is **not** a
separate thing to restore: since 10a.1 it is persisted unconditionally to Postgres
(`DataProtectionKeys` table, `Program.cs:177-179`), so it travels inside both this dump and Neon's
PITR, and email-confirmation, password-reset, and Google sign-in links already in flight survive the
restore along with everything else in the database. `Jwt__Key`, by contrast, travels in neither path
and does not need to: refresh tokens are opaque random values compared against a SHA-256 hash rather
than signed (`Data/Entities/RefreshToken.cs:8`), so a freshly generated signing key on the restored
instance costs only the 15-minute access tokens currently in flight, and every client recovers on its
next silent refresh. Beyond the database itself, a restored instance needs at minimum the same six
secrets any fresh deployment does - `Jwt__Key`, `ConnectionStrings__Postgres`, `Frontend__BaseUrl`,
`NEXT_PUBLIC_API_BASE`, `NEXT_PUBLIC_SITE_URL`, `Cors__AllowedOrigins__0` - see `CONTINUITY.md`'s
secret classification for which of these need to be freshly minted versus copied from the old
deployment.

**After restoring from a dump (I-4-6).** A restore un-anonymises accounts erased after the dump
and lifts sanctions issued after it: both are in-place mutations the dump predates, and erasure
anonymises in place, so the restored database has no request log of its own (L-2). Before putting
the instance in service:

1. Re-run erasure for every request in the `privacy@kirichess.com` mailbox dated after the dump's timestamp.
2. Re-apply any sanction issued after that point (the moderation log and the mailbox are the record).
3. Note the restore and its restore point in the incident record.

See [RETENTION.md § 6](../decisions/RETENTION.md#6-backups) for why backups and erasure interact
this way.

It branches the current production HEAD (not a timestamp - 9c.4 already covers PITR), dumps the
throwaway branch with `pg_dump --format=custom` against the **unpooled** endpoint, uploads to R2,
then restores *the R2 object* into a local Postgres 16/17 and runs § 5.2's schema / boot / replay
assertions against that. A truncated upload would pass every check against the local file and still
be unrestorable, which is why the restore target is the object that landed, not the file that was
sent. The dump branch is deleted on the way out, including on failure. It shares a concurrency
group with `restore-drill.yml` so the two never create compute endpoints at the same time.

**Setup, once:**

1. Cloudflare dashboard → R2 → Create bucket. Name: `kirichess-backups` (or set the
   `R2_BUCKET` repository variable to whatever you pick). **Jurisdiction: European Union (EU)** -
   the players are EU-heavy and Neon is Frankfurt. That is a jurisdiction lock, not a WEUR location
   hint: the S3 endpoint is `https://<account>.eu.r2.cloudflarestorage.com`. Public access: off.
2. R2 overview (the bucket *list*, not the bucket's Settings) → Account details → API Tokens →
   Manage → Create Account API token. Permission: Object Read & Write, scoped to that bucket.
   Save the Access Key ID and Secret Access Key; the secret is shown once.
3. GitHub → Settings → Secrets and variables → Actions:
   - **Secrets:** `R2_ACCESS_KEY_ID`, `R2_SECRET_ACCESS_KEY`
   - **Variables:** `R2_ACCOUNT_ID` (the 32-character id in the S3 URL, currently
     `c3ba1594949d710f9fa8d68b9398dc36`). Optional `R2_JURISDICTION` (defaults to `eu`). Optional
     `R2_BUCKET` if the bucket is not named `kirichess-backups`.
4. `NEON_API_KEY` is already set (9c.4). Do not dispatch **Off-site backup**, and do not restore
   the Sunday `41 5 * * 0` schedule, until GitHub's DPA is accepted and recorded in
   [DPA-CHECKLIST.md](../legal/DPA-CHECKLIST.md). A run restores the dump on a GitHub-hosted
   runner. The schedule was removed on 2026-09-28 for that reason; `workflow_dispatch` is what
   puts it back after the acceptance.

**What it asserts, on top of a successful put:**

| # | Assertion | A failure means |
| --- | --- | --- |
| 1 | **Dump** of a throwaway copy of production HEAD | Neon could not be read, or `pg_dump` wrote nothing worth storing. |
| 2 | **Upload** to R2, then a re-download whose byte count matches | The object in R2 is not the file we dumped. Treat the copy as missing. |
| 3 | **Schema / boot / replay** against a *local* Postgres restored from that object | The dump is not restorable without Neon, which is the failure this item exists to catch. Replay warns rather than fails when the dump holds no finished games, same as § 5.2. Pruning the oldest kept dump runs only after this assertion passes (see Retention below). |

**Retention:** the eight newest timestamped dumps are kept; older `kirichess/*.dump` objects are
deleted after assertion 3 passes, not after the upload (I-4-3, reordered 2026-09-06). Pruning used
to run right after the byte-count check in assertion 2, before schema/boot/replay had run at all,
so a dump that then failed assertion 3 still counted toward `KEEP_DUMPS` and could evict the
previous, known-good copy to make room for itself. Verifying first means a failed run leaves every
older dump untouched. A restore picks the newest `kirichess/*.dump` by name, which is newest by
time because `STAMP` sorts. There is deliberately **no `kirichess/latest` pointer object** any more
(removed 2026-09-04): a fixed key rewritten every run collides with the bucket lock below, and a
refused rewrite left a pointer naming the previous week's dump, which a disaster restore would have
followed without complaint.

**What reordering does not fix.** The bucket lock below means the script can never delete the
object a failed run just uploaded - the lock enforces at least a week's retention regardless of
whether assertion 3 passed - so a failed dump still becomes, and stays, the newest-named object
until the next successful run supersedes it. A disaster striking in that window and running `bash
scripts/offsite-backup.sh restore` with no `DUMP_KEY` would still pick it up. The failed run's
GitHub Actions email names the timestamp; a restore during that window should pass
`DUMP_KEY=kirichess/<previous-good-timestamp>.dump` explicitly rather than trust "newest" blindly.
This is a real gap, not one this fix closes - closing it fully would need the upload itself to land
somewhere other than its final `kirichess/` key until verification passes, which is a bigger change
than the reorder and interacts with the bucket lock's own prefix rule below.

**Bucket protection (I-4-1, closed 2026-10-04).** R2 has no object versioning and no write-only
token. The control that exists is a **bucket lock**, which refuses a delete or an overwrite inside
its retention window. `kirichess-backups` carries one rule, `dump-retention`, on prefix
`kirichess/`. Retention must sit **above one week and below the `KEEP_DUMPS` window** (eight weekly
dumps, so about 56 days): below that floor a leaked credential still erases nearly every copy, and
above that ceiling the lock starts refusing `prune_old_dumps`'s legitimate deletes and the weekly
job fails. **Set to 30 days by the owner on 2026-09-04.** The weekly job re-downloads each object
to check its size and deletes copies older than that window, so the token stays **Object Read &
Write** on this bucket. That is the narrowest R2 permission that can upload, verify, and prune.

The dump is plaintext: custom-format `pg_dump`, no client-side encryption. Cloudflare encrypts the
bucket on their side; anyone holding the token can still read the object. Encrypting before upload
stays off until the passphrase is in the succession note. An unrecoverable backup is worse than an
unencrypted one.

**Token scope (confirmed 2026-09-04).** The `kirichess-backups-github` R2 API token is
read-write and scoped to **`kirichess-backups` only** ("Apply to specific buckets only"), not
account-wide. A leak of the GitHub repository secrets reaches the backups and no other bucket.

**Same Cloudflare account (I-4-2, accepted 2026-10-04).** This bucket sits in the account that also
holds the domain, DNS, the proxy, and Turnstile. A lockout, suspension, or billing lapse there
takes the front door and this copy together. Neon PITR survives that event and fails in a different
one; it does not cover a Cloudflare loss. The accepted mitigation is one dump kept on the
maintainer's own disk, outside both Cloudflare and Neon, refreshed each quarter
([MAINTENANCE-CALENDAR.md](MAINTENANCE-CALENDAR.md) item 6). A second cloud account is not required.
`scripts/offsite-backup.sh` talks to R2 through the S3 API (`--endpoint-url`), so a second
destination can be added later if that decision changes.

**Notification is GitHub's own e-mail on a failed scheduled run**, same as § 5.2. A dump that
never runs (disabled schedule, missing secrets) is silent in exactly the same way a passing one
is. Empty `R2_*` secrets fail the job loudly rather than skipping, so a forgotten setup is an
email, not a green tick.

**Until the first successful dispatch, Neon is still the only copy.** The plumbing in this
section is not itself a backup. The `kirichess-backups` bucket exists (EU jurisdiction, created
2026-08-20); the API token and repository secrets are set. Two dispatches on 2026-08-20 never
got a runner (private-repo Actions quota). Parked until a green run, not until more setup.

## 6. Running the production images locally

`docker-compose.prod.yml` runs the **published images**, never `dotnet watch` over mounted
source, and never a source bind-mount. Copy `.env.example` to `.env` and fill it in first.

```bash
docker compose -f docker-compose.prod.yml --profile migrate run --rm migrate
docker compose -f docker-compose.prod.yml up -d
```

The first command runs migrations to completion and exits; it is not part of `up` because
migration is a deliberate, explicit step. The second brings up `db`, `backend`, and `frontend`.

**Add `--build` to the migrate command after adding a migration.** `run --rm migrate` will happily
reuse a cached image built before your migration existed, apply the migrations that image knows
about, and exit 0. The schema is then behind the code with nothing having failed, and the symptom
arrives much later and in the wrong place: a register or a game write returning 500 with a Postgres
`column ... does not exist`, which reads as a code defect and has been investigated as one here
before. The command that avoids it is:

```bash
docker compose -f docker-compose.prod.yml --profile migrate run --rm --build migrate
```

The same trap does not apply to Fly, where migrations run from `release_command` inside the image
just built for that release (§ 3). It is specific to the local compose path, where the image is
cached across builds.

## 7. Scaling past one machine (8.4)

Phase 8.4 added the code needed to run more than one `kirichess-api` machine, a SignalR Redis
backplane, per-game ownership leases, and a shared Data Protection key ring, but nothing about the
deployment has changed yet: `fly.backend.toml` still pins `auto_stop_machines = "off"` /
`min_machines_running = 1`, and no Redis has ever been provisioned against this app. Do not flip the
scaling knob until you have actually done the following, in order:

> **The key ring is no longer part of that list (10a.1).** Until 2026-08-18 the only
> `AddDataProtection()` call was inside the Redis branch, so a deployment with `redis: disabled`,
> which is every deployment this app has ever had, ran on ASP.NET's default ring: written to the
> container filesystem and gone with the machine. It is now persisted to Postgres unconditionally
> (`DataProtectionKeys`, added by the `AddDataProtectionKeys` migration), and Redis overrides that
> when configured. **What changed operationally:** email-confirmation and password-reset links, and
> Google sign-ins in flight, now survive a deploy or a machine restart. What did not change: a
> _shared_ ring is still what a second machine needs, and Redis is still where it comes from.

1. **Provision Redis.** A single instance is enough, Phase 8.4 does not assume Cluster/Sentinel.
   Fly Redis (Upstash) or any reachable Redis 6+ works; nothing here is Redis-specific beyond
   `StackExchange.Redis`'s own reconnection behaviour.
2. **Set the secret**, the same way as the other optional integrations in § 2:
   ```bash
   fly secrets set --app kirichess-api Redis__Configuration="<redis connection string>"
   ```
3. **Confirm `/health/ready` reports `"redis": "up"`** before touching `min_machines_running`, see
   the field added by `HealthEndpoints.MapHealthEndpoints`. `"disabled"` means the secret did not
   take; `"down"` means it took but Redis is unreachable. Either way, stop here, do not raise the
   machine count on top of it. Also confirm `fly machines list --app kirichess-api` still shows
   exactly one machine until Redis is `"up"`. Two machines plus `redis: "disabled"` is the failure
   I-2-4 exists to stop.
4. Only then raise `min_machines_running` in `fly.backend.toml` and redeploy.

What each Redis key group is for lives in the code (`RedisOptions.KeyPrefix` on every key);
this file does not duplicate it.

**What happens when Redis dies**, once a second machine is actually running: games in flight keep
running on their current owner (ownership is sticky, see the README's decision 2, a live game is
never migrated off a healthy instance), so nobody loses a game mid-turn. New takeovers stop (an
instance whose lease expires cannot be reclaimed without Redis to expire the lease against), and
presence counts / the public live-games list degrade to whatever each instance can see locally,
since those depend on data other instances wrote to Redis.

**None of the above has actually been run with two machines.** This section documents the intended
procedure and what the code is built to tolerate, not a verified deployment, the same honesty rule
as the rest of this file. Fly deploy machinery for the multi-instance case (rolling strategy,
`min_machines_running > 1`, wsgi drain behaviour) is untested territory.

## 8. Transactional email (8a.1)

Email verification and password reset are wired end to end but **inert until a provider is
configured**: with `Email__ApiKey` empty the server registers `LoggingEmailSender` and writes each
message to the log instead of sending it. That is the correct state for local development and for
any environment without a verified sending domain.

Turning it on is four steps, and the first three are DNS.

### 8.1 Verify a sending domain

1. Create a Resend account and add the site's domain (`https://resend.com/domains`). Use a
   subdomain - `mail.<domain>` - rather than the apex: sending reputation then lives on its own
   name and cannot damage the apex's, and a future marketing sender can have a separate one.
2. Resend issues three records. Add them at the DNS provider (Cloudflare, per DH.3) with
   **proxying off** - these are TXT/MX records, not HTTP:

   | Type | Name                        | Purpose                                                     |
   | ---- | --------------------------- | ----------------------------------------------------------- |
   | MX   | `send.mail.<domain>`        | bounce handling                                             |
   | TXT  | `send.mail.<domain>`        | SPF - authorises Resend's servers to send as this domain    |
   | TXT  | `resend._domainkey.<domain>`| DKIM public key - signs each message so it cannot be forged |

3. Wait for Resend to show the domain as Verified. This is minutes, not days, once DNS propagates.

### 8.2 Add a DMARC record

Resend does not issue this one and the domain is not protected without it. Add a TXT record at
`_dmarc.<domain>`:

```
v=DMARC1; p=none; rua=mailto:dmarc@<domain>; pct=100; adkim=s; aspf=s
```

Start at `p=none` (monitor only, nothing is rejected) and read the aggregate reports for a couple of
weeks before tightening to `p=quarantine` and then `p=reject`. Going straight to `p=reject` on a
domain whose mail flow has never been observed is how a launch loses its own confirmation emails.

### 8.3 Set the secrets

```bash
fly secrets set --app kirichess-api \
  Email__ApiKey='re_...' \
  Email__FromAddress='noreply@mail.<domain>' \
  Frontend__BaseUrl='https://<domain>' \
  Api__BaseUrl='https://api.<domain>'
```

`Frontend__BaseUrl` is not optional: every link in every email is built from it, and email stays
switched off until it is set. No trailing slash. `Email__FromAddress` must be on the verified
domain - Resend rejects anything else with a 422, which the dispatcher logs as permanent and does
not retry.

`Api__BaseUrl` is the API's own public origin, also with no trailing slash, and it is a different
host from the site. Mail still sends without it. What stays off is the RFC 8058 one-click
unsubscribe header: an empty value is silent, and reach-back mail then names the footer link with
no one-click promise. Production is set (2026-10-04) to `https://api.kirichess.com`. On a new app,
set it on its own so the command does not rewrite the mail secrets:

```powershell
fly secrets set --app kirichess-api Api__BaseUrl='https://api.kirichess.com'
```

Setting a secret restarts the machine.

### 8.4 Confirm it works

Register a throwaway account against the deployed API and check the boot log does **not** say
"Transactional email is not configured". Then send to a
[mail-tester.com](https://www.mail-tester.com) address and confirm SPF, DKIM and DMARC all pass -
a 10/10 there is the only evidence that the DNS above is actually correct.

### What this does not do

- **No bounce or complaint handling.** Resend's webhooks are not consumed, so a hard bounce is
  invisible to the application and the address is never suppressed.
- **The queue is in-process and not durable.** A restart drops whatever is queued.
  `/api/auth/resend-confirmation` and `/api/auth/forgot-password` are the recovery path.
- **Resend free is 100 emails/day** (and 3,000/month). The daily cap binds first. AD.7 sized
  launch-day well under that (25 signups in 14 days). Stay on free. A 429 is not a register-form
  bug; recovery is the two routes above. Do not upgrade for launch on volume grounds.
- **`Auth:RequireConfirmedEmail` stays `false`.** Delivery working is a precondition for flipping
  it, not a reason to. Note it would not buy what it sounds like it buys: it gates `/api/auth/login`
  only, and `/api/auth/register` issues a token pair unconditionally, so a new account plays a whole
  first session and meets the wall days later at its second sign-in. The gate that actually makes a
  confirmed address a precondition for a game is `Auth:RequireConfirmedEmailForPlayFrom`, below.

### Requiring a confirmed email before a player can play

`Auth:RequireConfirmedEmailForPlayFrom` is an ISO-8601 instant, and unset (the default) means off.
Set it and accounts created **at or after** that moment must confirm before they can join a queue,
create a game, or accept a challenge or rematch. `PlayEligibilityPolicy` owns the rule and every
entry point runs it; `PlayGatePathTests` fails if a new game-creating path appears without a decision.

- **It is a cutoff, not a boolean, on purpose.** Accounts created before the instant are
  grandfathered forever, so turning it on cannot lock out players who signed up when nothing asked
  them to click the link. Set it to roughly "now" when you enable it.
- **It fails off, never closed.** A value that does not parse logs an error and leaves the gate open:
  a typo in a deployment variable must not wall the whole player base.
- **Signup, `/learn`, `/rules` and spectating stay open**, so somebody whose mail has not landed yet
  still has something to do. The move and resign paths are deliberately never gated, for the reason
  `SanctionPolicy` already documents: a refusal there would turn a config change into a flagfall.
- **Delivery has to be genuinely working first**, and more so than for the login gate, since this one
  takes the player's *first* game rather than their second session. The in-app way out is the banner
  on `/` and `/profile`, which is wired to `/api/auth/resend-confirmation`. Remember the queue is
  in-process and not durable, so a restart drops whatever was pending and that resend is the recovery.
- **Swapping the provider is not swapping the code.** `ResendEmailSender` is one class behind
  `EmailDispatchService`, genuinely an afternoon of work to replace with SES, Postmark, Mailgun or
  SMTP. Section 8.1's SPF/DKIM records and section 8.2's DMARC `p=none` observation window are not:
  they are provider-shaped DNS, and rebuilding sending reputation from zero on a new provider is a
  DNS and deliverability exercise measured in weeks, not a code change.

## 9. Breached-password check (9d.1)

Off by default, a bare checkout and every test that registers an account must not depend on
reaching `api.pwnedpasswords.com`. No API key or account is needed; turning it on is one flag:

```bash
fly secrets set --app kirichess-api Auth__BreachedPasswordCheckEnabled=true
```

It fails open: a HIBP timeout or outage never blocks registration or a password reset, only a real
k-anonymity match does (`BreachedPasswordChecker`). Set this **before real signups**, 6b.2's lockout
limits online guessing, but does nothing about a password that is already public, and the check
cannot be applied retroactively to a password already accepted.

## 9.1 Turnstile server-side verification (DH.3)

The register tab on `/login` loads the Turnstile widget (`ui/components/TurnstileScript.tsx`). That
only gates a browser that renders the form; a script that skips the browser never meets the widget.
The backend verifies the token itself (`TurnstileVerifier`) before creating the account. The old
siteverify Worker `turnstile-siteverify-kirichess` was deleted 2026-10-04 (I-7-6); its
`workers.dev` address answers 404. Local checkouts and `WebApplicationFactory` tests stay
off: empty `Turnstile:SecretKey` skips the check, same shape as § 9, so they do not need
`challenges.cloudflare.com`.

**Production is on.** Confirmed 2026-09-04 against `fly secrets list --app kirichess-api`:
`Turnstile__SecretKey` is Deployed. The site key is the public half
(`NEXT_PUBLIC_TURNSTILE_SITE_KEY`); the secret is the matching private half from the same Cloudflare
widget. A missing or rejected token is refused. A Cloudflare outage still fails open (transport
failure never blocks registration). Direct POST to `/api/auth/register` without a valid token no
longer creates an account.

To rotate, or to enable the same gate on a new app:

```bash
fly secrets set --app kirichess-api Turnstile__SecretKey="<secret key from the Cloudflare dashboard>"
```

## 9.2 Ban-evasion marker (S-3-1)

TERMS § 6 tells every player that "sanctions survive account deletion ... precisely so that deleting
your account and registering again cannot be used to launder a ban." Retention alone did not deliver
that: the sanction row survives erasure, but it points at a tombstone id nothing ever matches a new
signup against, and erasure releases the email address back into the pool. **This secret is what makes
that sentence true.**

When an account is erased while a full account ban is active, `AccountDeletionService` writes a keyed
HMAC-SHA256 of the account's normalised email onto the tombstone row immediately before scrubbing the
column. `POST /api/auth/register`, and the Google create path, hash the candidate address the same
way and refuse a match while the ban is still unlifted and unexpired.

```bash
fly secrets set --app kirichess-api BanEvasion__MarkerSecret="$(openssl rand -base64 48)"
```

**Production is on.** Confirmed 2026-10-04 against `fly secrets list --app kirichess-api`:
`BanEvasion__MarkerSecret` is Deployed. Do not rotate it.

**Off by default, and the default is safe rather than silently broken.** Empty
`BanEvasion:MarkerSecret` means no marker is written and no registration is refused, the same shape
as § 9 and § 9.1, so a bare checkout and every `WebApplicationFactory` test behave exactly as they
did before this existed. What the code deliberately does **not** do is derive a marker from an empty
key: that would give every banned tombstone the same value, and the first re-registration of any
address at all would be refused. Erasing a banned account with no key set writes nothing and logs one
`Warning` naming the account id and this section.

**Set it before the first ban, not after.** The marker is written at erasure, so an account erased
before this secret exists is not retroactively markable, and there is no backfill: the address it was
derived from is gone by then, which is the point.

**Rotation ends enforcement for every marker written under the old key.** Existing tombstones keep a
value derived from the previous secret and no live address will ever hash to it again. That is the
same property that makes the stored value useless to anyone who steals the database without the key,
so it is not a flaw to fix, but it does mean a rotation is a deliberate act, not routine hygiene.

**So the standing policy is: do not rotate this key.** It is deliberately not on the rotation
schedule in [B-4's runbook](INCIDENTS.md), and that is not an oversight. Two things make rotation
worse here than for an ordinary secret:

- **It is irreversible by construction.** Re-deriving a marker under a new key needs the email
  address, and erasure destroyed it. There is no migration that can fix up the column afterwards.
- **A permanent ban's marker never expires**, because the sweep measures the grace from the instant
  the ban stopped being active and a permanent unlifted ban has no such instant. So rotation does not
  merely lapse enforcement for a while; it permanently un-enforces exactly the bans that were meant
  to last forever, and it does so silently, since a marker that no longer matches is indistinguishable
  from an address that was never banned.

If a compromise ever forces a rotation anyway, **clear the column in the same operation**
(`UPDATE "AspNetUsers" SET "BanEvasionEmailHash" = NULL WHERE "BanEvasionEmailHash" IS NOT NULL`).
Every surviving value is dead weight that can never match again, and leaving it in place means the
table keeps a pseudonymous identifier that no longer serves the purpose it was retained under, which
is the Art. 17(3)(e) basis in [RETENTION.md](../decisions/RETENTION.md) § 1 doing no work.

Lifetime is in [docs/decisions/RETENTION.md](../decisions/RETENTION.md) § 1: a marker only refuses a
registration while the ban behind it is still active, and `Retention:BanEvasionMarkerGraceDays`
(default 30) is how long after that ban goes inactive the sweep clears the column.

## 10. Alerting & uptime (9c.5)

6d shipped structured logging, metrics and Sentry, but nothing watches any of it: `/metrics` is, by
its own note, scraped by nobody until this runs, and there is no external check on `/health/ready`.
Terraform in [infra/betterstack/](../../infra/betterstack/) provisions both, against
[Better Stack](https://betterstack.com) (this task's vendor pick, chosen to share one account with
9c.6's log retention).

```bash
cd infra/betterstack
terraform init
cp terraform.tfvars.example terraform.tfvars   # fill in the tokens, or use TF_VAR_* env vars
terraform apply
```

Full prerequisites, what gets created, and the verification steps to run before trusting any of
it, are in [infra/betterstack/README.md](../../infra/betterstack/README.md) - the SQL behind the
metric alerts reads a schema that has never been checked against a live scrape, and that README's
"Confirm it works" section is not optional the first time this runs.

### What this does not do

- **No on-call rotation.** Every alert e-mails the whole team; nobody is staffed to answer a page
  yet (ROADMAP.md 8.7).
- **No log shipping in this section.** That's § 11 below - a second resource in the same Better
  Stack account and the same `terraform apply`, but a separate Fly app to deploy.

### 10.1 State as of 2026-08-16: uptime is live, metrics needs one redeploy

`terraform apply` **has** run (2026-08-15). All 12 resources exist: the escalation policy, the
`/health/ready` monitor, the Prometheus scrape source, the dashboard, its four charts and three
alerts. The monitor is genuinely polling every 3 minutes. One real bug surfaced on the way: this
account's default escalation severity is named `"High severity"`, not `"High Severity"` as Better
Stack's own Terraform example shows, and `main.tf` was corrected to match.

The metrics half was **not** live: `GET https://kirichess-api.fly.dev/metrics` with the correct
bearer token returned a bare `404`, so the scrape source had nothing to scrape and `games_live`,
`queue_depth` and `hub_error_rate` had no data behind them however correct their SQL was.

**Cause, found 2026-08-16, and it was never a misconfiguration.** `Metrics__ScrapeToken` was set
correctly and `MetricsOptions.Enabled` was true the whole time. The guard in
[MetricsEndpoint.cs](../../src/KiriChess.Server/Infrastructure/MetricsEndpoint.cs) treated its two
gates as a **conjunction**: a request whose local port was not `Metrics__Port` (9091) was 404'd
before the token was ever read, and only 8080 is published by `[http_service]`. So every request
from off the private network got a 404 no matter what it presented, while `/health` on the same
machine returned 200, which is exactly why this read as a broken app rather than a deliberate gate.

The gates are now alternatives: the private metrics port is served tokenless (Fly's built-in
scraper cannot send a header), any other port requires the exact bearer token, and with no token
configured the public port stays 404 so an unconfigured deploy still fails closed.

**This ships in code, so it needs a backend deploy to take effect** - a `v*` tag, or
`gh workflow run deploy.yml -f environment=production`. Nothing in Better Stack needs to change.
After the deploy, confirm with the check below; until it passes, § 10's three metric alerts are
still watching an empty source.

```bash
curl -sS -o /dev/null -w '%{http_code}\n' \
  -H "Authorization: Bearer $METRICS_SCRAPE_TOKEN" \
  https://kirichess-api.fly.dev/metrics          # expect 200, was 404
curl -sS -o /dev/null -w '%{http_code}\n' \
  https://kirichess-api.fly.dev/metrics          # expect 401, the token is the whole gate
```

9c.5 is not ticked until the first of those returns 200 and a chart shows a real number.

### 10.2 Database, edge, and "can a game be played" (10c.5)

9c.5 watches the app. This section watches everything that is not the app.

**This section says what each signal measures. [docs/ops/INCIDENTS.md](INCIDENTS.md) is what to do
about it** - a one-page lookup from alert name to first check to response action, covering every
Better Stack alert below, the four cron-failure emails from `restore-drill.yml`, `offsite-backup.yml`
and `ops-probes.yml`, the operational kill switches, and a leaked-secret / bad-deploy row. Go there
first when something has actually paged.

| Signal | How | What a page means |
| --- | --- | --- |
| **Edge API** | Better Stack status monitor on `https://api.kirichess.com/health/ready` | Origin not returning 2xx. `api` is DNS only (2026-09-04), so this is the Fly API, not a Cloudflare 5xx. Compare `kirichess-api.fly.dev` if you need a second origin URL. |
| **Edge site** | Better Stack expected-status monitor on `https://kirichess.com` (200 or 302) | Same, for the apex. 302 is the Access login wall until DH.3; a Cloudflare 5xx is not. **I-5-5:** once DH.3 removes Access, drop `302` from this monitor's `expected_status_codes` in `infra/betterstack/main.tf` (see the `# DH.3: drop 302` comment there) and update `BetterStackAlertingDriftTests` to match - a 302 from the apex after Access is gone is a redirect loop or a misconfiguration, and leaving it accepted means that never pages. |
| **Neon storage / disabled compute** | [scripts/neon-watch.sh](../../scripts/neon-watch.sh) every 15 minutes, pings a heartbeat | Synthetic storage ≥ 80% of the plan limit, or a read_write endpoint is disabled. Connection failures stay on `/health/ready`. |
| **A game can be played** | [scripts/synthetic-play.sh](../../scripts/synthetic-play.sh) every 15 minutes, pings a heartbeat | Could not create an unrated bounty1 practice game, play e2e4, or see White's clock move. Uses a pre-created `play` API token, not a register (Turnstile). |

Playwright monitors at Better Stack would run the synthetic in their cloud and are a paid add-on.
Heartbeats plus GitHub Actions stay inside the free-tier 10-monitor allowance. The workflow is
[.github/workflows/ops-probes.yml](../../.github/workflows/ops-probes.yml).

**Setup, once, after `cd infra/betterstack && terraform apply`:**

1. ~~On production, create a throwaway account, confirm the email, Profile → API tokens → `play`.
   GitHub → Settings → Secrets: `SYNTHETIC_API_TOKEN`.~~ **Done 2026-08-20.**
2. ~~From `infra/betterstack/` (assignment consumes stdout, so the URL never reaches scrollback):~~
   **Done 2026-08-20.** `SYNTHETIC_HEARTBEAT_URL` and `NEON_WATCH_HEARTBEAT_URL` are repo secrets.

   ```powershell
   terraform output -raw synthetic_play_heartbeat_url | gh secret set SYNTHETIC_HEARTBEAT_URL
   terraform output -raw neon_watch_heartbeat_url     | gh secret set NEON_WATCH_HEARTBEAT_URL
   ```

3. `NEON_API_KEY` is already set (9c.4). **Do not dispatch yet** - August private-repo minutes are
   spent. After 1 Sep, dispatch **Ops probes (10c.5)** once from the Actions tab. Until that first
   ping, the heartbeats will open an incident after 30 minutes (`period` 900 + `grace` 900).

`BetterStackAlertingDriftTests` and `CiWorkflowDriftTests` fail the build if the Terraform
resources, the two scripts, or the workflow's schedule disappear.

## 11. Log retention / aggregation (9c.6)

6d.2's `AddJsonConsole` writes one JSON object per event to stdout, the right format; Fly's own
log buffer holds a few hours of it and nothing more. This section is the destination that
outlives that buffer, so a cheating report or a disputed game can still be investigated from
operational logs rather than only 6e.2's report table (`psql`). Two pieces, both against the same
Better Stack account § 10 already provisions:

1. **The destination** - `logtail_source.app_logs` in
   [infra/betterstack/main.tf](../../infra/betterstack/main.tf) (`platform = "fly_io"`), created by
   the same `terraform apply` as § 10's alerting resources. Read the token with
   `terraform output -raw log_shipper_source_token` (it is `sensitive` in
   [outputs.tf](../../infra/betterstack/outputs.tf)); never a bare `terraform output`, which would
   print it to the terminal's scrollback. The host (`log_shipper_ingesting_host`) is a hostname,
   not a secret: Vector prefixes `https://` itself, so the value must stay a bare host like
   `sNNNN.us-west-2a.betterstackdata.com`. Using the default `in.logs.betterstack.com` against this
   source's token is a 401.
2. **The collector** - [fly.logshipper.toml](../../fly.logshipper.toml) deploys
   [superfly/fly-log-shipper](https://github.com/superfly/fly-log-shipper), a prebuilt Vector
   image that subscribes to this org's internal Fly log stream and forwards every line to Better
   Stack. It is a separate app, not our own code.

### 11.1 State as of 2026-08-19: live

`kirichess-log-shipper` is one Machine in `ams`, no public IPs, Vector `/health` passing. The four
secrets (`ORG`, `ACCESS_TOKEN`, `BETTER_STACK_SOURCE_TOKEN`, `BETTER_STACK_INGESTING_HOST`) are
set on the Fly app, never in this repo. Better Stack Live tail on **KiriChess app logs** has shown
real `kirichess-api` JSON (an EF command on `/api/auth/register`).

v1 (2026-08-17) created the app with no secrets and two HA Machines that exited on missing
`ACCESS_TOKEN`. Those were destroyed and the app redeployed `--ha=false`. Do **not** `fly launch`
again: staging already showed that `flyctl launch` rewrites the toml it is given. Recreate from
§ 11.2 only if the app is torn down.

`ACCESS_TOKEN` is a readonly token scoped to the org, not this app; it is what lets the shipper
subscribe to every other app's log stream without also being able to change anything. Org slug is
`personal`.

### 11.2 Recreate (PowerShell, from the repo root)

**I-2-9, 2026-09-05: the image is pinned by digest in `fly.logshipper.toml`, not `:latest`.**
Recreating means deliberately refreshing that digest first (the comment above the `image =` line
has the two-`curl` command that resolves what `:latest` currently points to) - Dependabot's docker
ecosystem only watches `Dockerfile.*`, not a `fly.toml` `[build] image`, so nothing bumps this one
automatically the way the base images in `Dockerfile.backend`/`Dockerfile.frontend` are.

If the app has leftover Machines, destroy them first so the next deploy is the only copy. Then set
the four secrets (assignment consumes stdout, so the token never reaches scrollback; take the last
line of `fly tokens create`, the CLI prints a header above the value). Then deploy one Machine, no
public IPs.

```powershell
fly machines list --app kirichess-log-shipper
# destroy both stopped ids from that list:
fly machines destroy <id-1> --app kirichess-log-shipper --force
fly machines destroy <id-2> --app kirichess-log-shipper --force

$org = "personal"
$accessToken = (fly tokens create readonly $org | Select-Object -Last 1).Trim()
Push-Location infra/betterstack
$sourceToken = (terraform output -raw log_shipper_source_token).Trim()
$ingestHost = (terraform output -raw log_shipper_ingesting_host).Trim()
Pop-Location
if ($ingestHost -match '^https?://') { $ingestHost = $ingestHost -replace '^https?://', '' }

fly secrets set --app kirichess-log-shipper "ORG=$org" "ACCESS_TOKEN=$accessToken" `
  "BETTER_STACK_SOURCE_TOKEN=$sourceToken" "BETTER_STACK_INGESTING_HOST=$ingestHost"

fly secrets list --app kirichess-log-shipper
# expect four names: ACCESS_TOKEN, BETTER_STACK_INGESTING_HOST, BETTER_STACK_SOURCE_TOKEN, ORG

fly deploy --config fly.logshipper.toml --no-public-ips --ha=false
```

**bash** (Git Bash on Windows, not PowerShell, same split as § 2.1):

```bash
fly machines destroy <id-1> --app kirichess-log-shipper --force
fly machines destroy <id-2> --app kirichess-log-shipper --force
ORG=personal
fly secrets set --app kirichess-log-shipper \
  ORG="$ORG" \
  ACCESS_TOKEN="$(fly tokens create readonly "$ORG" | tail -n 1)" \
  BETTER_STACK_SOURCE_TOKEN="$(cd infra/betterstack && terraform output -raw log_shipper_source_token)" \
  BETTER_STACK_INGESTING_HOST="$(cd infra/betterstack && terraform output -raw log_shipper_ingesting_host)"
fly deploy --config fly.logshipper.toml --no-public-ips --ha=false
```

`--ha=false` is load-bearing: without it Fly creates a second Machine again.

### 11.3 Confirm it works

```powershell
fly status --app kirichess-log-shipper          # one Machine, started, in ams
fly ips list --app kirichess-log-shipper        # empty (no public IPs)
fly checks list --app kirichess-log-shipper     # vector check passing
fly logs --app kirichess-log-shipper            # "Configured sinks: better-stack", no 401
```

Then Better Stack → Logs & Traces → source **KiriChess app logs** → Live tail. Look for lines
tagged `kirichess-api`, not "no data". Filter on `fly.app` if staging/UI noise is in the way.

If Live tail is empty, the shipper logs are the diagnosis, not another `terraform apply`:

- `401 Unauthorized` from the Better Stack sink: ingesting host is wrong (fell back to
  `in.logs.betterstack.com`) or the source token is for a different source.
- NATS auth errors: `ACCESS_TOKEN` is missing, not readonly, or `ORG` is not `personal`.
- `Configured sinks:` with nothing listed: the Better Stack secrets never reached the Machine
  (Vector only enables a sink when its env vars are set).

### What this does not do

- **Ships every app's logs, unfiltered.** `SUBJECT = "logs.>"` in `fly.logshipper.toml` takes
  production, staging and the shipper's own logs together; there is no per-app split in Better
  Stack yet, only whatever the `fly.app` field Vector attaches lets Live tail filter by. Narrow it
  with a NATS subject if the combined volume ever becomes the problem.
- **No retention policy chosen beyond Better Stack's plan default.** 8a.3 governs account *data*
  retention, not operational logs; this inherits whatever the account's plan says until someone
  deliberately decides otherwise.
- **The collector is live.** Recreate from § 11.2 only if `kirichess-log-shipper` is torn down.

## 12. Known gaps

**If the maintainer is unavailable, see [docs/ops/CONTINUITY.md](CONTINUITY.md).** It inventories
which of the providers named in this file are load-bearing, what breaks if each one lapses, how the
`Jwt__Key` / Data Protection / provider-token secrets are classified for a successor, and the
runbook for moving off Fly or off Neon in a weekend if either evicts this project.

- **A deploy today interrupts in-progress games.** `GameRegistry` and live clocks live in
  in-process memory with a single always-on backend machine (see the comment in
  `fly.backend.toml`). Rolling deploys / graceful game handoff is roadmap item **6c**, not covered
  by this task.
- **Alerting is provisioned; metrics still need a live scrape to trust the SQL.** See § 10 -
  `terraform apply` has run, the `/health/ready` monitor is polling, and `/metrics` is the
  remaining proof for the three threshold alerts. 10c.5's edge monitors and heartbeats are in
  Terraform and need the next apply plus the secrets in § 10.2.
- **Log shipping is live.** See § 11 - `kirichess-log-shipper` forwards stdout to Better Stack
  **KiriChess app logs**. Recreate with `--no-public-ips --ha=false`, not another `fly launch`.
