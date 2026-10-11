using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using KiriChess.Server.Account;
using KiriChess.Server.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Xunit;

namespace KiriChess.Server.Tests;

/// <summary>
/// End-to-end proof of uploaded avatars: multipart PUT /api/account/avatar, DELETE clear, and the
/// public profile + image GET echo. Requires Docker; skips cleanly when no Docker daemon is reachable.
/// </summary>
public sealed class AvatarEndpointTests : IAsyncLifetime
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
                    ["Jwt:Key"] = "avatar-endpoint-test-signing-key-at-least-32-bytes",
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
    public async Task Put_requires_auth()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        using var content = BuildMultipart(AvatarTestImages.Square128);
        var response = await _client!.PutAsync("/api/account/avatar", content);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [SkippableFact]
    public async Task Put_square_jpeg_is_echoed_on_the_public_profile_and_image_route()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var user = await RegisterAsync("avatar-upload@example.com", "AvatarUpload");
        AuthorizeAs(user);

        using var content = BuildMultipart(AvatarTestImages.Square128);
        var put = await _client!.PutAsync("/api/account/avatar", content);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var body = await put.Content.ReadFromJsonAsync<AvatarResponse>(JsonOptions);
        Assert.NotNull(body!.AvatarUrl);
        Assert.StartsWith($"/api/users/{user.UserId:D}/avatar?v=", body.AvatarUrl);

        var profile = await _client.GetAsync($"/api/users/{user.UserId}");
        Assert.Equal(HttpStatusCode.OK, profile.StatusCode);
        var dto = await profile.Content.ReadFromJsonAsync<PublicProfileDto>(JsonOptions);
        Assert.Equal(body.AvatarUrl, dto!.AvatarUrl);

        var image = await _client.GetAsync(body.AvatarUrl);
        Assert.Equal(HttpStatusCode.OK, image.StatusCode);
        Assert.Equal(JpegAvatar.ContentType, image.Content.Headers.ContentType?.MediaType);
        var bytes = await image.Content.ReadAsByteArrayAsync();
        Assert.True(JpegAvatar.TryValidate(bytes, out _));
    }

    [SkippableFact]
    public async Task Delete_restores_initials()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var user = await RegisterAsync("avatar-clear@example.com", "AvatarClear");
        AuthorizeAs(user);

        using (var content = BuildMultipart(AvatarTestImages.Square128))
            (await _client!.PutAsync("/api/account/avatar", content)).EnsureSuccessStatusCode();

        var cleared = await _client.DeleteAsync("/api/account/avatar");
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        var body = await cleared.Content.ReadFromJsonAsync<AvatarResponse>(JsonOptions);
        Assert.Null(body!.AvatarUrl);

        var profile = await _client.GetAsync($"/api/users/{user.UserId}");
        var dto = await profile.Content.ReadFromJsonAsync<PublicProfileDto>(JsonOptions);
        Assert.Null(dto!.AvatarUrl);

        var image = await _client.GetAsync($"/api/users/{user.UserId}/avatar");
        Assert.Equal(HttpStatusCode.NotFound, image.StatusCode);
    }

    [SkippableFact]
    public async Task Put_rejects_a_non_jpeg_body()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var user = await RegisterAsync("avatar-bad@example.com", "AvatarBad");
        AuthorizeAs(user);

        using var content = BuildMultipart([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var response = await _client!.PutAsync("/api/account/avatar", content);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static MultipartFormDataContent BuildMultipart(byte[] bytes)
    {
        var fileContent = new ByteArrayContent(bytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        var form = new MultipartFormDataContent();
        form.Add(fileContent, "file", "avatar.jpg");
        return form;
    }

    private void AuthorizeAs(AuthResult who) =>
        _client!.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", who.Token);

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

    private sealed record AuthResult(
        Guid UserId, string Email, string DisplayName, string Token, string RefreshToken,
        DateTimeOffset ExpiresAt, string[] Roles);
}
