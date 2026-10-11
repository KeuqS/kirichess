using KiriChess.Engine.Config;
using KiriChess.Engine.Core;
using KiriChess.Server.Data.Entities;
using KiriChess.Server.Infrastructure.OpenApi;

namespace KiriChess.Server.Games;

/// <summary>
/// Create a challenge. Send either an official <see cref="Pool"/> or a <see cref="Custom"/> ruleset,
/// not both. Friend challenges are always unrated: a shared link is too easy to farm Glicko against
/// a cooperating partner.
/// </summary>
public sealed record CreateChallengeRequest(
    string? Pool,
    // ChallengeEndpoints parses this with Enum.TryParse<ChallengeColour>(ignoreCase: true), so it is a
    // genuine closed vocabulary on the request side too, not just on ChallengeDto's response side
    // (Phase 9d.7, Task C's Vocabulary_valued_string_properties_declare_their_values test found this).
    [property: StringEnum(typeof(ChallengeColour))] string Colour,
    CustomConfigDto? Custom = null);

/// <summary>
/// What <c>GET /api/challenges/{token}</c> and <c>GET /api/challenges/mine</c> return.
/// <see cref="ChallengerName"/> is a display name only - the token endpoint is anonymous (a logged-out
/// friend has to be able to read it), and the challenger's id or email must never leak through it.
/// <see cref="ChallengerIsBot"/> is true when the link was minted with an API token. It is the
/// label on the accept page, not a secret about the account.
/// <see cref="Economy"/> is the ruleset the other player is being asked to accept. An official pool
/// already has a published page; a custom challenge does not, so the rules travel with the link.
/// </summary>
public sealed record ChallengeDto(
    string Token, string ChallengerName, string Pool,
    [property: StringEnum(typeof(ChallengeColour))] string Colour,
    [property: StringEnum(typeof(ChallengeStatus))] string Status,
    DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, Guid? GameId,
    EconomyDto Economy, bool ChallengerIsBot);

/// <summary>The HTTP response body of <c>POST /api/challenges/{token}/accept</c>.</summary>
public sealed record ChallengeAcceptedDto(
    Guid GameId,
    // Side, not ChallengeColour (Phase 9d.7, Task C): by the time a challenge is accepted the colour is
    // resolved to whoever actually plays White or Black, so ChallengeColour.Random is not a value this
    // property can ever hold, and documenting it as possible would be wrong.
    [property: StringEnum(typeof(Side))] string YourColour,
    string Pool);

/// <summary>
/// Pushed to SignalR group <c>challenge:{token}</c> when a friend accepts. See GameHub.WatchChallenge.
/// <see cref="YourColour"/> carries the same <see cref="Side"/>-not-<see cref="ChallengeColour"/>
/// reasoning as <see cref="ChallengeAcceptedDto.YourColour"/> above.
/// </summary>
public sealed record ChallengeAcceptedEvent(string Token, Guid GameId, [property: StringEnum(typeof(Side))] string YourColour, string Pool);

public static class ChallengeMapping
{
    /// <summary>
    /// <see cref="ChallengeDto.Colour"/> and <see cref="ChallengeDto.Status"/> carry <c>[StringEnum]</c>
    /// (Phase 9d.7, Task C), so the document now states their real vocabulary, but the mapping below
    /// still emits <c>.ToString()</c>: the attribute is metadata for the OpenAPI document only, and the
    /// wire format deliberately does not move (README scope decision 3).
    /// </summary>
    public static ChallengeDto ToDto(this Challenge c, DateTimeOffset now) => new(
        c.Token,
        c.Challenger!.DisplayName,
        c.Pool,
        c.Colour.ToString(),
        ChallengeService.StatusOf(c, now).ToString(),
        c.CreatedAt,
        c.ExpiresAt,
        c.GameId,
        RulesOf(c),
        c.ChallengerViaApi);

    /// <summary>
    /// The rules the row was minted with. Custom challenges store them on the row; an official pool
    /// challenge stores only the pool key, and the preset table is the ruleset. A row that is neither
    /// cannot be accepted either (<c>ChallengeEndpoints</c> refuses it), and disclosing a guessed
    /// config would be worse than failing the read.
    /// </summary>
    private static EconomyDto RulesOf(Challenge c)
    {
        if (c.CustomConfig is not null)
            return c.CustomConfig.ToEconomyDto();
        if (KiriChessConfig.Presets.TryGetValue(c.Pool, out var preset))
            return preset.ToEconomyDto();
        throw new InvalidOperationException(
            $"Challenge '{c.Token}' has no ruleset to disclose.");
    }
}
