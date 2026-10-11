import type { Metadata } from "next";
import type { ReactNode } from "react";
import { NavBar } from "@/components/NavBar";
import { Footer } from "@/components/Footer";
import { Link } from "@/i18n/navigation";

// Source of truth for this content is docs/legal/PRIVACY.md. The two must be kept in sync by
// hand: this page hand-renders the policy as JSX rather than reading the markdown at build
// time, because Dockerfile.frontend's builder stage only `COPY ui/ ./`s the frontend into the
// image; docs/ (the markdown's sibling directory in the repo) is not present in that build
// context, so a `fs.readFileSync` against it would work in `next dev`/local `next build` and
// silently fail in the production image. If you change PRIVACY.md, change this file to match.
export const metadata: Metadata = {
  title: "Privacy Policy, KiriChess",
  description:
    "How KiriChess collects, uses and lets you erase or export your data.",
};

function SubProcessorRow({
  party,
  purpose,
  status,
}: {
  party: string;
  purpose: string;
  status: string;
}) {
  return (
    <tr className="border-b border-[var(--border)]/50 align-top">
      <td className="py-3 pr-4 font-semibold text-white">{party}</td>
      <td className="py-3 pr-4 text-sm text-[var(--foreground)] leading-relaxed">
        {purpose}
      </td>
      <td className="py-3 text-sm text-[var(--foreground)] leading-relaxed">
        {status}
      </td>
    </tr>
  );
}

function RetentionRow({
  what,
  keptFor,
  why,
}: {
  what: ReactNode;
  keptFor: ReactNode;
  why: ReactNode;
}) {
  return (
    <tr className="border-b border-[var(--border)]/50 align-top">
      <td className="py-3 pr-4 font-semibold text-white">{what}</td>
      <td className="py-3 pr-4 text-sm text-[var(--foreground)] leading-relaxed">
        {keptFor}
      </td>
      <td className="py-3 text-sm text-[var(--foreground)] leading-relaxed">
        {why}
      </td>
    </tr>
  );
}

export default function PrivacyPage() {
  return (
    <div className="min-h-screen bg-[var(--background)] text-[var(--foreground)] font-sans selection:bg-[var(--bounty)] selection:text-[#282c34] flex flex-col">
      <NavBar />

      <main
        id="main-content"
        className="flex-1 page-prose pt-10 pb-32 scroll-mt-24"
      >
        <section>
          <h1 className="text-3xl font-bold leading-tight tracking-tight mb-4 text-white text-balance">
            Privacy Policy
          </h1>
          <p className="text-[15px] text-[var(--foreground)] leading-relaxed mb-10">
            Last updated: 2026-10-11 - Version: 2026-10-11
          </p>

          <article className="mb-14">
            <h2 className="text-xl font-bold text-white mb-4 tracking-tight">
              1. Who runs this
            </h2>
            <p className="text-[15px] text-[var(--foreground)] leading-relaxed">
              KiriChess is run by{" "}
              <span className="font-semibold text-white">Michiel Gekiere</span>,
              a private individual resident in Belgium, acting as the data
              controller for the purposes of the GDPR. There is no company
              behind it.
            </p>
            <p className="text-[15px] text-[var(--foreground)] leading-relaxed mt-5">
              Contact:{" "}
              <a
                href="mailto:privacy@kirichess.com"
                className="text-[var(--bounty)] font-medium hover:underline"
              >
                privacy@kirichess.com
              </a>
              . Every request under §8, and any question about this policy, goes
              to that address and reaches the controller directly.
            </p>
            <p className="text-[15px] text-[var(--foreground)] leading-relaxed mt-5">
              No postal address is published. Art. 13(1)(a) asks for the
              controller&rsquo;s identity and contact details, which the two
              lines above give; it does not require a street address, and
              publishing a private home address for a free hobby project is a
              disproportionate answer to it. The separate transparency rules for
              commercial online services (in Belgium, Book XII WER) are scoped
              to services &ldquo;normally provided for remuneration&rdquo;. A
              free site that accepts one-off donations sits outside them: a gift
              buys nothing, so there is no service provided for remuneration to
              disclose an address for. If KiriChess ever charges for access, or
              sells a recurring tier that delivers something in return, those
              rules apply and this section gains a geographic address at that
              point.
            </p>
            <p className="text-[15px] text-[var(--foreground)] leading-relaxed mt-5">
              This paragraph used to say the address appeared &ldquo;if
              KiriChess ever takes money for anything&rdquo;, which promised
              more than the rules ask and more than this project intends:
              one-off donations have been live since 2026-08-20 without
              triggering anything. Narrowed on 2026-09-04 to state the test that
              actually applies. Recurring tiers are off in the product for the
              same reason.
            </p>
          </article>

          <article className="mb-14">
            <h2 className="text-xl font-bold text-white mb-4 tracking-tight">
              2. What is collected
            </h2>
            <ul className="flex flex-col gap-4 text-[15px] text-[var(--foreground)] leading-relaxed">
              <li>
                <span className="font-semibold text-white">Account</span>:
                email, a hashed password (ASP.NET Core Identity&rsquo;s default
                hasher: PBKDF2 with HMAC-SHA256; the plaintext password is never
                stored or logged), display name and its normalized form used
                only to enforce uniqueness and block impersonation, account
                creation time, email-confirmation state,
                lockout/failed-login counters, and, if you set one, a cropped
                JPEG served at{" "}
                <code className="text-[var(--bounty)]">
                  /api/users/{"{id}"}/avatar
                </code>
                .
              </li>
              <li>
                <span className="font-semibold text-white">
                  Consent (this phase)
                </span>{" "}
                : the timestamp of accepting the Terms and Privacy Policy, the
                policy version accepted, and the timestamp of the age
                declaration made at signup.
              </li>
              <li>
                <span className="font-semibold text-white">
                  Optional Google sign-in
                </span>{" "}
                : only the provider&rsquo;s opaque account key. No Google
                password, access token or refresh token is ever stored, and this
                system never calls a Google API on your behalf.
              </li>
              <li>
                <span className="font-semibold text-white">Gameplay</span>:
                every game&rsquo;s full move list (each ply: the move, clock
                state, and server-measured think time), the exact rule
                configuration the game was played under, both players&rsquo;
                account ids, start/end timestamps, and the resulting Glicko-2
                rating and rating history (before/after rating, deviation and
                volatility per game).
              </li>
              <li>
                <span className="font-semibold text-white">
                  Direct challenges
                </span>
                : a challenge link&rsquo;s challenger id, the id of whoever
                accepted it, and its expiry.
              </li>
              <li>
                <span className="font-semibold text-white">Notifications</span>:
                two opt-out timestamps recording when you unsubscribed from each
                of the two game-event mail categories in §4 (&ldquo;your
                challenge was accepted&rdquo;, &ldquo;your opponent wants a
                rematch&rdquo;).
              </li>
              <li>
                <span className="font-semibold text-white">Web push</span>: if
                you enable browser notifications, the push service&rsquo;s
                endpoint URL, and the P-256 public key and auth secret used to
                encrypt a message to you, one row per browser you enabled it on.
              </li>
              <li>
                <span className="font-semibold text-white">Moderation</span>:
                reports you file against other players, reports other players
                file against you, and any sanction issued against your account.
                A moderator-only note exists on both reports and sanctions; it
                is never shown to you and never exported. Each game also stores,
                per seat, how many times that player dropped with no connection
                of theirs left, including drops they returned from within 30
                seconds. Your own count is shown to moderators and included in
                your data export; it does not by itself restrict your account.
                The other player&rsquo;s count is not in your export.
              </li>
              <li>
                <span className="font-semibold text-white">Sessions</span>:
                refresh tokens, stored as a SHA-256 hash, never the raw token,
                plus issue time, expiry time and revocation time.
              </li>
              <li>
                <span className="font-semibold text-white">API tokens</span>:
                independently revocable bot credentials: a display name you
                chose, <code className="text-[var(--bounty)]">read</code>/
                <code className="text-[var(--bounty)]">play</code> scopes,
                issue/expiry/revocation/last-used times, a short prefix for the
                profile page, and a SHA-256 hash of the secret. The raw{" "}
                <code className="text-[var(--bounty)]">kirichess_pat_...</code> value is
                shown once at creation and is never stored.
              </li>
              <li>
                <span className="font-semibold text-white">
                  Sign-in exchange codes
                </span>
                : when you sign in with Google, a single-use code is stored as a
                SHA-256 hash, never the raw code, with your account id, an issue
                time, an expiry 60 seconds later, and the time it was used. It
                exists so that a 30-day refresh token never travels in a
                redirect URL.
              </li>
              <li>
                <span className="font-semibold text-white">
                  Two-factor authentication
                </span>{" "}
                (optional; mandatory for moderators): if you turn on 2FA, this
                system stores a TOTP shared secret and a set of one-time
                recovery codes (both via ASP.NET Core Identity&rsquo;s own token
                store, no third-party authenticator service is involved), plus a
                short-lived, single-use login challenge ticket each time you
                sign in, stored as a SHA-256 hash the same way a sign-in
                exchange code is.
              </li>
              <li>
                <span className="font-semibold text-white">Logs</span>: your
                account id (not your email) is attached to the log lines
                produced while you are signed in, so an error can be traced to
                an account without a human reading email addresses out of logs.
              </li>
              <li>
                <span className="font-semibold text-white">Rate-limiting</span>:
                your IP address is used as an in-memory or Redis counter key to
                throttle most requests. Calls on the public{" "}
                <code className="text-[var(--bounty)]">/api/v1</code> bot API
                are partitioned by API token id instead. Neither key is written
                to a database table and neither is retained beyond the counting
                window.
              </li>
              <li>
                <span className="font-semibold text-white">
                  Anonymous counters
                </span>
                : a count, per calendar day (UTC), of how many times each of a
                fixed list of public pages was loaded and how many times a few
                first-session milestones happened (a tutorial finished, a search
                for an opponent started). Alongside each count, a one-word
                campaign label taken from a{" "}
                <code className="text-[var(--bounty)]">?src=</code> in the link
                that was clicked, never from your browser&rsquo;s referrer.
                Nothing identifies who did it: no cookie, no browser storage, no
                visitor or session identifier, no IP address, no user agent, and
                no account id even when you are signed in, the counters cannot
                tell a signed-in visitor from an anonymous one, and do not try.
              </li>
            </ul>
            <p className="text-[15px] text-[var(--foreground)] leading-relaxed mt-5">
              These counters are events, not people. With no identifier of any
              kind there is no way to tell one visitor loading a page five times
              from five visitors loading it once, and automated crawlers are
              counted the same as everybody else. Nor can any two of these
              counts be linked back together into one person&rsquo;s visit. That
              is a deliberate trade: the numbers are good enough to notice that
              a page got ten times busier, and are not capable of following
              anyone around.
            </p>
            <p className="text-[15px] text-[var(--foreground)] leading-relaxed mt-5">
              Nothing above includes a date of birth. See §9.
            </p>
          </article>

          <article className="mb-14">
            <h2 className="text-xl font-bold text-white mb-4 tracking-tight">
              2b. What this stores on your device
            </h2>
            <p className="text-[15px] text-[var(--foreground)] leading-relaxed mb-5">
              Separately from the list above, here is everything the site puts
              in your browser or on the API host, in one place:
            </p>
            <ul className="list-disc pl-5 space-y-2 text-[15px] text-[var(--foreground)] leading-relaxed">
              <li>
                <span className="font-semibold text-white">
                  Sign-in tokens.
                </span>{" "}
                Your access and refresh tokens are kept in your browser&rsquo;s{" "}
                <code className="text-[var(--bounty)]">localStorage</code>, so
                you stay signed in across a reload without a server-side
                session.
              </li>
              <li>
                <span className="font-semibold text-white">
                  Tutorial progress.
                </span>{" "}
                One flag in{" "}
                <code className="text-[var(--bounty)]">localStorage</code>{" "}
                recording that you finished the interactive tutorial, so it is
                not shown to you again.
              </li>
              <li>
                <span className="font-semibold text-white">Offline shell.</span>{" "}
                A small Cache Storage entry (a service worker) holding the{" "}
                <code className="text-[var(--bounty)]">/learn</code> page and a
                few static assets, so that one page still works with no
                connection.
              </li>
              <li>
                <span className="font-semibold text-white">
                  Google sign-in cookies.
                </span>{" "}
                Only if you use Google sign-in: a short-lived correlation cookie
                and a{" "}
                <code className="text-[var(--bounty)]">kirichess.external</code>{" "}
                cookie on the API host, both gone within minutes, needed to
                complete that one sign-in attempt and nothing else.
              </li>
              <li>
                <span className="font-semibold text-white">
                  Two-factor setup.
                </span>{" "}
                Only while a Google account is turning on two-factor
                authentication:{" "}
                <code className="text-[var(--bounty)]">sessionStorage</code>{" "}
                holds the new authenticator secret and a one-time
                reauthentication code, so that setup can survive the redirect to
                Google and back. Both are removed when setup finishes or is
                cancelled.
              </li>
              <li>
                <span className="font-semibold text-white">The bot check.</span>{" "}
                The login and register page loads a script, and sometimes an
                iframe, from{" "}
                <code className="text-[var(--bounty)]">
                  challenges.cloudflare.com
                </code>{" "}
                (Cloudflare&rsquo;s Turnstile widget). What that widget itself
                stores in your browser is Cloudflare&rsquo;s to state, not
                something this document can verify from the code here; see
                Cloudflare&rsquo;s own documentation on what Turnstile stores.
              </li>
            </ul>
            <p className="text-[15px] text-[var(--foreground)] leading-relaxed mt-5">
              All of the above exists because the site does not work without it,
              or because you started a sign-in that needs it to finish. That is
              also why there is no cookie banner: everything here is strictly
              necessary storage, exempt from the <em>consent</em> requirement of
              ePrivacy Art. 5(3) as transposed in Belgium, and this section is
              the <em>information</em> the same rule still requires.
            </p>
          </article>

          <article className="mb-14">
            <h2 className="text-xl font-bold text-white mb-4 tracking-tight">
              3. Why (lawful basis, GDPR Art. 6)
            </h2>
            <ul className="list-disc pl-5 space-y-2 text-[15px] text-[var(--foreground)] leading-relaxed">
              <li>
                <span className="font-semibold text-white">Contract</span> (Art.
                6(1)(b)): your account, display name, games, ratings, sessions
                and API tokens exist because running a chess account and its
                games is the service you asked for.
              </li>
              <li>
                <span className="font-semibold text-white">
                  Legitimate interest
                </span>{" "}
                (Art. 6(1)(f)): reports, sanctions, the user-id log scope, and
                IP-based rate limiting exist to prevent abuse, cheating and
                denial-of-service against other players and the service itself.
                The breached-password check in §4, when the flag that turns it
                on is set, rests on the same basis: it exists to keep a password
                that is already public from being accepted.
              </li>
              <li>
                <span className="font-semibold text-white">Consent</span> (Art.
                6(1)(a)): nothing is currently collected on the basis of consent
                alone. Terms/Privacy acceptance and the age declaration are a
                condition of using the service, not a separable consent to an
                optional processing activity. The anonymous counters in §2
                contain no personal data and therefore sit outside Art. 6
                entirely, rather than being justified under it.
              </li>
            </ul>
          </article>

          <article className="mb-14">
            <h2 className="text-xl font-bold text-white mb-4 tracking-tight">
              4. Who it is shared with
            </h2>
            <div className="overflow-x-auto">
              <table className="w-full text-left border-collapse">
                <thead>
                  <tr className="border-b border-[var(--border)]">
                    <th className="table-header py-2 pr-4">Party</th>
                    <th className="table-header py-2 pr-4">Purpose</th>
                    <th className="table-header py-2">Status in code</th>
                  </tr>
                </thead>
                <tbody>
                  <SubProcessorRow
                    party="Resend"
                    purpose="Transactional email (account confirmation, password reset), and game-event notification email (a challenge you sent was accepted, an opponent wants a rematch)"
                    status="Live. An API key is configured in production, so all four message types above are sent through Resend. It receives the recipient address and the message itself, nothing else. Confirmation and reset carry no unsubscribe link, they are security-critical, not reach-back mail; the two notification categories carry a one-click unsubscribe link and are sent only if you have not already used it."
                  />
                  <SubProcessorRow
                    party="Your browser's push service (Google FCM, Mozilla, or Apple, whichever your browser uses)"
                    purpose="Delivers a web push notification if you enable browser notifications"
                    status="Armed but not sending. /api/push/subscribe accepts subscriptions today, but the signing keys a push actually requires are not configured in any deployed environment, so none has ever been sent. If one is sent, the push service sees the destination endpoint and the timing, never the content: the payload is encrypted to your subscription's own keys before it leaves this server."
                  />
                  <SubProcessorRow
                    party="Sentry"
                    purpose="Error monitoring, and since Phase 9e.7, distributed request traces"
                    status="Live. Configured in both the frontend and the backend with sendDefaultPii: false. Traces travel the same channel as error events (OTLP), so wherever Sentry is receiving one it is receiving the other: request paths (which can carry a game id or an account id where the route itself contains one), timings and status codes, never a request body and never an email address. Still no profiling or session replay."
                  />
                  <SubProcessorRow
                    party="Google"
                    purpose="Optional OAuth sign-in"
                    status="Wired, but inactive without a configured client ID and secret; none exist in any deployed environment today."
                  />
                  <SubProcessorRow
                    party="Have I Been Pwned (Superlative Enterprises Pty Ltd, Queensland, Australia)"
                    purpose="Breached-password check at registration and password reset"
                    status="Off unless Auth:BreachedPasswordCheckEnabled is set. Nothing in this deployment records that flag as on. When it is, the first five characters of a SHA-1 hash of the candidate password are sent to api.pwnedpasswords.com. The password and the full hash stay here. Their privacy policy says they never receive the password and store no identifying information about whose password it was. No account is held with them and no data processing agreement is on file, so no Data Privacy Framework or Standard Contractual Clauses claim is made. Their own policy says personal information they hold is stored in a western United States Azure datacentre, and Cloudflare fronts the API."
                  />
                  <SubProcessorRow
                    party="Fly.io"
                    purpose="Application hosting, both the API and the site"
                    status="Live. The application and its API run here."
                  />
                  <SubProcessorRow
                    party="Neon, LLC (a Databricks company)"
                    purpose="Managed Postgres"
                    status="Live. This is where your account, games and ratings are actually stored."
                  />
                  <SubProcessorRow
                    party="Cloudflare"
                    purpose="DNS, proxy and CDN in front of both hosts, mail routing for the contact address in §1, Turnstile, the bot-check widget on the login and register page (challenges.cloudflare.com, see §2b), and an R2 bucket prepared for an off-site copy of the database"
                    status="Live for DNS, proxy, CDN, mail routing and Turnstile. All traffic to the site passes through it. The backup bucket is in Cloudflare's EU jurisdiction and holds no dump: the weekly job that would upload one is switched off (see GitHub, below). The Art. 28 contract already filed for Cloudflare on 2026-08-17 is the contract for that storage."
                  />
                  <SubProcessorRow
                    party="Better Stack (Logtail)"
                    purpose="Application and platform logs forwarded from Fly"
                    status="Live. A Vector collector on Fly forwards the org's log stream, including the account id attached as log scope (§2). Email addresses are not in those lines. No explicit log-retention setting is configured, so the current answer is the vendor's plan default. Art. 28 contract filed 2026-09-04 (DPA last updated 25 February 2025)."
                  />
                  <SubProcessorRow
                    party="Stripe (contracting entity Stripe Payments Europe, Limited, Ireland; US operations Stripe, LLC)"
                    purpose="Donation payments, and the patron badge that follows from them (Terms §12)"
                    status="Live when you donate, and only then. Nothing is sent to Stripe unless you start a donation. Card details go to Stripe directly and never reach KiriChess servers. If you are signed in, the donation carries a random single-use reference that means nothing outside our database; your account id, name and email are not sent to Stripe by us. Stripe returns the amount, whether it recurs, and its own customer and subscription ids, which are stored against your account so the badge can be removed when a subscription ends."
                  />
                  <SubProcessorRow
                    party="GitHub (GitHub, Inc.)"
                    purpose="The off-site backup job would restore a full database dump on a GitHub-hosted runner in order to check that the copy in Cloudflare R2 can actually be restored"
                    status="Not receiving data. The weekly schedule is off, and the job must not be started by hand, until a GitHub data processing agreement has been accepted. No such agreement is on file, so no Data Privacy Framework or Standard Contractual Clauses claim is made."
                  />
                </tbody>
              </table>
            </div>
            <p className="text-[15px] text-[var(--foreground)] leading-relaxed mt-5">
              Each processor above operates under its standard data processing
              agreement (Art. 28(3)), incorporated into the terms accepted when
              the account with it was created, and the applicable version of
              each is kept on file. Google, the browser push service, Have I
              Been Pwned, and GitHub are deliberately absent from that statement. For OAuth
              sign-in Google decides its own purposes and is an{" "}
              <span className="font-semibold text-white">
                independent controller
              </span>{" "}
              rather than a processor acting on instructions, so Art. 28 does
              not apply to it. The browser push service is a different kind of
              absence again: it is not a party this deployment contracts with at
              all, chosen by your browser rather than by this system, and
              whether sending it a push is even a controller/processor
              relationship in the ordinary sense is unsettled, which is exactly
              why no DPA is claimed for it. Have I Been Pwned is absent for a
              third reason: this deployment holds no account with them and has
              no contract, so there is no data processing agreement to file. The
              check, when the flag that turns it on is set, is a query to a
              public API. Stripe is filed: its Data Processing
              Agreement (last updated 18 November 2025, accepted 2026-09-04)
              makes Stripe a processor for the payment-platform services we
              instruct, and an independent controller for anti-fraud,
              anti-money-laundering, card-scheme and tax obligations it cannot
              be instructed out of. That split is Stripe&rsquo;s own wording,
              not a characterisation we invented. Better Stack is filed: its
              Data Processing Agreement (last updated 25 February 2025,
              incorporated into the Terms of Use and captured 2026-09-04) makes
              it a processor for the log stream we send, and an independent
              controller for its own account/billing data. GitHub is absent
              for a fourth reason: the backup job is switched off, and no
              GitHub data processing agreement has been accepted. Accepting
              that agreement is what would make GitHub a processor, and the
              weekly job stays off until that acceptance is recorded. What is
              owed for Google, the push service, Have I Been Pwned, and GitHub
              is the disclosure and transfer basis below, and it is given
              there.
            </p>
            <p className="text-[15px] text-[var(--foreground)] leading-relaxed mt-5">
              <span className="font-semibold text-white">
                International transfers
              </span>{" "}
              (Art. 13(1)(f), Art. 44 et seq.). Every party in the table above
              is a United States company{" "}
              <span className="font-semibold text-white">
                except Stripe and Have I Been Pwned
              </span>
              , so using any of the others, once active, is a transfer of
              personal data outside the EEA. Fly.io, Neon, Sentry, Google, Cloudflare and
              Better Stack each self-certify under the EU-U.S. Data Privacy
              Framework (and its UK and Swiss extensions), the adequacy
              mechanism for exactly this transfer, with Neon covered as Neon,
              LLC under the certification of its parent Databricks rather than
              one of its own; Resend&rsquo;s own Data Processing Addendum relies
              on Standard Contractual Clauses instead, without a DPF claim.
              Standard Contractual Clauses, incorporated into each
              provider&rsquo;s DPA, are the fallback basis across those
              contracted processors, for any processing a given provider&rsquo;s
              DPF certification does not cover or if that certification lapses.
              Better Stack&rsquo;s ingest host currently used by this deployment
              is in the United States (us-west-2); its DPA (last updated 25
              February 2025) relies on the DPF for that transfer, with SCCs as
              fallback. The browser push service is the exception: it is not
              covered by either mechanism. Google, Mozilla or Apple, whichever
              your browser selects, may or may not itself hold a DPF
              certification, but this deployment makes no claim on its behalf,
              because there is no contract to make the claim under. That gap is
              the disclosure this row exists to give, not a compliance position.
            </p>
            <p className="text-[15px] text-[var(--foreground)] leading-relaxed mt-5">
              Have I Been Pwned is the other exception to the United States
              sentence above. The operator is Superlative Enterprises Pty Ltd, a
              company in Queensland, Australia. Their privacy policy says the
              personal information they hold is stored in a western United
              States Azure datacentre, and that Cloudflare fronts the API. This
              deployment holds no account with them and has no data processing
              agreement, so it makes no Data Privacy Framework or Standard
              Contractual Clauses claim on their behalf. That is the same kind
              of gap as the browser push service: the disclosure is the
              position. Their policy also says a range query never includes the
              password, and that they store no identifying information about
              whose password it was.
            </p>
            <p className="text-[15px] text-[var(--foreground)] leading-relaxed mt-5">
              GitHub is a United States company, so it would be the same kind
              of transfer as the others once a dump is restored on a
              GitHub-hosted runner. No data processing agreement has been
              accepted, so this deployment makes no Data Privacy Framework or
              Standard Contractual Clauses claim on GitHub&rsquo;s behalf, and
              the job that would make the transfer is switched off. The R2
              bucket prepared for that dump is locked to Cloudflare&rsquo;s EU
              jurisdiction; it holds no dump today, and the storage itself is
              the Cloudflare contract already on file, not a separate transfer.
            </p>
            <p className="text-[15px] text-[var(--foreground)] leading-relaxed mt-5">
              Stripe is the exception worth stating separately, because the
              first leg of it is not an export at all. The contracting party for
              a business established in Belgium is{" "}
              <span className="font-semibold text-white">
                Stripe Payments Europe, Limited
              </span>
              , an Irish company, so payment data reaches an EEA entity first.
              The onward transfer from there to Stripe&rsquo;s United States
              operations is covered by the same mechanism as the rest of this
              table:{" "}
              <span className="font-semibold text-white">Stripe, LLC</span>{" "}
              self-certifies under the EU-U.S. DPF, the UK Extension and the
              Swiss-U.S. DPF (verified against Stripe&rsquo;s published Data
              Privacy Framework Policy, 2026-08-24; note the certified entity is
              Stripe, LLC, not Stripe, Inc., which is the name on the retired
              Privacy Shield listing). The Standard Contractual Clauses in
              Stripe&rsquo;s own Data Processing Agreement remain the fallback
              here exactly as they do for the others.
            </p>
            <p className="text-[15px] text-[var(--foreground)] leading-relaxed mt-5">
              Sentry separately offers EU-region data residency; this deployment
              has not selected it, and doing so is a config choice, not a
              compliance requirement, given the DPF/SCC coverage above. This
              paragraph states the transfer mechanism relied on. Stripe&rsquo;s
              and Better Stack&rsquo;s DPAs are on file (see the processor
              statement above).
            </p>
          </article>

          <article className="mb-14">
            <h2 className="text-xl font-bold text-white mb-4 tracking-tight">
              5. How long it is kept
            </h2>
            <p className="text-[15px] text-[var(--foreground)] leading-relaxed mb-5">
              Some of this is deleted automatically. Most of it is not, and the
              parts that are kept indefinitely are listed here rather than left
              to be discovered.
            </p>
            <div className="overflow-x-auto">
              <table className="w-full text-left border-collapse">
                <thead>
                  <tr className="border-b border-[var(--border)]">
                    <th className="table-header py-2 pr-4">What</th>
                    <th className="table-header py-2 pr-4">Kept for</th>
                    <th className="table-header py-2">Why</th>
                  </tr>
                </thead>
                <tbody>
                  <RetentionRow
                    what={
                      <>
                        Refresh tokens (
                        <code className="text-[var(--bounty)]">
                          RefreshTokens
                        </code>
                        )
                      </>
                    }
                    keptFor="Until 30 days after the token's own expiry, so at most about 60 days from issue"
                    why={
                      <>
                        A rotated token&rsquo;s row is what lets the server tell
                        a replayed{" "}
                        <span className="font-semibold text-white">stolen</span>{" "}
                        token from an unknown one. Deleting it as soon as it is
                        rotated would silently switch off that detection.
                      </>
                    }
                  />
                  <RetentionRow
                    what={
                      <>
                        API tokens (
                        <code className="text-[var(--bounty)]">ApiTokens</code>)
                      </>
                    }
                    keptFor="Revoked or expired tokens: until 30 days after that event. A token lasts 90 days unless you choose another life, from 1 to 365 days, when you create it"
                    why={
                      <>
                        Unlike refresh tokens these do not rotate, so a revoked
                        row is not a theft tripwire. Signing out, or turning 2FA
                        on or off, does{" "}
                        <span className="font-semibold text-white">not</span>{" "}
                        revoke them.
                      </>
                    }
                  />
                  <RetentionRow
                    what={
                      <>
                        Sign-in exchange codes (
                        <code className="text-[var(--bounty)]">
                          ExternalLoginCodes
                        </code>
                        )
                      </>
                    }
                    keptFor="Until 1 day after expiry: the codes themselves live 60 seconds"
                    why="Nothing is learned from a spent code, so it is kept only long enough to outlast clock skew."
                  />
                  <RetentionRow
                    what={
                      <>
                        2FA login challenge tickets (
                        <code className="text-[var(--bounty)]">
                          TwoFactorChallenges
                        </code>
                        )
                      </>
                    }
                    keptFor="Until 1 day after expiry: the tickets themselves live 5 minutes"
                    why="Same reasoning as sign-in exchange codes: a spent ticket carries no detection value."
                  />
                  <RetentionRow
                    what={
                      <>
                        2FA shared secret and recovery codes (
                        <code className="text-[var(--bounty)]">
                          AspNetUserTokens
                        </code>
                        , if 2FA is on)
                      </>
                    }
                    keptFor="For as long as 2FA stays enabled on the account"
                    why="Needed to verify a code on every sign-in; destroyed the moment you disable 2FA or delete your account (§6)."
                  />
                  <RetentionRow
                    what={
                      <>
                        Your account and consent record (
                        <code className="text-[var(--bounty)]">
                          AspNetUsers
                        </code>
                        )
                      </>
                    }
                    keptFor="For as long as the account exists"
                    why="It is the account. It is anonymised when you ask (§6)."
                  />
                  <RetentionRow
                    what={
                      <>
                        Direct challenge links (
                        <code className="text-[var(--bounty)]">Challenges</code>
                        )
                      </>
                    }
                    keptFor="Until 7 days after the link's own expiry"
                    why="Swept automatically. An accepted challenge is kept briefly past expiry as the audit trail for how the resulting game came to exist."
                  />
                  <RetentionRow
                    what={
                      <>
                        Your games and their move lists (
                        <code className="text-[var(--bounty)]">Games</code>)
                      </>
                    }
                    keptFor="Indefinitely"
                    why={
                      <>
                        A game is a{" "}
                        <span className="font-semibold text-white">
                          two-party record
                        </span>{" "}
                        : deleting it would destroy your opponent&rsquo;s copy
                        of a game they played. This is the same reasoning that
                        governs erasure (§6).
                      </>
                    }
                  />
                  <RetentionRow
                    what={
                      <>
                        Ratings and rating history (
                        <code className="text-[var(--bounty)]">Ratings</code>,{" "}
                        <code className="text-[var(--bounty)]">
                          RatingHistories
                        </code>
                        )
                      </>
                    }
                    keptFor="Indefinitely"
                    why="Every rating either player holds is computed from these; removing one player's history invalidates the other's rating."
                  />
                  <RetentionRow
                    what={
                      <>
                        Reports (
                        <code className="text-[var(--bounty)]">Reports</code>)
                      </>
                    }
                    keptFor="Indefinitely"
                    why="Retained under Art. 17(3)(e), for the establishment and exercise of legal claims."
                  />
                  <RetentionRow
                    what={
                      <>
                        Sanctions (
                        <code className="text-[var(--bounty)]">
                          UserSanctions
                        </code>
                        )
                      </>
                    }
                    keptFor="Indefinitely"
                    why="Same basis. A ban that could be erased by deleting the account it was issued against would not be a ban."
                  />
                  <RetentionRow
                    what="Ban-evasion marker (on the deleted account's row)"
                    keptFor="Only if you were under a full account ban when the account was deleted, and only while that ban is still in force. Cleared 30 days after the ban is lifted or expires"
                    why="Same basis. It is a one-way keyed hash of your email address, not the address, and it exists so a ban cannot be shed by deleting the account and signing up again with the same address (§6, and Terms §6)."
                  />
                  <RetentionRow
                    what="Notification opt-out timestamps"
                    keptFor="For as long as the account exists"
                    why="Part of the account row (§2); needed so an unsubscribe stays honoured rather than being re-asked."
                  />
                  <RetentionRow
                    what={
                      <>
                        Web push subscriptions (
                        <code className="text-[var(--bounty)]">
                          WebPushSubscriptions
                        </code>
                        )
                      </>
                    }
                    keptFor="Until you disable notifications for that browser, or the push service reports the subscription gone, or you delete your account"
                    why="Not currently swept on a timer. Removed entirely when you delete your account (§6)."
                  />
                  <RetentionRow
                    what="Rate-limiting counters"
                    keptFor="The counting window only"
                    why="They live in memory or in Redis with an expiry and are never written to a database table."
                  />
                  <RetentionRow
                    what={
                      <>
                        Anonymous counters (
                        <code className="text-[var(--bounty)]">
                          DailyCounters
                        </code>
                        )
                      </>
                    }
                    keptFor="Indefinitely"
                    why="They hold no personal data, and traffic from a particular week cannot be counted again afterwards."
                  />
                  <RetentionRow
                    what="Server logs"
                    keptFor="Whatever Fly and Better Stack keep"
                    why="They carry your account id, never your email address (§2). Better Stack has no explicit retention set, so the current answer is the vendor's plan default."
                  />
                  <RetentionRow
                    what="Off-site database copy (Cloudflare R2)"
                    keptFor="None today. When a dump exists, the eight newest copies are kept"
                    why="The bucket is empty. The job that would fill it is off until GitHub's data processing agreement is accepted (§4)."
                  />
                </tbody>
              </table>
            </div>
            <p className="text-[15px] text-[var(--foreground)] leading-relaxed mt-5">
              The automatic deletions above are performed by a job that runs
              once a day. Everything else on this list is removed only when you
              delete your account, and only to the extent §6 describes.
            </p>
            <p className="text-[15px] text-[var(--foreground)] leading-relaxed mt-5">
              <span className="font-semibold text-white">
                Inactive accounts are not deleted automatically.
              </span>{" "}
              An account that has never been used still holds an email address,
              and the storage-limitation principle points at exactly that case.
              This system reports such accounts to a moderator for a human
              decision rather than erasing them on a timer, because there are
              currently no real accounts to check that decision against, and an
              erasure predicate that has never run against a real account is not
              one to trust with something irreversible. If that changes, this
              section changes with it.
            </p>
          </article>

          <article className="mb-14">
            <h2 className="text-xl font-bold text-white mb-4 tracking-tight">
              6. Erasure: what &ldquo;delete my account&rdquo; actually does
            </h2>
            <p className="text-[15px] text-[var(--foreground)] leading-relaxed mb-5">
              Deleting your account does not remove your row from the database,
              because a chess game is a two-party record: your opponent&rsquo;s
              copy of a game you played, and every rating computed from it,
              would otherwise be destroyed along with your identity. Instead,
              deletion is anonymisation and tombstoning.
            </p>
            <p className="text-[15px] text-[var(--foreground)] leading-relaxed mb-5">
              <span className="font-semibold text-white">Cascade-deleted</span>{" "}
              (removed entirely, because it belongs to your account alone):
            </p>
            <ul className="list-disc pl-5 space-y-2 text-[15px] text-[var(--foreground)] leading-relaxed mb-5">
              <li>
                Refresh tokens, API tokens, external-login codes and 2FA login
                challenge tickets (your active sessions and bot keys)
              </li>
              <li>
                Direct challenge links you created or accepted (
                <code className="text-[var(--bounty)]">Challenges</code>)
              </li>
              <li>
                Rematch offers you made or accepted (
                <code className="text-[var(--bounty)]">RematchOffers</code>)
              </li>
              <li>
                Web push subscriptions (
                <code className="text-[var(--bounty)]">
                  WebPushSubscriptions
                </code>
                )
              </li>
              <li>
                Your <code className="text-[var(--bounty)]">Ratings</code> rows
              </li>
              <li>
                Your Identity logins, claims, tokens and role assignments,
                including your TOTP shared secret and recovery codes, if you had
                two-factor authentication turned on
              </li>
            </ul>
            <p className="text-[15px] text-[var(--foreground)] leading-relaxed mb-5">
              <span className="font-semibold text-white">
                Scrubbed on the surviving account row
              </span>{" "}
              : your email, password hash, phone number and every other
              identifying credential field are cleared, and your avatar is
              removed; your display name becomes &ldquo;Deleted account&rdquo;;
              your security stamp is rotated so no existing session survives.
            </p>
            <p className="text-[15px] text-[var(--foreground)] leading-relaxed mb-5">
              <span className="font-semibold text-white">
                Retained, still pointing at the now-anonymised row:
              </span>{" "}
              your games, your rating history, reports you were involved in, and
              sanctions issued against you. This is retained because:
            </p>
            <ul className="list-disc pl-5 space-y-2 text-[15px] text-[var(--foreground)] leading-relaxed mb-5">
              <li>
                Games and rating history are also the record of the people you
                played against, Art. 17 requires that personal data no longer
                identify you, not that a shared historical record be destroyed.
              </li>
              <li>
                Reports and sanctions are retained under Art. 17(3)(e), for the
                establishment and exercise of legal claims, specifically so that
                a ban cannot be erased by deleting the account it was issued
                against and registering again.
              </li>
            </ul>
            <p className="text-[15px] text-[var(--foreground)] leading-relaxed mb-5">
              <span className="font-semibold text-white">
                One extra thing is written, and only if you were banned.
              </span>{" "}
              If a full account ban is in force at the moment your account is
              deleted, a one-way keyed hash of your email address is written to
              the tombstoned row just before the address itself is erased, and
              signing up again with that same address is refused for as long as
              the ban is. This is the mechanism behind the point above and
              behind Terms §6, retained on the same Art. 17(3)(e) basis as the
              sanction itself. Three things about it:
            </p>
            <ul className="list-disc pl-5 space-y-2 text-[15px] text-[var(--foreground)] leading-relaxed mb-5">
              <li>
                <span className="font-semibold text-white">
                  It is not your email address and cannot be turned back into
                  one.
                </span>{" "}
                It is a hash under a secret key held only by the server; nothing
                in this system, and nobody who obtained a copy of the database
                without that key, can read an address out of it. Your address
                itself is erased exactly as described above.
              </li>
              <li>
                <span className="font-semibold text-white">
                  It is not written at all unless you were banned.
                </span>{" "}
                Deleting an account that carries a warning, a ban from rated
                play, or no sanction at all writes nothing, and deletion for
                those accounts is unchanged in every respect.
              </li>
              <li>
                <span className="font-semibold text-white">
                  It does not outlive the ban.
                </span>{" "}
                It stops blocking anything the moment the ban is lifted or
                expires, and the row is cleared 30 days after that.
              </li>
            </ul>
            <p className="text-[15px] text-[var(--foreground)] leading-relaxed mb-5">
              <span className="font-semibold text-white">
                What &ldquo;anonymised&rdquo; means for data already written.
              </span>{" "}
              Your account id is not deleted, it stays the primary key of the
              tombstoned row, your games, and every server log line that was
              produced while you were signed in (§2). &ldquo;Anonymised&rdquo;
              means that id stops resolving to a name or an email address
              anywhere in this system, not that the id itself disappears from
              records already written.
            </p>
            <p className="text-[15px] text-[var(--foreground)] leading-relaxed">
              Deletion is{" "}
              <span className="font-semibold text-white">
                immediate and irreversible
              </span>
              . There is no undo window. If you have a game in progress, you
              must finish or resign it first.
            </p>
          </article>

          <article className="mb-14">
            <h2 className="text-xl font-bold text-white mb-4 tracking-tight">
              7. Export (GDPR Art. 20)
            </h2>
            <p className="text-[15px] text-[var(--foreground)] leading-relaxed mb-5">
              <code className="text-[var(--bounty)]">
                GET /api/account/export
              </code>{" "}
              returns a JSON file of your data as an authenticated, synchronous
              download. There is no emailed link and no background job, because
              a synchronous download is simply the right shape for this export:
              it is one JSON file, generated on request, and there is nothing
              here large or slow enough to need a queue and a follow-up email
              instead. The export excludes:
            </p>
            <ul className="list-disc pl-5 space-y-2 text-[15px] text-[var(--foreground)] leading-relaxed">
              <li>
                Any other player&rsquo;s email address or account id: games list
                opponents by display name only, the same name already public on
                the leaderboards and every game page.
              </li>
              <li>
                The identity of anyone who reported you: a report filed against
                you is exported without the reporter&rsquo;s id or name.
              </li>
              <li>Every moderator note, on both reports and sanctions.</li>
              <li>
                Any raw session credential: session entries carry only their
                timestamps, never the token itself.
              </li>
              <li>
                Any raw API token: those entries carry the name, prefix, scopes
                and timestamps, never the hash or the secret shown once at
                creation.
              </li>
              <li>
                Your TOTP shared secret and recovery codes, if two-factor
                authentication is on: the export tells you that 2FA is enabled,
                never the secret material behind it.
              </li>
            </ul>
            <p className="text-[15px] text-[var(--foreground)] leading-relaxed mt-5">
              Very large accounts are capped (5,000 games, 10,000 rating-history
              rows) and the export says, per collection, whether the cap was
              hit.
            </p>
          </article>

          <article className="mb-14">
            <h2 className="text-xl font-bold text-white mb-4 tracking-tight">
              8. Your rights
            </h2>
            <ul className="list-disc pl-5 space-y-2 text-[15px] text-[var(--foreground)] leading-relaxed">
              <li>
                <span className="font-semibold text-white">Access</span>: the
                export in §7 is the access mechanism.
              </li>
              <li>
                <span className="font-semibold text-white">Rectification</span>:
                your display name can be changed by you (subject to the rename
                cooldown and naming policy) or by a moderator.
              </li>
              <li>
                <span className="font-semibold text-white">Erasure</span>: §6.
              </li>
              <li>
                <span className="font-semibold text-white">Portability</span>:
                §7.
              </li>
              <li>
                <span className="font-semibold text-white">Objection</span>:
                because most processing here rests on contract or legitimate
                interest rather than consent, objection in practice means using
                the erasure right in §6.
              </li>
              <li>
                <span className="font-semibold text-white">Complaint</span>: you
                may lodge a complaint with your local data-protection
                supervisory authority.
              </li>
              <li>
                <span className="font-semibold text-white">
                  Banned accounts
                </span>
                . A full account ban closes sign-in, the second factor, and
                refresh. An already-issued access token still works until it
                expires, and during that window the signed-in export and
                erasure routes still work: a ban is not a second precondition on deletion,
                the only one remains a game in progress (§6).
                After the token expires, a banned account exercises access,
                portability and erasure by writing to{" "}
                <a
                  href="mailto:privacy@kirichess.com"
                  className="text-[var(--bounty)] font-medium hover:underline"
                >
                  privacy@kirichess.com
                </a>
                . The operator can run the same export and erasure the signed-in
                routes use, on that mailbox&rsquo;s request. Sanctions still
                survive erasure, as §6 already says.
              </li>
              <li>
                <span className="font-semibold text-white">
                  Manifestly unfounded or excessive requests
                </span>{" "}
                (GDPR Art. 12(5)), in particular because of their repetitive
                character, may be refused or made subject to a reasonable fee.
              </li>
            </ul>
          </article>

          <article className="mb-14">
            <h2 className="text-xl font-bold text-white mb-4 tracking-tight">
              9. Minimum age
            </h2>
            <p className="text-[15px] text-[var(--foreground)] leading-relaxed mb-5">
              You must be at least{" "}
              <span className="font-semibold text-white">16 years old</span> to
              create an account and play. This single global threshold is chosen
              because GDPR Art. 8 sets the age of digital consent between 13 and
              16 depending on EU member state, and 16 satisfies the strictest of
              them while also clearing the US COPPA threshold of 13 outright:
              the only choice that does not require detecting which country you
              are in. Reading the public pages without an account is not gated
              on age.
            </p>
            <p className="text-[15px] text-[var(--foreground)] leading-relaxed">
              The age check is a self-declared checkbox at signup, recorded as a
              timestamp. No date of birth is collected or stored anywhere in
              this system. A self-declared date of birth would not provide any
              more real assurance than a checkbox, and it would create a new
              piece of sensitive personal data that then needs its own
              retention, export and erasure handling for no actual gain in
              enforcement. On credible notice that an account holder is under
              16, the account is closed and anonymised.
            </p>
          </article>

          <article className="mb-14">
            <h2 className="text-xl font-bold text-white mb-4 tracking-tight">
              10. Security
            </h2>
            <p className="text-[15px] text-[var(--foreground)] leading-relaxed">
              Passwords are never stored in plaintext: only a PBKDF2/HMAC-
              SHA256 hash (ASP.NET Core Identity&rsquo;s default). Refresh
              tokens and one-time external-login codes are stored as SHA-256
              hashes, not as their raw usable form. Traffic is expected to run
              over TLS in any real deployment. Requests are rate-limited by IP
              to slow abuse. None of this has been externally audited,
              certified, or subjected to a bug bounty: do not read any of the
              above as a compliance certification, because it is not one.
            </p>
          </article>

          <article className="mb-14">
            <h2 className="text-xl font-bold text-white mb-4 tracking-tight">
              11. Changes to this policy
            </h2>
            <p className="text-[15px] text-[var(--foreground)] leading-relaxed">
              This document is versioned by the &ldquo;Last updated&rdquo; /
              &ldquo;Version&rdquo; date at the top. A future version that
              changes what is collected or how it is used will change that date;
              the version an account most recently agreed to is recorded on that
              account.
            </p>
          </article>

          <p className="text-[15px] text-[var(--foreground)] leading-relaxed">
            See also the{" "}
            <Link
              href="/terms"
              className="text-[var(--bounty)] font-medium hover:underline"
            >
              Terms of Service
            </Link>
            .
          </p>
        </section>
      </main>
      <Footer />
    </div>
  );
}
