namespace KiriChess.Server.Account;

/// <summary>
/// The upload contract: a square JPEG, 64–512 px on a side, at most 256 KiB. The browser crops
/// and re-encodes before PUT, and this parser is the server's own check so a crafted body cannot
/// store an SVG, a URL, or an arbitrarily large blob. Dimensions come from the SOF marker, not
/// from a decoder library, so an unknown JPEG variant fails closed.
/// </summary>
public static class JpegAvatar
{
    public const int MinEdge = 64;
    public const int MaxEdge = 512;
    public const int MaxBytes = 256 * 1024;
    public const int OutputEdge = 256;
    public const string ContentType = "image/jpeg";

    public static bool TryValidate(ReadOnlySpan<byte> data, out string error)
    {
        if (data.Length < 24)
        {
            error = "That file is not a valid JPEG.";
            return false;
        }

        if (data.Length > MaxBytes)
        {
            error = "Profile pictures must be 256 KB or smaller.";
            return false;
        }

        if (data[0] != 0xFF || data[1] != 0xD8 || data[2] != 0xFF)
        {
            error = "Profile pictures must be a JPEG.";
            return false;
        }

        if (!TryReadSize(data, out var width, out var height))
        {
            error = "That file is not a valid JPEG.";
            return false;
        }

        if (width != height)
        {
            error = "Profile pictures must be square.";
            return false;
        }

        if (width < MinEdge || width > MaxEdge)
        {
            error = $"Profile pictures must be between {MinEdge} and {MaxEdge} pixels.";
            return false;
        }

        error = "";
        return true;
    }

    /// <summary>
    /// Reads width and height from the first SOF marker. Returns false when the stream is truncated
    /// or never declares a frame, which is the fail-closed path for a body that is not a JPEG.
    /// </summary>
    internal static bool TryReadSize(ReadOnlySpan<byte> data, out int width, out int height)
    {
        width = 0;
        height = 0;
        var i = 2;
        while (i + 3 < data.Length)
        {
            if (data[i] != 0xFF)
                return false;

            while (i < data.Length && data[i] == 0xFF)
                i++;
            if (i >= data.Length)
                return false;

            var marker = data[i++];
            if (marker == 0xD9 || marker == 0xDA)
                return false;
            if (IsStandalone(marker))
                continue;
            if (i + 1 >= data.Length)
                return false;

            var segmentLength = (data[i] << 8) | data[i + 1];
            if (segmentLength < 2 || i + segmentLength > data.Length)
                return false;

            if (IsSof(marker))
            {
                if (segmentLength < 8)
                    return false;
                height = (data[i + 3] << 8) | data[i + 4];
                width = (data[i + 5] << 8) | data[i + 6];
                return width > 0 && height > 0;
            }

            i += segmentLength;
        }

        return false;
    }

    private static bool IsStandalone(byte marker) =>
        marker == 0x01 || marker is >= 0xD0 and <= 0xD7 || marker == 0xD8;

    private static bool IsSof(byte marker) =>
        marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC);
}
