using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using KiriChess.Server.Auth;
using KiriChess.Server.Data;
using KiriChess.Server.Data.Entities;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Xunit;

namespace KiriChess.Server.Tests;

/// <summary>
/// End-to-end proof of the Phase 6b exit criterion: refresh-token rotation + reuse detection,
/// logout revocation, account lockout, and the email-confirmation/password-reset token flows.
/// Requires Docker; skips cleanly when no Docker daemon is reachable.
/// </summary>
public sealed class AuthLifecycleTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private PostgreSqlContainer? _pg;
    private WebApplicationFactory<Program>? _factory;
    private HttpClient? _client;

    public async Task InitializeAsync()
    {
        if (!DockerAvailability.IsAvailable) return;

        _pg = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
        await _pg.StartAsync();

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Postgres"] = _pg!.GetConnectionString(),
                    ["Jwt:Key"] = "auth-lifecycle-test-signing-key-at-least-32-bytes",
                    ["Jwt:Issuer"] = "kirichess",
                    ["Jwt:Audience"] = "kirichess",
                    ["Jwt:ExpiryMinutes"] = "60",
                    ["Jwt:RefreshTokenExpiryDays"] = "30",
                    // Off by default anyway (BreachedPasswordCheckOptions), but explicit here: Phase
                    // 9d.1's HIBP check is unit-tested in isolation (BreachedPasswordCheckerTests), and
                    // this integration suite registering real accounts must never depend on reaching
                    // api.pwnedpasswords.com.
                    ["Auth:BreachedPasswordCheckEnabled"] = "false"
                });
            });
        });

        using (var scope = _factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<KiriChessDbContext>().Database.MigrateAsync();

        _client = _factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_factory is not null) await _factory.DisposeAsync();
        if (_pg is not null) await _pg.DisposeAsync();
    }

    [SkippableFact]
    public async Task Refresh_rotates_the_token_and_old_token_stops_working()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var auth = await RegisterAsync("refresh-player@example.com", "Refresh Player");

        var refreshResponse = await _client!.PostAsJsonAsync("/api/auth/refresh",
            new { RefreshToken = auth.RefreshToken }, JsonOptions);
        refreshResponse.EnsureSuccessStatusCode();
        var rotated = await refreshResponse.Content.ReadFromJsonAsync<AuthResult>(JsonOptions);
        Assert.NotNull(rotated);
        Assert.NotEqual(auth.RefreshToken, rotated!.RefreshToken);

        var reuseResponse = await _client.PostAsJsonAsync("/api/auth/refresh",
            new { RefreshToken = auth.RefreshToken }, JsonOptions);
        Assert.Equal(HttpStatusCode.Unauthorized, reuseResponse.StatusCode);

        // Reuse revokes the whole chain: even the *new* token from the successful rotation is dead now.
        var deadChainResponse = await _client.PostAsJsonAsync("/api/auth/refresh",
            new { RefreshToken = rotated.RefreshToken }, JsonOptions);
        Assert.Equal(HttpStatusCode.Unauthorized, deadChainResponse.StatusCode);
    }

    [SkippableFact]
    public async Task Logout_revokes_the_refresh_token()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var auth = await RegisterAsync("logout-player@example.com", "Logout Player");

        var logoutResponse = await _client!.PostAsJsonAsync("/api/auth/logout",
            new { RefreshToken = auth.RefreshToken }, JsonOptions);
        Assert.Equal(HttpStatusCode.NoContent, logoutResponse.StatusCode);

        var refreshAfterLogout = await _client.PostAsJsonAsync("/api/auth/refresh",
            new { RefreshToken = auth.RefreshToken }, JsonOptions);
        Assert.Equal(HttpStatusCode.Unauthorized, refreshAfterLogout.StatusCode);
    }

    [SkippableFact]
    public async Task Repeated_failed_logins_lock_the_account()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        await RegisterAsync("lockout-player@example.com", "Lockout Player");

        for (var i = 0; i < 5; i++)
        {
            var attempt = await _client!.PostAsJsonAsync("/api/auth/login",
                new { Email = "lockout-player@example.com", Password = "WrongPassword!" }, JsonOptions);
            Assert.Equal(HttpStatusCode.Unauthorized, attempt.StatusCode);
        }

        var lockedAttempt = await _client!.PostAsJsonAsync("/api/auth/login",
            new { Email = "lockout-player@example.com", Password = "P@ssword123!" }, JsonOptions);
        Assert.Equal((HttpStatusCode)423, lockedAttempt.StatusCode);
    }

    [SkippableFact]
    public async Task Failed_login_returns_invalid_credentials_not_a_bare_401()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        await RegisterAsync("credentials-message@example.com", "Credentials Message");

        var unknown = await _client!.PostAsJsonAsync("/api/auth/login",
            new { Email = "no-such-user@example.com", Password = "P@ssword123!" }, JsonOptions);
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        var unknownBody = await unknown.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        Assert.Equal("Invalid email or password.", unknownBody.GetProperty("error").GetString());

        var wrongPassword = await _client.PostAsJsonAsync("/api/auth/login",
            new { Email = "  credentials-message@example.com  ", Password = "WrongPassword!" }, JsonOptions);
        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);
        var wrongBody = await wrongPassword.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        Assert.Equal("Invalid email or password.", wrongBody.GetProperty("error").GetString());

        var paddedOk = await _client.PostAsJsonAsync("/api/auth/login",
            new { Email = "  credentials-message@example.com  ", Password = "P@ssword123!" }, JsonOptions);
        paddedOk.EnsureSuccessStatusCode();
    }

    [SkippableFact]
    public async Task Password_reset_revokes_existing_refresh_tokens()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var auth = await RegisterAsync("reset-player@example.com", "Reset Player");

        string resetToken;
        using (var scope = _factory!.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = await users.FindByEmailAsync("reset-player@example.com");
            resetToken = await users.GeneratePasswordResetTokenAsync(user!);
        }

        var resetResponse = await _client!.PostAsJsonAsync("/api/auth/reset-password", new
        {
            Email = "reset-player@example.com",
            Token = resetToken,
            NewPassword = "N3wP@ssword!"
        }, JsonOptions);
        resetResponse.EnsureSuccessStatusCode();

        var refreshResponse = await _client.PostAsJsonAsync("/api/auth/refresh",
            new { RefreshToken = auth.RefreshToken }, JsonOptions);
        Assert.Equal(HttpStatusCode.Unauthorized, refreshResponse.StatusCode);

        var loginResponse = await _client.PostAsJsonAsync("/api/auth/login", new
        {
            Email = "reset-player@example.com",
            Password = "N3wP@ssword!"
        }, JsonOptions);
        loginResponse.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// S-1-1. The reset used to revoke refresh tokens only, which left the one credential a takeover
    /// can mint and the victim cannot see (AUTH.md § 2: a stolen access token can call
    /// POST /api/account/api-tokens and get a `kirichess_pat_` PAT that outlives both the 15-minute JWT and the
    /// victim's own logout) alive through the single action a compromised user is told to take.
    ///
    /// Asserted end to end rather than by reading RevokedAt off the row: a set column proves the
    /// service was called, and what the finding is actually about is whether the bearer still opens a
    /// door. PublicApiTests.Logout_all_does_not_revoke_api_tokens pins the deliberate other half of this
    /// decision, and is untouched: signing out everywhere still leaves a PAT working.
    /// </summary>
    [SkippableFact]
    public async Task Password_reset_revokes_api_tokens_so_the_bearer_stops_authenticating()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var auth = await RegisterAsync("reset-pat@example.com", "Reset Pat");

        _client!.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.Token);
        var mint = await _client.PostAsJsonAsync("/api/account/api-tokens",
            new { Name = "bot", Scopes = new[] { "read" }, Password = "P@ssword123!" }, JsonOptions);
        Assert.Equal(HttpStatusCode.Created, mint.StatusCode);
        var created = await mint.Content.ReadFromJsonAsync<CreatedApiToken>(JsonOptions);
        Assert.NotNull(created);
        Assert.True(ApiTokenFormat.IsWellFormed(created!.Token));

        // The PAT is presented to the /api/v1 surface, which names the ApiToken scheme - a `kirichess_pat_`
        // bearer is deliberately rejected everywhere else (AUTH.md § 2, scheme isolation).
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", created.Token);
        var beforeReset = await _client.GetAsync("/api/v1/account");
        Assert.Equal(HttpStatusCode.OK, beforeReset.StatusCode);

        string resetToken;
        using (var scope = _factory!.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = await users.FindByEmailAsync("reset-pat@example.com");
            resetToken = await users.GeneratePasswordResetTokenAsync(user!);
        }

        // Unauthenticated on purpose: /api/auth/reset-password takes an emailed token, not a session.
        _client.DefaultRequestHeaders.Authorization = null;
        var resetResponse = await _client.PostAsJsonAsync("/api/auth/reset-password", new
        {
            Email = "reset-pat@example.com",
            Token = resetToken,
            NewPassword = "N3wP@ssword!"
        }, JsonOptions);
        resetResponse.EnsureSuccessStatusCode();

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", created.Token);
        var afterReset = await _client.GetAsync("/api/v1/account");
        Assert.Equal(HttpStatusCode.Unauthorized, afterReset.StatusCode);

        // The route still works for a token minted after the reset, so this revokes the tokens rather
        // than the ability to have any.
        _client.DefaultRequestHeaders.Authorization = null;
        var relogin = await _client.PostAsJsonAsync("/api/auth/login",
            new { Email = "reset-pat@example.com", Password = "N3wP@ssword!" }, JsonOptions);
        relogin.EnsureSuccessStatusCode();
        var reauth = await relogin.Content.ReadFromJsonAsync<AuthResult>(JsonOptions);
        Assert.NotNull(reauth);

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", reauth!.Token);
        var remint = await _client.PostAsJsonAsync("/api/account/api-tokens",
            new { Name = "bot-again", Scopes = new[] { "read" }, Password = "N3wP@ssword!" }, JsonOptions);
        Assert.Equal(HttpStatusCode.Created, remint.StatusCode);
        var replacement = await remint.Content.ReadFromJsonAsync<CreatedApiToken>(JsonOptions);
        Assert.NotNull(replacement);

        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", replacement!.Token);
        var withReplacement = await _client.GetAsync("/api/v1/account");
        Assert.Equal(HttpStatusCode.OK, withReplacement.StatusCode);

        _client.DefaultRequestHeaders.Authorization = null;
    }

    /// <summary>
    /// S-1-13. An unknown email used to answer keyed "Email"; a known email with a bad token fell
    /// through to Identity and answered keyed "InvalidToken" - two distinguishable shapes that made
    /// this an account-existence oracle. Both now answer with the same body, so the assertion that
    /// actually matters is that the two responses are indistinguishable, not just that they share a
    /// status code.
    ///
    /// "Indistinguishable" is everything except <c>traceId</c>, which ProblemDetails stamps per
    /// request from the current Activity. It differs between any two calls whatever they ask for, so
    /// a byte-for-byte comparison of the raw bodies can never pass and would be testing the trace
    /// infrastructure rather than the refusal. It is also not a signal a prober can use: it is a
    /// fresh correlation id, present in identical shape either way, and it says nothing about
    /// whether the account exists.
    /// </summary>
    [SkippableFact]
    public async Task Unknown_email_and_a_bad_token_for_a_real_account_answer_identically()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        await RegisterAsync("reset-oracle@example.com", "Reset Oracle");

        var unknownEmail = await _client!.PostAsJsonAsync("/api/auth/reset-password", new
        {
            Email = "no-such-reset-account@example.com",
            Token = "not-a-real-token",
            NewPassword = "N3wP@ssword!"
        }, JsonOptions);

        var badToken = await _client.PostAsJsonAsync("/api/auth/reset-password", new
        {
            Email = "reset-oracle@example.com",
            Token = "not-a-real-token",
            NewPassword = "N3wP@ssword!"
        }, JsonOptions);

        Assert.Equal(HttpStatusCode.BadRequest, unknownEmail.StatusCode);
        Assert.Equal(unknownEmail.StatusCode, badToken.StatusCode);

        // Each body is read exactly once, into a string, and everything below parses that string.
        // ReadFromJsonAsync disposes the content stream, so reading either response a second time
        // throws ObjectDisposedException rather than returning the same body again.
        var unknownBody = await unknownEmail.Content.ReadAsStringAsync();
        var badTokenBody = await badToken.Content.ReadAsStringAsync();

        Assert.Equal(
            ProblemBodyWithoutTrace(unknownBody),
            ProblemBodyWithoutTrace(badTokenBody));

        // And it is the flattened shape, not Identity's own InvalidToken code leaking through.
        using var parsedDoc = JsonDocument.Parse(badTokenBody);
        var errors = parsedDoc.RootElement.GetProperty("errors");
        Assert.True(errors.TryGetProperty("Email", out _));
        Assert.False(errors.TryGetProperty("InvalidToken", out _));

        // The real account is untouched: a bad token must never have moved the password.
        var stillOldPassword = await _client.PostAsJsonAsync("/api/auth/login",
            new { Email = "reset-oracle@example.com", Password = "P@ssword123!" }, JsonOptions);
        stillOldPassword.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// A ProblemDetails body with <c>traceId</c> dropped, so two responses can be compared for the
    /// thing S-1-13 is about: whether a caller can tell which of them came from a real account.
    /// Re-serialised through a sorted dictionary so property order cannot make two equal bodies
    /// compare unequal.
    /// </summary>
    private static string ProblemBodyWithoutTrace(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var fields = doc.RootElement.EnumerateObject()
            .Where(p => p.Name != "traceId")
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ToDictionary(p => p.Name, p => p.Value.GetRawText());
        return JsonSerializer.Serialize(fields);
    }

    [SkippableFact]
    public async Task Email_confirmation_succeeds_with_a_generated_token()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var auth = await RegisterAsync("confirm-player@example.com", "Confirm Player");

        string confirmToken;
        using (var scope = _factory!.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = await users.FindByIdAsync(auth.UserId.ToString());
            confirmToken = await users.GenerateEmailConfirmationTokenAsync(user!);
        }

        var confirmResponse = await _client!.PostAsJsonAsync("/api/auth/confirm-email",
            new { UserId = auth.UserId, Token = confirmToken }, JsonOptions);
        confirmResponse.EnsureSuccessStatusCode();

        using var verifyScope = _factory!.Services.CreateScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
        var confirmedUser = await db.Users.SingleAsync(u => u.Id == auth.UserId);
        Assert.True(confirmedUser.EmailConfirmed);
    }

    // --- Phase 8a.4: register-path content policy enforcement (Task B) ---------------------------

    [SkippableFact]
    public async Task Blank_display_name_is_refused_and_never_leaks_the_email_as_a_handle()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        const string email = "blank-name-player@example.com";
        var response = await _client!.PostAsJsonAsync("/api/auth/register", new
        {
            Email = email,
            Password = "P@ssword123!",
            DisplayName = "",
            AcceptedTerms = true,
            ConfirmedMinimumAge = true
        }, JsonOptions);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // The privacy defect this closes: the old fallback published the email as the public handle.
        // A 400 alone would still pass if someone reinstated that fallback behind a different code
        // path, so assert directly that no row exists with the email as its DisplayName.
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
        var leaked = await db.Users.AnyAsync(u => u.DisplayName == email);
        Assert.False(leaked);
    }

    [SkippableFact]
    public async Task Profane_display_name_is_refused()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var response = await _client!.PostAsJsonAsync("/api/auth/register", new
        {
            Email = "profane-name-player@example.com",
            Password = "P@ssword123!",
            DisplayName = "FuckFace",
            AcceptedTerms = true,
            ConfirmedMinimumAge = true
        }, JsonOptions);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        Assert.True(body.GetProperty("errors").TryGetProperty("DisplayName", out _));
    }

    [SkippableFact]
    public async Task Reserved_display_name_is_refused_including_under_the_normalizer()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var literal = await _client!.PostAsJsonAsync("/api/auth/register", new
        {
            Email = "reserved-literal-player@example.com",
            Password = "P@ssword123!",
            DisplayName = "Admin",
            AcceptedTerms = true,
            ConfirmedMinimumAge = true
        }, JsonOptions);
        Assert.Equal(HttpStatusCode.BadRequest, literal.StatusCode);

        // "4dm1n" pins that the normalizer is in the path, not a literal string compare: '4' folds to
        // 'a' and '1' folds to (among others) 'i' under DisplayNameNormalizer's confusable table.
        var leetspeak = await _client.PostAsJsonAsync("/api/auth/register", new
        {
            Email = "reserved-leet-player@example.com",
            Password = "P@ssword123!",
            DisplayName = "4dm1n",
            AcceptedTerms = true,
            ConfirmedMinimumAge = true
        }, JsonOptions);
        Assert.Equal(HttpStatusCode.BadRequest, leetspeak.StatusCode);
    }

    [SkippableFact]
    public async Task Lookalike_duplicate_display_name_is_refused()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var first = await _client!.PostAsJsonAsync("/api/auth/register", new
        {
            Email = "lookalike-first-player@example.com",
            Password = "P@ssword123!",
            DisplayName = "Bishop0",
            AcceptedTerms = true,
            ConfirmedMinimumAge = true
        }, JsonOptions);
        first.EnsureSuccessStatusCode();

        // "BishopO" (capital letter O, not zero) folds to the same identity key as "Bishop0" under
        // NormalizeForIdentity - exactly the impersonation vector the unique index closes.
        var second = await _client.PostAsJsonAsync("/api/auth/register", new
        {
            Email = "lookalike-second-player@example.com",
            Password = "P@ssword123!",
            DisplayName = "BishopO",
            AcceptedTerms = true,
            ConfirmedMinimumAge = true
        }, JsonOptions);
        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
    }

    [SkippableFact]
    public async Task Legitimate_display_name_with_space_dash_and_digit_still_works()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var response = await _client!.PostAsJsonAsync("/api/auth/register", new
        {
            Email = "legit-name-player@example.com",
            Password = "P@ssword123!",
            DisplayName = "Player One-42",
            AcceptedTerms = true,
            ConfirmedMinimumAge = true
        }, JsonOptions);

        response.EnsureSuccessStatusCode();
        var auth = await response.Content.ReadFromJsonAsync<AuthResult>(JsonOptions);
        Assert.NotNull(auth);
        Assert.Equal("Player One-42", auth!.DisplayName);
    }

    [SkippableFact]
    public async Task Register_accepts_a_password_without_a_symbol()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var response = await _client!.PostAsJsonAsync("/api/auth/register", new
        {
            Email = "no-symbol-password@example.com",
            Password = "Password123",
            DisplayName = "No Symbol Pw",
            AcceptedTerms = true,
            ConfirmedMinimumAge = true
        }, JsonOptions);

        response.EnsureSuccessStatusCode();
    }

    [SkippableFact]
    public async Task Duplicate_email_does_not_report_the_address_as_a_taken_username()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var first = await _client!.PostAsJsonAsync("/api/auth/register", new
        {
            Email = "dup-email-player@example.com",
            Password = "P@ssword123!",
            DisplayName = "Dup Email One",
            AcceptedTerms = true,
            ConfirmedMinimumAge = true
        }, JsonOptions);
        first.EnsureSuccessStatusCode();

        var second = await _client.PostAsJsonAsync("/api/auth/register", new
        {
            Email = "dup-email-player@example.com",
            Password = "P@ssword123!",
            DisplayName = "Dup Email Two",
            AcceptedTerms = true,
            ConfirmedMinimumAge = true
        }, JsonOptions);
        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
        var body = await second.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Username", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Email", body, StringComparison.OrdinalIgnoreCase);
    }

    // --- Phase 8a.2: consent and the age gate at signup (Task B) ---------------------------------

    [SkippableFact]
    public async Task Register_refuses_without_accepted_terms()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var response = await _client!.PostAsJsonAsync("/api/auth/register", new
        {
            Email = "no-terms-player@example.com",
            Password = "P@ssword123!",
            DisplayName = "No Terms Player",
            AcceptedTerms = false,
            ConfirmedMinimumAge = true
        }, JsonOptions);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        Assert.True(body.GetProperty("errors").TryGetProperty("AcceptedTerms", out _));
    }

    [SkippableFact]
    public async Task Register_refuses_without_age_confirmation()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var response = await _client!.PostAsJsonAsync("/api/auth/register", new
        {
            Email = "no-age-player@example.com",
            Password = "P@ssword123!",
            DisplayName = "No Age Player",
            AcceptedTerms = true,
            ConfirmedMinimumAge = false
        }, JsonOptions);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        Assert.True(body.GetProperty("errors").TryGetProperty("ConfirmedMinimumAge", out _));
    }

    [SkippableFact]
    public async Task Register_stamps_the_consent_columns()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var auth = await RegisterAsync("consent-stamp-player@example.com", "Consent Stamp Player");

        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
        var user = await db.Users.SingleAsync(u => u.Id == auth.UserId);
        Assert.NotNull(user.TermsAcceptedAt);
        Assert.NotNull(user.AgeConfirmedAt);
        Assert.Equal(TermsVersion.Current, user.TermsVersion);
    }

    [SkippableFact]
    public async Task Register_without_consent_does_not_create_the_account()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        const string displayName = "Consent Race Player";
        var refused = await _client!.PostAsJsonAsync("/api/auth/register", new
        {
            Email = "consent-race-refused@example.com",
            Password = "P@ssword123!",
            DisplayName = displayName,
            AcceptedTerms = false,
            ConfirmedMinimumAge = false
        }, JsonOptions);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);

        // If the refusal above had reached CreateAsync (or burned the display name) before failing,
        // this second, properly-consented registration for the same name would 400 too.
        var accepted = await _client.PostAsJsonAsync("/api/auth/register", new
        {
            Email = "consent-race-accepted@example.com",
            Password = "P@ssword123!",
            DisplayName = displayName,
            AcceptedTerms = true,
            ConfirmedMinimumAge = true
        }, JsonOptions);
        accepted.EnsureSuccessStatusCode();
    }

    private async Task<AuthResult> RegisterAsync(string email, string displayName)
    {
        var registerResponse = await _client!.PostAsJsonAsync("/api/auth/register", new
        {
            Email = email,
            Password = "P@ssword123!",
            DisplayName = displayName,
            AcceptedTerms = true,
            ConfirmedMinimumAge = true
        }, JsonOptions);
        registerResponse.EnsureSuccessStatusCode();
        var auth = await registerResponse.Content.ReadFromJsonAsync<AuthResult>(JsonOptions);
        Assert.NotNull(auth);
        return auth!;
    }

    private sealed record AuthResult(
        Guid UserId, string Email, string DisplayName, string Token, string RefreshToken, DateTimeOffset ExpiresAt);

    // Only the fields this suite reads. PublicApiTests carries the full shape; duplicating all eight
    // columns here would just be a second place to update when the DTO grows.
    private sealed record CreatedApiToken(Guid Id, string Name, string Token);
}
