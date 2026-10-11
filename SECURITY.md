# Security policy

## Reporting a vulnerability

Do **not** open a public GitHub issue for an unfixed vulnerability.

Prefer [GitHub private vulnerability reporting](https://github.com/MichielGekiere/kirichess/security/advisories/new)
on this repository. You can also email **contact@kirichess.com** with a description of the issue
and how to reproduce it.

You should hear back within a few days. Please give a reasonable window to fix and publish before
any public disclosure.

## Scope

This policy covers the KiriChess source in this repository and the hosted service at
kirichess.com / api.kirichess.com: authentication, authorization, game integrity, injection, and
exposure of credentials or personal data.

It does not cover:

- Denial of service against a third-party processor (Fly, Neon, Cloudflare, …)
- Findings that require a stolen moderator session on a single-admin hobby deploy
- Theoretical issues with no practical path in this codebase

## Supported versions

Only current `main` (and the production deploy it tracks) is supported. There are no long-term
release branches.
