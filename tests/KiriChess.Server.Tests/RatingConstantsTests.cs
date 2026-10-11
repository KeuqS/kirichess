using KiriChess.Server.Ratings;
using Xunit;

namespace KiriChess.Server.Tests;

public sealed class RatingConstantsTests
{
    [Theory]
    [InlineData("bounty1", "bot:bounty1")]
    [InlineData("Bounty1", "bot:bounty1")]
    [InlineData("bot:bounty1", "bot:bounty1")]
    [InlineData("bot:burn2", "bot:burn2")]
    public void RemapApiTokenQueuePool_maps_official_and_already_prefixed_names(string input, string expected)
    {
        Assert.Equal(expected, RatingConstants.RemapApiTokenQueuePool(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("custom")]
    [InlineData("bot:nope")]
    [InlineData("queue:bounty1")]
    public void RemapApiTokenQueuePool_rejects_unknown_names(string? input)
    {
        Assert.Null(RatingConstants.RemapApiTokenQueuePool(input));
    }

    [Fact]
    public void Bot_pool_keys_fit_the_rating_pool_column()
    {
        Assert.All(RatingConstants.BotPools, pool =>
        {
            Assert.True(pool.Length <= 32, pool);
            Assert.True(RatingConstants.IsBotPool(pool));
        });
    }

    [Fact]
    public void Jwt_callers_cannot_treat_bot_pools_as_official()
    {
        Assert.False(RatingConstants.IsOfficialPool("bot:bounty1"));
        Assert.True(RatingConstants.IsOfficialPool("bounty1"));
    }
}
