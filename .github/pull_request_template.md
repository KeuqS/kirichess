## Summary

<!-- Why this change exists. -->

## Test plan

- [ ] `dotnet test KiriChess.slnx` (or a named filter, if this is a narrow change)
- [ ] If `ui/` changed: `npm run lint`, `npx tsc --noEmit`, `npm test` in `ui/`
- [ ] If HTTP DTOs changed: OpenAPI export + `npm run gen:api`
- [ ] If `config/kirichess.rules.json` changed: `npm --prefix ui run gen:rules`

## Licence

- [ ] I license this contribution under Apache-2.0, and any rules-manual change (`config/kirichess.rules.json` / `docs/RULES.md`) under CC BY 4.0 (see [CONTRIBUTING.md](../CONTRIBUTING.md)).
