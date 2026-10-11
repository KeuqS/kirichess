import type { Metadata } from "next";
import { NavBar } from "@/components/NavBar";
import { Footer } from "@/components/Footer";
import { Card } from "@/components/Card";
import { Link } from "@/i18n/navigation";

// Source of truth for this content is docs/legal/TERMS.md. The two must be kept in sync by
// hand: this page hand-renders the terms as JSX rather than reading the markdown at build
// time, because Dockerfile.frontend's builder stage only `COPY ui/ ./`s the frontend into the
// image; docs/ (the markdown's sibling directory in the repo) is not present in that build
// context, so a `fs.readFileSync` against it would work in `next dev`/local `next build` and
// silently fail in the production image. If you change TERMS.md, change this file to match.
export const metadata: Metadata = {
  title: "Terms of Service, KiriChess",
  description:
    "The rules for using KiriChess: accounts, fair play, and moderation.",
};

export default function TermsPage() {
  return (
    <div className="min-h-screen bg-[var(--background)] text-[var(--foreground)] font-sans selection:bg-[var(--bounty)] selection:text-[#282c34] flex flex-col">
      <NavBar />

      <main
        id="main-content"
        className="flex-1 page-prose pt-10 pb-32 scroll-mt-24"
      >
        <section>
          <h1 className="text-3xl font-bold leading-tight tracking-tight mb-4 text-white text-balance">
            Terms of Service
          </h1>
          <p className="text-[15px] text-[var(--foreground)] leading-relaxed mb-10">
            Last updated: 2026-10-11 - Version: 2026-10-11
          </p>

          <Card accentColorVar="--loss" className="mb-14">
            <p className="text-sm text-[var(--foreground)] leading-relaxed">
              This document describes accurately what the KiriChess codebase
              does, as of the version above. KiriChess is a free, self-funded
              project run by one person rather than a company, and this document
              was written by its author. The Privacy Policy&rsquo;s §4 lists
              every third party that receives data and the basis on which it
              does. See{" "}
              <code className="text-[var(--bounty)]">docs/game/CLAIMS.md</code>{" "}
              in the project repository. Professional-indemnity or
              cyber-liability insurance is disproportionate at zero revenue and
              has not been bought; that decision is revisited only if the
              service takes on paid obligations.
            </p>
          </Card>

          <article className="mb-14">
            <h2 className="text-xl font-bold text-white mb-4 tracking-tight">
              1. Acceptance
            </h2>
            <p className="text-[15px] text-[var(--foreground)] leading-relaxed">
              By creating an account you agree to these Terms and to the{" "}
              <Link
                href="/privacy"
                className="text-[var(--bounty)] font-medium hover:underline"
              >
                Privacy Policy
              </Link>
              . Both are accepted together, once, at signup, and the acceptance
              is timestamped on your account. If these Terms change materially,
              the version string above changes, and accounts that accepted an
              older version are not automatically treated as having accepted the
              new one.
            </p>
          </article>

          <article className="mb-14">
            <h2 className="text-xl font-bold text-white mb-4 tracking-tight">
              2. Minimum age
            </h2>
            <p className="text-[15px] text-[var(--foreground)] leading-relaxed">
              You must be at least{" "}
              <span className="font-semibold text-white">16 years old</span> to
              create an account. This is a single global threshold rather than a
              per-country one: GDPR Art. 8 sets the digital-consent age between
              13 and 16 depending on EU member state, and 16 both satisfies the
              strictest member state and clears the US COPPA threshold of 13
              outright, without needing to detect where you are. This is
              declared by a checkbox at signup, not verified, and no date of
              birth is collected: see the Privacy Policy §9 for why. On credible
              notice that an account holder is under 16, the account is closed
              and anonymised.
            </p>
          </article>

          <article className="mb-14">
            <h2 className="text-xl font-bold text-white mb-4 tracking-tight">
              3. Account responsibilities
            </h2>
            <p className="text-[15px] text-[var(--foreground)] leading-relaxed">
              You are responsible for the account you create: keeping your
              credentials to yourself, and for activity that happens through
              your account. Google sign-in is optional; using it does not create
              a separate set of obligations from registering directly.
            </p>
          </article>

          <article className="mb-14">
            <h2 className="text-xl font-bold text-white mb-4 tracking-tight">
              4. Display names
            </h2>
            <p className="text-[15px] text-[var(--foreground)] leading-relaxed">
              Your display name is public: it appears on leaderboards, on every
              game you play, and in replays. Names are subject to the moderation
              rules this codebase actually enforces: ASCII characters only, no
              impersonation of another player or of staff, and no reserved or
              blocked terms. Names can be changed by you, subject to a cooldown,
              or by a moderator when a name violates policy.
            </p>
          </article>

          <article className="mb-14">
            <h2 className="text-xl font-bold text-white mb-4 tracking-tight">
              5. Fair play
            </h2>
            <p className="text-[15px] text-[var(--foreground)] leading-relaxed mb-4">
              The following are prohibited. Some are supported by statistical
              signals a moderator can run against an account; all of them are
              decided by a human.
            </p>
            <p className="text-[15px] text-[var(--foreground)] leading-relaxed mb-4">
              Those signals run on demand, never on a schedule, and never issue
              a sanction by themselves. Engine-assistance analysis is run
              against a named account or a named game, typically after a
              report. Sandbagging has a separate on-demand scan a moderator can
              start by hand. There is no pool-wide engine scan.
            </p>
            <ul className="list-disc pl-5 space-y-2 text-[15px] text-[var(--foreground)] leading-relaxed">
              <li>
                <span className="font-semibold text-white">
                  Engine assistance
                </span>
                : using chess engine analysis, in real time or otherwise, to
                choose your moves.
              </li>
              <li>
                <span className="font-semibold text-white">Sandbagging</span>:
                deliberately losing rating or manipulating results to obtain an
                easier matchmaking pool.
              </li>
              <li>
                <span className="font-semibold text-white">
                  Deliberate disconnection or stalling
                </span>
                : intentionally dropping your connection, abandoning a game, or
                running a game out without playing it, to manipulate its outcome
                or the opponent&rsquo;s clock.
              </li>
              <li>
                <span className="font-semibold text-white">
                  Harassment, threats or abuse
                </span>{" "}
                of another player or of the maintainer, through any surface the
                site provides.
              </li>
              <li>
                <span className="font-semibold text-white">Unlawful use</span>{" "}
                of the service, including using an account for an unlawful
                purpose or to impersonate anyone.
              </li>
            </ul>
            <p className="text-[15px] text-[var(--foreground)] leading-relaxed mt-4">
              Playing through the public API may use a chess engine. That is
              not engine assistance. When that play is against a person, the
              game is unrated, and the person is shown that the opponent is a
              bot: on the challenge, when the bot sent it, and on the game
              either way. Using an engine against a person in any other game
              is still prohibited.
            </p>
          </article>

          <article className="mb-14">
            <h2 className="text-xl font-bold text-white mb-4 tracking-tight">
              6. Moderation and sanctions
            </h2>
            <div className="flex flex-col gap-5">
              <p className="text-[15px] text-[var(--foreground)] leading-relaxed">
                To report content or conduct on this site, including anything
                you believe to be unlawful, write to{" "}
                <a
                  href="mailto:contact@kirichess.com"
                  className="text-[var(--bounty)] font-medium hover:underline"
                >
                  contact@kirichess.com
                </a>
                . Notices are read by the operator. There is no automated
                decision-making, and you will get a reply. In-product reports
                from a signed-in account are an additional channel, not a
                substitute for that address.
              </p>
              <p className="text-[15px] text-[var(--foreground)] leading-relaxed">
                Reports and moderator actions are handled under the moderation
                system described in the Privacy Policy. Reports are read by the
                operator, typically within a few days. Outcomes are not
                disclosed to the reporter, because doing so would tell a
                suspected cheat they were detected. A player who is sanctioned
                is shown the reason on their profile.
              </p>
              <p className="text-[15px] text-[var(--foreground)] leading-relaxed">
                A sanction (warning, ban from rated play, or full account ban)
                is recorded permanently against your account, including its
                reason.{" "}
                <span className="font-semibold text-white">
                  Sanctions survive account deletion
                </span>
                : because deletion anonymises rather than deletes your account
                row, a sanction record is retained against the anonymised row.
                And if a{" "}
                <span className="font-semibold text-white">
                  full account ban
                </span>{" "}
                is in force at the moment you delete, a one-way keyed hash of
                your email address is retained alongside it, so that deleting
                your account and signing up again with{" "}
                <span className="font-semibold text-white">
                  the same address
                </span>{" "}
                is refused for as long as that ban lasts. That is what stops
                account deletion being used to launder a ban. The Privacy
                Policy, §5 and §6, describes exactly what that hash is, why it
                is not your address, and how long it is kept. Registering an
                entirely new account under a different email is not prevented by
                this.
              </p>
              <p className="text-[15px] text-[var(--foreground)] leading-relaxed">
                A sanction does not rewrite a rating. Games already played keep
                the rating change they produced, and points are not given back
                to the other player.
              </p>
              <p className="text-[15px] text-[var(--foreground)] leading-relaxed">
                Repeated abandonment can also trigger a short automatic cooldown
                on matchmaking. That cooldown is separate from the sanctions
                above: it is not a sanction row, it is not decided by a
                moderator, and it expires on its own.
              </p>
            </div>
          </article>

          <article className="mb-14">
            <h2 className="text-xl font-bold text-white mb-4 tracking-tight">
              7. The service is free, run at cost, and provided as is
            </h2>
            <p className="text-[15px] text-[var(--foreground)] leading-relaxed">
              KiriChess is free to use and independently run by one person,
              funded out of pocket and by voluntary donations (§12) rather than
              by advertising, subscriptions or paid features.{" "}
              <span className="font-semibold text-white">
                It is not a company and not a registered non-profit
              </span>
              , and nothing here should be read as claiming either:
              &ldquo;non-profit&rdquo; is a legal form, and this project does
              not have one. It is provided{" "}
              <span className="font-semibold text-white">as is</span>, without
              any warranty of any kind, and with{" "}
              <span className="font-semibold text-white">
                no guaranteed uptime
              </span>
              . Because the project is pre-launch and under active development,{" "}
              <span className="font-semibold text-white">
                accounts, games and rating history may be lost or reset
              </span>{" "}
              without notice while it remains in this state.
            </p>
          </article>

          <article className="mb-14">
            <h2 className="text-xl font-bold text-white mb-4 tracking-tight">
              8. Termination
            </h2>
            <p className="text-[15px] text-[var(--foreground)] leading-relaxed">
              You may stop using the service, or delete your account, at any
              time (see Privacy Policy §6 for exactly what deletion does). We
              may suspend or terminate an account for violating these Terms, at
              our discretion, through the moderation and sanctions process
              described above.
            </p>
          </article>

          <article className="mb-14">
            <h2 className="text-xl font-bold text-white mb-4 tracking-tight">
              9. Governing law
            </h2>
            <p className="text-[15px] text-[var(--foreground)] leading-relaxed">
              These Terms are governed by{" "}
              <span className="font-semibold text-white">Belgian law</span>, and
              the courts of Belgium have jurisdiction. One carve-out is stated
              here rather than left implied, because it applies whatever this
              section says: if you use KiriChess as a consumer, EU law leaves
              your home-country position intact. Under Rome I Art. 6(2) a choice
              of law cannot deprive you of the mandatory consumer protections of
              the country you live in, and under Brussels I bis Art. 18 you may
              bring proceedings in the courts of the country where you live, and
              may only be sued there. The clause above therefore sets the
              default; it does not move your case to Belgium and does not remove
              protections your own country gives you.
            </p>
          </article>

          <article className="mb-14">
            <h2 className="text-xl font-bold text-white mb-4 tracking-tight">
              10. Software licence and the hosted service
            </h2>
            <p className="text-[15px] text-[var(--foreground)] leading-relaxed">
              These Terms apply to{" "}
              <span className="font-semibold text-white">
                accounts on the hosted service
              </span>{" "}
              at kirichess.com (and its API). They are not the software licence.
              The software is licensed under the Apache License 2.0; the rules
              manual is licensed under CC BY 4.0. The vendored Stockfish engine
              is GNU GPL-3.0: it is loaded as a separate Web Worker program and
              is not relicensed as Apache-2.0. Board-move sounds are CC0 1.0
              recordings; the outcome sounds are original and part of the
              Apache-2.0 software. The name &ldquo;KiriChess&rdquo;, the
              logo and the wordmark are not licensed. See{" "}
              <a
                href="/STOCKFISH.LICENSE"
                className="text-[var(--bounty)] font-medium hover:underline"
              >
                STOCKFISH.LICENSE
              </a>{" "}
              and{" "}
              <a
                href="/sounds/SOUNDS.LICENSE"
                className="text-[var(--bounty)] font-medium hover:underline"
              >
                SOUNDS.LICENSE
              </a>
              . The development repository is public at{" "}
              <a
                href="https://github.com/MichielGekiere/kirichess"
                className="text-[var(--bounty)] font-medium hover:underline"
              >
                github.com/MichielGekiere/kirichess
              </a>
              . That repository is the place to read those licence files. These
              Terms apply to the hosted service whether or not you clone or run
              the software yourself. Running your own instance of that software
              is not use of this service: it does not create an account here, and
              these Terms do not apply to it.
            </p>
          </article>

          <article className="mb-14">
            <h2 className="text-xl font-bold text-white mb-4 tracking-tight">
              11. Changes to these Terms
            </h2>
            <p className="text-[15px] text-[var(--foreground)] leading-relaxed">
              These Terms are versioned by the &ldquo;Last updated&rdquo; /
              &ldquo;Version&rdquo; date at the top, matching the Privacy
              Policy&rsquo;s versioning and the version recorded on your
              account.
            </p>
          </article>

          <article className="mb-14">
            <h2 className="text-xl font-bold text-white mb-4 tracking-tight">
              12. Donations and patron badges
            </h2>
            <div className="flex flex-col gap-5">
              <p className="text-[15px] text-[var(--foreground)] leading-relaxed">
                KiriChess accepts voluntary <strong>one-off</strong> donations
                through Stripe. Donating is optional, and nothing on the site
                asks for it as a condition of anything.{" "}
                <strong>
                  There is no subscription and no recurring donation
                </strong>
                , and no paid tier of any kind.
              </p>
              <p className="text-[15px] text-[var(--foreground)] leading-relaxed">
                <strong>A donation buys nothing.</strong> It is not payment for
                the service, for access to it, or for any feature of it.
                Everything on KiriChess is free to everyone, whether they donate
                or not. No game, variant, rating pool, board, tool or page is
                unlocked, improved, made faster or made available sooner by
                donating, and there is no paid tier.
              </p>
              <p className="text-[15px] text-[var(--foreground)] leading-relaxed">
                <strong>
                  The patron badge is a token of thanks, not a thing you have
                  bought.
                </strong>{" "}
                Donating may display a small heart icon beside your display
                name. It is decorative and nothing else: it has no effect on
                pairing, matchmaking, ratings, clocks, the clock economy, move
                legality, moderation, or any other part of how the game is
                played, and it grants no priority, privilege or access. The
                badge lapses by itself after a set period rather than lasting
                forever, and it may be changed or withdrawn at any time. Its
                removal is not a refund event, because nothing was sold.
              </p>
              <p className="text-[15px] text-[var(--foreground)] leading-relaxed">
                <strong>
                  Payments are handled by Stripe, not by KiriChess.
                </strong>{" "}
                Card details are never sent to, seen by, or stored on KiriChess
                servers. The{" "}
                <Link
                  href="/privacy"
                  className="text-[var(--bounty)] font-medium hover:underline"
                >
                  Privacy Policy
                </Link>{" "}
                records what Stripe receives.
              </p>
              <p className="text-[15px] text-[var(--foreground)] leading-relaxed">
                <strong>Refunds.</strong> A one-off donation is a single charge;
                there is nothing to cancel and nothing recurring to stop.
                Because donations are voluntary gifts rather than purchases,
                they are not generally refundable, but a donation made in error
                will be dealt with in good faith: write to the contact address
                in the{" "}
                <Link
                  href="/privacy"
                  className="text-[var(--bounty)] font-medium hover:underline"
                >
                  Privacy Policy
                </Link>
                .
              </p>
            </div>
          </article>

          <p className="text-[15px] text-[var(--foreground)] leading-relaxed">
            See also the{" "}
            <Link
              href="/privacy"
              className="text-[var(--bounty)] font-medium hover:underline"
            >
              Privacy Policy
            </Link>
            .
          </p>
        </section>
      </main>
      <Footer />
    </div>
  );
}
