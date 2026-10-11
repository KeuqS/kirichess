namespace KiriChess.Server.Auth;

/// <summary>
/// S-4-3. A raw API token is <see cref="ApiTokenDefaults.Prefix"/>, then
/// <see cref="ApiTokenDefaults.SecretLength"/> characters of URL-safe base64 (32 random bytes,
/// padding stripped), then a <see cref="ApiTokenDefaults.ChecksumLength"/>-character checksum.
/// The checksum is CRC-32 (IEEE / ISO-HDLC) of those secret characters only, written as base62
/// in <see cref="ApiTokenDefaults.ChecksumAlphabet"/> and padded on the left with
/// <c>0</c>. A secret scanner can reject a string that fails it without calling this server.
/// The same rule is written on <c>/developers</c>.
/// </summary>
public static class ApiTokenFormat
{
    public static string Create(ReadOnlySpan<byte> secret)
    {
        if (secret.Length != 32)
            throw new ArgumentException("An API token secret is 32 bytes.", nameof(secret));

        var body = Convert.ToBase64String(secret).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return ApiTokenDefaults.Prefix + body + Checksum(body);
    }

    /// <summary>
    /// True when <paramref name="raw"/> has the prefix, the secret alphabet, the right length,
    /// and a checksum that matches. A false result is not a credential and is not looked up.
    /// </summary>
    public static bool IsWellFormed(string? raw)
    {
        var prefixLength = ApiTokenDefaults.Prefix.Length;
        var expected = prefixLength + ApiTokenDefaults.SecretLength + ApiTokenDefaults.ChecksumLength;
        if (raw is null || raw.Length != expected) return false;
        if (!raw.StartsWith(ApiTokenDefaults.Prefix, StringComparison.Ordinal)) return false;

        var secret = raw.AsSpan(prefixLength, ApiTokenDefaults.SecretLength);
        foreach (var c in secret)
        {
            if (!IsSecretChar(c)) return false;
        }

        var presented = raw.AsSpan(prefixLength + ApiTokenDefaults.SecretLength);
        var expectedChecksum = Checksum(secret);
        return presented.SequenceEqual(expectedChecksum);
    }

    public static string Checksum(ReadOnlySpan<char> secret)
    {
        var crc = Crc32(secret);
        Span<char> chars = stackalloc char[ApiTokenDefaults.ChecksumLength];
        for (var i = ApiTokenDefaults.ChecksumLength - 1; i >= 0; i--)
        {
            chars[i] = ApiTokenDefaults.ChecksumAlphabet[(int)(crc % 62)];
            crc /= 62;
        }

        return new string(chars);
    }

    private static bool IsSecretChar(char c) =>
        c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_';

    /// <summary>CRC-32/ISO-HDLC, the same polynomial and reflection as zlib's <c>crc32</c>.</summary>
    private static uint Crc32(ReadOnlySpan<char> text)
    {
        var crc = 0xFFFF_FFFFu;
        foreach (var c in text)
        {
            crc ^= c;
            for (var i = 0; i < 8; i++)
            {
                var lsb = crc & 1;
                crc >>= 1;
                if (lsb != 0) crc ^= 0xEDB8_8320;
            }
        }

        return ~crc;
    }
}
