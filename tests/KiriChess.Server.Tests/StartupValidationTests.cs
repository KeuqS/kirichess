using KiriChess.Server.Auth;
using KiriChess.Server.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KiriChess.Server.Tests;

/// <summary>Covers <see cref="StartupValidation.ValidateJwt"/> against a minimal service collection.</summary>
public sealed class StartupValidationTests
{
    [Fact]
    public void ValidateJwt_throws_when_key_missing()
    {
        var services = BuildServiceProvider(new JwtOptions { Key = "" });

        var ex = Assert.Throws<InvalidOperationException>(() => StartupValidation.ValidateJwt(services));
        Assert.Contains("Jwt:Key is not configured", ex.Message);
    }

    [Fact]
    public void ValidateJwt_throws_when_key_shorter_than_32_bytes()
    {
        var services = BuildServiceProvider(new JwtOptions { Key = "too-short-key" });

        var ex = Assert.Throws<InvalidOperationException>(() => StartupValidation.ValidateJwt(services));
        Assert.Contains("too short", ex.Message);
    }

    [Fact]
    public void ValidateJwt_does_not_throw_for_a_32_byte_or_longer_key()
    {
        var services = BuildServiceProvider(new JwtOptions { Key = "this-key-is-at-least-32-bytes-long!!" });

        var exception = Record.Exception(() => StartupValidation.ValidateJwt(services));
        Assert.Null(exception);
    }

    private static IServiceProvider BuildServiceProvider(JwtOptions jwtOptions)
    {
        var services = new ServiceCollection();
        services.Configure<JwtOptions>(o =>
        {
            o.Key = jwtOptions.Key;
            o.Issuer = jwtOptions.Issuer;
            o.Audience = jwtOptions.Audience;
            o.ExpiryMinutes = jwtOptions.ExpiryMinutes;
        });
        return services.BuildServiceProvider();
    }
}
