using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using KiriChess.Server.Auth;
using KiriChess.Server.Data;
using KiriChess.Server.Data.Entities;
using KiriChess.Server.Infrastructure;
using KiriChess.Server.Moderation.Content;
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
/// Drives <see cref="ExternalLoginService"/> and <see cref="ExternalLoginCodeService"/> directly
/// against a real Postgres, the linking policy is what Phase 6b.3's Task A exists for, and there is
/// no way to reach a live Google from a test, so these exercise the same policy
/// <c>ExternalAuthEndpoints</c> calls after <c>AuthenticateAsync</c> would have succeeded. Requires
/// Docker; skips cleanly when no Docker daemon is reachable.
/// </summary>
public sealed class ExternalLoginTests : IAsyncLifetime
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
                    ["Jwt:Key"] = "external-login-test-signing-key-at-least-32-bytes",
                    ["Jwt:Issuer"] = "kirichess",
                    ["Jwt:Audience"] = "kirichess",
                    ["Jwt:ExpiryMinutes"] = "60",
                    ["Jwt:RefreshTokenExpiryDays"] = "30"
                    // Authentication:Google:ClientId/ClientSecret deliberately left unset, appsettings
                    // defaults them to "". That is exactly the fail-closed configuration
                    // Fail_closed_when_google_is_not_configured pins; every other case here drives
                    // ExternalLoginService/ExternalLoginCodeService directly rather than through the
                    // HTTP challenge/callback, which needs a live Google to ever complete.
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

    private static ExternalIdentity Identity(string providerKey, string email, bool verified = true, string? name = null) =>
        new("Google", providerKey, email, verified, name);

    /// <summary>
    /// The exchange-code tests only care about the code lifecycle, not the linking policy, but
    /// <see cref="ExternalLoginCode.UserId"/> has a real foreign key onto <c>AspNetUsers</c>, a
    /// random Guid violates it. This mints a minimal, password-less user the same way
    /// <see cref="ExternalLoginService"/> would for a brand-new provider identity.
    /// </summary>
    private static async Task<Guid> CreateUserAsync(IServiceScope scope, string email)
    {
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser { UserName = email, Email = email, DisplayName = email, EmailConfirmed = true };
        var result = await users.CreateAsync(user);
        Assert.True(result.Succeeded);
        return user.Id;
    }

    [SkippableFact]
    public async Task New_identity_creates_a_confirmed_account()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        using var scope = _factory!.Services.CreateScope();
        var logins = scope.ServiceProvider.GetRequiredService<ExternalLoginService>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var result = await logins.SignInOrCreateAsync(Identity("google-sub-1", "new-oauth-player@example.com"));

        Assert.Equal(ExternalLoginOutcome.Created, result.Outcome);
        Assert.NotNull(result.User);
        Assert.True(result.User!.EmailConfirmed);

        var found = await users.FindByLoginAsync("Google", "google-sub-1");
        Assert.NotNull(found);
        Assert.Equal(result.User.Id, found!.Id);
    }

    [SkippableFact]
    public async Task Same_provider_key_twice_returns_the_same_user()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        using var scope = _factory!.Services.CreateScope();
        var logins = scope.ServiceProvider.GetRequiredService<ExternalLoginService>();
        var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();

        var identity = Identity("google-sub-2", "repeat-oauth-player@example.com");
        var first = await logins.SignInOrCreateAsync(identity);
        var second = await logins.SignInOrCreateAsync(identity);

        Assert.Equal(ExternalLoginOutcome.Created, first.Outcome);
        Assert.Equal(ExternalLoginOutcome.SignedIn, second.Outcome);
        Assert.Equal(first.User!.Id, second.User!.Id);

        var accountCount = await db.Users.CountAsync(u => u.Email == "repeat-oauth-player@example.com");
        Assert.Equal(1, accountCount);
    }

    [SkippableFact]
    public async Task Verified_email_links_onto_an_existing_password_account()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var registerResponse = await _client!.PostAsJsonAsync("/api/auth/register", new
        {
            Email = "password-then-google@example.com",
            Password = "P@ssword123!",
            DisplayName = "Password Then Google",
            AcceptedTerms = true,
            ConfirmedMinimumAge = true
        }, JsonOptions);
        registerResponse.EnsureSuccessStatusCode();
        var registered = await registerResponse.Content.ReadFromJsonAsync<AuthResult>(JsonOptions);
        Assert.NotNull(registered);

        using var scope = _factory!.Services.CreateScope();
        var logins = scope.ServiceProvider.GetRequiredService<ExternalLoginService>();

        var result = await logins.SignInOrCreateAsync(Identity("google-sub-3", "password-then-google@example.com"));

        Assert.Equal(ExternalLoginOutcome.Linked, result.Outcome);
        Assert.Equal(registered!.UserId, result.User!.Id);
        Assert.True(result.User.EmailConfirmed);
    }

    [SkippableFact]
    public async Task Unverified_email_never_links()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        // The account-takeover regression test, the single most important assertion in this task.
        // An attacker asserting an unverified provider email matching the victim's password account
        // must never gain a login row on that account, and must never get a new account either.
        var registerResponse = await _client!.PostAsJsonAsync("/api/auth/register", new
        {
            Email = "victim@example.com",
            Password = "P@ssword123!",
            DisplayName = "Victim",
            AcceptedTerms = true,
            ConfirmedMinimumAge = true
        }, JsonOptions);
        registerResponse.EnsureSuccessStatusCode();
        var registered = await registerResponse.Content.ReadFromJsonAsync<AuthResult>(JsonOptions);
        Assert.NotNull(registered);

        using var scope = _factory!.Services.CreateScope();
        var logins = scope.ServiceProvider.GetRequiredService<ExternalLoginService>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();

        var result = await logins.SignInOrCreateAsync(Identity("attacker-sub", "victim@example.com", verified: false));

        Assert.Equal(ExternalLoginOutcome.UnverifiedEmail, result.Outcome);
        Assert.Null(result.User);

        var attackerLogin = await users.FindByLoginAsync("Google", "attacker-sub");
        Assert.Null(attackerLogin);

        var accountCount = await db.Users.CountAsync(u => u.Email == "victim@example.com");
        Assert.Equal(1, accountCount);

        var victim = await users.FindByEmailAsync("victim@example.com");
        var victimLogins = await users.GetLoginsAsync(victim!);
        Assert.Empty(victimLogins);
    }

    [SkippableFact]
    public async Task Different_provider_key_same_verified_email_after_a_link_already_exists()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        using var scope = _factory!.Services.CreateScope();
        var logins = scope.ServiceProvider.GetRequiredService<ExternalLoginService>();

        var first = await logins.SignInOrCreateAsync(Identity("google-sub-first", "shared-email@example.com"));
        Assert.Equal(ExternalLoginOutcome.Created, first.Outcome);

        // A second, *different* provider key asserting the same already-verified email is not the
        // account-takeover shape step 2 guards against: the caller has proven control of the address
        // via the provider's own verification, exactly as step 3 (link a verified email) requires.
        // UserManager.AddLoginAsync only enforces a unique (provider, key) pair, not one login per
        // provider name per user, so this is accepted as a second Google login on the same account
        // (Linked) rather than refused. Pinning Linked here: this is "the same person now has two
        // Google identities asserting one address", not a hijack of someone else's account.
        var second = await logins.SignInOrCreateAsync(Identity("google-sub-second", "shared-email@example.com"));

        Assert.Equal(ExternalLoginOutcome.Linked, second.Outcome);
        Assert.Equal(first.User!.Id, second.User!.Id);
    }

    // --- S-5-2: the Registration kill switch reaches this path too -------------------------------

    /// <summary>
    /// The switch's whole point on this path: it gates account creation, and only creation. An
    /// account that already exists (created before the switch flipped, or the operator's own account)
    /// must still be able to sign back in while `Registration` is off - flipping the switch during an
    /// incident must not also lock out every existing OAuth user.
    /// </summary>
    [SkippableFact]
    public async Task Registration_switch_off_refuses_new_oauth_signup_but_not_an_existing_sign_in()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        using var scope = _factory!.Services.CreateScope();
        var logins = scope.ServiceProvider.GetRequiredService<ExternalLoginService>();
        var killSwitches = scope.ServiceProvider.GetRequiredService<KillSwitchService>();

        var existing = await logins.SignInOrCreateAsync(
            Identity("google-sub-preexisting", "preexisting-oauth@example.com"));
        Assert.Equal(ExternalLoginOutcome.Created, existing.Outcome);

        await killSwitches.SetEnabledAsync(KillSwitch.Registration, enabled: false, Guid.NewGuid());

        var blocked = await logins.SignInOrCreateAsync(
            Identity("google-sub-blocked-by-switch", "blocked-by-switch-oauth@example.com"));
        Assert.Equal(ExternalLoginOutcome.RegistrationPaused, blocked.Outcome);
        Assert.Null(blocked.User);

        var stillSignsIn = await logins.SignInOrCreateAsync(
            Identity("google-sub-preexisting", "preexisting-oauth@example.com"));
        Assert.Equal(ExternalLoginOutcome.SignedIn, stillSignsIn.Outcome);

        // And it is a pause, not a one-way door.
        await killSwitches.SetEnabledAsync(KillSwitch.Registration, enabled: true, Guid.NewGuid());
        var afterReenable = await logins.SignInOrCreateAsync(
            Identity("google-sub-after-reenable", "after-reenable-oauth@example.com"));
        Assert.Equal(ExternalLoginOutcome.Created, afterReenable.Outcome);
    }

    // --- Phase 8a.4: the external-login fallback (Task B) ------------------------------------------

    [SkippableFact]
    public async Task External_login_sanitizes_rather_than_refuses_a_disallowed_provider_name()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        using var scope = _factory!.Services.CreateScope();
        var logins = scope.ServiceProvider.GetRequiredService<ExternalLoginService>();

        // A candidate that would fail the register-path policy outright (illegal characters, bad
        // separator placement via the trailing space). An OAuth callback has nobody to ask for a
        // correction, so DeriveAvailableAsync sanitizes instead of refusing (README asymmetry, Task B).
        var result = await logins.SignInOrCreateAsync(
            Identity("google-sub-sanitize", "sanitized-oauth-player@example.com", name: "Bob! "));

        Assert.Equal(ExternalLoginOutcome.Created, result.Outcome);
        Assert.NotNull(result.User);
        Assert.False(string.IsNullOrWhiteSpace(result.User!.DisplayName));

        var policy = scope.ServiceProvider.GetRequiredService<ContentPolicy>();
        var options = scope.ServiceProvider.GetRequiredService<ContentModerationOptions>();
        var verdict = policy.CheckDisplayName(result.User.DisplayName, options);
        Assert.True(verdict.Allowed);
    }

    [SkippableFact]
    public async Task External_login_suffixes_the_display_name_on_collision()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var registerResponse = await _client!.PostAsJsonAsync("/api/auth/register", new
        {
            Email = "bob-password-player@example.com",
            Password = "P@ssword123!",
            DisplayName = "Bob",
            AcceptedTerms = true,
            ConfirmedMinimumAge = true
        }, JsonOptions);
        registerResponse.EnsureSuccessStatusCode();

        using var scope = _factory!.Services.CreateScope();
        var logins = scope.ServiceProvider.GetRequiredService<ExternalLoginService>();

        var result = await logins.SignInOrCreateAsync(
            Identity("google-sub-collision", "bob-oauth-player@example.com", name: "Bob"));

        Assert.Equal(ExternalLoginOutcome.Created, result.Outcome);
        Assert.NotNull(result.User);
        Assert.NotEqual("bob", DisplayNameNormalizer.NormalizeForIdentity(result.User!.DisplayName));
    }

    [SkippableFact]
    public async Task Exchange_code_is_single_use()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        using var scope = _factory!.Services.CreateScope();
        var codes = scope.ServiceProvider.GetRequiredService<ExternalLoginCodeService>();
        var userId = await CreateUserAsync(scope, "exchange-code-user@example.com");

        var raw = await codes.IssueAsync(userId);

        var first = await codes.ConsumeAsync(raw);
        var second = await codes.ConsumeAsync(raw);

        Assert.Equal(userId, first?.UserId);
        Assert.False(first?.AccountCreated);
        Assert.Null(second);
    }

    [SkippableFact]
    public async Task Expired_code_is_rejected()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        using var scope = _factory!.Services.CreateScope();
        var codes = scope.ServiceProvider.GetRequiredService<ExternalLoginCodeService>();
        var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
        var userId = await CreateUserAsync(scope, "expired-code-user@example.com");

        var raw = await codes.IssueAsync(userId);

        // Backdate ExpiresAt directly through the context, IssueAsync always issues a code that is
        // valid *right now*, so the only way to exercise the expiry branch without sleeping the test
        // is to move the clock on the row itself.
        var row = await db.ExternalLoginCodes.SingleAsync(c => c.UserId == userId);
        row.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1);
        await db.SaveChangesAsync();

        var consumed = await codes.ConsumeAsync(raw);
        Assert.Null(consumed);
    }

    /// <summary>
    /// C-10-1's second half. The first half (a banned account cannot exchange a code) is already
    /// covered, and covered better than the finding asked, by
    /// <c>TokenMintingPathTests.Banned_account_cannot_exchange_an_external_sign_in_code</c> plus that
    /// file's declared table of every minting route. Nothing anywhere pinned this branch.
    ///
    /// What it guards: a linked Google account must not be a way around the account's own second
    /// factor. Without the <c>user.TwoFactorEnabled</c> branch in <c>/external/exchange</c>, anyone
    /// who can complete the Google handshake for that address skips TOTP entirely, which makes 2FA
    /// optional in practice for every account that ever linked a provider. The branch exists; this is
    /// what stops it being deleted by a refactor that still passes every other test.
    ///
    /// Asserted on the shape rather than only on the status code, because both outcomes are 200: the
    /// failure mode is a full <c>AuthResponse</c> where a <c>TwoFactorRequiredResponse</c> belongs, so
    /// the test has to say that no token and no refresh token came back.
    /// </summary>
    [SkippableFact]
    public async Task Exchange_hands_a_two_factor_account_a_challenge_rather_than_a_session()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        string raw;
        Guid userId;
        using (var scope = _factory!.Services.CreateScope())
        {
            userId = await CreateUserAsync(scope, "exchange-two-factor@example.com");

            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = await users.FindByIdAsync(userId.ToString());
            Assert.NotNull(user);
            // The endpoint branches on this flag alone, so setting it is the whole precondition. No
            // authenticator is enrolled on purpose: the point is that the exchange refuses to mint a
            // session, not that a particular TOTP code verifies.
            user!.TwoFactorEnabled = true;
            Assert.True((await users.UpdateAsync(user)).Succeeded);

            raw = await scope.ServiceProvider.GetRequiredService<ExternalLoginCodeService>().IssueAsync(userId);
        }

        var response = await _client!.PostAsJsonAsync(
            "/api/auth/external/exchange", new { Code = raw }, JsonOptions);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        Assert.True(body.GetProperty("twoFactorRequired").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("twoFactorTicket").GetString()));

        // The half that actually matters: no session was minted alongside the challenge.
        Assert.False(body.TryGetProperty("token", out _));
        Assert.False(body.TryGetProperty("refreshToken", out _));
        Assert.False(body.TryGetProperty("created", out _));
    }

    /// <summary>
    /// M-4-1. The signup counter can only fire for a handshake that registered the account. The flag
    /// rides on the one-time code because creation happens in the provider callback and the counter
    /// fires later, when the browser exchanges that code. A returning sign-in issues the same kind of
    /// code with the flag off, and must not look like a signup.
    /// </summary>
    [SkippableFact]
    public async Task Exchange_reports_created_only_for_a_code_issued_at_registration()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        string createdCode;
        string returningCode;
        using (var scope = _factory!.Services.CreateScope())
        {
            var codes = scope.ServiceProvider.GetRequiredService<ExternalLoginCodeService>();
            var userId = await CreateUserAsync(scope, "exchange-created-flag@example.com");
            createdCode = await codes.IssueAsync(userId, accountCreated: true);
            returningCode = await codes.IssueAsync(userId);
        }

        var created = await ExchangeAsync(createdCode);
        Assert.True(created.GetProperty("created").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(created.GetProperty("token").GetString()));

        var returning = await ExchangeAsync(returningCode);
        Assert.False(returning.GetProperty("created").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(returning.GetProperty("token").GetString()));
    }

    private async Task<JsonElement> ExchangeAsync(string raw)
    {
        var response = await _client!.PostAsJsonAsync(
            "/api/auth/external/exchange", new { Code = raw }, JsonOptions);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
    }

    [SkippableFact]
    public async Task Fail_closed_when_google_is_not_configured()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var response = await _client!.GetAsync("/api/auth/external/google/start");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [SkippableFact]
    public async Task Callback_fails_closed_when_google_is_not_configured()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        // The callback is anonymous and publicly reachable, and when Google is unconfigured the
        // external cookie scheme is never registered, so without its own guard this would throw
        // "No authentication handler is registered for the scheme 'External'" and answer a stray GET
        // with a 500. Unconfigured is the shipped default, so this is the common case, not the edge.
        var response = await _client!.GetAsync("/api/auth/external/callback");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private sealed record AuthResult(
        Guid UserId, string Email, string DisplayName, string Token, string RefreshToken, DateTimeOffset ExpiresAt);
}
