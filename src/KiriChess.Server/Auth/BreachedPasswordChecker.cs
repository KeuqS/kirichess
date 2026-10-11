using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace KiriChess.Server.Auth;

/// <summary>
/// Config for the HIBP breached-password check (Phase 9d.1). Same "plain record + From(config), fail
/// closed absent explicit config" shape as <c>EmailOptions</c>/<c>ExternalAuthOptions</c>/
/// <c>Sentry:Dsn</c>, even though this integration needs no secret, defaulting it off keeps a bare
/// checkout and every <c>WebApplicationFactory&lt;Program&gt;</c>-driven test (none of which expect
/// outbound network calls on every registered test account) inert by default. Set
/// <c>Auth:BreachedPasswordCheckEnabled</c> (env: <c>Auth__BreachedPasswordCheckEnabled=true</c>)
/// deliberately before real signups, see docs/ops/DEPLOYMENT.md § 9, "Breached-password check".
/// </summary>
/// <param name="Enabled">Set true to turn the check on. Off by default.</param>
/// <param name="TimeoutSeconds">Per-attempt HTTP timeout, short, since a slow HIBP must not stall registration long before failing open.</param>
/// <remarks>
/// C-1-9: this is an eager <see cref="From"/> read registered once at startup as a plain singleton
/// instance, not <c>Configure&lt;T&gt;</c>/<see cref="Microsoft.Extensions.Options.IOptions{TOptions}"/>,
/// so a test's <c>WebApplicationFactory.WithWebHostBuilder(b =&gt; b.ConfigureAppConfiguration(...))</c>
/// runs too late to change it, the value was already read and captured before that callback fires.
/// This is the same shape as <c>EmailOptions</c>/<c>VapidOptions</c>/<c>TurnstileOptions</c>, and it
/// has already cost a session real time on those two. The working override is
/// <c>ConfigureServices(s =&gt; s.AddSingleton(new BreachedPasswordCheckOptions(...)))</c> registered
/// after the real one, which the last-registration-wins DI rule then resolves instead. See
/// <c>PushNotificationTests.cs</c>'s <c>VapidOptions</c> override for the pattern actually in use.
/// </remarks>
public sealed record BreachedPasswordCheckOptions(bool Enabled, int TimeoutSeconds)
{
    public static BreachedPasswordCheckOptions From(IConfiguration c) => new(
        c.GetValue("Auth:BreachedPasswordCheckEnabled", false),
        c.GetValue("Auth:BreachedPasswordCheckTimeoutSeconds", 3));
}

/// <summary>
/// Checks a candidate password against Have I Been Pwned's breached-password corpus using its
/// k-anonymity range API (Phase 9d.1), only the first 5 hex characters of the password's SHA-1 hash
/// ever leave this process; HIBP never sees the password or the full hash.
///
/// <para><b>Fails open on any error</b>, a HIBP outage, timeout, or unexpected response must never
/// turn into a registration or password-reset outage. Same instinct as
/// <see cref="KiriChess.Server.Moderation.SanctionPolicy.BlockingSanctionAsync"/> and 8.4 Task E's
/// <c>RedisRateLimiter</c>, both of which treat "the check itself failed" as "let it through" rather
/// than as a rejection.</para>
///
/// Registered as a singleton in Program.cs regardless of <see cref="BreachedPasswordCheckOptions.Enabled"/>
///, the flag is read inside <see cref="IsBreachedAsync"/> rather than gating DI registration, so
/// callers (the register and reset-password endpoints) never need a null check.
/// </summary>
public sealed class BreachedPasswordChecker
{
    /// <summary>Named <see cref="HttpClient"/> registered in Program.cs with HIBP's base address and a short timeout.</summary>
    public const string HttpClientName = "hibp";

    private readonly IHttpClientFactory _clients;
    private readonly BreachedPasswordCheckOptions _options;
    private readonly ILogger<BreachedPasswordChecker> _logger;

    public BreachedPasswordChecker(
        IHttpClientFactory clients, BreachedPasswordCheckOptions options, ILogger<BreachedPasswordChecker> logger)
    {
        _clients = clients;
        _options = options;
        _logger = logger;
    }

    /// <summary>True if <paramref name="password"/> appears in HIBP's corpus. False on disable, timeout, or any other error, see the type doc.</summary>
    public async Task<bool> IsBreachedAsync(string password, CancellationToken ct = default)
    {
        if (!_options.Enabled) return false;

        try
        {
            var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(password)));
            var prefix = hash[..5];
            var suffix = hash[5..];

            var client = _clients.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Get, $"range/{prefix}");
            // Asks HIBP to pad the response with decoy entries so response length can't be used to
            // infer how many real matches came back, cheap, and their documented recommendation.
            request.Headers.Add("Add-Padding", "true");

            using var response = await client.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);
            string? line;
            while ((line = await reader.ReadLineAsync(ct)) is not null)
            {
                // Each line is "SUFFIX:COUNT" for every hash sharing our prefix, only ours matters.
                var separator = line.IndexOf(':');
                if (separator < 0) continue;
                if (string.Equals(line[..separator], suffix, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "HIBP breached-password check failed; failing open");
            return false;
        }
    }
}
