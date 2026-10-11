using KiriChess.Server.Auth;

namespace KiriChess.Server.Tests;

/// <summary>
/// Pure unit tests for <see cref="ExternalAuthEndpoints.SafeReturnPath"/>, the open-redirect guard on
/// the OAuth callback. No Docker, no host: this is the coverage that survives a Docker-less machine.
/// </summary>
public sealed class ExternalReturnPathTests
{
    [Theory]
    [InlineData(null, "/")]
    [InlineData("", "/")]
    [InlineData("/", "/")]
    [InlineData("/profile", "/profile")]
    [InlineData("//evil.com", "/")]
    [InlineData("https://evil.com", "/")]
    [InlineData("http:/evil", "/")]
    [InlineData("javascript:alert(1)", "/")]
    [InlineData("/\\evil.com", "/")]
    [InlineData("profile", "/")]
    public void Reduces_to_a_safe_site_relative_path(string? candidate, string expected)
    {
        Assert.Equal(expected, ExternalAuthEndpoints.SafeReturnPath(candidate));
    }
}
