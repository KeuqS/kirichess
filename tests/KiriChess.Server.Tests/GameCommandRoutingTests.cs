using System.Text.Json;
using KiriChess.Server.Games.Distributed;
using KiriChess.Server.Infrastructure.Redis;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KiriChess.Server.Tests;

/// <summary>
/// Phase 8.4 Task B: the forwarding RPC envelope/reply shape (pinned in Phase 8.4)
/// and <see cref="GameCommandRouter"/>'s own timeout behaviour. All pure, no Docker required.
/// </summary>
public sealed class GameCommandRoutingTests
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static IEnumerable<object[]> AllKinds() =>
        new[]
        {
            GameCommandKind.JoinGame, GameCommandKind.SubmitMove, GameCommandKind.Resign,
            GameCommandKind.OfferDraw, GameCommandKind.RespondDraw, GameCommandKind.Abandon,
            GameCommandKind.JoinAsSpectator
        }.Select(k => new object[] { k });

    [Theory]
    [MemberData(nameof(AllKinds))]
    public void Envelope_round_trips_through_json_with_a_null_arg(string kind)
    {
        var envelope = new GameCommandEnvelope(
            Guid.NewGuid().ToString("N"), "instance-a", kind, Guid.NewGuid(), Guid.NewGuid(), "conn-1", null);

        var json = JsonSerializer.Serialize(envelope, JsonOptions);
        var roundTripped = JsonSerializer.Deserialize<GameCommandEnvelope>(json, JsonOptions);

        Assert.Equal(envelope, roundTripped);
        Assert.Null(roundTripped!.Arg);
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public void Envelope_round_trips_through_json_with_a_uci_arg(string kind)
    {
        var envelope = new GameCommandEnvelope(
            Guid.NewGuid().ToString("N"), "instance-b", kind, Guid.NewGuid(), Guid.NewGuid(), "conn-2", "e2e4");

        var json = JsonSerializer.Serialize(envelope, JsonOptions);
        var roundTripped = JsonSerializer.Deserialize<GameCommandEnvelope>(json, JsonOptions);

        Assert.Equal(envelope, roundTripped);
        Assert.Equal("e2e4", roundTripped!.Arg);
    }

    [Fact]
    public void Reply_round_trips_through_json_on_both_success_and_failure()
    {
        var ok = new GameCommandReply("corr-1", true, null, "{\"gameId\":\"" + Guid.NewGuid() + "\"}");
        var okJson = JsonSerializer.Serialize(ok, JsonOptions);
        Assert.Equal(ok, JsonSerializer.Deserialize<GameCommandReply>(okJson, JsonOptions));

        var failed = new GameCommandReply("corr-2", false, "Not your turn.", null);
        var failedJson = JsonSerializer.Serialize(failed, JsonOptions);
        Assert.Equal(failed, JsonSerializer.Deserialize<GameCommandReply>(failedJson, JsonOptions));
    }

    [Fact]
    public async Task A_timed_out_command_does_not_retry()
    {
        // A real GameCommandRouter, a fake ISubscriber that accepts the publish but never delivers a
        // reply, exactly "a router whose owner never replies". CommandTimeoutSeconds is 1 so the test
        // does not wait out the production default.
        var options = new RedisOptions("fake:6379", "test:", InstanceTtlSeconds: 30, HeartbeatSeconds: 10, CommandTimeoutSeconds: 1);
        var identity = new InstanceIdentity("caller-instance");
        var subscriber = new FakeSubscriber();
        var connection = new FakeRedisConnection { Subscriber = subscriber };
        var services = new ServiceCollection().BuildServiceProvider(); // never resolved: SendAsync does not touch the lazy fields
        var router = new GameCommandRouter(connection, identity, options, services, NullLogger<GameCommandRouter>.Instance);

        var envelope = new GameCommandEnvelope(
            Guid.NewGuid().ToString("N"), identity.Id, GameCommandKind.SubmitMove, Guid.NewGuid(), Guid.NewGuid(), "conn-1", "e2e4");

        var ex = await Assert.ThrowsAsync<HubException>(() => router.SendAsync("owner-instance", envelope));

        Assert.Equal("The game server did not respond. Try again.", ex.Message);
        Assert.Single(subscriber.Published); // exactly one publish, never retried
        Assert.Equal("test:game:cmd:owner-instance", subscriber.Published[0].Channel.ToString());
    }
}
