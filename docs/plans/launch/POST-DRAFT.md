# 8.7 - the soft launch post, drafted

> **Drafted 2026-08-17. You post this.** Numbers rechecked **2026-10-09** against live `/rules` and
> `npm --prefix ui run gen:rules:check` (clean). Source of truth:
> [RULES.md](../../RULES.md) ← `config/kirichess.rules.json` (same document the engine loads). If a
> rule is retuned before you post, regenerate and re-read this — a wrong number in the first post is
> the one correction that follows a variant around.

## Before you post, in this order

Do not post until the boxes below are true. The first two remain the traps that fail in opposite
directions: the Access gate means nobody sees the site, a stale `main` means everybody sees an
old one. The UI pass was a third shape and is closed (2026-09-24).

- [x] The release is deployed and `main` carries the legal pass (`/privacy` names a controller and
      does **not** say `[CONTROLLER NAME AND CONTACT ADDRESS...]`). Production is `v1.3.3`
      (2026-10-09): Google sign-in UI + Access off; legal pass already on `main` from the soft-launch tree.
- [x] The [UI-CHECKLIST.md](../../ops/UI-CHECKLIST.md) pass (roadmap **8.8** / #4) is done on that
      deployed site, desktop and mobile, and in-scope **Notes** are shipped (**8.8a** / #4a).
      Closed 2026-09-24, before Access came down.
- [x] The Cloudflare Access application is removed, and you have loaded `kirichess.com` in a private
      window on a device that has never authenticated, to prove it. Done 2026-10-08 (#12).
- [x] A throwaway registration receives its confirmation email
      ([PROVIDER-ACCOUNTS.md](../../ops/PROVIDER-ACCOUNTS.md) § 3). Closed 2026-08-20: production
      Resend delivered confirmation and password-reset to a real inbox.
- [x] You can play one full game against a second account, on the deployed site, start to finish.
      Owner play confirmed through the soft-launch / Access-off window (LAUNCH A5 + post-#12).
- [ ] You have half a day free. The first two hours decide whether the thread lives, and an
      unanswered thread is a dead one.
- [ ] You are posting on a Thursday if you can, so the named hour (**Fridays 20:00 UTC**) is
      tomorrow rather than six days away. AD.5: a read that is an appointment for tomorrow is
      the only mechanism that turns launch attention into two people at a board in the same minute.
      (If posting on a Friday: the standing hour is **today** 20:00 UTC, not tomorrow.)

## Where, and in what order

Tag every launch link. This post uses `?src=reddit`; the lichess-forum follow-up uses
`?src=lichess`. AD.7 reads tagged landings; an untagged URL is counted as `direct`.

**r/chessvariants first**, alone, and wait a day before anywhere else. It is the most forgiving
audience, the most likely to engage with the design rather than the product, and the cheapest place
to discover that an explanation does not land. If the first post goes badly you will want to have
rewritten it before the lichess forum sees it.

**No press kit at launch.** Do not attach a clip and do not pitch journalists or creators on day
one. AD.1 is after this post on purpose: the first games should be people from this thread who
cared enough to read the rules.

Then, a day or two later and only if the first went well: the lichess forum's variant section, and
any chess-variant Discord you already sit in. **Submit to
[chessvariants.com](https://www.chessvariants.com) separately**, whatever happens here; per
[CLAIMS.md](../../game/CLAIMS.md) that catalogue entry is the single highest-leverage move available for
attribution, and it is not a launch post, it is a permanent citation.

## Title

Pick one. The first is the recommendation.

1. **KiriChess: a chess variant where captures, moves and checks all move time on the clocks**
2. I built a chess variant where the clock is the resource, and you can play it online now
3. Chess where taking a queen is worth 25 seconds: KiriChess, free, looking for feedback

Avoid "Introducing", "Announcing", and exclamation marks. Sounds like an ad.

---

## The post

> Copy from here down. Reddit markdown, no edits needed.

Hi, I am a student and I built a chess variant as a hobby. It is finally playable online, free, no
ads: **https://kirichess.com/?src=reddit**. I care more about feedback on the rules than about the
website, so this post is mostly how it works.

**The idea in one line:** normal chess still applies, but the clock is a resource that moves between
the players, not only a timer that goes down.

There are four rules, always in the same order.

**1. Captures move time.** When you take a piece, you get (or remove) time based on how strong it
is: pawn 5 seconds, knight or bishop 10, rook 15, queen 25. What happens with that time depends on
the pool:

- **Steal** - you gain it, the opponent loses it. Only winning material changes the gap.
- **Bounty** - you gain it, nobody loses. Captures add time to the game.
- **Burn** - the opponent loses it, you gain nothing. Even equal trades take time from both clocks.

**2. Every move costs time from your own clock**, based on the piece you moved. Pawn 1 second,
knight or bishop 2, rook 3, queen 4. The king is also 1, so moving the king in the endgame is not
too expensive. Castling is a flat 3 (not 4).

**3. Giving check moves 2 seconds from the checked player to the checker.** You still pay the move
cost from rule 2. A queen check basically pays for itself (the gap does not change). A pawn check
is good for you (+3 seconds on the gap). Cheaper checking pieces are better for the attack.

**4. If the same position already happened, there are no time effects.** No capture time, no move
cost, no check time. I added this because without it, perpetual check can just farm the opponent's
clock down to zero without improving the position. Once a position has been seen, it stays "off"
for the rest of the game. You cannot avoid this by switching between two patterns.

**Why I wanted to build this:** mate needs moves, and moves cost time, so a winning position has a
price. King and rook against a lone king is about 70 seconds of move costs. Bishop and knight is
about 88. In a 1+0 pool you can be totally winning on the board and still not have enough time to
finish. So you have to plan the endgame *before* you trade into it. Trading down when you are ahead
is not always correct anymore, and that changes middlegame decisions more than I expected.

There are six rated pools with the three economies above, different time controls, and different
capture multipliers, from 1+0 Bounty (Winder) up to 15+0 Steal (Eclipse). Full rules are on the
site under Rules. The numbers there come from the same config the server uses.

**What is there:** rated play (Glicko-2), a tutorial, replays with clock analysis after the game,
spectating, and bots to practice against. It works on a phone. Rules text is CC BY 4.0. Platform
source is Apache-2.0 at https://github.com/MichielGekiere/kirichess.

**What is not there yet:** no tournaments, no puzzles, the UI is okay but not beautiful, and right
now the player base is mostly me. If you queue and nobody is there, that is not a bug — it is the
usual problem for a new variant. There is one standing hour on purpose: **Fridays 20:00 UTC**
(evening in western Europe, afternoon in the Americas). Outside that, play a bot or bring a friend.

I am one person, not a company. It is free because I want players more than money. There is a donate
page, but nobody has to use it.

**Things I would really like to know:**

1. Does the capture economy break at a high level in a way I cannot see? I think Steal is the most
   solid and Burn is the most unstable, but strong players have not really tested it yet.
2. Are the endgame costs about right, or should mating be cheaper?
3. Is the repetition rule a good fix for perpetual check, or does it create a worse problem
   somewhere else?
4. Does the tutorial actually teach the clock ideas, or only the rules text?

Happy to answer questions, and also happy if you tell me the whole idea is bad. If you want to talk
more after the thread: https://discord.gg/GRusewDw4

> End of post.

---

## Notes for the first two hours

- **Answer every top-level comment**, including the dismissive ones, briefly and without defending.
  "That is fair, I had not tested that" is a better reply than a counter-argument, and this audience
  can tell the difference.
- **The most likely substantive objection** is that the clock economy makes the game about time
  scrambles rather than chess. The honest answer is rule 4 plus the toll table: tolls never scale
  between pools, so the endgame bill is one number to learn, and the repetition guard exists
  precisely because the first version of this idea did degenerate. Do not promise it is solved.
- **The most likely cheap objection** is "this is just X". Ask which X, and look it up before
  answering. If a genuine prior variant exists, say so in an edit at the top of the post. That costs
  nothing and buys the thread's trust permanently.
- **Link the public repo only if it helps the thread** (`https://github.com/MichielGekiere/kirichess`).
  Prefer rules and play first; code review is secondary. Never link `kirichess-archive`.
- **Expect the site to be found by more than the thread.** Removing the Access gate makes the domain
  publicly reachable, and it has been in Certificate Transparency logs for a while.
- **Watch Better Stack and Sentry, not the thread.** The uptime monitor and the error rate are the
  two things that decide whether the launch is a success; the upvotes are not.
- **The play hour is Fridays 20:00 UTC**, one slot, not per-region. If someone asks for an India
  evening or a US-evening second hour, the answer is not yet: splitting ten people is three empty
  rooms. Repeat the UTC time; do not switch to "8pm Eastern" in replies.
