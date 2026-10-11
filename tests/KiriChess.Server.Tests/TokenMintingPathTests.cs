using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using KiriChess.Server.Auth;
using KiriChess.Server.Data;
using KiriChess.Server.Data.Entities;
using KiriChess.Server.Infrastructure.OpenApi;
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
/// C-1-1. The finding was that sanction enforcement is hand-applied per endpoint and two endpoints
/// were missed, and that <c>SanctionPolicy</c>'s own doc comment enumerated its enforcement points as
/// if the list were complete, which is what made the misses invisible. Fixing the two instances does
/// not close the class of miss; this file is what closes it.
///
/// Two guards, deliberately of different kinds:
///
/// <list type="number">
/// <item>A source scan (no Docker) that finds every call site which mints a token pair and requires
/// each one to appear in <see cref="Declared"/> with an explicit decision. A new token-minting
/// endpoint fails this test until somebody writes down whether it enforces the ban and why. That is
/// the actual guard, since the failure mode being defended against is a future endpoint nobody
/// thought to check, not the three fixed here.</item>
/// <item>Behavioural tests (Docker) proving a banned account really is refused at the paths the
/// finding named and at the two the scan turned up.</item>
/// </list>
///
/// A source scan was chosen over the finding's other suggested option, a shared filter that every
/// endpoint opts out of deliberately. A filter changes behaviour for every endpoint in the
/// application at once, including the anonymous ones and the read-only ones, and the blast radius of
/// getting that wrong is the whole site failing closed. A test that fails loudly on a new call site
/// gets the same "opt out deliberately" property at no runtime risk.
/// </summary>
public sealed class TokenMintingPathTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const string Password = "P@ssword123!";

    // ------------------------------------------------------------------------------------------
    // The table.
    // ------------------------------------------------------------------------------------------

    /// <summary>Why a minting path does or does not enforce an AccountBan.</summary>
    private enum BanCheck
    {
        /// <summary>The path must call SanctionPolicy.BlockingSanctionAsync before it mints.</summary>
        Required,
        /// <summary>The path deliberately does not, for the reason recorded beside it.</summary>
        NotApplicable
    }

    /// <summary>
    /// Every route that mints an access/refresh pair, keyed by source file and route literal. Adding a
    /// row is a deliberate act: the scan below fails on any minting call site missing from here, and
    /// fails on any row here that no longer matches a call site.
    /// </summary>
    private static readonly Dictionary<(string File, string Route), (BanCheck Check, string Why)> Declared = new()
    {
        [("AuthEndpoints.cs", "/register")] =
            (BanCheck.NotApplicable,
             "The account does not exist until CreateAsync returns, so it cannot carry a sanction. "
             + "A ban follows the account row, and there is none to look up."),

        [("AuthEndpoints.cs", "/login")] = (BanCheck.Required, "Phase 8.5."),
        [("AuthEndpoints.cs", "/login/2fa")] = (BanCheck.Required, "Phase 8.5, re-checked minutes after /login."),
        [("AuthEndpoints.cs", "/refresh")] =
            (BanCheck.Required, "The point at which an already-issued session finally dies."),

        [("ExternalAuthEndpoints.cs", "/external/exchange")] =
            (BanCheck.Required,
             "C-1-1, the instance the finding named. A banned player who can still complete the Google "
             + "handshake was drawing fresh access tokens indefinitely."),

        [("TwoFactorEndpoints.cs", "/enable")] =
            (BanCheck.Required,
             "C-1-1, found by this test rather than named in the finding. Mints a fresh pair off "
             + "nothing but an unexpired access token, so toggling 2FA was a way to keep renewing a "
             + "session without ever presenting a refresh token to the check at /refresh."),

        [("TwoFactorEndpoints.cs", "/disable")] =
            (BanCheck.Required, "The other half of /enable, same mechanism."),
    };

    // ------------------------------------------------------------------------------------------
    // The scan.
    // ------------------------------------------------------------------------------------------

    // IssueAuthResponseAsync is the shared mint (it creates the access token AND issues the refresh
    // token); tokens.CreateToken is the one place that mints an access token without going through it,
    // on /refresh. Between them these two patterns are the complete set of ways a pair reaches a
    // caller, which the site-count floor below is what actually pins.
    private static readonly Regex MintRegex = new(
        @"IssueAuthResponseAsync\(|\btokens\.CreateToken\(", RegexOptions.Compiled);

    private static readonly Regex RouteRegex = new(
        @"Map(?:Get|Post|Put|Patch|Delete)\(\s*""([^""]*)""", RegexOptions.Compiled);

    [Fact]
    public void Every_token_minting_call_site_is_declared()
    {
        var (found, _) = ScanMintingSites();

        var undeclared = found.Keys.Where(k => !Declared.ContainsKey(k)).ToArray();
        var stale = Declared.Keys.Where(k => !found.ContainsKey(k)).ToArray();

        Assert.True(undeclared.Length == 0,
            "These routes mint a token pair and are not declared in TokenMintingPathTests.Declared:\n  "
            + string.Join("\n  ", undeclared.Select(k => $"{k.File} {k.Route}"))
            + "\n\nAdd a row saying whether the route enforces an AccountBan and why. This is C-1-1's "
            + "'close the class of miss': a token-minting endpoint that nobody decided about is the bug.");

        Assert.True(stale.Length == 0,
            "These declared routes no longer mint a token pair; remove the rows:\n  "
            + string.Join("\n  ", stale.Select(k => $"{k.File} {k.Route}")));
    }

    [Fact]
    public void Every_path_declared_ban_checked_checks_before_it_mints()
    {
        var (found, _) = ScanMintingSites();

        var offenders = Declared
            .Where(d => d.Value.Check == BanCheck.Required)
            .Where(d => found.TryGetValue(d.Key, out var checksFirst) && !checksFirst)
            .Select(d => $"{d.Key.File} {d.Key.Route}")
            .ToArray();

        Assert.True(offenders.Length == 0,
            "These routes are declared BanCheck.Required but no BlockingSanctionAsync call appears "
            + "between the route's Map* call and the point where it mints a token pair:\n  "
            + string.Join("\n  ", offenders));
    }

    [Fact]
    public void Account_delete_is_deliberately_not_an_enforcement_point()
    {
        // C-1-1's other named miss. Owner 2026-09-08: keep self-delete. A future "fix" that adds
        // BlockingSanctionAsync( here would deny Art. 17 during the still-valid access-token window.
        var path = Path.Combine(OpenApiExport.FindRepoRoot(), "src", "KiriChess.Server", "Account", "AccountEndpoints.cs");
        var source = File.ReadAllText(path);
        var start = source.IndexOf("MapPost(\"/delete\"", StringComparison.Ordinal);
        Assert.True(start >= 0, "AccountEndpoints.cs no longer maps POST /delete; update this pin.");
        var next = source.IndexOf("account.Map", start + 1, StringComparison.Ordinal);
        var handler = next >= 0 ? source[start..next] : source[start..];
        Assert.DoesNotContain("BlockingSanctionAsync(", handler);
        Assert.Contains("keep self-delete", handler, StringComparison.Ordinal);
    }

    /// <summary>
    /// Returns each minting route as (file, route) -> whether a BlockingSanctionAsync call appears
    /// between the route's <c>Map*</c> literal and the minting call.
    ///
    /// Matching over source text is fragile by nature, which is why the site-count floor exists: it is
    /// the guard against the regex itself quietly breaking and this whole file then passing because it
    /// found nothing. Same reasoning, and the same shape, as HubContractTests' own floor.
    /// </summary>
    private static (Dictionary<(string File, string Route), bool> Found, int Sites) ScanMintingSites()
    {
        var repoRoot = OpenApiExport.FindRepoRoot();
        var authRoot = Path.Combine(repoRoot, "src", "KiriChess.Server");

        var found = new Dictionary<(string, string), bool>();
        var sites = 0;

        var files = Directory
            .EnumerateFiles(authRoot, "*.cs", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
            })
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                     && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            var name = Path.GetFileName(file);

            // AuthEndpoints.cs declares the shared mint below all of its own Map* calls, so the
            // CreateToken inside IssueAuthResponseAsync would otherwise be attributed to whichever
            // route happens to be last in the file. The helper is the mint, not a policy point: every
            // caller of it is a declared row above. Everything from its declaration onward is that
            // private-helper region and is skipped, and the assertion below is what keeps that from
            // becoming a hiding place for a new endpoint.
            var helperAt = text.IndexOf(
                "internal static async Task<AuthResponse> IssueAuthResponseAsync", StringComparison.Ordinal);
            if (helperAt >= 0)
            {
                var mapAfterHelper = RouteRegex.Match(text, helperAt);
                Assert.False(mapAfterHelper.Success,
                    $"{name} maps a route at or below IssueAuthResponseAsync's declaration "
                    + $"(\"{(mapAfterHelper.Success ? mapAfterHelper.Groups[1].Value : string.Empty)}\"). "
                    + "The scan in TokenMintingPathTests skips that region, so a token-minting route "
                    + "placed there would go unchecked. Move the route above the helper.");
            }
            var scanLimit = helperAt >= 0 ? helperAt : text.Length;

            foreach (Match mint in MintRegex.Matches(text))
            {
                if (mint.Index >= scanLimit) continue;
                sites++;

                // Walk back to the route this mint sits under.
                Match? enclosing = null;
                foreach (Match route in RouteRegex.Matches(text))
                {
                    if (route.Index > mint.Index) break;
                    enclosing = route;
                }

                Assert.NotNull(enclosing);
                var routeLiteral = enclosing!.Groups[1].Value;
                var checksFirst = text
                    .AsSpan(enclosing.Index, mint.Index - enclosing.Index)
                    .Contains("BlockingSanctionAsync(", StringComparison.Ordinal);

                // A route can mint on more than one branch (an early return and a fall-through). Every
                // branch has to be behind the check, so && rather than an overwrite.
                var key = (name, routeLiteral);
                found[key] = found.TryGetValue(key, out var existing) ? existing && checksFirst : checksFirst;
            }
        }

        Assert.True(sites >= 7,
            $"Only {sites} token-minting call sites were found, which means MintRegex has stopped "
            + "matching rather than that the endpoints went away. Fix the regex; do not lower this floor.");

        return (found, sites);
    }

    // ------------------------------------------------------------------------------------------
    // The behavioural half.
    // ------------------------------------------------------------------------------------------

    private static int _issuerCounter;

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
                    ["Jwt:Key"] = "token-minting-test-signing-key-at-least-32-bytes",
                    ["Jwt:Issuer"] = "kirichess",
                    ["Jwt:Audience"] = "kirichess",
                    ["Jwt:ExpiryMinutes"] = "60",
                    ["Jwt:RefreshTokenExpiryDays"] = "30"
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
    public async Task Banned_account_cannot_exchange_an_external_sign_in_code()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var user = await RegisterAsync("mint-external@example.com", "Mint External");
        await BanAsync(user.UserId);

        // A real Google handshake cannot be driven from a test, so the code is issued directly, which
        // is exactly the state the callback leaves behind after a successful handshake. Everything
        // from here on is the endpoint under test.
        string code;
        using (var scope = _factory!.Services.CreateScope())
            code = await scope.ServiceProvider.GetRequiredService<ExternalLoginCodeService>().IssueAsync(user.UserId);

        var response = await _client!.PostAsJsonAsync("/api/auth/external/exchange", new { Code = code }, JsonOptions);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Your account is suspended", body, StringComparison.Ordinal);
        Assert.DoesNotContain("refreshToken", body, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task Banned_account_cannot_mint_a_pair_by_enabling_two_factor()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var user = await RegisterAsync("mint-2fa-enable@example.com", "Mint Enable");
        Authorize(user.Token);

        var enrol = await _client!.PostAsync("/api/account/2fa/enrol", content: null);
        enrol.EnsureSuccessStatusCode();
        var enrolment = await enrol.Content.ReadFromJsonAsync<EnrolmentDto>(JsonOptions);
        Assert.NotNull(enrolment);

        await BanAsync(user.UserId);

        // The access token minted at registration is still valid: a ban does not invalidate an issued
        // JWT, which is the whole premise of C-1-1. Without the check this returns 200 with a fresh
        // pair, renewing the session the ban was supposed to be ending.
        var response = await _client.PostAsJsonAsync("/api/account/2fa/enable",
            new { Code = TotpTestHelper.Compute(enrolment!.SharedKey) }, JsonOptions);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.DoesNotContain("refreshToken", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task Banned_account_cannot_mint_a_pair_by_disabling_two_factor()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var user = await RegisterAsync("mint-2fa-disable@example.com", "Mint Disable");
        var sharedKey = await TotpTestHelper.EnableTwoFactorAsync(_factory!.Services, user.UserId);

        var login = await TotpTestHelper.LoginWithTwoFactorAsync(
            _client!, "mint-2fa-disable@example.com", Password, sharedKey, JsonOptions);
        login.EnsureSuccessStatusCode();
        var authed = await login.Content.ReadFromJsonAsync<AuthResult>(JsonOptions);
        Assert.NotNull(authed);
        Authorize(authed!.Token);

        await BanAsync(user.UserId);

        var response = await _client!.PostAsJsonAsync("/api/account/2fa/disable",
            new { Code = TotpTestHelper.Compute(sharedKey) }, JsonOptions);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.DoesNotContain("refreshToken", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    // --- helpers -----------------------------------------------------------------------------------

    private void Authorize(string jwt) =>
        _client!.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);

    private async Task<AuthResult> RegisterAsync(string email, string displayName)
    {
        var response = await _client!.PostAsJsonAsync("/api/auth/register", new
        {
            Email = email,
            Password,
            DisplayName = displayName,
            AcceptedTerms = true,
            ConfirmedMinimumAge = true
        }, JsonOptions);
        response.EnsureSuccessStatusCode();
        var auth = await response.Content.ReadFromJsonAsync<AuthResult>(JsonOptions);
        Assert.NotNull(auth);
        return auth!;
    }

    /// <summary>
    /// Writes the ban row straight through the context, the same way SanctionPolicyTests does, rather
    /// than through POST /api/moderation/sanctions. The moderation route needs an Admin with an mfa
    /// claim, which is a second enrolment flow these tests are not about, and issuing the row directly
    /// is a truer reproduction anyway: it is the account's state that matters here, not the route that
    /// produced it. IssuedByUserId has a real foreign key, hence the second account.
    /// </summary>
    private async Task BanAsync(Guid userId)
    {
        var tag = Interlocked.Increment(ref _issuerCounter);
        var issuer = await RegisterAsync($"mint-issuer-{tag}@example.com", $"Mint Issuer {tag}");

        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
        db.UserSanctions.Add(new UserSanction
        {
            UserId = userId,
            Type = SanctionType.AccountBan,
            Reason = "Test sanction.",
            IssuedByUserId = issuer.UserId,
            ExpiresAt = null
        });
        await db.SaveChangesAsync();
    }

    private sealed record AuthResult(
        Guid UserId, string Email, string DisplayName, string Token, string RefreshToken, DateTimeOffset ExpiresAt);

    private sealed record EnrolmentDto(string SharedKey, string OtpauthUri);
}
