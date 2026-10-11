using KiriChess.Server.Auth;
using Xunit;

namespace KiriChess.Server.Tests;

/// <summary>
/// S-4-3. The checksum is the part a secret scanner can check without this server, so the
/// vector below is the published one: forty-three <c>A</c> characters, CRC-32/ISO-HDLC
/// <c>0x0C2B5986</c>, base62 <c>0DofJ8</c>.
/// </summary>
public sealed class ApiTokenFormatTests
{
    [Fact]
    public void The_published_checksum_vector_is_0DofJ8()
    {
        var secret = new string('A', ApiTokenDefaults.SecretLength);
        Assert.Equal("0DofJ8", ApiTokenFormat.Checksum(secret));
    }

    [Fact]
    public void Thirty_two_zero_bytes_are_the_published_vector()
    {
        var raw = ApiTokenFormat.Create(new byte[32]);
        Assert.Equal(ApiTokenDefaults.Prefix + new string('A', 43) + "0DofJ8", raw);
        Assert.True(ApiTokenFormat.IsWellFormed(raw));
    }

    [Fact]
    public void A_flipped_checksum_character_is_rejected()
    {
        var raw = ApiTokenFormat.Create(new byte[32]);
        var flipped = raw[..^1] + (raw[^1] == '0' ? "1" : "0");
        Assert.False(ApiTokenFormat.IsWellFormed(flipped));
    }

    [Fact]
    public void The_old_kc_prefix_is_not_a_token()
    {
        Assert.False(ApiTokenFormat.IsWellFormed("kc_" + new string('A', 43) + "0DofJ8"));
    }

    [Fact]
    public void A_random_token_round_trips_and_a_short_string_does_not()
    {
        var raw = ApiTokenFormat.Create(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        Assert.True(ApiTokenFormat.IsWellFormed(raw));
        Assert.StartsWith(ApiTokenDefaults.Prefix, raw, StringComparison.Ordinal);
        Assert.Equal(
            ApiTokenDefaults.Prefix.Length + ApiTokenDefaults.SecretLength + ApiTokenDefaults.ChecksumLength,
            raw.Length);
        Assert.False(ApiTokenFormat.IsWellFormed(raw[..^1]));
        Assert.False(ApiTokenFormat.IsWellFormed(null));
    }
}
