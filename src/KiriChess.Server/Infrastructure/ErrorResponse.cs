namespace KiriChess.Server.Infrastructure;

/// <summary>
/// The error envelope this API has always returned - <c>Results.BadRequest(new { error = "..." })</c>
/// and friends - given a name so the OpenAPI document (Phase 9d.7) can describe it and the client can
/// type it. <c>ui/lib/api.ts</c>'s <c>extractErrorMessage</c> already depends on this exact shape.
///
/// Deliberately NOT wired into the handlers: every existing <c>new { error = ... }</c> return stays
/// exactly as it is. Minimal APIs serialize with <see cref="System.Text.Json.JsonSerializerDefaults.Web"/>,
/// so the anonymous object and this record produce byte-identical JSON, and a metadata-only change
/// cannot alter what any endpoint actually sends. See the phase README, scope decision 1.
/// </summary>
public sealed record ErrorResponse(string Error);
