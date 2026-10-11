using KiriChess.Server.Data;

namespace KiriChess.Server.Tests;

public sealed class ServingConnectionTests
{
    [Fact]
    public void Migrate_uses_the_owner_string_when_one_is_set()
    {
        var selected = ServingConnection.Select("app-role", "owner-role", migrating: true);

        Assert.Equal("owner-role", selected);
    }

    [Fact]
    public void Serving_process_keeps_its_own_string_even_when_an_owner_string_is_configured()
    {
        var selected = ServingConnection.Select("app-role", "owner-role", migrating: false);

        Assert.Equal("app-role", selected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Migrate_falls_back_to_the_serving_string_when_no_owner_string_is_set(string? migrator)
    {
        var selected = ServingConnection.Select("app-role", migrator, migrating: true);

        Assert.Equal("app-role", selected);
    }
}
