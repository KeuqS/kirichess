namespace KiriChess.Server.Moderation.Content;

/// <summary>
/// Phase 8a.4 tunables. Read at request time via the same rule the rest of this codebase follows -
/// see RateLimiting.cs's header comment and the 6a.8 post-mortem it cites.
///
/// MaxLength is 24 while ApplicationUser.DisplayName is HasMaxLength(64) on purpose: the column has to
/// keep holding names created before this phase existed, and DeriveAvailableAsync needs headroom to
/// append a collision suffix. 24 is the policy for a name a human chooses.
/// </summary>
public sealed record ContentModerationOptions(int MinLength, int MaxLength, int RenameCooldownDays)
{
    public static ContentModerationOptions From(IConfiguration configuration) => new(
        configuration.GetValue("ContentModeration:MinLength", 3),
        configuration.GetValue("ContentModeration:MaxLength", 24),
        configuration.GetValue("ContentModeration:RenameCooldownDays", 30));
}
