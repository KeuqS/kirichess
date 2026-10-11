using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using KiriChess.Server.Auth;
using KiriChess.Server.Data;
using KiriChess.Server.Data.Entities;
using KiriChess.Server.Moderation;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Xunit;

namespace KiriChess.Server.Tests;

/// <summary>
/// S-3-1, end to end: the marker written at erasure and the refusal at registration that together
/// make TERMS § 6's "deleting your account and registering again cannot be used to launder a ban"
/// a statement about the code rather than about the schema.
///
/// Requires Docker; skips cleanly when no Docker daemon is reachable. Fixture scaffolding is copied
/// verbatim from <see cref="AccountDataTests"/>, with one addition: a <b>second</b> factory over the
/// same database with no <c>BanEvasion:MarkerSecret</c> configured, because "unset degrades to
/// exactly the old behaviour" is a property of this feature and not an assumption about it.
/// </summary>
public sealed class BanEvasionTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const string Password = "P@ssword123!";
    private const string MarkerSecret = "ban-evasion-test-marker-secret-at-least-32-bytes";

    /// <summary>Duplicated from <see cref="BanEvasionMarker"/> on purpose: a golden-value test that
    /// imported the constant would still pass if the derivation silently changed.</summary>
    private const string DomainSeparator = "kirichess:ban-evasion:v1:";

    /// <summary>ProblemDetails' per-request activity id. Different on every call, and the only thing
    /// that legitimately differs between two otherwise identical validation responses.</summary>
    private static readonly Regex TraceIdRegex = new("\"traceId\":\"[^\"]*\"", RegexOptions.Compiled);

    private PostgreSqlContainer? _pg;
    private WebApplicationFactory<Program>? _factory;
    private HttpClient? _client;
    private WebApplicationFactory<Program>? _noSecretFactory;
    private HttpClient? _noSecretClient;

    public async Task InitializeAsync()
    {
        if (!DockerAvailability.IsAvailable) return;

        _pg = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
        await _pg.StartAsync();

        _factory = BuildFactory(MarkerSecret);

        using (var scope = _factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<KiriChessDbContext>().Database.MigrateAsync();

        _client = _factory.CreateClient();

        // Same database, no secret. This is the shipped default (appsettings.json leaves it empty),
        // so it is the configuration every other test in this suite already runs under.
        _noSecretFactory = BuildFactory(secret: "");
        _noSecretClient = _noSecretFactory.CreateClient();
    }

    private WebApplicationFactory<Program> BuildFactory(string secret) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Postgres"] = _pg!.GetConnectionString(),
                    ["Jwt:Key"] = "ban-evasion-endpoint-test-signing-key-at-least-32-bytes",
                    ["Jwt:Issuer"] = "kirichess",
                    ["Jwt:Audience"] = "kirichess",
                    ["Jwt:ExpiryMinutes"] = "60",
                    ["BanEvasion:MarkerSecret"] = secret
                });
            });
        });

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        _noSecretClient?.Dispose();
        if (_factory is not null) await _factory.DisposeAsync();
        if (_noSecretFactory is not null) await _noSecretFactory.DisposeAsync();
        if (_pg is not null) await _pg.DisposeAsync();
    }

    // --- What erasure writes, and for which sanctions ---------------------------------------------

    [SkippableFact]
    public async Task Erasure_of_a_banned_account_writes_the_marker()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        const string email = "evader-writes@example.com";
        var user = await RegisterAsync(email, "EvaderWrites");
        await AddSanctionAsync(user.UserId, SanctionType.AccountBan);

        await DeleteAsync(user);

        Assert.Equal(ExpectedMarker(email), await MarkerOfAsync(user.UserId));
    }

    [SkippableFact]
    public async Task Erasure_of_an_unsanctioned_account_writes_no_marker()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        // The whole "erasure is unchanged for everyone who was not banned" claim, in one assertion.
        var user = await RegisterAsync("evader-clean@example.com", "EvaderClean");

        await DeleteAsync(user);

        Assert.Null(await MarkerOfAsync(user.UserId));
    }

    [SkippableTheory]
    [InlineData(SanctionType.Warning)]
    [InlineData(SanctionType.RatedPlayBan)]
    public async Task Erasure_under_a_lesser_sanction_writes_no_marker(SanctionType type)
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        // Scope, stated as a test rather than as a comment: only a full account ban is something a
        // fresh account escapes, so only a full account ban earns a marker. An over-eager
        // "Type != Warning" refactor fails here, the same shape SanctionPolicyTests guards.
        var user = await RegisterAsync($"evader-lesser-{type}@example.com", $"EvLesser{type}");
        await AddSanctionAsync(user.UserId, type);

        await DeleteAsync(user);

        Assert.Null(await MarkerOfAsync(user.UserId));
    }

    [SkippableFact]
    public async Task Erasure_of_a_banned_account_still_scrubs_everything_it_scrubbed_before()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        // The marker must not turn out to be a way the address survives. Erasure has to stay exactly
        // as irreversible for a banned account as for anyone else.
        const string email = "evader-still-scrubs@example.com";
        var user = await RegisterAsync(email, "EvaderStillScrubs");
        await AddSanctionAsync(user.UserId, SanctionType.AccountBan);

        await DeleteAsync(user);

        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
        var row = await db.Users.SingleAsync(u => u.Id == user.UserId);
        Assert.Null(row.Email);
        Assert.Null(row.NormalizedEmail);
        Assert.Null(row.PasswordHash);
        Assert.Equal("Deleted account", row.DisplayName);
        Assert.NotNull(row.DeletedAt);
    }

    [SkippableFact]
    public async Task The_marker_is_a_keyed_one_way_value_and_not_the_address()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        const string email = "evader-opaque@example.com";
        var user = await RegisterAsync(email, "EvaderOpaque");
        await AddSanctionAsync(user.UserId, SanctionType.AccountBan);

        await DeleteAsync(user);
        var marker = await MarkerOfAsync(user.UserId);
        Assert.NotNull(marker);

        // Not the address, in any casing, and not a base64 or hex encoding of it.
        Assert.DoesNotContain("evader-opaque", marker, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("example.com", marker, StringComparison.OrdinalIgnoreCase);
        var normalized = email.ToUpperInvariant();
        Assert.DoesNotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes(normalized)), marker!, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToHexStringLower(Encoding.UTF8.GetBytes(normalized)), marker!, StringComparison.Ordinal);

        // And not the unkeyed digest either. An unkeyed SHA-256 of an email is not one-way in
        // practice - the space of real addresses is small enough to enumerate offline - so "it is
        // hashed" is not the property that matters here; "it is hashed under a key nobody has" is.
        var unkeyed = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        Assert.NotEqual(unkeyed, marker);

        // The key is genuinely in the derivation: the same address under a different secret gives a
        // different value, which is what makes rotation an invalidation rather than a no-op.
        Assert.NotEqual(ExpectedMarker(email, secret: MarkerSecret + "-other"), marker);

        Assert.Equal(64, marker!.Length);
    }

    // --- What registration does with it ------------------------------------------------------------

    [SkippableFact]
    public async Task Re_registration_with_the_same_address_after_a_banned_erasure_is_refused()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        // The finding, exactly: ban, erase (which releases the address and the handle), sign up again
        // with both. Before S-3-1 this returned 200 and a clean, rated account.
        const string email = "evader-refused@example.com";
        var user = await RegisterAsync(email, "EvaderRefused");
        await AddSanctionAsync(user.UserId, SanctionType.AccountBan);
        await DeleteAsync(user);

        var again = await AttemptRegisterAsync(_client!, email, "EvaderRefused");

        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
    }

    [SkippableFact]
    public async Task The_refusal_is_indistinguishable_from_the_answer_a_live_address_gets()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        // The enumeration property. Registration already tells a caller that an address is taken;
        // that is a pre-existing shape (and S-5-5 covers the same class on the reset path). What must
        // NOT happen is a second, recognisable answer that means "this address was banned", because
        // that would turn /register into a ban oracle for any address an attacker cares to try.
        const string bannedEmail = "evader-oracle-banned@example.com";
        var banned = await RegisterAsync(bannedEmail, "EvaderOracleBanned");
        await AddSanctionAsync(banned.UserId, SanctionType.AccountBan);
        await DeleteAsync(banned);

        const string liveEmail = "evader-oracle-live@example.com";
        await RegisterAsync(liveEmail, "EvaderOracleLive");

        var bannedResponse = await AttemptRegisterAsync(_client!, bannedEmail, "EvaderOracleProbeA");
        var takenResponse = await AttemptRegisterAsync(_client!, liveEmail, "EvaderOracleProbeB");

        Assert.Equal(takenResponse.StatusCode, bannedResponse.StatusCode);

        // Byte for byte the same body once two things that legitimately differ per request are
        // substituted out: each one's own address (Identity's DuplicateEmail description quotes it)
        // and ProblemDetails' traceId, which is the current activity and is different on every call.
        var bannedBody = Normalize(await bannedResponse.Content.ReadAsStringAsync(), bannedEmail);
        var takenBody = Normalize(await takenResponse.Content.ReadAsStringAsync(), liveEmail);
        Assert.Equal(takenBody, bannedBody);

        static string Normalize(string body, string email) => TraceIdRegex.Replace(
            body.Replace(email, "ADDRESS", StringComparison.Ordinal), "\"traceId\":\"TRACE\"");
    }

    [SkippableFact]
    public async Task A_different_address_is_unaffected_by_a_banned_erasure()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var banned = await RegisterAsync("evader-other-banned@example.com", "EvaderOtherBanned");
        await AddSanctionAsync(banned.UserId, SanctionType.AccountBan);
        await DeleteAsync(banned);

        var other = await AttemptRegisterAsync(_client!, "evader-other-fresh@example.com", "EvaderOtherFresh");

        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
    }

    [SkippableFact]
    public async Task Re_registration_is_allowed_once_the_ban_is_lifted()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        // Lifetime, half one: the refusal is a query-time condition on the sanction row, so a lift
        // takes effect on the very next request. Nothing waits on the retention sweep for this.
        const string email = "evader-lifted@example.com";
        var user = await RegisterAsync(email, "EvaderLifted");
        var sanctionId = await AddSanctionAsync(user.UserId, SanctionType.AccountBan);
        await DeleteAsync(user);
        Assert.Equal(HttpStatusCode.BadRequest, (await AttemptRegisterAsync(_client!, email, "EvaderLiftedProbe")).StatusCode);

        using (var scope = _factory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
            var sanction = await db.UserSanctions.SingleAsync(s => s.Id == sanctionId);
            sanction.LiftedAt = DateTimeOffset.UtcNow;
            sanction.LiftReason = "Appeal upheld.";
            await db.SaveChangesAsync();
        }

        var again = await AttemptRegisterAsync(_client!, email, "EvaderLiftedAgain");
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
    }

    [SkippableFact]
    public async Task Re_registration_is_allowed_once_the_ban_has_expired()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        // Lifetime, half two. Expiry is evaluated at read time and there is no sweeper for it (see
        // UserSanction's own doc), so this has to hold with nothing having run in between.
        const string email = "evader-expired@example.com";
        var user = await RegisterAsync(email, "EvaderExpired");
        var sanctionId = await AddSanctionAsync(user.UserId, SanctionType.AccountBan,
            expiresAt: DateTimeOffset.UtcNow.AddHours(1));
        await DeleteAsync(user);
        Assert.Equal(HttpStatusCode.BadRequest, (await AttemptRegisterAsync(_client!, email, "EvaderExpiredProbe")).StatusCode);

        using (var scope = _factory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
            var sanction = await db.UserSanctions.SingleAsync(s => s.Id == sanctionId);
            sanction.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }

        var again = await AttemptRegisterAsync(_client!, email, "EvaderExpiredAgain");
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
    }

    [SkippableFact]
    public async Task The_google_create_path_refuses_a_banned_address_too()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        // Gating only /api/auth/register would have shipped a control with a one-click bypass: after
        // erasure the Email column is null, so a Google handshake for the same address falls through
        // to ExternalLoginService's CREATE branch rather than its link branch.
        const string email = "evader-google@example.com";
        var user = await RegisterAsync(email, "EvaderGoogle");
        await AddSanctionAsync(user.UserId, SanctionType.AccountBan);
        await DeleteAsync(user);

        using var scope = _factory!.Services.CreateScope();
        var logins = scope.ServiceProvider.GetRequiredService<ExternalLoginService>();
        var result = await logins.SignInOrCreateAsync(
            new ExternalIdentity("Google", "google-key-evader", email, EmailVerified: true, "Evader Google"));

        Assert.Equal(ExternalLoginOutcome.LinkRefused, result.Outcome);
        Assert.Null(result.User);
    }

    // --- Unset secret: the whole feature is inert, and erasure never fails because of it ------------

    [SkippableFact]
    public async Task With_no_marker_secret_a_banned_erasure_writes_nothing_and_still_succeeds()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        // The shipped default. An Art. 17 erasure must not depend on an operator having set an
        // optional key, and the one thing that must never happen is a marker derived from an empty
        // key - that value would be identical for every banned tombstone and the first
        // re-registration of any address at all would collide with all of them.
        const string email = "evader-nosecret@example.com";
        var user = await RegisterAsync(email, "EvaderNoSecret", _noSecretClient!);
        await AddSanctionAsync(user.UserId, SanctionType.AccountBan);

        var deleted = await DeleteAsync(user, _noSecretClient!);
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);

        Assert.Null(await MarkerOfAsync(user.UserId));

        var again = await AttemptRegisterAsync(_noSecretClient!, email, "EvaderNoSecretAgain");
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
    }

    // --- Helpers -----------------------------------------------------------------------------------

    private static string ExpectedMarker(string email, string secret = MarkerSecret) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(secret),
            Encoding.UTF8.GetBytes(DomainSeparator + email.ToUpperInvariant())));

    private async Task<string?> MarkerOfAsync(Guid userId)
    {
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
        return await db.Users.Where(u => u.Id == userId).Select(u => u.BanEvasionEmailHash).SingleAsync();
    }

    /// <summary>
    /// Seeds a sanction straight through the DbContext, the same way <see cref="SanctionPolicyTests"/>
    /// does: the moderation endpoint would need an admin with 2FA, which is a different feature's
    /// setup and not what any of these tests are about.
    /// </summary>
    private async Task<Guid> AddSanctionAsync(
        Guid userId, SanctionType type, DateTimeOffset? expiresAt = null)
    {
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
        var sanction = new UserSanction
        {
            UserId = userId,
            Type = type,
            Reason = "Test sanction.",
            IssuedByUserId = userId, // self-issued: the issuer's identity is irrelevant to this policy
            ExpiresAt = expiresAt
        };
        db.UserSanctions.Add(sanction);
        await db.SaveChangesAsync();
        return sanction.Id;
    }

    private async Task<AuthResult> RegisterAsync(string email, string displayName, HttpClient? client = null)
    {
        var response = await AttemptRegisterAsync(client ?? _client!, email, displayName);
        response.EnsureSuccessStatusCode();
        var auth = await response.Content.ReadFromJsonAsync<AuthResult>(JsonOptions);
        Assert.NotNull(auth);
        return auth!;
    }

    private static Task<HttpResponseMessage> AttemptRegisterAsync(
        HttpClient client, string email, string displayName) =>
        client.PostAsJsonAsync("/api/auth/register", new
        {
            Email = email,
            Password,
            DisplayName = displayName,
            AcceptedTerms = true,
            ConfirmedMinimumAge = true
        }, JsonOptions);

    private async Task<HttpResponseMessage> DeleteAsync(AuthResult who, HttpClient? client = null)
    {
        var target = client ?? _client!;
        target.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", who.Token);
        var response = await target.PostAsJsonAsync("/api/account/delete",
            new { Acknowledgement = "DELETE MY ACCOUNT", Password }, JsonOptions);
        target.DefaultRequestHeaders.Authorization = null;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return response;
    }

    private sealed record AuthResult(
        Guid UserId, string Email, string DisplayName, string Token, string RefreshToken,
        DateTimeOffset ExpiresAt, string[] Roles);
}
