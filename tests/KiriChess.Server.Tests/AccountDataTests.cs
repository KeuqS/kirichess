using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using KiriChess.Server.Account;
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
/// End-to-end proof of the Phase 8a.2 GDPR surface: export privacy rules and account erasure,
/// including the partial-index fix that lets a second deletion succeed. Requires Docker; skips
/// cleanly when no Docker daemon is reachable. Fixture scaffolding is copied verbatim from
/// <see cref="DisplayNameEndpointTests"/>.
/// </summary>
public sealed class AccountDataTests : IAsyncLifetime
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
                    ["Jwt:Key"] = "account-data-endpoint-test-signing-key-at-least-32-bytes",
                    ["Jwt:Issuer"] = "kirichess",
                    ["Jwt:Audience"] = "kirichess",
                    ["Jwt:ExpiryMinutes"] = "60"
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
    public async Task Export_requires_auth()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var response = await _client!.GetAsync("/api/account/export");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [SkippableFact]
    public async Task Export_returns_the_profile_and_an_attachment_header()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var user = await RegisterAsync("export-profile@example.com", "ExportProfile");
        AuthorizeAs(user);

        var response = await _client!.GetAsync("/api/account/export");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("attachment", response.Content.Headers.ContentDisposition?.ToString() ?? string.Empty);

        var export = await response.Content.ReadFromJsonAsync<ExportDto>(JsonOptions);
        Assert.NotNull(export);
        Assert.Equal("export-profile@example.com", export!.Profile.Email);
    }

    [SkippableFact]
    public async Task Export_never_contains_a_token_hash()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        // Registration already issues a refresh token, which is the session under test.
        var user = await RegisterAsync("export-no-hash@example.com", "ExportNoHash");
        AuthorizeAs(user);

        string rawTokenHash;
        using (var scope = _factory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
            var token = await db.RefreshTokens.SingleAsync(t => t.UserId == user.UserId);
            rawTokenHash = token.TokenHash;
        }

        var response = await _client!.GetAsync("/api/account/export");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain(rawTokenHash, body);
        Assert.DoesNotContain(user.RefreshToken, body);

        var export = JsonSerializer.Deserialize<ExportDto>(body, JsonOptions);
        Assert.NotNull(export);
        Assert.NotEmpty(export!.Sessions);
    }

    [SkippableFact]
    public async Task Export_of_a_report_against_the_user_hides_the_reporter()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var reporter = await RegisterAsync("export-hide-reporter@example.com", "HiddenReporter");
        var reported = await RegisterAsync("export-hide-reported@example.com", "ReportedPlayer");

        AuthorizeAs(reporter);
        var reportResponse = await _client!.PostAsJsonAsync("/api/reports", new
        {
            ReportedUserId = reported.UserId,
            GameId = (Guid?)null,
            Reason = "Abuse",
            Details = "Some detail."
        }, JsonOptions);
        reportResponse.EnsureSuccessStatusCode();

        AuthorizeAs(reported);
        var response = await _client!.GetAsync("/api/account/export");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain(reporter.UserId.ToString(), body);
        Assert.DoesNotContain("HiddenReporter", body);

        var export = JsonSerializer.Deserialize<ExportDto>(body, JsonOptions);
        Assert.NotNull(export);
        Assert.Single(export!.ReportsAgainst);
    }

    [SkippableFact]
    public async Task Delete_requires_the_exact_acknowledgement()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var user = await RegisterAsync("delete-bad-ack@example.com", "BadAck");
        AuthorizeAs(user);

        var response = await _client!.PostAsJsonAsync("/api/account/delete",
            new { Acknowledgement = "delete my account" }, JsonOptions);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var login = await _client!.PostAsJsonAsync("/api/auth/login",
            new { Email = "delete-bad-ack@example.com", Password }, JsonOptions);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    [SkippableFact]
    public async Task A_banned_account_with_a_live_access_token_can_still_export_and_delete()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        // B11a / L-2-3, owner 2026-09-08: keep self-delete. A ban revokes refresh, not the current
        // JWT, and this route must not add a second deletion precondition on top of a game in
        // progress. C-1-1's suggested gate would answer 403 or 409 here.
        var user = await RegisterAsync("delete-while-banned@example.com", "DeleteWhileBanned");
        await AddAccountBanAsync(user.UserId);

        AuthorizeAs(user);
        var export = await _client!.GetAsync("/api/account/export");
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);

        var delete = await _client.PostAsJsonAsync("/api/account/delete",
            new { Acknowledgement = "DELETE MY ACCOUNT", Password }, JsonOptions);
        Assert.Equal(HttpStatusCode.OK, delete.StatusCode);

        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
        var row = await db.Users.SingleAsync(u => u.Id == user.UserId);
        Assert.NotNull(row.DeletedAt);
        Assert.Null(row.Email);
        Assert.True(await db.UserSanctions.AnyAsync(s =>
            s.UserId == user.UserId && s.Type == SanctionType.AccountBan && s.LiftedAt == null));
    }

    [SkippableFact]
    public async Task Delete_removes_rematch_offers_in_both_directions()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var white = await RegisterAsync("delete-rematch-white@example.com", "DeleteRematchWhite");
        var black = await RegisterAsync("delete-rematch-black@example.com", "DeleteRematchBlack");
        var gameId = await CreateAndFinishGameAsync(white, black);

        using (var seedScope = _factory!.Services.CreateScope())
        {
            var seedDb = seedScope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
            seedDb.RematchOffers.Add(new RematchOffer
            {
                GameId = gameId,
                OfferedById = white.UserId,
                OfferedAt = DateTimeOffset.UtcNow,
                AcceptedById = black.UserId,
                AcceptedAt = DateTimeOffset.UtcNow,
            });
            await seedDb.SaveChangesAsync();
        }

        AuthorizeAs(white);
        var response = await _client!.PostAsJsonAsync("/api/account/delete",
            new { Acknowledgement = "DELETE MY ACCOUNT", Password }, JsonOptions);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
        Assert.False(await db.RematchOffers.AnyAsync(r => r.GameId == gameId));
    }

    [SkippableFact]
    public async Task Delete_anonymises_the_row_and_keeps_the_game()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var white = await RegisterAsync("delete-keeps-game-white@example.com", "DeleteKeepsWhite");
        var black = await RegisterAsync("delete-keeps-game-black@example.com", "DeleteKeepsBlack");
        var gameId = await CreateAndFinishGameAsync(white, black);

        AuthorizeAs(white);
        var response = await _client!.PostAsJsonAsync("/api/account/delete",
            new { Acknowledgement = "DELETE MY ACCOUNT", Password }, JsonOptions);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();

        var game = await db.Games.SingleAsync(g => g.Id == gameId);
        Assert.Equal(white.UserId, game.WhitePlayerId);

        var row = await db.Users.SingleAsync(u => u.Id == white.UserId);
        Assert.Null(row.Email);
        Assert.Null(row.PasswordHash);
        Assert.Equal("Deleted account", row.DisplayName);
        Assert.Null(row.AvatarImage);
        Assert.Null(row.AvatarUpdatedAt);
        Assert.NotNull(row.DeletedAt);
    }

    [SkippableFact]
    public async Task Delete_clears_the_patron_badge()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var user = await RegisterAsync("delete-clears-patron@example.com", "DeleteClearsPatron");

        // No HTTP path grants a badge without a real Stripe webhook, so this test sets the columns
        // directly, exactly as PatronService.GrantAsync would have left them.
        using (var seedScope = _factory!.Services.CreateScope())
        {
            var seedDb = seedScope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
            var seedRow = await seedDb.Users.SingleAsync(u => u.Id == user.UserId);
            seedRow.IsPatron = true;
            seedRow.PatronTier = PatronTier.Queen;
            seedRow.PatronKind = PatronKind.Monthly;
            seedRow.PatronSince = DateTimeOffset.UtcNow;
            seedRow.StripeSubscriptionId = "sub_erasure";
            seedRow.StripeCustomerId = "cus_erasure";
            await seedDb.SaveChangesAsync();
        }

        AuthorizeAs(user);
        var deleteResponse = await _client!.PostAsJsonAsync("/api/account/delete",
            new
            {
                Acknowledgement = "DELETE MY ACCOUNT",
                Password
            }, JsonOptions);
        Assert.Equal(HttpStatusCode.OK, deleteResponse.StatusCode);

        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
        var row = await db.Users.SingleAsync(u => u.Id == user.UserId);
        Assert.False(row.IsPatron);
        Assert.Equal(PatronTier.None, row.PatronTier);
        Assert.Equal(PatronKind.None, row.PatronKind);
        Assert.Null(row.PatronSince);
        Assert.Null(row.StripeSubscriptionId);
        Assert.Null(row.StripeCustomerId);
    }

    [SkippableFact]
    public async Task Delete_prevents_login()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var user = await RegisterAsync("delete-prevents-login@example.com", "DeletePreventsLogin");
        AuthorizeAs(user);

        var deleteResponse = await _client!.PostAsJsonAsync("/api/account/delete",
            new { Acknowledgement = "DELETE MY ACCOUNT", Password }, JsonOptions);
        Assert.Equal(HttpStatusCode.OK, deleteResponse.StatusCode);

        var login = await _client!.PostAsJsonAsync("/api/auth/login",
            new { Email = "delete-prevents-login@example.com", Password }, JsonOptions);
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
    }

    [SkippableFact]
    public async Task Delete_is_refused_during_an_ongoing_game()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var white = await RegisterAsync("delete-ongoing-white@example.com", "DeleteOngoingWhite");
        var black = await RegisterAsync("delete-ongoing-black@example.com", "DeleteOngoingBlack");

        // Created but never resigned - Result stays GameResult.Ongoing. Phase 9d.8 closed POST
        // /api/games' OpponentId hole, so a two-distinct-player game is created the same way a real
        // player would: a challenge from white, accepted by black.
        AuthorizeAs(white);
        var challengeResponse = await _client!.PostAsJsonAsync("/api/challenges", new
        {
            Pool = "steal1",
            Colour = "White"
        }, JsonOptions);
        challengeResponse.EnsureSuccessStatusCode();
        var challenge = await challengeResponse.Content.ReadFromJsonAsync<ChallengeCreatedDto>(JsonOptions);
        Assert.NotNull(challenge);

        AuthorizeAs(black);
        var acceptResponse = await _client.PostAsync($"/api/challenges/{challenge!.Token}/accept", content: null);
        acceptResponse.EnsureSuccessStatusCode();

        // Back to white for the delete attempt this test is actually about.
        AuthorizeAs(white);
        var response = await _client!.PostAsJsonAsync("/api/account/delete",
            new { Acknowledgement = "DELETE MY ACCOUNT", Password }, JsonOptions);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [SkippableFact]
    public async Task Two_deletions_do_not_collide_on_the_display_name_index()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var first = await RegisterAsync("delete-collision-one@example.com", "CollisionOne");
        var second = await RegisterAsync("delete-collision-two@example.com", "CollisionTwo");

        AuthorizeAs(first);
        var firstDelete = await _client!.PostAsJsonAsync("/api/account/delete",
            new { Acknowledgement = "DELETE MY ACCOUNT", Password }, JsonOptions);
        Assert.Equal(HttpStatusCode.OK, firstDelete.StatusCode);

        AuthorizeAs(second);
        var secondDelete = await _client!.PostAsJsonAsync("/api/account/delete",
            new { Acknowledgement = "DELETE MY ACCOUNT", Password }, JsonOptions);
        Assert.Equal(HttpStatusCode.OK, secondDelete.StatusCode);

        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
        var firstRow = await db.Users.SingleAsync(u => u.Id == first.UserId);
        var secondRow = await db.Users.SingleAsync(u => u.Id == second.UserId);
        Assert.Equal("deletedaccount", firstRow.NormalizedDisplayName);
        Assert.Equal("deletedaccount", secondRow.NormalizedDisplayName);
    }

    [SkippableFact]
    public async Task Deleted_account_cannot_export()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var user = await RegisterAsync("delete-then-export@example.com", "DeleteThenExport");
        AuthorizeAs(user);

        var deleteResponse = await _client!.PostAsJsonAsync("/api/account/delete",
            new { Acknowledgement = "DELETE MY ACCOUNT", Password }, JsonOptions);
        Assert.Equal(HttpStatusCode.OK, deleteResponse.StatusCode);

        // The token is still nominally valid (it was never revoked - it just cannot get past the
        // BuildAsync tombstone check), so re-using it proves the endpoint's own check, not merely
        // that the caller no longer has a token.
        var response = await _client!.GetAsync("/api/account/export");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // --- Phase 9d.2: 2FA and the 8a.2 export/erasure surface --------------------------------------

    [SkippableFact]
    public async Task Export_includes_the_two_factor_fact_but_never_the_secret_or_a_recovery_code()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var user = await RegisterAsync("export-two-factor@example.com", "ExportTwoFactor");
        AuthorizeAs(user);

        var (key, recoveryCodes) = await EnrolAndEnableTwoFactorAsync();

        var response = await _client!.GetAsync("/api/account/export");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain(key, body);
        foreach (var code in recoveryCodes) Assert.DoesNotContain(code, body);

        var export = JsonSerializer.Deserialize<ExportDto>(body, JsonOptions);
        Assert.NotNull(export);
        Assert.True(export!.Profile.TwoFactorEnabled);
    }

    [SkippableFact]
    public async Task Delete_erases_two_factor_credentials_and_challenge_tickets()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var user = await RegisterAsync("delete-erases-two-factor@example.com", "DeleteErasesTwoFactor");
        AuthorizeAs(user);
        await EnrolAndEnableTwoFactorAsync();

        // Leave one outstanding login challenge ticket behind - deleting mid-login-flow must not skip it.
        var loginResponse = await _client!.PostAsJsonAsync("/api/auth/login",
            new { Email = "delete-erases-two-factor@example.com", Password }, JsonOptions);
        loginResponse.EnsureSuccessStatusCode();

        var deleteResponse = await _client!.PostAsJsonAsync("/api/account/delete",
            new { Acknowledgement = "DELETE MY ACCOUNT", Password }, JsonOptions);
        Assert.Equal(HttpStatusCode.OK, deleteResponse.StatusCode);

        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();

        var row = await db.Users.SingleAsync(u => u.Id == user.UserId);
        Assert.False(row.TwoFactorEnabled);
        Assert.False(await db.UserTokens.AnyAsync(t => t.UserId == user.UserId));
        Assert.False(await db.TwoFactorChallenges.AnyAsync(c => c.UserId == user.UserId));
    }

    [SkippableFact]
    public async Task Delete_erases_web_push_subscriptions()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var user = await RegisterAsync("delete-erases-push@example.com", "DeleteErasesPush");

        // No VAPID config is wired up in this fixture, so seed the row directly - same shape as
        // Delete_clears_the_patron_badge seeding the badge columns rather than going through Stripe.
        using (var seedScope = _factory!.Services.CreateScope())
        {
            var seedDb = seedScope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
            seedDb.WebPushSubscriptions.Add(new WebPushSubscription
            {
                Id = Guid.NewGuid(),
                UserId = user.UserId,
                Endpoint = "https://push.example.com/erasure-subscription",
                P256dh = "seed-p256dh-key",
                Auth = "seed-auth-secret"
            });
            await seedDb.SaveChangesAsync();
        }

        AuthorizeAs(user);
        var deleteResponse = await _client!.PostAsJsonAsync("/api/account/delete",
            new { Acknowledgement = "DELETE MY ACCOUNT", Password }, JsonOptions);
        Assert.Equal(HttpStatusCode.OK, deleteResponse.StatusCode);

        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
        Assert.False(await db.WebPushSubscriptions.AnyAsync(s => s.UserId == user.UserId));
    }

    [SkippableFact]
    public async Task Delete_without_password_on_a_password_account_is_400_and_keeps_the_row()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var user = await RegisterAsync("delete-no-password@example.com", "DeleteNoPassword");
        AuthorizeAs(user);

        var response = await _client!.PostAsJsonAsync("/api/account/delete",
            new { Acknowledgement = "DELETE MY ACCOUNT" }, JsonOptions);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
        var row = await db.Users.SingleAsync(u => u.Id == user.UserId);
        Assert.Null(row.DeletedAt);
        Assert.Equal("delete-no-password@example.com", row.Email);
    }

    [SkippableFact]
    public async Task Google_only_delete_without_password_still_works()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var user = await CreateGoogleOnlyUserAsync("delete-google-only@example.com", "DeleteGoogleOnly");
        AuthorizeAs(user);

        var response = await _client!.PostAsJsonAsync("/api/account/delete",
            new { Acknowledgement = "DELETE MY ACCOUNT" }, JsonOptions);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
        var row = await db.Users.SingleAsync(u => u.Id == user.UserId);
        Assert.NotNull(row.DeletedAt);
        Assert.Null(row.Email);
    }

    /// <summary>Enrols and enables 2FA for the currently-authorized user; returns the shared key and the recovery codes.</summary>
    private async Task<(string Key, string[] RecoveryCodes)> EnrolAndEnableTwoFactorAsync()
    {
        var enrolResponse = await _client!.PostAsync("/api/account/2fa/enrol", content: null);
        enrolResponse.EnsureSuccessStatusCode();
        var enrolment = await enrolResponse.Content.ReadFromJsonAsync<EnrolmentDto>(JsonOptions);
        Assert.NotNull(enrolment);

        var enableResponse = await _client!.PostAsJsonAsync("/api/account/2fa/enable",
            new { Code = ComputeTotp(enrolment!.SharedKey), Password }, JsonOptions);
        enableResponse.EnsureSuccessStatusCode();
        var enabled = await enableResponse.Content.ReadFromJsonAsync<EnableDto>(JsonOptions);
        Assert.NotNull(enabled);

        // The enable response hands back a fresh token pair carrying the mfa claim - adopt it so
        // later calls on this client are still authorized.
        _client!.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", enabled!.Auth.Token);

        return (enrolment.SharedKey, enabled.RecoveryCodes);
    }

    /// <summary>
    /// Computes the TOTP code Identity's AuthenticatorTokenProvider will accept for a given shared key.
    /// Copied from TwoFactorTests.cs (UserManager.GenerateTwoFactorTokenAsync cannot produce one; see
    /// that file's copy for the full explanation).
    /// </summary>
    private static string ComputeTotp(string base32Key)
    {
        var key = FromBase32(base32Key);
        var timestep = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;

        var counter = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, timestep);

        var hash = HMACSHA1.HashData(key, counter);
        var offset = hash[^1] & 0x0F;
        var binary =
            ((hash[offset] & 0x7F) << 24) |
            ((hash[offset + 1] & 0xFF) << 16) |
            ((hash[offset + 2] & 0xFF) << 8) |
            (hash[offset + 3] & 0xFF);

        return (binary % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
    }

    private static byte[] FromBase32(string input)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var trimmed = input.TrimEnd('=').ToUpperInvariant();
        var bits = 0;
        var value = 0;
        var output = new List<byte>(trimmed.Length * 5 / 8);

        foreach (var c in trimmed)
        {
            var index = alphabet.IndexOf(c);
            if (index < 0) throw new FormatException($"'{c}' is not a base32 character.");
            value = (value << 5) | index;
            bits += 5;
            if (bits < 8) continue;
            output.Add((byte)((value >> (bits - 8)) & 0xFF));
            bits -= 8;
        }

        return output.ToArray();
    }

    private void AuthorizeAs(AuthResult who) =>
        _client!.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", who.Token);

    /// <summary>
    /// Seeds the ban row directly. Going through <c>SanctionService.IssueAsync</c> would also revoke
    /// refresh tokens, which this pin does not need: C-1-1's window is the still-valid access token.
    /// </summary>
    private async Task AddAccountBanAsync(Guid userId)
    {
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
        db.UserSanctions.Add(new UserSanction
        {
            UserId = userId,
            Type = SanctionType.AccountBan,
            Reason = "Test ban.",
            IssuedByUserId = userId,
            ExpiresAt = null
        });
        await db.SaveChangesAsync();
    }

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
    /// Password-less user, same shape ExternalLoginTests uses for a brand-new Google identity.
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

    /// <summary>
    /// Creates a two-player unrated game and resigns it, authorized as <paramref name="white"/>.
    ///
    /// Phase 9d.8 closed POST /api/games' OpponentId hole (README scope decision 5), so a two-distinct-
    /// player game is now created the same way a real player would: a challenge, created by white and
    /// accepted by black, colour pinned to White so the rest of this helper's "white resigns" contract
    /// is unchanged.
    /// </summary>
    private async Task<Guid> CreateAndFinishGameAsync(AuthResult white, AuthResult black)
    {
        AuthorizeAs(white);
        var challengeResponse = await _client!.PostAsJsonAsync("/api/challenges", new
        {
            Pool = "steal1",
            Colour = "White"
        }, JsonOptions);
        challengeResponse.EnsureSuccessStatusCode();
        var challenge = await challengeResponse.Content.ReadFromJsonAsync<ChallengeCreatedDto>(JsonOptions);
        Assert.NotNull(challenge);

        AuthorizeAs(black);
        var acceptResponse = await _client.PostAsync($"/api/challenges/{challenge!.Token}/accept", content: null);
        acceptResponse.EnsureSuccessStatusCode();
        var accepted = await acceptResponse.Content.ReadFromJsonAsync<ChallengeAcceptResultDto>(JsonOptions);
        Assert.NotNull(accepted);
        var gameId = accepted!.GameId;

        AuthorizeAs(white);
        var resignResponse = await _client.PostAsync($"/api/games/{gameId}/resign", content: null);
        resignResponse.EnsureSuccessStatusCode();
        return gameId;
    }

    private sealed record AuthResult(
        Guid UserId, string Email, string DisplayName, string Token, string RefreshToken,
        DateTimeOffset ExpiresAt, string[] Roles);

    private sealed record GameStateDto(
        Guid GameId,
        string Fen,
        string SideToMove,
        int WhiteTime,
        int BlackTime,
        string Status,
        string? Winner,
        string Termination,
        Guid WhitePlayerId,
        Guid BlackPlayerId,
        int MoveCount);

    private sealed record ExportProfileDto(
        Guid UserId, string? Email, bool EmailConfirmed, string DisplayName, bool TwoFactorEnabled);

    private sealed record ExportReportAgainstDto(Guid ReportId, Guid? GameId, string Reason, string Status, DateTimeOffset CreatedAt);

    private sealed record ExportSessionDto(DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, DateTimeOffset? RevokedAt);

    private sealed record ExportDto(
        string ExportVersion, DateTimeOffset GeneratedAt, ExportProfileDto Profile,
        ExportReportAgainstDto[] ReportsAgainst, ExportSessionDto[] Sessions);

    private sealed record EnrolmentDto(string SharedKey, string OtpauthUri);
    private sealed record EnableDto(string[] RecoveryCodes, AuthResult Auth);

    private sealed record ChallengeCreatedDto(string Token);

    private sealed record ChallengeAcceptResultDto(Guid GameId);
}
