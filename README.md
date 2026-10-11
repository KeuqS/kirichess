# KiriChess

Chess where the clock is the battlefield: captures, moves, and checks add or remove time.

Play at [kirichess.com](https://kirichess.com). Rules: [kirichess.com/rules](https://kirichess.com/rules). Public / bot API: [kirichess.com/developers](https://kirichess.com/developers).

KiriChess is free, independent, and run at cost by one person: no ads, no paywalled features, donations optional. It is not a company and not a registered non-profit. The software is Apache-2.0; the rules manual is CC BY 4.0. **This repository is the public source of truth** for development, issues, pull requests, and production deploys. Clone: [github.com/MichielGekiere/kirichess](https://github.com/MichielGekiere/kirichess). How the clean import was cut (no private history): [`docs/ops/PUBLIC-SNAPSHOT.md`](docs/ops/PUBLIC-SNAPSHOT.md).

KiriChess is an original chess variant invented by Michiel Gekiere in February 2026. The project began in 2026 under the working title Clockstarved; later renamed to KiriChess. Origin: [kirichess.com/about](https://kirichess.com/about).

## Looking for playtesters

KiriChess is early. Try the game modes, poke at [custom games](https://kirichess.com/custom), and say what feels off - rules, clocks, UI, or the site itself. Experiments and weird configs are welcome; useful feedback is “I tried X and it felt Y.” [Discord](https://discord.gg/GRusewDw4) or [contact@kirichess.com](mailto:contact@kirichess.com). Code contributions: see [CONTRIBUTING.md](CONTRIBUTING.md).

## Stack

- **Engine:** `src/KiriChess.Engine/` - pure chess + clock-economy logic
- **Backend:** ASP.NET Core minimal API, EF Core, PostgreSQL, Redis, SignalR - `src/KiriChess.Server/`
- **Frontend:** Next.js App Router - `ui/`
- **Tests:** `tests/` and `e2e/`
- **Solution:** `KiriChess.slnx`

## Run locally

Copy [`.env.example`](.env.example) to `.env` and set at least `Jwt__Key` (32+ bytes). Then:

```bash
docker compose up
```

API: `http://localhost:5053`. Site: `http://localhost:3000`.

Without Docker: `dotnet run --project src/KiriChess.Server` and, in `ui/`, `npm install && npm run dev`. This path needs a PostgreSQL on `localhost:5432` with user/password `postgres`/`postgres` and a `kirichess` database (the connection string in `appsettings.Development.json`), or run `docker compose up db` for just that container. See [`ui/README.md`](ui/README.md) and [`docs/ops/DEPLOYMENT.md`](docs/ops/DEPLOYMENT.md).

## Tests

```bash
dotnet test KiriChess.slnx
cd ui && npm run lint && npx tsc --noEmit && npm test && npm run build
cd ui && npm run gen:api:check
```

Documentation map: [`docs/README.md`](docs/README.md). Contributing, including how PRs are licensed: [`CONTRIBUTING.md`](CONTRIBUTING.md). Security reports: [`SECURITY.md`](SECURITY.md).

Project conventions live in [`.claude/rules/`](.claude/rules/).

## Licence

- **Software** (source, tests, infrastructure, site code): [Apache License 2.0](LICENSE).
- **Rules manual** (`docs/RULES.md`, `config/kirichess.rules.json`, and the `/rules` and `/learn` pages): [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/). Attribution: “KiriChess” by Michiel Gekiere, licensed under CC BY 4.0. https://kirichess.com
- **Name, logo, wordmark:** not licensed. Apache-2.0 does not grant trademark rights. See [`NOTICE`](NOTICE).
- **`ui/public/stockfish-19-lite-single.js`** and **`ui/public/stockfish-19-lite-single.wasm`:** GNU GPL v3, a separate program (Stockfish 19 Lite, release [v19.0.0](https://github.com/nmrugg/stockfish.js/releases/tag/v19.0.0)). See [`ui/public/STOCKFISH.LICENSE`](ui/public/STOCKFISH.LICENSE).
- **`ui/public/sounds/*.mp3`:** board-move cues from el_boss's Chess Puzzle Blitz SFX, [CC0 1.0](https://creativecommons.org/publicdomain/zero/1.0/); outcome cues original and Apache-2.0. See [`ui/public/sounds/SOUNDS.LICENSE`](ui/public/sounds/SOUNDS.LICENSE).

The hosted service at kirichess.com is governed by the [Terms of Service](https://kirichess.com/terms), not by this licence. Running your own instance is not use of that service.

## Running your own instance

`docker compose up` (above) starts the **development** stack: an SDK image running `dotnet watch` with the repo bind-mounted. It does not seed demo data (`SeedDemoData=false`), so a fresh database starts empty. For a real instance, use [`docker-compose.prod.yml`](docker-compose.prod.yml) instead:

```bash
docker compose -f docker-compose.prod.yml up
```

Two things this will not tell you on its own:

- Migrations do not run at boot (`RunMigrationsOnStartup=false`). Run them separately: `docker compose -f docker-compose.prod.yml --profile migrate run --rm migrate`.
- `NEXT_PUBLIC_*` values are baked into the frontend image at build time, not read at container start. Getting one wrong means rebuilding the image, not restarting the container.

See [`docs/ops/DEPLOYMENT.md`](docs/ops/DEPLOYMENT.md) section 6 for the longer version.

NOTICE reserves the name "KiriChess", the wordmark, and the marks under `press-kit/logo/` and `ui/public/logo.svg`: a fork must not run a competing service, or distribute a fork, under this name. Most of the string `KiriChess` elsewhere in the tree is not brand and does not need to change: `docker-compose*.yml` container names, the `kirichess` database name, `Jwt:Issuer`/`Jwt:Audience`, assembly and project names, and the rules config filename are inert identifiers a fork may keep or change freely. What actually carries the brand, and needs replacing for a renamed fork:

- `ui/public/logo.svg`, `ui/public/logo.png`, `ui/public/logo-wordmark.png`, `ui/public/logo-transparent.png`, `ui/app/icon.svg`
- `ui/components/BrandMark.tsx`
- the `Footer.brand` / `Footer.tagline` keys in `ui/messages/*.json`
- `press-kit/logo/` (drop it, or replace with the fork's own mark)

The CC BY 4.0 attribution for the rules text (above) stays regardless of the fork's own name, and NOTICE gives a one-line credit a renamed fork can use without implying endorsement.
