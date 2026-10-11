using KiriChess.Engine.Config;
using KiriChess.Server.Data.Entities;
using KiriChess.Server.Games;
using Xunit;

namespace KiriChess.Server.Tests;

/// <summary>
/// C-8-2. The accept page and the replay can only state a custom challenge's rules if the DTO
/// carries them. This is the mapping; <see cref="ChallengeTests"/> covers the anonymous wire.
/// </summary>
public class ChallengeMappingTests
{
    [Fact]
    public void Custom_challenge_carries_the_minted_economy()
    {
        var config = new KiriChessConfig
        {
            StartTimeSeconds = 123,
            BaseIncrement = 4,
            FlagFallLoses = false,
            ThreefoldRepetitionIsDraw = false,
        };
        var challenge = new Challenge
        {
            Token = "tok",
            Pool = ChallengeService.CustomPool,
            Colour = ChallengeColour.White,
            CustomConfig = config,
            Challenger = new ApplicationUser { DisplayName = "Ada" },
            CreatedAt = DateTimeOffset.UnixEpoch,
            ExpiresAt = DateTimeOffset.UnixEpoch.AddHours(1),
        };

        var dto = challenge.ToDto(DateTimeOffset.UnixEpoch);

        Assert.Equal(123, dto.Economy.StartTimeSeconds);
        Assert.Equal(4, dto.Economy.BaseIncrement);
        Assert.False(dto.Economy.FlagFallLoses);
        Assert.False(dto.Economy.ThreefoldRepetitionIsDraw);
        Assert.Equal(config.RepeatedPositionNullifiesTime, dto.Economy.RepeatedPositionNullifiesTime);
        Assert.Equal(config.FlagVsInsufficientMaterialIsDraw, dto.Economy.FlagVsInsufficientMaterialIsDraw);
    }

    [Fact]
    public void Official_pool_challenge_carries_that_pools_economy()
    {
        var preset = KiriChessConfig.Presets["steal1"];
        var challenge = new Challenge
        {
            Token = "tok",
            Pool = "steal1",
            Colour = ChallengeColour.Random,
            Challenger = new ApplicationUser { DisplayName = "Ada" },
            CreatedAt = DateTimeOffset.UnixEpoch,
            ExpiresAt = DateTimeOffset.UnixEpoch.AddHours(1),
        };

        var dto = challenge.ToDto(DateTimeOffset.UnixEpoch);

        Assert.Equal(preset.StartTimeSeconds, dto.Economy.StartTimeSeconds);
        Assert.Equal(preset.BaseIncrement, dto.Economy.BaseIncrement);
        Assert.Equal(preset.CaptureCaptorGainEnabled, dto.Economy.CaptureCaptorGainEnabled);
        Assert.Equal(preset.CaptureVictimLossEnabled, dto.Economy.CaptureVictimLossEnabled);
        Assert.Equal(preset.FlagFallLoses, dto.Economy.FlagFallLoses);
        Assert.Equal(preset.FlagVsInsufficientMaterialIsDraw, dto.Economy.FlagVsInsufficientMaterialIsDraw);
    }
}
