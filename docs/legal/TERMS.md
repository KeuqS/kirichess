# KiriChess Terms of Service

> This document describes accurately what the KiriChess
> codebase does, as of the version below. KiriChess is a free, self-funded project run by one person
> rather than a company, and this document was written by its author. [PRIVACY.md](PRIVACY.md) §4
> lists every third party that receives data and the basis on which it does.
> See [docs/game/CLAIMS.md](../game/CLAIMS.md).
> Professional-indemnity or cyber-liability insurance is disproportionate at zero revenue and has
> not been bought; that decision is revisited only if the service takes on paid obligations.

**Last updated: 2026-10-11** - **Version: 2026-10-11**

---

## 1. Acceptance

By creating an account you agree to these Terms and to the [Privacy Policy](PRIVACY.md). Both are
accepted together, once, at signup, and the acceptance is timestamped on your account
(`TermsAcceptedAt`, `TermsVersion`). If these Terms change materially, the version string above
changes, and accounts that accepted an older version are not automatically treated as having accepted
the new one, see the Known Limits entry in the roadmap for what this does and does not do today.

## 2. Minimum age

You must be at least **16 years old** to create an account. This is a single global threshold rather
than a per-country one: GDPR Art. 8 sets the digital-consent age between 13 and 16 depending on EU
member state, and 16 both satisfies the strictest member state and clears the US COPPA threshold of
13 outright, without needing to detect where you are. This is declared by a checkbox at signup, not
verified, and no date of birth is collected, see the Privacy Policy §9 for why. On credible notice
that an account holder is under 16, the account is closed and anonymised.

## 3. Account responsibilities

You are responsible for the account you create: keeping your credentials to yourself, and for
activity that happens through your account. Google sign-in is optional; using it does not create a
separate set of obligations from registering directly.

## 4. Display names

Your display name is public, it appears on leaderboards, on every game you play, and in replays.
Names are subject to the moderation rules this codebase actually enforces: ASCII
characters only, no impersonation of another player or of staff, and no reserved or blocked terms.
Names can be changed by you, subject to a cooldown, or by a moderator when a name violates policy.

## 5. Fair play

The following are prohibited. Some are supported by statistical signals a moderator can run against
an account; all of them are decided by a human.

Those signals run on demand, never on a schedule, and never issue a sanction by themselves.
Engine-assistance analysis is run against a named account or a named game, typically after a
report. Sandbagging has a separate on-demand scan a moderator can start by hand. There is no
pool-wide engine scan.

- **Engine assistance**, using chess engine analysis, in real time or otherwise, to choose your
  moves.
- **Sandbagging**, deliberately losing rating or manipulating results to obtain an easier matchmaking
  pool.
- **Deliberate disconnection or stalling**, intentionally dropping your connection, abandoning a
  game, or running a game out without playing it, to manipulate its outcome or the opponent's clock.
- **Harassment, threats or abuse** of another player or of the maintainer, through any surface the
  site provides.
- **Unlawful use** of the service, including using an account for an unlawful purpose or to
  impersonate anyone.

Playing through the public API may use a chess engine. That is not engine assistance. When that
play is against a person, the game is unrated, and the person is shown that the opponent is a bot:
on the challenge, when the bot sent it, and on the game either way. Using an engine against a
person in any other game is still prohibited.

## 6. Moderation and sanctions

To report content or conduct on this site, including anything you believe to be unlawful, write to
**contact@kirichess.com**. Notices are read by the operator. There is no automated decision-making,
and you will get a reply. In-product reports from a signed-in account are an additional channel, not
a substitute for that address.

Reports and moderator actions are handled under the moderation system described in the Privacy
Policy. Reports are read by the operator, typically within a few days. Outcomes are not disclosed to
the reporter, because doing so would tell a suspected cheat they were detected. A player who is
sanctioned is shown the reason on their profile.

A sanction (warning, ban from rated play, or full account ban) is recorded permanently against
your account, including its reason. **Sanctions survive account deletion**: because deletion
anonymises rather than deletes your account row, a sanction record is retained against the
anonymised row. And if a **full account ban** is in force at the moment you delete, a one-way keyed
hash of your email address is retained alongside it, so that deleting your account and signing up
again with **the same address** is refused for as long as that ban lasts. That is what stops account
deletion being used to launder a ban. The Privacy Policy, §5 and §6, describes exactly what that hash
is, why it is not your address, and how long it is kept. Registering an entirely new account under a
different email is not prevented by this, that limitation is already documented elsewhere in this
project and is not solved by these Terms.

A sanction does not rewrite a rating. Games already played keep the rating change they produced,
and points are not given back to the other player.

Repeated abandonment can also trigger a short automatic cooldown on matchmaking. That cooldown is
separate from the sanctions above: it is not a sanction row, it is not decided by a moderator, and
it expires on its own.

## 7. The service is free, run at cost, and provided as is

KiriChess is free to use and independently run by one person, funded out of pocket and by voluntary
donations (§12) rather than by advertising, subscriptions or paid features. **It is not a company and
not a registered non-profit**, and nothing here should be read as claiming either: "non-profit" is a
legal form, and this project does not have one. It is provided **as is**, without any warranty of any
kind, and with **no guaranteed uptime**. Because the project is pre-launch and under
active development, **accounts, games and rating history may be lost or reset** without notice while
it remains in this state.

## 8. Termination

You may stop using the service, or delete your account, at any time (see Privacy Policy §6 for exactly
what deletion does). We may suspend or terminate an account for violating these Terms, at our
discretion, through the moderation and sanctions process described above.

## 9. Governing law

These Terms are governed by **Belgian law**, and the courts of Belgium have jurisdiction. One
carve-out is stated here rather than left implied, because it applies whatever this section says: if
you use KiriChess as a consumer, EU law leaves your home-country position intact. Under Rome I
Art. 6(2) a choice of law cannot deprive you of the mandatory consumer protections of the country you
live in, and under Brussels I bis Art. 18 you may bring proceedings in the courts of the country where
you live, and may only be sued there. The clause above therefore sets the default; it does not move
your case to Belgium and does not remove protections your own country gives you.

## 10. Software licence and the hosted service

These Terms apply to **accounts on the hosted service** at kirichess.com (and its API). They are
not the software licence.

The software is licensed under the Apache License 2.0; the rules manual and
`config/kirichess.rules.json` are licensed under CC BY 4.0. The vendored Stockfish engine
(`ui/public/stockfish-19-lite-single.js` and `ui/public/stockfish-19-lite-single.wasm`) is GNU GPL-3.0: it is loaded as a separate Web Worker program and is not
relicensed as Apache-2.0. The board-move cues in `ui/public/sounds/` are built from el_boss's
Chess Puzzle Blitz SFX pack, CC0 1.0, not Apache-2.0; the outcome cues are original and are part of
the Apache-2.0 software. The name "KiriChess", the logo
and the wordmark are not licensed. See `LICENSE`, `NOTICE`, `STOCKFISH.LICENSE` and
`ui/public/sounds/SOUNDS.LICENSE`.

The development repository is public at
[github.com/MichielGekiere/kirichess](https://github.com/MichielGekiere/kirichess).
That repository is the place to read those licence files. These Terms apply to the hosted service
whether or not you clone or run the software yourself.

Running your own instance of that software is not use of this service: it does not create an
account here, and these Terms do not apply to it.

## 11. Changes to these Terms

These Terms are versioned by the `Last updated` / `Version` date at the top, matching the Privacy
Policy's versioning and the `TermsVersion` recorded on your account.

## 12. Donations and patron badges

KiriChess accepts voluntary **one-off** donations through Stripe. Donating is optional, and nothing
on the site asks for it as a condition of anything. **There is no subscription and no recurring
donation**, and no paid tier of any kind.

**A donation buys nothing.** It is not payment for the service, for access to it, or for any feature
of it. Everything on KiriChess is free to everyone, whether they donate or not. No game, variant,
rating pool, board, tool or page is unlocked, improved, made faster or made available sooner by
donating, and there is no paid tier.

**The patron badge is a token of thanks, not a thing you have bought.** Donating may display a small
heart icon beside your display name. It is decorative and nothing else: it has no effect on pairing,
matchmaking, ratings, clocks, the clock economy, move legality, moderation, or any other part of how
the game is played, and it grants no priority, privilege or access. The badge lapses by itself after a
set period rather than lasting forever, and it may be changed or withdrawn at any time. Its removal
is not a refund event, because nothing was sold.

**Payments are handled by Stripe, not by KiriChess.** Card details are never sent to, seen by, or
stored on KiriChess servers. [PRIVACY.md](PRIVACY.md) §4 records what Stripe receives.

**Refunds.** A one-off donation is a single charge; there is nothing to cancel and nothing recurring
to stop. Because donations are voluntary gifts rather than purchases, they are not generally
refundable, but a donation made in error will be dealt with in good faith: write to the contact
address in [PRIVACY.md](PRIVACY.md) §1.
