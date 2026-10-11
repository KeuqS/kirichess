using System.Net.Http.Json;
using System.Text.Json;
using KiriChess.Server.Data;
using KiriChess.Server.Data.Entities;
using Microsoft.AspNetCore.DataProtection;
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
/// Phase 10a.1. Pins that the Data Protection key ring outlives the process that created it, which
/// is what makes an email-confirmation or password-reset link survive a deploy.
/// <para>
/// <b>Why the obvious test would not have caught the defect.</b> Asserting that
/// <c>AddDataProtection()</c> was called passes against a ring that still evaporates, and so does a
/// plain protect/restart/unprotect round trip inside one test process: ASP.NET's default provider
/// writes to a per-user folder on the machine, so two hosts started by the same test run share a key
/// ring even when nothing is persisted anywhere the container would keep. The discriminating
/// assertion is therefore the one in the first test below, which unprotects the app's payload from a
/// provider that has <em>no</em> source of keys except the database.
/// </para>
/// Requires Docker; skips cleanly when no Docker daemon is reachable.
/// </summary>
public sealed class DataProtectionKeyRingTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Any purpose string works; this is the shape Identity's token providers use.</summary>
    private const string Purpose = "KiriChess.Tests.KeyRing";

    private PostgreSqlContainer? _pg;

    public async Task InitializeAsync()
    {
        if (!DockerAvailability.IsAvailable) return;
        _pg = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
        await _pg.StartAsync();

        var options = new DbContextOptionsBuilder<KiriChessDbContext>()
            .UseNpgsql(_pg.GetConnectionString())
            .Options;
        await using var db = new KiriChessDbContext(options);
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_pg is not null) await _pg.DisposeAsync();
    }

    [SkippableFact]
    public async Task Key_ring_the_app_writes_is_readable_from_the_database_alone()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        string protectedPayload;
        await using (var host = CreateHost())
        {
            using var scope = host.Services.CreateScope();
            var protector = scope.ServiceProvider
                .GetRequiredService<IDataProtectionProvider>()
                .CreateProtector(Purpose);
            protectedPayload = protector.Protect("a confirmation link somebody already received");
        }

        // The key ring is in Postgres, not on the machine that happened to run the host. Checked
        // before the round trip so a failure here reads as "nothing was persisted" rather than as a
        // cryptography error further down.
        await using (var db = new KiriChessDbContext(new DbContextOptionsBuilder<KiriChessDbContext>()
            .UseNpgsql(_pg!.GetConnectionString()).Options))
        {
            Assert.NotEmpty(await db.DataProtectionKeys.ToListAsync());
        }

        // The discriminating half: this provider is built from nothing but the database, so it
        // cannot fall back to the filesystem ring the default registration would have written. If
        // AddDataProtection() ever moves back inside the Redis guard, this line throws.
        var services = new ServiceCollection();
        services.AddDbContext<KiriChessDbContext>(o => o.UseNpgsql(_pg!.GetConnectionString()));
        services.AddDataProtection()
            .SetApplicationName("KiriChess")
            .PersistKeysToDbContext<KiriChessDbContext>();

        await using var databaseOnly = services.BuildServiceProvider();
        using var databaseOnlyScope = databaseOnly.CreateScope();
        var unprotected = databaseOnlyScope.ServiceProvider
            .GetRequiredService<IDataProtectionProvider>()
            .CreateProtector(Purpose)
            .Unprotect(protectedPayload);

        Assert.Equal("a confirmation link somebody already received", unprotected);
    }

    [SkippableFact]
    public async Task Email_confirmation_token_still_works_on_the_host_after_the_one_that_minted_it()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        // What a deploy looks like from the recipient's side: the link was minted by a process that
        // no longer exists by the time they click it. AddDefaultTokenProviders makes this token a
        // DataProtector payload, so it is the key ring above that decides whether the click works.
        Guid userId;
        string confirmToken;
        await using (var minting = CreateHost())
        {
            using var scope = minting.Services.CreateScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

            var user = new ApplicationUser
            {
                UserName = "keyring-player@example.com",
                Email = "keyring-player@example.com",
                DisplayName = "Key Ring Player"
            };
            var created = await users.CreateAsync(user, "P@ssword123!");
            Assert.True(created.Succeeded);

            userId = user.Id;
            confirmToken = await users.GenerateEmailConfirmationTokenAsync(user);
        }

        await using var serving = CreateHost();
        using var client = serving.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/confirm-email",
            new { UserId = userId, Token = confirmToken }, JsonOptions);

        response.EnsureSuccessStatusCode();

        using var verifyScope = serving.Services.CreateScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
        Assert.True((await db.Users.SingleAsync(u => u.Id == userId)).EmailConfirmed);
    }

    private WebApplicationFactory<Program> CreateHost() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Postgres"] = _pg!.GetConnectionString(),
                    ["Jwt:Key"] = "data-protection-test-signing-key-at-least-32-bytes",
                    ["Jwt:Issuer"] = "kirichess",
                    ["Jwt:Audience"] = "kirichess",
                    ["Jwt:ExpiryMinutes"] = "60",
                    ["Jwt:RefreshTokenExpiryDays"] = "30",
                    ["Auth:BreachedPasswordCheckEnabled"] = "false"
                });
            });
        });
}
