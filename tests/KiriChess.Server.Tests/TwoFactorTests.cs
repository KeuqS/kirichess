using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using KiriChess.Server.Auth;
using KiriChess.Server.Data;
using KiriChess.Server.Data.Entities;
using KiriChess.Server.Moderation;
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
/// End-to-end proof of the Phase 9d.2 TOTP two-factor surface: two-step enrolment, the login
/// challenge/ticket exchange, recovery codes, lockout interaction, and the moderator soft-lock (§ 2 of
/// Phase 9d.2). Requires Docker; skips cleanly when no Docker daemon is reachable.
/// Fixture scaffolding is copied verbatim from <see cref="AuthLifecycleTests"/>.
/// </summary>
public sealed class TwoFactorTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const string Password = "P@ssword123!";

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
                    ["Jwt:Key"] = "two-factor-test-signing-key-at-least-32-bytes",
                    ["Jwt:Issuer"] = "kirichess",
                    ["Jwt:Audience"] = "kirichess",
                    ["Jwt:ExpiryMinutes"] = "60",
                    ["Jwt:RefreshTokenExpiryDays"] = "30",
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
    public async Task Enrolment_is_two_steps_a_user_who_never_scans_is_not_locked_out()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var user = await RegisterAsync("enrol-two-step@example.com", "EnrolTwoStep");
        AuthorizeAs(user);

        var enrolResponse = await _client!.PostAsync("/api/account/2fa/enrol", content: null);
        Assert.Equal(HttpStatusCode.OK, enrolResponse.StatusCode);
        var enrolment = await enrolResponse.Content.ReadFromJsonAsync<EnrolmentDto>(JsonOptions);
        Assert.NotNull(enrolment);
        Assert.False(string.IsNullOrWhiteSpace(enrolment!.SharedKey));
        Assert.StartsWith("otpauth://totp/", enrolment.OtpauthUri);

        var statusResponse = await _client!.GetAsync("/api/account/2fa");
        Assert.Equal(HttpStatusCode.OK, statusResponse.StatusCode);
        var status = await statusResponse.Content.ReadFromJsonAsync<StatusDto>(JsonOptions);
        Assert.NotNull(status);
        Assert.False(status!.Enabled);
    }

    [SkippableFact]
    public async Task Enable_requires_a_real_code()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var user = await RegisterAsync("enable-real-code@example.com", "EnableRealCode");
        AuthorizeAs(user);

        var key = await EnrolAsync();

        var wrongResponse = await _client!.PostAsJsonAsync("/api/account/2fa/enable",
            new { Code = "000000", Password }, JsonOptions);
        Assert.Equal(HttpStatusCode.BadRequest, wrongResponse.StatusCode);

        var statusAfterWrong = await _client!.GetAsync("/api/account/2fa");
        var statusAfterWrongBody = await statusAfterWrong.Content.ReadFromJsonAsync<StatusDto>(JsonOptions);
        Assert.False(statusAfterWrongBody!.Enabled);

        var rightResponse = await _client!.PostAsJsonAsync("/api/account/2fa/enable",
            new { Code = ComputeTotp(key), Password }, JsonOptions);
        Assert.Equal(HttpStatusCode.OK, rightResponse.StatusCode);
        var enabled = await rightResponse.Content.ReadFromJsonAsync<EnableDto>(JsonOptions);
        Assert.NotNull(enabled);
        Assert.Equal(10, enabled!.RecoveryCodes.Length);
        Assert.False(string.IsNullOrWhiteSpace(enabled.Auth.Token));
        Assert.False(string.IsNullOrWhiteSpace(enabled.Auth.RefreshToken));
    }

    [SkippableFact]
    public async Task Login_with_two_factor_on_returns_a_challenge_not_tokens()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var user = await RegisterAsync("login-challenge@example.com", "LoginChallenge");
        AuthorizeAs(user);
        var key = await EnrolAsync();
        await EnableAsync(key);

        var loginResponse = await _client!.PostAsJsonAsync("/api/auth/login",
            new { Email = "login-challenge@example.com", Password }, JsonOptions);
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);

        using var body = JsonDocument.Parse(await loginResponse.Content.ReadAsStringAsync());
        var root = body.RootElement;
        Assert.True(root.TryGetProperty("twoFactorRequired", out var required));
        Assert.True(required.GetBoolean());
        Assert.True(root.TryGetProperty("twoFactorTicket", out var ticket));
        Assert.False(string.IsNullOrWhiteSpace(ticket.GetString()));
        Assert.False(root.TryGetProperty("refreshToken", out _));
    }

    [SkippableFact]
    public async Task Ticket_plus_valid_code_mints_a_real_pair_and_refresh_works_downstream()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var user = await RegisterAsync("ticket-mints-pair@example.com", "TicketMintsPair");
        AuthorizeAs(user);
        var key = await EnrolAsync();
        await EnableAsync(key);

        var ticket = await LoginForTicketAsync("ticket-mints-pair@example.com");

        var codeResponse = await _client!.PostAsJsonAsync("/api/auth/login/2fa",
            new { Ticket = ticket, Code = ComputeTotp(key) }, JsonOptions);
        Assert.Equal(HttpStatusCode.OK, codeResponse.StatusCode);
        var auth = await codeResponse.Content.ReadFromJsonAsync<AuthResult>(JsonOptions);
        Assert.NotNull(auth);
        Assert.False(string.IsNullOrWhiteSpace(auth!.RefreshToken));

        var refreshResponse = await _client!.PostAsJsonAsync("/api/auth/refresh",
            new { RefreshToken = auth.RefreshToken }, JsonOptions);
        Assert.Equal(HttpStatusCode.OK, refreshResponse.StatusCode);
    }

    [SkippableFact]
    public async Task Wrong_code_does_not_burn_the_ticket()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var user = await RegisterAsync("wrong-code-survives@example.com", "WrongCodeSurvives");
        AuthorizeAs(user);
        var key = await EnrolAsync();
        await EnableAsync(key);

        var ticket = await LoginForTicketAsync("wrong-code-survives@example.com");

        var wrongResponse = await _client!.PostAsJsonAsync("/api/auth/login/2fa",
            new { Ticket = ticket, Code = FarWrongCode(key) }, JsonOptions);
        Assert.Equal(HttpStatusCode.Unauthorized, wrongResponse.StatusCode);

        var rightResponse = await _client!.PostAsJsonAsync("/api/auth/login/2fa",
            new { Ticket = ticket, Code = ComputeTotp(key) }, JsonOptions);
        Assert.Equal(HttpStatusCode.OK, rightResponse.StatusCode);
    }

    [SkippableFact]
    public async Task Consumed_ticket_is_dead()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var user = await RegisterAsync("consumed-ticket-dead@example.com", "ConsumedTicketDead");
        AuthorizeAs(user);
        var key = await EnrolAsync();
        await EnableAsync(key);

        var ticket = await LoginForTicketAsync("consumed-ticket-dead@example.com");

        var firstResponse = await _client!.PostAsJsonAsync("/api/auth/login/2fa",
            new { Ticket = ticket, Code = ComputeTotp(key) }, JsonOptions);
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);

        var replayResponse = await _client!.PostAsJsonAsync("/api/auth/login/2fa",
            new { Ticket = ticket, Code = ComputeTotp(key) }, JsonOptions);
        Assert.Equal(HttpStatusCode.Unauthorized, replayResponse.StatusCode);
    }

    [SkippableFact]
    public async Task Failed_codes_trip_the_lockout()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var user = await RegisterAsync("failed-codes-lockout@example.com", "FailedCodesLockout");
        AuthorizeAs(user);
        var key = await EnrolAsync();
        await EnableAsync(key);

        // One ticket for the whole test, taken before any failure: once the lockout trips, /login
        // itself answers 423 and there is no second ticket to be had. Reusing it is not a workaround
        // but the point - a wrong code deliberately does not consume the ticket.
        var ticket = await LoginForTicketAsync("failed-codes-lockout@example.com");

        for (var i = 0; i < 5; i++)
        {
            var attempt = await _client!.PostAsJsonAsync("/api/auth/login/2fa",
                new { Ticket = ticket, Code = FarWrongCode(key) }, JsonOptions);
            Assert.Equal(HttpStatusCode.Unauthorized, attempt.StatusCode);
        }

        // A correct code on a still-live ticket: the 423 can only be the lockout, which is exactly
        // the property 6b.2 is supposed to give the second step.
        var lockedResponse = await _client!.PostAsJsonAsync("/api/auth/login/2fa",
            new { Ticket = ticket, Code = ComputeTotp(key) }, JsonOptions);
        Assert.Equal((HttpStatusCode)423, lockedResponse.StatusCode);
    }

    [SkippableFact]
    public async Task A_recovery_code_works_exactly_once()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var user = await RegisterAsync("recovery-code-once@example.com", "RecoveryCodeOnce");
        AuthorizeAs(user);
        var key = await EnrolAsync();
        var enableDto = await EnableFullAsync(key);
        var recoveryCode = enableDto.RecoveryCodes[0];

        var ticket = await LoginForTicketAsync("recovery-code-once@example.com");
        var firstUse = await _client!.PostAsJsonAsync("/api/auth/login/2fa",
            new { Ticket = ticket, Code = recoveryCode }, JsonOptions);
        Assert.Equal(HttpStatusCode.OK, firstUse.StatusCode);

        var statusResponse = await _client!.GetAsync("/api/account/2fa");
        var status = await statusResponse.Content.ReadFromJsonAsync<StatusDto>(JsonOptions);
        Assert.Equal(9, status!.RecoveryCodesRemaining);

        var secondTicket = await LoginForTicketAsync("recovery-code-once@example.com");
        var secondUse = await _client!.PostAsJsonAsync("/api/auth/login/2fa",
            new { Ticket = secondTicket, Code = recoveryCode }, JsonOptions);
        Assert.Equal(HttpStatusCode.Unauthorized, secondUse.StatusCode);
    }

    [SkippableFact]
    public async Task Moderator_soft_lock_end_to_end()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var admin = await RegisterAndPromoteAsync("mod-soft-lock@example.com", "ModSoftLock");
        AuthorizeAs(admin);

        // No 2FA yet: the role alone must not open the moderation surface.
        var beforeResponse = await _client!.GetAsync($"/api/moderation/users/{admin.UserId}");
        Assert.Equal(HttpStatusCode.Forbidden, beforeResponse.StatusCode);

        var key = await EnrolAsync();
        var enableDto = await EnableFullAsync(key);
        AdoptAuth(enableDto.Auth);

        var afterEnableResponse = await _client!.GetAsync($"/api/moderation/users/{admin.UserId}");
        Assert.Equal(HttpStatusCode.OK, afterEnableResponse.StatusCode);

        var disableResponse = await _client!.PostAsJsonAsync("/api/account/2fa/disable",
            new { Code = ComputeTotp(key), Password }, JsonOptions);
        Assert.Equal(HttpStatusCode.OK, disableResponse.StatusCode);
        var disableDto = await disableResponse.Content.ReadFromJsonAsync<DisableDto>(JsonOptions);
        AdoptAuth(disableDto!.Auth);

        var afterDisableResponse = await _client!.GetAsync($"/api/moderation/users/{admin.UserId}");
        Assert.Equal(HttpStatusCode.Forbidden, afterDisableResponse.StatusCode);
    }

    [SkippableFact]
    public async Task Disable_destroys_the_secret_a_fresh_enrolment_returns_a_different_key()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var user = await RegisterAsync("disable-destroys-secret@example.com", "DisableDestroysSecret");
        AuthorizeAs(user);
        var key = await EnrolAsync();
        await EnableAsync(key);

        var disableResponse = await _client!.PostAsJsonAsync("/api/account/2fa/disable",
            new { Code = ComputeTotp(key), Password }, JsonOptions);
        Assert.Equal(HttpStatusCode.OK, disableResponse.StatusCode);
        var disableDto = await disableResponse.Content.ReadFromJsonAsync<DisableDto>(JsonOptions);
        AdoptAuth(disableDto!.Auth);

        var reEnrolKey = await EnrolAsync();
        Assert.NotEqual(key, reEnrolKey);
    }

    [SkippableFact]
    public async Task Enable_without_password_on_a_password_account_is_400_and_does_not_revoke_sessions()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var user = await RegisterAsync("enable-no-password@example.com", "EnableNoPassword");
        AuthorizeAs(user);
        var key = await EnrolAsync();

        var response = await _client!.PostAsJsonAsync("/api/account/2fa/enable",
            new { Code = ComputeTotp(key) }, JsonOptions);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _client.PostAsJsonAsync("/api/auth/refresh",
            new { RefreshToken = user.RefreshToken }, JsonOptions)).StatusCode);

        using (var scope = _factory!.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var row = await users.FindByIdAsync(user.UserId.ToString());
            Assert.False(row!.TwoFactorEnabled);
        }
    }

    [SkippableFact]
    public async Task Enable_with_wrong_password_is_400_and_does_not_enable()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var user = await RegisterAsync("enable-wrong-password@example.com", "EnableWrongPassword");
        AuthorizeAs(user);
        var key = await EnrolAsync();

        var response = await _client!.PostAsJsonAsync("/api/account/2fa/enable",
            new { Code = ComputeTotp(key), Password = "Wr0ngP@ssword!" }, JsonOptions);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("isn't right", body, StringComparison.Ordinal);

        using (var scope = _factory!.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var row = await users.FindByIdAsync(user.UserId.ToString());
            Assert.False(row!.TwoFactorEnabled);
        }
    }

    [SkippableFact]
    public async Task Disable_without_password_on_a_password_account_is_400()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var user = await RegisterAsync("disable-no-password@example.com", "DisableNoPassword");
        AuthorizeAs(user);
        var key = await EnrolAsync();
        await EnableAsync(key);

        var response = await _client!.PostAsJsonAsync("/api/account/2fa/disable",
            new { Code = ComputeTotp(key) }, JsonOptions);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using (var scope = _factory!.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var row = await users.FindByIdAsync(user.UserId.ToString());
            Assert.True(row!.TwoFactorEnabled);
        }
    }

    [SkippableFact]
    public async Task Google_only_enable_without_reauth_code_is_400_and_does_not_enable()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var user = await CreateGoogleOnlyUserAsync("enable-google-no-code@example.com", "EnableGoogleNoCode");
        AuthorizeAs(user);
        var key = await EnrolAsync();

        var response = await _client!.PostAsJsonAsync("/api/account/2fa/enable",
            new { Code = ComputeTotp(key) }, JsonOptions);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using (var scope = _factory!.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var row = await users.FindByIdAsync(user.UserId.ToString());
            Assert.False(row!.TwoFactorEnabled);
            Assert.True(string.IsNullOrEmpty(row.PasswordHash));
        }
    }

    [SkippableFact]
    public async Task Google_only_enable_with_another_users_code_is_400()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var caller = await CreateGoogleOnlyUserAsync("enable-google-other@example.com", "EnableGoogleOther");
        var other = await CreateGoogleOnlyUserAsync("enable-google-victim@example.com", "EnableGoogleVictim");
        string otherCode;
        using (var scope = _factory!.Services.CreateScope())
            otherCode = await scope.ServiceProvider.GetRequiredService<ExternalLoginCodeService>()
                .IssueAsync(other.UserId);

        AuthorizeAs(caller);
        var key = await EnrolAsync();

        var response = await _client!.PostAsJsonAsync("/api/account/2fa/enable",
            new { Code = ComputeTotp(key), GoogleReauthCode = otherCode }, JsonOptions);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("someone else", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(other.UserId.ToString(), body, StringComparison.OrdinalIgnoreCase);

        using (var scope = _factory!.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var row = await users.FindByIdAsync(caller.UserId.ToString());
            Assert.False(row!.TwoFactorEnabled);
        }
    }

    [SkippableFact]
    public async Task Google_only_enable_with_own_reauth_code_succeeds()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var user = await CreateGoogleOnlyUserAsync("enable-google-ok@example.com", "EnableGoogleOk");
        AuthorizeAs(user);
        var key = await EnrolAsync();
        string reauth;
        using (var scope = _factory!.Services.CreateScope())
            reauth = await scope.ServiceProvider.GetRequiredService<ExternalLoginCodeService>()
                .IssueAsync(user.UserId);

        var response = await _client!.PostAsJsonAsync("/api/account/2fa/enable",
            new { Code = ComputeTotp(key), GoogleReauthCode = reauth }, JsonOptions);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using (var scope = _factory!.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var row = await users.FindByIdAsync(user.UserId.ToString());
            Assert.True(row!.TwoFactorEnabled);
        }
    }

    // --- helpers -----------------------------------------------------------------------------------

    private async Task<string> EnrolAsync()
    {
        var response = await _client!.PostAsync("/api/account/2fa/enrol", content: null);
        response.EnsureSuccessStatusCode();
        var dto = await response.Content.ReadFromJsonAsync<EnrolmentDto>(JsonOptions);
        Assert.NotNull(dto);
        return dto!.SharedKey;
    }

    private async Task EnableAsync(string key)
    {
        var response = await _client!.PostAsJsonAsync("/api/account/2fa/enable",
            new { Code = ComputeTotp(key), Password }, JsonOptions);
        response.EnsureSuccessStatusCode();
    }

    private async Task<EnableDto> EnableFullAsync(string key)
    {
        var response = await _client!.PostAsJsonAsync("/api/account/2fa/enable",
            new { Code = ComputeTotp(key), Password }, JsonOptions);
        response.EnsureSuccessStatusCode();
        var dto = await response.Content.ReadFromJsonAsync<EnableDto>(JsonOptions);
        Assert.NotNull(dto);
        AdoptAuth(dto!.Auth);
        return dto;
    }

    private async Task<string> LoginForTicketAsync(string email)
    {
        var response = await _client!.PostAsJsonAsync("/api/auth/login", new { Email = email, Password }, JsonOptions);
        response.EnsureSuccessStatusCode();
        var dto = await response.Content.ReadFromJsonAsync<TwoFactorRequiredDto>(JsonOptions);
        Assert.NotNull(dto);
        Assert.True(dto!.TwoFactorRequired);
        return dto.TwoFactorTicket;
    }

    private void AuthorizeAs(AuthResult who) =>
        _client!.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", who.Token);

    private void AdoptAuth(AuthResult auth) =>
        _client!.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.Token);

    private async Task<AuthResult> RegisterAsync(string email, string displayName)
    {
        var registerResponse = await _client!.PostAsJsonAsync("/api/auth/register", new
        {
            Email = email,
            Password,
            DisplayName = displayName,
            AcceptedTerms = true,
            ConfirmedMinimumAge = true
        }, JsonOptions);
        registerResponse.EnsureSuccessStatusCode();
        var auth = await registerResponse.Content.ReadFromJsonAsync<AuthResult>(JsonOptions);
        Assert.NotNull(auth);
        return auth!;
    }

    /// <summary>
    /// Password-less user, same shape <see cref="ExternalLoginTests"/> uses for a brand-new Google
    /// identity. Minted through <see cref="AuthEndpoints.IssueAuthResponseAsync"/> so the JWT is a
    /// real access token, not a backdoor that writes TwoFactorEnabled.
    /// </summary>
    private async Task<AuthResult> CreateGoogleOnlyUserAsync(string email, string displayName)
    {
        using var scope = _factory!.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var tokens = scope.ServiceProvider.GetRequiredService<TokenService>();
        var refreshTokens = scope.ServiceProvider.GetRequiredService<RefreshTokenService>();
        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            DisplayName = displayName,
            EmailConfirmed = true,
            LockoutEnabled = true
        };
        var created = await users.CreateAsync(user);
        Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(e => e.Description)));
        Assert.True(string.IsNullOrEmpty(user.PasswordHash));

        var issued = await AuthEndpoints.IssueAuthResponseAsync(
            user, users, tokens, refreshTokens, CancellationToken.None);
        return new AuthResult(
            issued.UserId, issued.Email, issued.DisplayName, issued.Token, issued.RefreshToken,
            issued.ExpiresAt, issued.Roles);
    }

    /// <summary>Registers, promotes to Admin, then re-logs in so the token actually carries the role claim.</summary>
    private async Task<AuthResult> RegisterAndPromoteAsync(string email, string displayName)
    {
        var registered = await RegisterAsync(email, displayName);

        using (var scope = _factory!.Services.CreateScope())
        {
            var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            if (!await roles.RoleExistsAsync(ModerationRoles.Admin))
                await roles.CreateAsync(new IdentityRole<Guid>(ModerationRoles.Admin));
            var user = await users.FindByIdAsync(registered.UserId.ToString());
            await users.AddToRoleAsync(user!, ModerationRoles.Admin);
        }

        var loginResponse = await _client!.PostAsJsonAsync("/api/auth/login", new { Email = email, Password }, JsonOptions);
        loginResponse.EnsureSuccessStatusCode();
        var relogged = await loginResponse.Content.ReadFromJsonAsync<AuthResult>(JsonOptions);
        Assert.NotNull(relogged);
        return relogged!;
    }

    /// <summary>A code guaranteed wrong: ten minutes off the current step, far outside the +/-2 window.</summary>
    private static string FarWrongCode(string key) => ComputeTotp(key, stepOffset: 20);

    /// <summary>
    /// Computes the TOTP code Identity's AuthenticatorTokenProvider will accept for a given shared key.
    /// Lives in <see cref="TotpTestHelper"/> because every admin fixture in the suite now needs it too -
    /// ModerationPolicies.Admin requires the mfa claim, so those classes have to enrol as well.
    /// </summary>
    private static string ComputeTotp(string base32Key, long stepOffset = 0) =>
        TotpTestHelper.Compute(base32Key, stepOffset);

    private sealed record AuthResult(
        Guid UserId, string Email, string DisplayName, string Token, string RefreshToken,
        DateTimeOffset ExpiresAt, string[] Roles);

    private sealed record EnrolmentDto(string SharedKey, string OtpauthUri);
    private sealed record StatusDto(bool Enabled, int RecoveryCodesRemaining, bool HasPassword);
    private sealed record EnableDto(string[] RecoveryCodes, AuthResult Auth);
    private sealed record DisableDto(AuthResult Auth);
    private sealed record TwoFactorRequiredDto(bool TwoFactorRequired, string TwoFactorTicket, int ExpiresInSeconds);
}
