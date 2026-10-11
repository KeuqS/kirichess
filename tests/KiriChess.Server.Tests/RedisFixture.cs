using Testcontainers.Redis;
using Xunit;

namespace KiriChess.Server.Tests;

/// <summary>
/// Shared Testcontainers Redis instance for <c>RedisFoundationContainerTests</c>, mirroring the
/// Postgres fixture pattern in <see cref="DatabaseSmokeTests"/>: gated on
/// <see cref="DockerAvailability.IsAvailable"/>, so tests skip cleanly with no Docker daemon rather
/// than failing. One container backs every test in the class (<c>IClassFixture</c>), each test
/// gets its own unique key prefix instead of its own container, which is why the prefix discipline
/// in the tests is not optional.
/// </summary>
public sealed class RedisFixture : IAsyncLifetime
{
    public RedisContainer? Container { get; private set; }

    public async Task InitializeAsync()
    {
        if (!DockerAvailability.IsAvailable) return;
        Container = new RedisBuilder().WithImage("redis:7-alpine").Build();
        await Container.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (Container is not null) await Container.DisposeAsync();
    }
}
