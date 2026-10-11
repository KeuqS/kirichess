using System.Buffers.Binary;
using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using KiriChess.Server.Data.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace KiriChess.Server.Tests;

/// <summary>
/// Phase 9d.2. Computes the TOTP code Identity's AuthenticatorTokenProvider will accept for a given
/// shared key.
///
/// UserManager.GenerateTwoFactorTokenAsync cannot be used for this: the authenticator provider's
/// GenerateAsync returns "" by design, because the code is supposed to come from the user's phone.
/// This mirrors AuthenticatorTokenProvider.ValidateAsync exactly - HMACSHA1, 30-second timestep, no
/// modifier, six digits - and Identity's own Base32/Rfc6238 helpers are internal, hence the copies.
///
/// Shared rather than private to TwoFactorTests because every test class that authenticates as an
/// Admin now has to enrol first: ModerationPolicies.Admin requires the mfa claim on top of the role.
/// </summary>
internal static class TotpTestHelper
{
    /// <summary>
    /// Turns 2FA on for a user through UserManager and hands back the shared key. Deliberately not
    /// driven through /api/account/2fa/enrol + /enable: the classes that call this are testing the
    /// moderation surface, not enrolment, and going through UserManager keeps their client's
    /// Authorization header out of the setup entirely.
    /// </summary>
    public static async Task<string> EnableTwoFactorAsync(IServiceProvider services, Guid userId)
    {
        using var scope = services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await users.FindByIdAsync(userId.ToString());

        await users.ResetAuthenticatorKeyAsync(user!);
        var key = await users.GetAuthenticatorKeyAsync(user!);
        await users.SetTwoFactorEnabledAsync(user!, true);

        return key!;
    }

    /// <summary>
    /// Signs in an account that has 2FA enabled, both steps, and returns the /login/2fa response for
    /// the caller to deserialize into its own auth record. Phase 9d.2: /login answers with a challenge
    /// ticket rather than a token pair for such an account, so a one-step login no longer works.
    /// </summary>
    public static async Task<HttpResponseMessage> LoginWithTwoFactorAsync(
        HttpClient client, string email, string password, string sharedKey, JsonSerializerOptions jsonOptions)
    {
        var challengeResponse = await client.PostAsJsonAsync("/api/auth/login",
            new { Email = email, Password = password }, jsonOptions);
        challengeResponse.EnsureSuccessStatusCode();

        var challenge = await challengeResponse.Content.ReadFromJsonAsync<ChallengeDto>(jsonOptions);
        if (challenge is null || !challenge.TwoFactorRequired)
            throw new InvalidOperationException("Expected a two-factor challenge from /api/auth/login.");

        return await client.PostAsJsonAsync("/api/auth/login/2fa",
            new { Ticket = challenge.TwoFactorTicket, Code = Compute(sharedKey) }, jsonOptions);
    }

    private sealed record ChallengeDto(bool TwoFactorRequired, string TwoFactorTicket, int ExpiresInSeconds);

    public static string Compute(string base32Key, long stepOffset = 0)
    {
        var key = FromBase32(base32Key);
        var timestep = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30 + stepOffset;

        var counter = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, timestep);

        var hash = HMACSHA1.HashData(key, counter);
        var offset = hash[^1] & 0x0F;
        var binary =
            ((hash[offset] & 0x7F) << 24) |
            ((hash[offset + 1] & 0xFF) << 16) |
            ((hash[offset + 2] & 0xFF) << 8) |
            (hash[offset + 3] & 0xFF);

        return (binary % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
    }

    private static byte[] FromBase32(string input)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var trimmed = input.TrimEnd('=').ToUpperInvariant();
        var bits = 0;
        var value = 0;
        var output = new List<byte>(trimmed.Length * 5 / 8);

        foreach (var c in trimmed)
        {
            var index = alphabet.IndexOf(c);
            if (index < 0) throw new FormatException($"'{c}' is not a base32 character.");
            value = (value << 5) | index;
            bits += 5;
            if (bits < 8) continue;
            output.Add((byte)((value >> (bits - 8)) & 0xFF));
            bits -= 8;
        }

        return output.ToArray();
    }
}
