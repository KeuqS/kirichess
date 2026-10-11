using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using KiriChess.Engine.Config;
using KiriChess.Engine.Core;
using KiriChess.Server.Data;
using KiriChess.Server.Data.Entities;
using KiriChess.Server.Games;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Xunit;

namespace KiriChess.Server.Tests;

/// <summary>
/// Phase 9d.8 direct challenge / play-a-friend link, end to end: create, accept, the single-use race
/// guard, cancellation, the open-challenge cap, colour assignment, always-unrated games, a rated-play
/// ban still permitting a friend game, the anonymous read, and the POST /api/games OpponentId hole
/// this phase closes (README scope decision 5). Requires Docker; skips cleanly when no Docker daemon
/// is reachable. Fixture scaffolding is copied verbatim from <see cref="ReportEndpointTests"/>.
/// </summary>
public sealed class ChallengeTests : IAsyncLifetime
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

        var connectionString = _pg.GetConnectionString();

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Postgres"] = connectionString,
                    ["Jwt:Key"] = "challenge-test-signing-key-at-least-32-bytes-long",
                    ["Jwt:Issuer"] = "kirichess",
                    ["Jwt:Audience"] = "kirichess",
                    ["Jwt:ExpiryMinutes"] = "60"
                });
            });
        });

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
            await db.Database.MigrateAsync();
        }

        _client = _factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_factory is not null) await _factory.DisposeAsync();
        if (_pg is not null) await _pg.DisposeAsync();
    }

    [SkippableFact]
    public async Task Create_then_accept_puts_both_players_in_one_game()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var challenger = await RegisterAsync("challenge-create-accept-challenger@example.com", "Challenger");
        var friend = await RegisterAsync("challenge-create-accept-friend@example.com", "Friend");

        AuthorizeAs(challenger);
        var challenge = await CreateChallengeAsync("steal1", colour: "Random");

        AuthorizeAs(friend);
        var acceptResponse = await _client!.PostAsync($"/api/challenges/{challenge.Token}/accept", content: null);
        Assert.Equal(HttpStatusCode.OK, acceptResponse.StatusCode);
        var accepted = await acceptResponse.Content.ReadFromJsonAsync<ChallengeAcceptedDto>(JsonOptions);
        Assert.NotNull(accepted);
        Assert.NotEqual(Guid.Empty, accepted!.GameId);
        Assert.Equal("steal1", accepted.Pool);

        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
        var game = await db.Games.SingleAsync(g => g.Id == accepted.GameId);
        Assert.False(game.IsRated);
        Assert.False(game.WhiteViaApi);
        Assert.False(game.BlackViaApi);
        var players = new[] { game.WhitePlayerId, game.BlackPlayerId };
        Assert.Contains(challenger.UserId, players);
        Assert.Contains(friend.UserId, players);

        var row = await db.Challenges.SingleAsync(c => c.Token == challenge.Token);
        Assert.False(row.ChallengerViaApi);
        Assert.Equal(accepted.GameId, row.GameId);
        Assert.Equal(friend.UserId, row.AcceptedById);
        Assert.NotNull(row.AcceptedAt);
    }

    [SkippableFact]
    public async Task Accepting_twice_returns_409_and_creates_only_one_game()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var challenger = await RegisterAsync("challenge-double-accept-challenger@example.com", "DoubleChallenger");
        var friend = await RegisterAsync("challenge-double-accept-friend@example.com", "DoubleFriend");

        AuthorizeAs(challenger);
        var challenge = await CreateChallengeAsync("steal1", colour: "White");

        AuthorizeAs(friend);
        var first = await _client!.PostAsync($"/api/challenges/{challenge.Token}/accept", content: null);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var second = await _client.PostAsync($"/api/challenges/{challenge.Token}/accept", content: null);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
        var gameCount = await db.Games.CountAsync(
            g => g.WhitePlayerId == challenger.UserId || g.BlackPlayerId == challenger.UserId);
        Assert.Equal(1, gameCount);
    }

    [SkippableFact]
    public async Task Accepting_a_challenge_whose_pool_has_since_been_retired_returns_409_not_500()
    {
        // C-6-8: the pool was validated as official at create time (TryPrepareCreate) and never
        // re-validated at accept time, so a pool key removed from config/kirichess.rules.json while a
        // challenge naming it is still inside its TTL used to index KiriChessConfig.Presets[pool] and
        // throw KeyNotFoundException - an unhandled 500 for the friend clicking the link, rather than
        // the same 409 an already-resolved challenge gets.
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var challenger = await RegisterAsync("challenge-retired-pool-challenger@example.com", "RetiredChallenger");
        var friend = await RegisterAsync("challenge-retired-pool-friend@example.com", "RetiredFriend");

        var token = await InsertChallengeAsync(
            challenger.UserId, "no-longer-a-pool", ChallengeColour.Random, DateTimeOffset.UtcNow.AddMinutes(30));

        AuthorizeAs(friend);
        var response = await _client!.PostAsync($"/api/challenges/{token}/accept", content: null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorDto>(JsonOptions);
        Assert.Equal("That challenge is no longer open.", error!.Error);

        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
        var gameCount = await db.Games.CountAsync(
            g => g.WhitePlayerId == friend.UserId || g.BlackPlayerId == friend.UserId);
        Assert.Equal(0, gameCount); // never claimed, so the friend's one live challenge link is not consumed
    }

    [SkippableFact]
    public async Task Challenger_cannot_accept_their_own_challenge()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var challenger = await RegisterAsync("challenge-self-accept@example.com", "SelfAccepter");

        AuthorizeAs(challenger);
        var challenge = await CreateChallengeAsync("steal1", colour: "Random");

        var response = await _client!.PostAsync($"/api/challenges/{challenge.Token}/accept", content: null);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [SkippableFact]
    public async Task Expired_challenge_cannot_be_accepted()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var challenger = await RegisterAsync("challenge-expired-challenger@example.com", "ExpiredChallenger");
        var friend = await RegisterAsync("challenge-expired-friend@example.com", "ExpiredFriend");

        var token = await InsertChallengeAsync(
            challenger.UserId, "steal1", colour: ChallengeColour.Random,
            expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1));

        AuthorizeAs(friend);
        var response = await _client!.PostAsync($"/api/challenges/{token}/accept", content: null);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [SkippableFact]
    public async Task Cancelled_challenge_cannot_be_accepted()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var challenger = await RegisterAsync("challenge-cancelled-challenger@example.com", "CancelledChallenger");
        var friend = await RegisterAsync("challenge-cancelled-friend@example.com", "CancelledFriend");

        AuthorizeAs(challenger);
        var challenge = await CreateChallengeAsync("steal1", colour: "Random");

        var cancelResponse = await _client!.DeleteAsync($"/api/challenges/{challenge.Token}");
        Assert.Equal(HttpStatusCode.NoContent, cancelResponse.StatusCode);

        AuthorizeAs(friend);
        var acceptResponse = await _client.PostAsync($"/api/challenges/{challenge.Token}/accept", content: null);
        Assert.Equal(HttpStatusCode.Conflict, acceptResponse.StatusCode);
    }

    [SkippableFact]
    public async Task Fourth_open_challenge_is_refused()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var challenger = await RegisterAsync("challenge-cap-challenger@example.com", "CapChallenger");
        AuthorizeAs(challenger);

        await CreateChallengeAsync("steal1", colour: "Random");
        await CreateChallengeAsync("bounty1", colour: "Random");
        await CreateChallengeAsync("steal2", colour: "Random");

        var response = await _client!.PostAsJsonAsync("/api/challenges", new
        {
            Pool = "burn1",
            Colour = "Random"
        }, JsonOptions);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ErrorDto>(JsonOptions);
        Assert.NotNull(body);
        Assert.Contains("3 open challenges", body!.Error);
    }

    [SkippableFact]
    public async Task Colour_White_puts_the_challenger_on_white()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var challenger = await RegisterAsync("challenge-colour-white-challenger@example.com", "WhiteChallenger");
        var friend = await RegisterAsync("challenge-colour-white-friend@example.com", "WhiteFriend");

        AuthorizeAs(challenger);
        var challenge = await CreateChallengeAsync("steal1", colour: "White");

        AuthorizeAs(friend);
        var acceptResponse = await _client!.PostAsync($"/api/challenges/{challenge.Token}/accept", content: null);
        var accepted = await acceptResponse.Content.ReadFromJsonAsync<ChallengeAcceptedDto>(JsonOptions);
        Assert.NotNull(accepted);
        Assert.Equal("Black", accepted!.YourColour);

        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
        var game = await db.Games.SingleAsync(g => g.Id == accepted.GameId);
        Assert.Equal(challenger.UserId, game.WhitePlayerId);
        Assert.Equal(friend.UserId, game.BlackPlayerId);
    }

    [SkippableFact]
    public async Task Colour_Black_puts_the_challenger_on_black()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var challenger = await RegisterAsync("challenge-colour-black-challenger@example.com", "BlackChallenger");
        var friend = await RegisterAsync("challenge-colour-black-friend@example.com", "BlackFriend");

        AuthorizeAs(challenger);
        var challenge = await CreateChallengeAsync("steal1", colour: "Black");

        AuthorizeAs(friend);
        var acceptResponse = await _client!.PostAsync($"/api/challenges/{challenge.Token}/accept", content: null);
        var accepted = await acceptResponse.Content.ReadFromJsonAsync<ChallengeAcceptedDto>(JsonOptions);
        Assert.NotNull(accepted);
        Assert.Equal("White", accepted!.YourColour);

        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
        var game = await db.Games.SingleAsync(g => g.Id == accepted.GameId);
        Assert.Equal(friend.UserId, game.WhitePlayerId);
        Assert.Equal(challenger.UserId, game.BlackPlayerId);
    }

    [SkippableFact]
    public async Task Rated_play_ban_does_not_block_a_friend_challenge()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var challenger = await RegisterAsync("challenge-rated-ban-challenger@example.com", "RatedBanChallenger");
        var bannedFriend = await RegisterAsync("challenge-rated-ban-friend@example.com", "RatedBanFriend");
        await AddRatedPlayBanAsync(bannedFriend.UserId, challenger.UserId);

        AuthorizeAs(challenger);
        var challenge = await CreateChallengeAsync("steal1", colour: "Random");

        AuthorizeAs(bannedFriend);
        var accept = await _client!.PostAsync($"/api/challenges/{challenge.Token}/accept", content: null);
        Assert.Equal(HttpStatusCode.OK, accept.StatusCode);
    }

    [SkippableFact]
    public async Task Anonymous_caller_can_read_a_challenge_and_sees_only_the_display_name()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var challenger = await RegisterAsync("challenge-anon-read@example.com", "AnonRead");
        AuthorizeAs(challenger);
        var challenge = await CreateChallengeAsync("steal1", colour: "Random");

        using var anonymousClient = _factory!.CreateClient();
        var response = await anonymousClient.GetAsync($"/api/challenges/{challenge.Token}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.Equal("AnonRead", root.GetProperty("challengerName").GetString());
        Assert.Equal("steal1", root.GetProperty("pool").GetString());
        Assert.Equal("Open", root.GetProperty("status").GetString());

        // The whole point of the test: no id, no email, nothing that identifies the account beyond
        // the display name it chose to show.
        Assert.False(root.TryGetProperty("challengerId", out _));
        Assert.False(root.TryGetProperty("email", out _));
        Assert.False(root.TryGetProperty("challenger", out _));
        Assert.False(root.TryGetProperty("rated", out _));

        var preset = KiriChessConfig.Presets["steal1"];
        var economy = root.GetProperty("economy");
        Assert.Equal(preset.StartTimeSeconds, economy.GetProperty("startTimeSeconds").GetInt32());
        Assert.Equal(preset.BaseIncrement, economy.GetProperty("baseIncrement").GetInt32());
        Assert.Equal(preset.FlagFallLoses, economy.GetProperty("flagFallLoses").GetBoolean());
        Assert.Equal(preset.ThreefoldRepetitionIsDraw, economy.GetProperty("threefoldRepetitionIsDraw").GetBoolean());
    }

    [SkippableFact]
    public async Task Custom_config_create_then_accept_uses_the_minted_ruleset()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var challenger = await RegisterAsync("challenge-custom-challenger@example.com", "CustomChallenger");
        var friend = await RegisterAsync("challenge-custom-friend@example.com", "CustomFriend");

        AuthorizeAs(challenger);
        var createResponse = await _client!.PostAsJsonAsync("/api/challenges", new
        {
            Colour = "White",
            Custom = CustomFromDefaults(startTimeSeconds: 123)
        }, JsonOptions);
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var challenge = await createResponse.Content.ReadFromJsonAsync<ChallengeDto>(JsonOptions);
        Assert.NotNull(challenge);
        Assert.Equal(ChallengeService.CustomPool, challenge!.Pool);

        using var anonymousClient = _factory!.CreateClient();
        var anon = await anonymousClient.GetAsync($"/api/challenges/{challenge.Token}");
        Assert.Equal(HttpStatusCode.OK, anon.StatusCode);
        using (var doc = JsonDocument.Parse(await anon.Content.ReadAsStringAsync()))
        {
            Assert.Equal(ChallengeService.CustomPool, doc.RootElement.GetProperty("pool").GetString());
            Assert.False(doc.RootElement.TryGetProperty("customConfig", out _));
            Assert.False(doc.RootElement.TryGetProperty("custom", out _));
            var minted = new KiriChessConfig { StartTimeSeconds = 123 };
            var economy = doc.RootElement.GetProperty("economy");
            Assert.Equal(123, economy.GetProperty("startTimeSeconds").GetInt32());
            Assert.Equal(minted.BaseIncrement, economy.GetProperty("baseIncrement").GetInt32());
            Assert.Equal(minted.RepeatedPositionNullifiesTime, economy.GetProperty("repeatedPositionNullifiesTime").GetBoolean());
            Assert.Equal(minted.ThreefoldRepetitionIsDraw, economy.GetProperty("threefoldRepetitionIsDraw").GetBoolean());
            Assert.Equal(minted.FlagFallLoses, economy.GetProperty("flagFallLoses").GetBoolean());
            Assert.Equal(minted.FlagVsInsufficientMaterialIsDraw, economy.GetProperty("flagVsInsufficientMaterialIsDraw").GetBoolean());
        }

        AuthorizeAs(friend);
        var acceptResponse = await _client.PostAsync($"/api/challenges/{challenge.Token}/accept", content: null);
        Assert.Equal(HttpStatusCode.OK, acceptResponse.StatusCode);
        var accepted = await acceptResponse.Content.ReadFromJsonAsync<ChallengeAcceptedDto>(JsonOptions);
        Assert.NotNull(accepted);
        Assert.Equal(ChallengeService.CustomPool, accepted!.Pool);

        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
        var game = await db.Games.SingleAsync(g => g.Id == accepted.GameId);
        Assert.False(game.IsRated);
        Assert.Null(game.RatingPool);
        Assert.Equal(123, game.ConfigSnapshot.StartTimeSeconds);
        Assert.Equal(challenger.UserId, game.WhitePlayerId);
        Assert.Equal(friend.UserId, game.BlackPlayerId);
    }

    [SkippableFact]
    public async Task Official_pool_plus_custom_config_is_refused()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var challenger = await RegisterAsync("challenge-mixed-challenger@example.com", "MixedChallenger");
        AuthorizeAs(challenger);

        var response = await _client!.PostAsJsonAsync("/api/challenges", new
        {
            Pool = "steal1",
            Colour = "Random",
            Custom = CustomFromDefaults()
        }, JsonOptions);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [SkippableFact]
    public async Task Third_party_opponent_id_is_refused()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var caller = await RegisterAsync("challenge-opponentid-caller@example.com", "OpponentIdCaller");
        var stranger = await RegisterAsync("challenge-opponentid-stranger@example.com", "OpponentIdStranger");

        AuthorizeAs(caller);
        var response = await _client!.PostAsJsonAsync("/api/games", new
        {
            OpponentId = stranger.UserId,
            Preset = "steal1",
            Rated = false
        }, JsonOptions);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static object CustomFromDefaults(int startTimeSeconds = 123)
    {
        var c = new KiriChessConfig { StartTimeSeconds = startTimeSeconds };
        return new
        {
            StartTimeSeconds = c.StartTimeSeconds,
            BaseIncrement = c.BaseIncrement,
            EconomyScale = c.EconomyScale,
            PieceValues = new
            {
                Pawn = c.PieceValues[PieceType.Pawn],
                Knight = c.PieceValues[PieceType.Knight],
                Bishop = c.PieceValues[PieceType.Bishop],
                Rook = c.PieceValues[PieceType.Rook],
                Queen = c.PieceValues[PieceType.Queen],
            },
            CaptureMult = c.CaptureMult,
            CaptureCaptorGainEnabled = c.CaptureCaptorGainEnabled,
            CaptureVictimLossEnabled = c.CaptureVictimLossEnabled,
            MoveCostMode = c.MoveCostMode.ToString(),
            MoveCostPerPiece = new
            {
                Pawn = c.MoveCostPerPiece[PieceType.Pawn],
                Knight = c.MoveCostPerPiece[PieceType.Knight],
                Bishop = c.MoveCostPerPiece[PieceType.Bishop],
                Rook = c.MoveCostPerPiece[PieceType.Rook],
                Queen = c.MoveCostPerPiece[PieceType.Queen],
                King = c.MoveCostPerPiece[PieceType.King],
            },
            CastleCostFlat = c.CastleCostFlat,
            MoveCostFlat = c.MoveCostFlat,
            MoveCostPerSquare = c.MoveCostPerSquare,
            CheckCheckerGain = c.CheckCheckerGain,
            CheckVictimLoss = c.CheckVictimLoss,
            DoubleCheckChipMultiplier = c.DoubleCheckChipMultiplier,
            PromotionCostMult = c.PromotionCostMult,
            RepeatedPositionNullifiesTime = c.RepeatedPositionNullifiesTime,
            ThreefoldRepetitionIsDraw = c.ThreefoldRepetitionIsDraw,
            FlagFallLoses = c.FlagFallLoses,
            FlagVsInsufficientMaterialIsDraw = c.FlagVsInsufficientMaterialIsDraw,
        };
    }

    private void AuthorizeAs(AuthResult who) =>
        _client!.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", who.Token);

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

    private async Task<ChallengeDto> CreateChallengeAsync(string pool, string colour)
    {
        var response = await _client!.PostAsJsonAsync("/api/challenges", new
        {
            Pool = pool,
            Colour = colour
        }, JsonOptions);
        response.EnsureSuccessStatusCode();
        var dto = await response.Content.ReadFromJsonAsync<ChallengeDto>(JsonOptions);
        Assert.NotNull(dto);
        return dto!;
    }

    /// <summary>Inserts a challenge row directly, bypassing ChallengeService.CreateAsync's TTL, for the expired/edge tests.</summary>
    private async Task<string> InsertChallengeAsync(
        Guid challengerId, string pool, ChallengeColour colour, DateTimeOffset expiresAt)
    {
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
        var challenge = new Challenge
        {
            Token = ChallengeService.NewToken(),
            ChallengerId = challengerId,
            Pool = pool,
            Colour = colour,
            CreatedAt = DateTimeOffset.UtcNow.AddHours(-2),
            ExpiresAt = expiresAt,
        };
        db.Challenges.Add(challenge);
        await db.SaveChangesAsync();
        return challenge.Token;
    }

    private async Task AddRatedPlayBanAsync(Guid userId, Guid issuedByUserId)
    {
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
        db.UserSanctions.Add(new UserSanction
        {
            UserId = userId,
            Type = SanctionType.RatedPlayBan,
            Reason = "Test sanction.",
            IssuedByUserId = issuedByUserId,
        });
        await db.SaveChangesAsync();
    }

    private sealed record AuthResult(Guid UserId, string Email, string DisplayName, string Token);

    private sealed record ErrorDto(string Error);
}
