using KiriChess.Server.Data.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;

namespace KiriChess.Server.Moderation;

/// <summary>
/// Grants the <see cref="ModerationRoles.Admin"/> role to every account listed in
/// <c>Moderation:AdminEmails</c> (Phase 8.5). There is no self-service route to becoming a moderator,
/// so the config file (or its env-var form) is the only way in - the same "no schema, no UI, config is
/// the source of truth" posture 8.3 used for its access token.
/// </summary>
public static class ModerationSeeder
{
    /// <summary>
    /// Idempotent: running this on every boot is cheap, and adding a user already in the role is a
    /// no-op. Never removes anyone - dropping an email from the config is not a revocation, because a
    /// config typo must not silently de-admin the only moderator. That is a deliberate DB action.
    /// </summary>
    public static async Task SeedAsync(
        RoleManager<IdentityRole<Guid>> roles, UserManager<ApplicationUser> users,
        ModerationOptions options, ILogger logger)
    {
        if (options.AdminEmails.Count == 0) return;

        if (!await roles.RoleExistsAsync(ModerationRoles.Admin))
            await roles.CreateAsync(new IdentityRole<Guid>(ModerationRoles.Admin));

        foreach (var email in options.AdminEmails)
        {
            var user = await users.FindByEmailAsync(email);
            if (user is null)
            {
                // Not an error: the configured admin may not have registered yet. They get the role
                // the moment this runs again after they sign up.
                logger.LogInformation("Moderation:AdminEmails lists {Email}, which has no account yet; skipping", email);
                continue;
            }

            // S-3-2. Registration issues tokens immediately and never proves the registrant controls
            // the address (Auth:RequireConfirmedEmail defaults to false, and DEPLOYMENT.md records
            // that it stays false in production). So without this check, whoever registers a listed
            // address FIRST gets Admin on the next boot, owns it or not, then self-enrols TOTP and
            // satisfies ModerationPolicies.Admin, which is the whole moderation surface: every
            // account's email address, the report queue, sanctions and forced renames. The 2FA
            // requirement is no help, because the holder enrols their own authenticator.
            //
            // Refusing here is safe in the direction that matters. SeedAsync never removes a role,
            // so this cannot de-admin an existing moderator; it can only decline to create a new one.
            if (!user.EmailConfirmed)
            {
                logger.LogWarning(
                    "Moderation:AdminEmails lists {Email}, which HAS a registered account but an " +
                    "unverified address; refusing the Admin role. This is not the same as the address " +
                    "having no account yet: somebody holds this account. If that is you, confirm the " +
                    "address and restart. If it is not, you are looking at an attempted takeover of " +
                    "the moderator role, and the account should be investigated before anything else.",
                    email);
                continue;
            }

            if (!await users.IsInRoleAsync(user, ModerationRoles.Admin))
                await users.AddToRoleAsync(user, ModerationRoles.Admin);
        }
    }
}
