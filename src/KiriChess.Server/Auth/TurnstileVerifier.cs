using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace KiriChess.Server.Auth;

/// <summary>
/// Config for server-side Cloudflare Turnstile verification (DH.3). Empty secret key = disabled,
/// same "empty config = off" shape as <c>EmailOptions</c>/<c>ExternalAuthOptions</c>, so a bare
/// checkout and every <c>WebApplicationFactory&lt;Program&gt;</c>-driven test stay inert by default -
/// no Cloudflare account is needed to register a test account. Set <c>Turnstile:SecretKey</c> (env:
/// <c>Turnstile__SecretKey</c>) once the widget's secret key exists, see docs/ops/DEPLOYMENT.md § 9.1.
/// </summary>
/// <param name="SecretKey">The widget's secret key from the Cloudflare dashboard. Empty disables the check.</param>
/// <param name="TimeoutSeconds">Per-attempt HTTP timeout, short, since a slow siteverify must not stall registration long before failing open.</param>
/// <remarks>
/// C-1-9: this is an eager <see cref="From"/> read registered once at startup as a plain singleton
/// instance, not <c>Configure&lt;T&gt;</c>/<see cref="Microsoft.Extensions.Options.IOptions{TOptions}"/>,
/// so a test's <c>WebApplicationFactory.WithWebHostBuilder(b =&gt; b.ConfigureAppConfiguration(...))</c>
/// runs too late to change it, the value was already read and captured before that callback fires.
/// This is the same shape as <c>EmailOptions</c>/<c>VapidOptions</c>/<c>BreachedPasswordCheckOptions</c>,
/// and it has already cost a session real time on those two. The working override is
/// <c>ConfigureServices(s =&gt; s.AddSingleton(new TurnstileOptions(...)))</c> registered after the
/// real one, which the last-registration-wins DI rule then resolves instead. See
/// <c>PushNotificationTests.cs</c>'s <c>VapidOptions</c> override for the pattern actually in use.
/// </remarks>
public sealed record TurnstileOptions(string SecretKey, int TimeoutSeconds)
{
    public static TurnstileOptions From(IConfiguration c) => new(
        c.GetValue("Turnstile:SecretKey", string.Empty) ?? string.Empty,
        c.GetValue("Turnstile:TimeoutSeconds", 5));

    public bool Enabled => !string.IsNullOrWhiteSpace(SecretKey);
}

/// <summary>
/// Verifies a Cloudflare Turnstile token against the siteverify API (DH.3). The widget on
/// <c>/login</c> only produces a token; this is the sole siteverify, because a token can be
/// redeemed once. A script that skips the browser can post directly to the endpoint, so this is
/// the actual enforcement.
///
/// <para><b>Fails open on disable or transport error</b>, same instinct as
/// <see cref="BreachedPasswordChecker"/>: a Cloudflare outage must never turn into a registration
/// outage. It does <b>not</b> fail open on a genuine verification failure (missing or rejected
/// token) - that is the one outcome this check exists to produce.</para>
///
/// Registered as a singleton in Program.cs regardless of <see cref="TurnstileOptions.Enabled"/>, the
/// flag is read inside <see cref="IsValidAsync"/> rather than gating DI registration, so the register
/// endpoint never needs a null check.
/// </summary>
public sealed class TurnstileVerifier
{
    /// <summary>Named <see cref="HttpClient"/> registered in Program.cs with Cloudflare's siteverify base address and a short timeout.</summary>
    public const string HttpClientName = "turnstile";

    private readonly IHttpClientFactory _clients;
    private readonly TurnstileOptions _options;
    private readonly ILogger<TurnstileVerifier> _logger;

    public TurnstileVerifier(IHttpClientFactory clients, TurnstileOptions options, ILogger<TurnstileVerifier> logger)
    {
        _clients = clients;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// True when disabled (nothing configured to check against), a real Cloudflare success, or any
    /// transport/parse failure (fail open). False when enabled and the token is missing or rejected.
    /// </summary>
    public async Task<bool> IsValidAsync(string? token, CancellationToken ct = default)
    {
        if (!_options.Enabled)
            return true;
        if (string.IsNullOrWhiteSpace(token))
            return false;

        try
        {
            var client = _clients.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Post, "turnstile/v0/siteverify")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["secret"] = _options.SecretKey,
                    ["response"] = token,
                }),
            };

            using var response = await client.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<SiteverifyResponse>(cancellationToken: ct);
            return result?.Success == true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Turnstile verification failed; failing open");
            return true;
        }
    }

    private sealed record SiteverifyResponse(bool Success);
}
