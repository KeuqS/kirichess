using KiriChess.Server.Account;

namespace KiriChess.Server.Tests;

public sealed class JpegAvatarTests
{
    [Fact]
    public void TryValidate_accepts_the_test_fixture()
    {
        Assert.True(JpegAvatar.TryValidate(AvatarTestImages.Square128, out var error), error);
    }

    [Fact]
    public void TryValidate_rejects_a_non_jpeg_prefix()
    {
        ReadOnlySpan<byte> png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        Assert.False(JpegAvatar.TryValidate(png, out _));
    }

    [Fact]
    public void TryReadSize_reads_the_fixture_dimensions()
    {
        Assert.True(JpegAvatar.TryReadSize(AvatarTestImages.Square128, out var width, out var height));
        Assert.Equal(128, width);
        Assert.Equal(128, height);
    }
}
