using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace KiriChess.Server.Data;

/// <summary>
/// Lets <c>dotnet ef migrations add</c> build the model with no live database or configured
/// environment. The connection string is a placeholder used only for provider metadata at
/// design time, no connection is opened by <c>migrations add</c>.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<KiriChessDbContext>
{
    public KiriChessDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<KiriChessDbContext>()
            .UseNpgsql("Host=localhost;Database=kirichess;Username=postgres;Password=postgres")
            .Options;
        return new KiriChessDbContext(options);
    }
}
