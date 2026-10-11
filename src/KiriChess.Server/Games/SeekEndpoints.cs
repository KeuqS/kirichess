using KiriChess.Server.Infrastructure;

namespace KiriChess.Server.Games;

/// <summary>
/// Phase 9e.4. The public open-seek list: one anonymous read endpoint, no mutation of its own.
/// <see cref="MatchmakingService.TryJoinAsync"/> already pairs two players in the same pool
/// instantly (<see cref="MatchQueue"/>'s Lua script), so "accepting" a seek is just joining that
/// pool's queue, <c>GameHub.JoinQueue</c>, unchanged. This endpoint exists purely so a player can
/// see that somebody is already waiting before queuing blind, which is the entire mechanism 9e.4
/// converts near-misses into games with.
///
/// Anonymous, mapped on <c>app</c> directly, same reasoning as <c>GET /api/games/live</c>: a
/// stranger deciding whether the site has anyone home right now is exactly who this is for.
/// </summary>
public static class SeekEndpoints
{
    public static IEndpointRouteBuilder MapSeekEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/seeks", async (MatchmakingService matchmaking, KillSwitchService killSwitches, CancellationToken ct) =>
        {
            // Phase 10c.4. Checked before the query below: it costs a dictionary lookup, not a round
            // trip, and the whole point of a kill switch is that it must not itself become the slow
            // path during the incident it exists for.
            if (!await killSwitches.IsEnabledAsync(KillSwitch.SeekList, ct))
                return Results.Json(new { error = "The seek list is temporarily unavailable." },
                    statusCode: StatusCodes.Status503ServiceUnavailable);

            var seeks = await matchmaking.OpenSeeksAsync(ct);
            var rows = seeks.Select(s => new OpenSeekDto(s.Pool, s.CreatedAt)).ToArray();
            return Results.Ok(rows);
        })
        .Produces<OpenSeekDto[]>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status503ServiceUnavailable);

        return app;
    }
}
