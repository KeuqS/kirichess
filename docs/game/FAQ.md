# Frequently asked questions

These are the current answers to the questions raised in the [soft-launch post](../plans/launch/POST-DRAFT.md). They describe what is known today, not a claim that the balance has already been proven by high-level play.

## Does the capture economy break at a high level?

Not proven yet. **Steal** is currently the most convincing economy, while **Burn** is the one most likely to need more careful testing. The three economies deliberately create different incentives: Steal transfers time between clocks, Burn removes time from captures without giving it to the capturer, and Bounty adds time to the capturer without taking it from the opponent.

The useful answer is still player evidence rather than a theory claim. If a strong game exposes a dominant capture pattern or an unstable pool, please share the position, pool, and clock state so it can be reproduced. The full capture rules and current values are in the [rules manual](../RULES.md) and on [/rules](https://kirichess.com/rules).

## Are the endgame move costs about right?

The current costs are intentional but still open to feedback. Under the present table, converting king and rook against a lone king costs roughly 70 seconds of movement tolls; bishop and knight costs roughly 88 seconds. That means a winning position can still be too expensive to convert if the clock is low.

That is the design question: budget the conversion before trading into the endgame. It is not yet a claim that mating should never be cheaper. A concrete position and clock total are more useful than a general feeling that the ending is slow.

## Is the repetition rule a good fix for perpetual check?

It is the current protection against farming time with a repeated check. Once a position has occurred, later occurrences of that position produce no capture value, movement toll, or check chip; the third occurrence is an automatic draw. The rule also stays active after the first repetition, so alternating between two patterns does not bypass it.

The trade-off is deliberate: a repeated position becomes strategically inert instead of continuing to move the clocks. If that creates a worse edge case, please report the position and move sequence so it can be examined against the [full repetition rules](../RULES.md#4-the-repetition-guard).

## Does the tutorial teach the clock ideas, or only the rules text?

It teaches the main clock ideas interactively: moving costs time, captures behave differently in Steal, Burn, and Bounty, a check moves a chip between clocks, and repetition cancels time effects. The [tutorial](https://kirichess.com/learn) is intentionally short; the [rules page](https://kirichess.com/rules) is the reference for the complete tables and edge cases.

If a step explains the rule but does not make the clock change understandable, feedback should include the step and what was unclear. That distinction helps separate a missing rule from a tutorial teaching problem.

## Where can I discuss or report one of these cases?

For a concrete rules or balance example, open an issue with the position, pool, moves, and clock state when possible. For discussion, join the [KiriChess Discord](https://discord.gg/GRusewDw4). The [About page](https://kirichess.com/about) has the project background and contact details.
