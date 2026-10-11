using KiriChess.Server.Auth;
using Microsoft.Extensions.Options;

namespace KiriChess.Server.Infrastructure;

/// <summary>Boot-time guards that turn misconfiguration into a loud crash instead of a runtime 401.</summary>
public static class StartupValidation
{
    /// <summary>HMAC-SHA256 needs at least 256 bits of key material.</summary>
    public const int MinimumJwtKeyBytes = 32;

    /// <summary>
    /// Throws when the JWT signing key is absent or too short. Called after the host is built so it
    /// sees environment variables and secret providers, not just appsettings.
    /// </summary>
    public static void ValidateJwt(IServiceProvider services)
    {
        var jwt = services.GetRequiredService<IOptions<JwtOptions>>().Value;
        if (string.IsNullOrWhiteSpace(jwt.Key))
            throw new InvalidOperationException(
                "Jwt:Key is not configured. Set the Jwt__Key environment variable (or a secret) to a value of at least "
                + $"{MinimumJwtKeyBytes} bytes.");

        if (System.Text.Encoding.UTF8.GetByteCount(jwt.Key) < MinimumJwtKeyBytes)
            throw new InvalidOperationException(
                $"Jwt:Key is too short: HMAC-SHA256 requires at least {MinimumJwtKeyBytes} bytes.");
    }
}
