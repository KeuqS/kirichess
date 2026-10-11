namespace KiriChess.Server.Moderation;

/// <summary>
/// The one role this codebase has (Phase 8.5). Seeded from <c>Moderation:AdminEmails</c> at boot by
/// <see cref="ModerationSeeder"/> rather than through a signup path, because there is no self-service
/// route to becoming a moderator and there should not be one.
/// </summary>
public static class ModerationRoles
{
    public const string Admin = "Admin";
}

/// <summary>
/// Authorization policy names for the moderator surface. 8.3 gated its dashboard with a static token
/// and deferred the role to this phase for one specific reason: a <c>UserSanction</c> row carries
/// <c>IssuedByUserId</c>, and a shared secret cannot attribute an account action to a person.
/// </summary>
public static class ModerationPolicies
{
    public const string Admin = "moderation-admin";
}
