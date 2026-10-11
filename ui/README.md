# KiriChess, Web Frontend

Next.js App Router frontend in `ui/` (npm package name `"web"`). Play is live: matchmaking, SignalR hubs, rated pools, bots, replays, and account settings. Board rendering is `react-chessboard`; the server is the move-legality and clock authority. Cost preview is generated from `config/kirichess.rules.json` (`lib/costPreview.ts`, `lib/rules.generated.ts`).

Project overview, licence, and verify commands live in the [root README](../README.md).

## Running locally

From the repo root: copy [`.env.example`](../.env.example) to `.env`, then `docker compose up`. API: `http://localhost:5053`. Site: `http://localhost:3000`.

Without Docker:

1. Backend from repo root: `dotnet run --project src/KiriChess.Server` (dev CORS allows `http://localhost:3000`). This needs a PostgreSQL on `localhost:5432` with user/password `postgres`/`postgres` and a `kirichess` database, or run `docker compose up db` from the repo root for just that container.
2. Frontend from `ui/`:

   ```bash
   npm install
   npm run dev
   ```

   `NEXT_PUBLIC_API_BASE` defaults to `http://localhost:5053`.

## Layout

- `app/`, App Router pages
- `components/`, shared UI
- `lib/`, API client (`api.ts` + generated `api.generated.ts`), realtime, types
- `messages/en.json`, copy

HTTP DTOs come from OpenAPI codegen. After backend API changes, from `ui/`: `npm run gen:api`.

## Scripts

```bash
npm run dev            # start the dev server
npm run lint           # eslint
npx tsc --noEmit       # strict typecheck
npm test               # vitest
npm run build          # production build
npm run gen:api        # regenerate api.generated.ts from docs/api/openapi.json
npm run gen:api:check  # fail if the generated client is stale
npm run gen:rules      # regenerate rules.generated.ts from config/kirichess.rules.json
```
