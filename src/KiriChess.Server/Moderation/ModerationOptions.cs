namespace KiriChess.Server.Moderation;

/// <summary>
/// Reads Moderation:AdminEmails - a string array, so Moderation__AdminEmails__0 works as an env var on
/// Fly. Unset means no admin exists and every moderator route 403s for everybody, which is the correct
/// fail-closed default for a surface that can ban accounts.
/// </summary>
public sealed record ModerationOptions(IReadOnlyList<string> AdminEmails)
{
    public static ModerationOptions From(IConfiguration configuration) =>
        new(configuration.GetSection("Moderation:AdminEmails").Get<string[]>() ?? []);
}
