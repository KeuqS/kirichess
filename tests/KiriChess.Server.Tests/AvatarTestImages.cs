using KiriChess.Server.Account;

namespace KiriChess.Server.Tests;

public sealed class AvatarTestImages
{
    public static byte[] Square128 { get; } = File.ReadAllBytes(
        Path.Combine(AppContext.BaseDirectory, "TestData", "avatar-128.jpg"));
}
