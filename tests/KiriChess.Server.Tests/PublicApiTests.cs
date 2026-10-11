using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using KiriChess.Server.Auth;
using KiriChess.Server.Data;
using KiriChess.Server.Data.Entities;
using KiriChess.Server.Games;
using KiriChess.Server.Infrastructure;
using KiriChess.Server.Moderation;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Xunit;

namespace KiriChess.Server.Tests;

/// <summary>
/// Phase 9f.5: API tokens, the /api/v1 surface, scheme isolation, and bot-pool matchmaking.
/// Requires Docker; skips cleanly when no daemon is reachable.
/// </summary>
public sealed class PublicApiTests : IAsyncLifetime
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

        _factory = BuildFactory(_pg.GetConnectionString());
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

    private static WebApplicationFactory<Program> BuildFactory(
        string connectionString, params (string Key, string? Value)[] extra)
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                var settings = new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Postgres"] = connectionString,
                    ["Jwt:Key"] = "public-api-test-signing-key-at-least-32-bytes",
                    ["Jwt:Issuer"] = "kirichess",
                    ["Jwt:Audience"] = "kirichess",
                    ["Jwt:ExpiryMinutes"] = "60",
                    ["Auth:BreachedPasswordCheckEnabled"] = "false",
                    // S-4-1: re-check the API token on every hub invocation rather than once every
                    // thirty seconds. The interval is a cost trade, not the mechanism, and pinning it
                    // to zero is what lets a test assert the mechanism without sleeping.
                    ["ApiTokens:HubRevalidationSeconds"] = "0"
                };
                foreach (var (key, value) in extra)
                    settings[key] = value;
                cfg.AddInMemoryCollection(settings);
            });
        });
    }

    [SkippableFact]
    public async Task Issue_lists_without_the_secret_and_create_returns_it_once()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var user = await RegisterAsync("pat-issue@example.com", "Pat Issue");
        AuthorizeJwt(user);

        var created = await CreateTokenAsync("engine", ["play"]);
        Assert.True(ApiTokenFormat.IsWellFormed(created.Token));
        Assert.Contains("read", created.Scopes);
        Assert.Contains("play", created.Scopes);

        var list = await _client!.GetFromJsonAsync<ApiTokenListDto[]>("/api/account/api-tokens", JsonOptions);
        Assert.NotNull(list);
        var row = Assert.Single(list!);
        Assert.Equal(created.Id, row.Id);
        Assert.Null(row.Token);
        Assert.False(string.IsNullOrEmpty(row.TokenPrefix));
    }

    /// <summary>
    /// S-1-4. <c>ExpiresAt</c> existed and was never written, so every PAT was immortal until a human
    /// revoked it. Omitted <c>ExpiresInDays</c> is 90 days; a chosen life is honoured; outside 1–365
    /// is a 400 and mints nothing.
    /// </summary>
    [SkippableFact]
    public async Task Mint_sets_a_finite_expiry_and_rejects_a_life_outside_one_to_365_days()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var user = await RegisterAsync("pat-expiry@example.com", "Pat Expiry");
        AuthorizeJwt(user);

        var created = await CreateTokenAsync("engine", ["read"]);
        Assert.NotNull(created.ExpiresAt);
        var life = created.ExpiresAt!.Value - created.CreatedAt;
        Assert.InRange(life.TotalDays, 89.9, 90.1);

        var week = await PostTokenAsync("week", ["read"], expiresInDays: 7);
        week.EnsureSuccessStatusCode();
        var weekDto = await week.Content.ReadFromJsonAsync<CreatedApiTokenDto>(JsonOptions);
        Assert.NotNull(weekDto);
        Assert.InRange((weekDto!.ExpiresAt!.Value - weekDto.CreatedAt).TotalDays, 6.9, 7.1);

        var zero = await PostTokenAsync("zero", ["read"], expiresInDays: 0);
        Assert.Equal(HttpStatusCode.BadRequest, zero.StatusCode);
        var forever = await PostTokenAsync("forever", ["read"], expiresInDays: 366);
        Assert.Equal(HttpStatusCode.BadRequest, forever.StatusCode);

        var list = await _client!.GetFromJsonAsync<ApiTokenListDto[]>("/api/account/api-tokens", JsonOptions);
        Assert.Equal(2, list!.Length);
    }

    /// <summary>
    /// S-1-4. The read path already refused a token whose <c>ExpiresAt</c> had passed. This pins that
    /// a minted token, once that instant is moved into the past, no longer authenticates.
    /// </summary>
    [SkippableFact]
    public async Task Expired_token_is_rejected()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var user = await RegisterAsync("pat-expired@example.com", "Pat Expired");
        AuthorizeJwt(user);
        var created = await CreateTokenAsync("engine", ["read"]);

        using (var scope = _factory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
            var row = await db.ApiTokens.SingleAsync(t => t.Id == created.Id);
            row.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }

        AuthorizeToken(created.Token);
        var response = await _client!.GetAsync("/api/v1/account");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [SkippableFact]
    public async Task Mint_without_password_on_a_password_account_is_400_and_creates_no_row()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var user = await RegisterAsync("pat-no-password@example.com", "Pat No Password");
        AuthorizeJwt(user);

        var response = await _client!.PostAsJsonAsync("/api/account/api-tokens",
            new { Name = "bot", Scopes = new[] { "read" } }, JsonOptions);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var list = await _client.GetFromJsonAsync<ApiTokenListDto[]>("/api/account/api-tokens", JsonOptions);
        Assert.NotNull(list);
        Assert.Empty(list!);
    }

    [SkippableFact]
    public async Task Cap_of_ten_active_tokens_returns_409()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var user = await RegisterAsync("pat-cap@example.com", "Pat Cap");
        AuthorizeJwt(user);
        for (var i = 0; i < 10; i++)
            (await CreateTokenRawAsync($"t{i}", ["read"])).EnsureSuccessStatusCode();

        var eleventh = await CreateTokenRawAsync("overflow", ["read"]);
        Assert.Equal(HttpStatusCode.Conflict, eleventh.StatusCode);
    }

    /// <summary>
    /// S-4-5. The count and the insert used to be two round trips with nothing behind them, so N
    /// concurrent mints could each observe a count under the cap and each insert. Fires more than the
    /// cap at once and asserts the database row count rather than the individual HTTP responses - a
    /// serialization failure the execution strategy retries can still end in a 201, so the only
    /// assertion that actually pins the invariant is how many active tokens exist afterwards.
    /// </summary>
    [SkippableFact]
    public async Task Concurrent_mints_never_exceed_the_ten_token_cap()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var user = await RegisterAsync("pat-race@example.com", "Pat Race");
        AuthorizeJwt(user);

        var tasks = Enumerable.Range(0, 15)
            .Select(i => CreateTokenRawAsync($"race-{i}", ["read"]))
            .ToArray();
        await Task.WhenAll(tasks);

        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
        var now = DateTimeOffset.UtcNow;
        var active = await db.ApiTokens.CountAsync(t =>
            t.UserId == user.UserId && t.RevokedAt == null && (t.ExpiresAt == null || t.ExpiresAt > now));
        Assert.True(active <= 10, $"expected at most 10 active tokens under concurrent mints, found {active}");
    }

    /// <summary>
    /// S-4-6. Listing the caller's own open challenges changes nothing, so a read-only bot must be
    /// able to call it without the play scope - which also grants game creation, moves and
    /// resignation, defeating the point of a least-privilege read token for exactly the monitoring
    /// case this route exists for.
    /// </summary>
    [SkippableFact]
    public async Task Read_scope_can_list_its_own_challenges()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var user = await RegisterAsync("pat-chal-mine@example.com", "Pat Chal Mine");
        AuthorizeJwt(user);
        var read = await CreateTokenAsync("reader", ["read"]);
        AuthorizeToken(read.Token);

        var response = await _client!.GetAsync("/api/v1/challenges/mine");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// S-1-7 / C-6-9. This was the one /api/v1 route with no rate-limit policy at all, and /api/v1 is
    /// exempt from the global per-IP backstop, so there was nothing underneath it either. The play
    /// group now defaults every route to ApiPlayCreate; this pins that the default actually reaches a
    /// route which never named a policy of its own.
    /// </summary>
    [SkippableFact]
    public async Task Challenge_cancel_is_rate_limited_by_the_play_groups_default_policy()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        await using var factory = BuildFactory(_pg!.GetConnectionString(),
            ("RateLimiting:ApiPlayCreate:PermitsPerMinute", "2"));
        using var client = factory.CreateClient();

        var user = await RegisterWithAsync(client, "pat-delete-rl@example.com", "Pat Delete RL");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", user.Token);
        var token = await CreateTokenWithAsync(client, "bot", ["play"]);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

        HttpStatusCode last = HttpStatusCode.OK;
        for (var i = 0; i < 3; i++)
            last = (await client.DeleteAsync($"/api/v1/challenges/nonexistent-{i}")).StatusCode;
        Assert.Equal(HttpStatusCode.TooManyRequests, last);
    }

    /// <summary>
    /// S-5-4: the coarse lever for the whole surface, checked once on the parent group so every route
    /// under it - read or play, GET or POST - answers the same 503 while it is off, and resumes the
    /// moment it is flipped back on.
    /// </summary>
    [SkippableFact]
    public async Task Api_switch_off_refuses_every_v1_route_and_resumes_when_reenabled()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var user = await RegisterAsync("pat-apiswitch@example.com", "Pat Api Switch");
        AuthorizeJwt(user);
        var token = await CreateTokenAsync("bot", ["read"]);
        AuthorizeToken(token.Token);

        using (var scope = _factory!.Services.CreateScope())
        {
            var killSwitches = scope.ServiceProvider.GetRequiredService<KillSwitchService>();
            await killSwitches.SetEnabledAsync(KillSwitch.Api, enabled: false, Guid.NewGuid());
        }

        var refused = await _client!.GetAsync("/api/v1/account");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);

        using (var scope = _factory!.Services.CreateScope())
        {
            var killSwitches = scope.ServiceProvider.GetRequiredService<KillSwitchService>();
            await killSwitches.SetEnabledAsync(KillSwitch.Api, enabled: true, Guid.NewGuid());
        }

        var restored = await _client.GetAsync("/api/v1/account");
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
    }

    [SkippableFact]
    public async Task Revoke_is_no_oracle_204_and_stops_the_token()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var user = await RegisterAsync("pat-revoke@example.com", "Pat Revoke");
        AuthorizeJwt(user);
        var created = await CreateTokenAsync("bot", ["read"]);

        AuthorizeJwt(user);
        var missing = await _client!.DeleteAsync($"/api/account/api-tokens/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NoContent, missing.StatusCode);

        var gone = await _client.DeleteAsync($"/api/account/api-tokens/{created.Id}");
        Assert.Equal(HttpStatusCode.NoContent, gone.StatusCode);

        AuthorizeToken(created.Token);
        var v1 = await _client.GetAsync("/api/v1/account");
        Assert.Equal(HttpStatusCode.Unauthorized, v1.StatusCode);
    }

    [SkippableFact]
    public async Task Api_token_authenticates_v1_jwt_does_not_and_token_cannot_hit_account_delete()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var user = await RegisterAsync("pat-iso@example.com", "Pat Iso");
        AuthorizeJwt(user);
        var created = await CreateTokenAsync("bot", ["read"]);

        AuthorizeJwt(user);
        var jwtOnV1 = await _client!.GetAsync("/api/v1/account");
        Assert.Equal(HttpStatusCode.Unauthorized, jwtOnV1.StatusCode);

        AuthorizeToken(created.Token);
        var ok = await _client.GetAsync("/api/v1/account");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        var delete = await _client.PostAsJsonAsync("/api/account/delete",
            new { Acknowledgement = "DELETE MY ACCOUNT" }, JsonOptions);
        Assert.Equal(HttpStatusCode.Unauthorized, delete.StatusCode);

        var moderation = await _client.GetAsync("/api/moderation/kill-switches/");
        Assert.Equal(HttpStatusCode.Unauthorized, moderation.StatusCode);
    }

    [SkippableFact]
    public async Task Admin_users_token_carries_no_role_and_cannot_reach_moderation()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var admin = await RegisterAndPromoteAsync("pat-admin@example.com", "Pat Admin");
        AuthorizeJwt(admin);
        var created = await CreateTokenAsync("admin-bot", ["play"]);

        AuthorizeToken(created.Token);
        var account = await _client!.GetFromJsonAsync<PublicAccountDto>("/api/v1/account", JsonOptions);
        Assert.NotNull(account);
        Assert.Equal(admin.UserId, account!.Id);

        var moderation = await _client.GetAsync("/api/moderation/kill-switches/");
        Assert.Equal(HttpStatusCode.Unauthorized, moderation.StatusCode);
    }

    [SkippableFact]
    public async Task Read_scope_cannot_post_moves_play_can_create_an_unrated_game()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var user = await RegisterAsync("pat-scopes@example.com", "Pat Scopes");
        AuthorizeJwt(user);
        var read = await CreateTokenAsync("reader", ["read"]);
        var play = await CreateTokenAsync("player", ["play"]);

        AuthorizeToken(read.Token);
        var refused = await _client!.PostAsJsonAsync("/api/v1/games", new { Preset = "bounty1" }, JsonOptions);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

        AuthorizeToken(play.Token);
        var rated = await _client.PostAsJsonAsync("/api/v1/games",
            new { Preset = "bounty1", Rated = true }, JsonOptions);
        Assert.Equal(HttpStatusCode.BadRequest, rated.StatusCode);

        var created = await _client.PostAsJsonAsync("/api/v1/games",
            new { Preset = "bounty1" }, JsonOptions);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var state = await created.Content.ReadFromJsonAsync<GameStateResponse>(JsonOptions);
        Assert.NotNull(state);
        Assert.False(state!.Rated);

        var move = await _client.PostAsJsonAsync($"/api/v1/games/{state.GameId}/moves",
            new { Uci = "e2e4" }, JsonOptions);
        Assert.Equal(HttpStatusCode.OK, move.StatusCode);
    }

    [SkippableFact]
    public async Task Api_challenge_is_always_unrated()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var user = await RegisterAsync("pat-chal@example.com", "Pat Chal");
        AuthorizeJwt(user);
        var play = await CreateTokenAsync("player", ["play"]);
        AuthorizeToken(play.Token);

        var response = await _client!.PostAsJsonAsync("/api/v1/challenges",
            new { Pool = "bounty1", Colour = "White" }, JsonOptions);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<ChallengeDto>(JsonOptions);
        Assert.NotNull(dto);
        Assert.Equal("bounty1", dto!.Pool);
        Assert.True(dto.ChallengerIsBot);
    }

    /// <summary>
    /// L-11-3. A token that sends or accepts a challenge is a bot for that game, and a person
    /// using the site is not, even when the two share a match.
    /// </summary>
    [SkippableFact]
    public async Task Api_challenge_labels_the_token_seat_and_not_the_site_seat()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var bot = await RegisterAsync("pat-bot-label@example.com", "Pat Bot");
        AuthorizeJwt(bot);
        var play = await CreateTokenAsync("player", ["play"]);

        AuthorizeToken(play.Token);
        var created = await _client!.PostAsJsonAsync("/api/v1/challenges",
            new { Pool = "bounty1", Colour = "White" }, JsonOptions);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var botChallenge = await created.Content.ReadFromJsonAsync<ChallengeDto>(JsonOptions);
        Assert.NotNull(botChallenge);

        _client.DefaultRequestHeaders.Authorization = null;
        var anon = await _client.GetFromJsonAsync<ChallengeDto>(
            $"/api/challenges/{botChallenge!.Token}", JsonOptions);
        Assert.NotNull(anon);
        Assert.True(anon!.ChallengerIsBot);

        var human = await RegisterAsync("pat-human-label@example.com", "Pat Human");
        AuthorizeJwt(human);
        var accept = await _client.PostAsync($"/api/challenges/{botChallenge.Token}/accept", content: null);
        Assert.Equal(HttpStatusCode.OK, accept.StatusCode);
        var accepted = await accept.Content.ReadFromJsonAsync<ChallengeAcceptedDto>(JsonOptions);
        Assert.NotNull(accepted);

        var botGame = await _client.GetFromJsonAsync<GameStateResponse>(
            $"/api/games/{accepted!.GameId}", JsonOptions);
        Assert.NotNull(botGame);
        Assert.True(botGame!.WhiteViaApi);
        Assert.False(botGame.BlackViaApi);
        Assert.Equal(bot.UserId, botGame.WhitePlayerId);
        Assert.Equal(human.UserId, botGame.BlackPlayerId);

        var resign = await _client.PostAsync($"/api/games/{accepted.GameId}/resign", content: null);
        Assert.Equal(HttpStatusCode.OK, resign.StatusCode);

        AuthorizeJwt(human);
        var humanCreated = await _client.PostAsJsonAsync("/api/challenges",
            new { Pool = "bounty1", Colour = "Black" }, JsonOptions);
        Assert.Equal(HttpStatusCode.Created, humanCreated.StatusCode);
        var humanChallenge = await humanCreated.Content.ReadFromJsonAsync<ChallengeDto>(JsonOptions);
        Assert.NotNull(humanChallenge);
        Assert.False(humanChallenge!.ChallengerIsBot);

        AuthorizeToken(play.Token);
        var botAccept = await _client.PostAsync(
            $"/api/v1/challenges/{humanChallenge.Token}/accept", content: null);
        Assert.Equal(HttpStatusCode.OK, botAccept.StatusCode);
        var botAccepted = await botAccept.Content.ReadFromJsonAsync<ChallengeAcceptedDto>(JsonOptions);
        Assert.NotNull(botAccepted);

        AuthorizeJwt(human);
        var humanGame = await _client.GetFromJsonAsync<GameStateResponse>(
            $"/api/games/{botAccepted!.GameId}", JsonOptions);
        Assert.NotNull(humanGame);
        Assert.True(humanGame!.WhiteViaApi);
        Assert.False(humanGame.BlackViaApi);
        Assert.Equal(bot.UserId, humanGame.WhitePlayerId);
        Assert.Equal(human.UserId, humanGame.BlackPlayerId);
    }

    [SkippableFact]
    public async Task Logout_all_does_not_revoke_api_tokens()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var user = await RegisterAsync("pat-logout@example.com", "Pat Logout");
        AuthorizeJwt(user);
        var created = await CreateTokenAsync("bot", ["read"]);

        AuthorizeJwt(user);
        var logout = await _client!.PostAsync("/api/auth/logout-all", null);
        logout.EnsureSuccessStatusCode();

        AuthorizeToken(created.Token);
        var v1 = await _client.GetAsync("/api/v1/account");
        Assert.Equal(HttpStatusCode.OK, v1.StatusCode);
    }

    [SkippableFact]
    public async Task Account_ban_and_erasure_revoke_api_tokens()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var admin = await RegisterAndPromoteAsync("pat-ban-admin@example.com", "Ban Admin");
        var target = await RegisterAsync("pat-ban-target@example.com", "Ban Target");
        AuthorizeJwt(target);
        var bannedToken = await CreateTokenAsync("bot", ["read"]);

        AuthorizeJwt(admin);
        var ban = await _client!.PostAsJsonAsync("/api/moderation/sanctions", new
        {
            UserId = target.UserId,
            Type = "AccountBan",
            Reason = "Abuse.",
            ModeratorNote = "test",
            ExpiresAt = (DateTimeOffset?)null,
            ReportId = (Guid?)null
        }, JsonOptions);
        Assert.Equal(HttpStatusCode.Created, ban.StatusCode);

        AuthorizeToken(bannedToken.Token);
        var afterBan = await _client.GetAsync("/api/v1/account");
        Assert.Equal(HttpStatusCode.Unauthorized, afterBan.StatusCode);

        var erased = await RegisterAsync("pat-erase@example.com", "Erase Me");
        AuthorizeJwt(erased);
        var eraseToken = await CreateTokenAsync("bot", ["read"]);
        var delete = await _client.PostAsJsonAsync("/api/account/delete",
            new { Acknowledgement = "DELETE MY ACCOUNT", Password }, JsonOptions);
        delete.EnsureSuccessStatusCode();

        AuthorizeToken(eraseToken.Token);
        var afterErase = await _client.GetAsync("/api/v1/account");
        Assert.Equal(HttpStatusCode.Unauthorized, afterErase.StatusCode);
    }

    /// <summary>
    /// S-4-1, and the half the test above could not see. The assertion above proves a ban closes the
    /// HTTP surface, because every /api/v1 request re-authenticates through
    /// <c>ApiTokenService.FindValidAsync</c>. A live hub connection does not: SignalR authorizes a
    /// method against the principal captured at connect time, so before
    /// <c>ApiTokenHubRevalidationFilter</c> the banned account kept playing - on the hub, which is the
    /// <em>rated</em> bot path (/api/v1 play is always unrated), for as long as it held the socket.
    /// </summary>
    [SkippableFact]
    public async Task A_ban_stops_an_already_open_hub_connection_from_playing()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var admin = await RegisterAndPromoteAsync("pat-hubban-admin@example.com", "Hub Ban Admin");
        var target = await RegisterAsync("pat-hubban@example.com", "Hub Ban Target");
        AuthorizeJwt(target);
        var bot = await CreateTokenAsync("hub-bot", ["play"]);

        AuthorizeToken(bot.Token);
        var created = await _client!.PostAsJsonAsync("/api/v1/games", new { Preset = "bounty1" }, JsonOptions);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var state = await created.Content.ReadFromJsonAsync<GameStateResponse>(JsonOptions);
        Assert.NotNull(state);
        var gameId = state!.GameId.ToString();

        await using var conn = BuildGameHub(bot.Token);
        await conn.StartAsync();
        await conn.InvokeAsync<JsonElement>("JoinGame", gameId);
        // A real move first, so the assertion at the end is about the ban and not about a socket that
        // was never able to play in the first place.
        await conn.InvokeAsync("SubmitMove", gameId, "e2e4");

        AuthorizeJwt(admin);
        var ban = await _client.PostAsJsonAsync("/api/moderation/sanctions", new
        {
            UserId = target.UserId,
            Type = "AccountBan",
            Reason = "Abuse.",
            ModeratorNote = "test",
            ExpiresAt = (DateTimeOffset?)null,
            ReportId = (Guid?)null
        }, JsonOptions);
        Assert.Equal(HttpStatusCode.Created, ban.StatusCode);

        // e7e5 is legal for the side to move (the game has one human on both seats), so the only
        // reason this can fail is the revoked key.
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => conn.InvokeAsync("SubmitMove", gameId, "e7e5"));
        Assert.Contains("no longer valid", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task Api_token_JoinQueue_pairs_in_the_bot_pool_and_jwt_cannot_join_bot_keys()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var a = await RegisterAsync("pat-q-a@example.com", "Queue A");
        var b = await RegisterAsync("pat-q-b@example.com", "Queue B");
        AuthorizeJwt(a);
        var tokenA = await CreateTokenAsync("bot-a", ["play"]);
        AuthorizeJwt(b);
        var tokenB = await CreateTokenAsync("bot-b", ["play"]);

        await using var connA = BuildGameHub(tokenA.Token);
        await using var connB = BuildGameHub(tokenB.Token);
        var found = new TaskCompletionSource<MatchFoundDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        connA.On<MatchFoundDto>("MatchFound", dto => found.TrySetResult(dto));
        connB.On<MatchFoundDto>("MatchFound", dto => found.TrySetResult(dto));

        await connA.StartAsync();
        await connB.StartAsync();
        await connA.InvokeAsync("JoinQueue", "bounty1");
        await connB.InvokeAsync("JoinQueue", "bounty1");

        var match = await found.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("bot:bounty1", match.Pool);

        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
        var game = await db.Games.SingleAsync(g => g.Id == match.GameId);
        Assert.Equal("bot:bounty1", game.RatingPool);
        Assert.True(game.IsRated);

        var human = await RegisterAsync("pat-q-human@example.com", "Human Q");
        await using var jwtConn = BuildGameHub(human.Token);
        await jwtConn.StartAsync();
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => jwtConn.InvokeAsync("JoinQueue", "bot:bounty1"));
        Assert.Contains("not a matchmaking pool", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task Two_tokens_do_not_share_an_api_read_bucket()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        await using var factory = BuildFactory(_pg!.GetConnectionString(),
            ("RateLimiting:ApiRead:PermitsPerMinute", "2"));
        using var client = factory.CreateClient();

        var user = await RegisterWithAsync(client, "pat-rl@example.com", "Pat RL");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", user.Token);
        var tokenA = await CreateTokenWithAsync(client, "a", ["read"]);
        var tokenB = await CreateTokenWithAsync(client, "b", ["read"]);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokenA.Token);
        HttpStatusCode lastA = HttpStatusCode.OK;
        for (var i = 0; i < 5; i++)
            lastA = (await client.GetAsync("/api/v1/account")).StatusCode;
        Assert.Equal(HttpStatusCode.TooManyRequests, lastA);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokenB.Token);
        var b = await client.GetAsync("/api/v1/account");
        Assert.Equal(HttpStatusCode.OK, b.StatusCode);
    }

    /// <summary>
    /// S-4-2. The test above stays exactly as it was: per-token partitioning is the right primitive
    /// and one key's exhausted bucket must not touch another's. What was missing is a ceiling above
    /// those partitions, because an account may hold ten active tokens
    /// (<c>ApiTokenDefaults.MaxActivePerUser</c>) and /api/v1 is exempt from the per-IP global
    /// backstop, so its real budget was ten times every number /developers publishes.
    ///
    /// The account limit is set to 4 here and <c>ApiRead</c> is left at its 300/minute default, so a
    /// rejection can only be the ceiling: neither key comes anywhere near its own bucket.
    /// </summary>
    [SkippableFact]
    public async Task Two_tokens_share_one_per_account_ceiling()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        await using var factory = BuildFactory(_pg!.GetConnectionString(),
            ("RateLimiting:ApiAccount:PermitsPerMinute", "4"));
        using var client = factory.CreateClient();

        var user = await RegisterWithAsync(client, "pat-ceiling@example.com", "Pat Ceiling");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", user.Token);
        var tokenA = await CreateTokenWithAsync(client, "a", ["read"]);
        var tokenB = await CreateTokenWithAsync(client, "b", ["read"]);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokenA.Token);
        for (var i = 0; i < 4; i++)
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/account")).StatusCode);

        // The second key has spent none of its own bucket and is still refused, which is only
        // possible if the two share something above the token partition.
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokenB.Token);
        var spillover = await client.GetAsync("/api/v1/account");
        Assert.Equal(HttpStatusCode.TooManyRequests, spillover.StatusCode);
    }

    private HubConnection BuildGameHub(string accessToken) =>
        new HubConnectionBuilder()
            .WithUrl(new Uri(_factory!.Server.BaseAddress, "hubs/game"), options =>
            {
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                options.AccessTokenProvider = () => Task.FromResult<string?>(accessToken);
            })
            .Build();

    private void AuthorizeJwt(AuthResult who) =>
        _client!.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", who.Token);

    private void AuthorizeToken(string raw) =>
        _client!.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", raw);

    private async Task<CreatedApiTokenDto> CreateTokenAsync(string name, string[] scopes)
    {
        var response = await CreateTokenRawAsync(name, scopes);
        response.EnsureSuccessStatusCode();
        var dto = await response.Content.ReadFromJsonAsync<CreatedApiTokenDto>(JsonOptions);
        Assert.NotNull(dto);
        return dto!;
    }

    private Task<HttpResponseMessage> CreateTokenRawAsync(string name, string[] scopes) =>
        PostTokenAsync(name, scopes, expiresInDays: null);

    private Task<HttpResponseMessage> PostTokenAsync(string name, string[] scopes, int? expiresInDays) =>
        _client!.PostAsJsonAsync("/api/account/api-tokens",
            new { Name = name, Scopes = scopes, Password, ExpiresInDays = expiresInDays }, JsonOptions);

    private async Task<AuthResult> RegisterAsync(string email, string displayName) =>
        await RegisterWithAsync(_client!, email, displayName);

    private static async Task<AuthResult> RegisterWithAsync(HttpClient client, string email, string displayName)
    {
        var registerResponse = await client.PostAsJsonAsync("/api/auth/register", new
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

    private static async Task<CreatedApiTokenDto> CreateTokenWithAsync(HttpClient client, string name, string[] scopes)
    {
        var response = await client.PostAsJsonAsync("/api/account/api-tokens",
            new { Name = name, Scopes = scopes, Password }, JsonOptions);
        response.EnsureSuccessStatusCode();
        var dto = await response.Content.ReadFromJsonAsync<CreatedApiTokenDto>(JsonOptions);
        Assert.NotNull(dto);
        return dto!;
    }

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

        var sharedKey = await TotpTestHelper.EnableTwoFactorAsync(_factory!.Services, registered.UserId);

        var loginResponse = await TotpTestHelper.LoginWithTwoFactorAsync(
            _client!, email, Password, sharedKey, JsonOptions);
        loginResponse.EnsureSuccessStatusCode();
        var relogged = await loginResponse.Content.ReadFromJsonAsync<AuthResult>(JsonOptions);
        Assert.NotNull(relogged);
        return relogged!;
    }

    private sealed record AuthResult(
        Guid UserId, string Email, string DisplayName, string Token, string RefreshToken, DateTimeOffset ExpiresAt);

    private sealed record CreatedApiTokenDto(
        Guid Id, string Name, string TokenPrefix, string[] Scopes,
        DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt, DateTimeOffset? LastUsedAt, string Token);

    private sealed record ApiTokenListDto(
        Guid Id, string Name, string TokenPrefix, string[] Scopes,
        DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt, DateTimeOffset? LastUsedAt, string? Token);

    private sealed record PublicAccountDto(Guid Id, string DisplayName, string[] Scopes);
}
