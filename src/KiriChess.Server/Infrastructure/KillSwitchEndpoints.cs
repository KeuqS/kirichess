using System.Security.Claims;
using KiriChess.Server.Auth;
using KiriChess.Server.Moderation;

namespace KiriChess.Server.Infrastructure;

/// <summary>
/// Phase 10c.4 admin surface: GET the current state of all four kill switches, PUT to flip one. No
/// dashboard beyond this - the item's own text argues against a feature-flag vendor for four
/// booleans, and this is the minimal thing an incident responder needs: read the state, change it,
/// know who changed it last (<see cref="Data.Entities.KillSwitchState.UpdatedByUserId"/>).
/// </summary>
public static class KillSwitchEndpoints
{
    public static IEndpointRouteBuilder MapKillSwitchEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/moderation/kill-switches")
                       .RequireAuthorization(ModerationPolicies.Admin);

        admin.MapGet("/", async (KillSwitchService killSwitches, CancellationToken ct) =>
            Results.Ok(await killSwitches.GetAllAsync(ct)))
            .Produces<Dictionary<string, bool>>(StatusCodes.Status200OK);

        admin.MapPut("/{name}", async (
            string name, SetKillSwitchRequest req, ClaimsPrincipal caller,
            KillSwitchService killSwitches, CancellationToken ct) =>
        {
            var me = caller.GetUserId();
            if (me is null) return Results.Unauthorized();

            if (!Enum.TryParse<KillSwitch>(name, ignoreCase: true, out var which))
                return Results.NotFound(new { error = $"Unknown kill switch '{name}'." });

            await killSwitches.SetEnabledAsync(which, req.Enabled, me.Value, ct);
            return Results.NoContent();
        })
        .Produces(StatusCodes.Status204NoContent)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status404NotFound);

        return app;
    }

}

public sealed record SetKillSwitchRequest(bool Enabled);
