using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace KiriChess.Server.Infrastructure.OpenApi;

/// <summary>
/// Turns the several <c>.Produces&lt;T&gt;(200)</c> declarations on a single operation into a real
/// <c>oneOf</c> (Phase 9d.7).
///
/// <b>Why this exists.</b> <c>.Produces&lt;T&gt;</c> is one type per status code by construction: a
/// second call for the same code replaces the first, silently. Two routes genuinely have two 200
/// shapes: <c>POST /api/auth/login</c> and <c>POST /api/auth/external/exchange</c>. Each returns a
/// token pair for an account without 2FA and a <c>TwoFactorRequiredResponse</c> ticket for one with
/// it (Phase 9d.2). Left alone, the document would claim each always returns the 2FA ticket, because
/// that declaration happens to come second.
///
/// That is not a cosmetic inaccuracy. This phase exists because "a generated type that differs subtly
/// from the hand-written one it replaces changes runtime behaviour while typechecking cleanly", and
/// login is the most-called endpoint in the product: a generated client that types it as the ticket
/// shape is wrong for every user who does not have 2FA enabled, which is nearly all of them. The
/// document has to state what the endpoint really does.
///
/// <b>Why a transformer rather than richer route metadata.</b> The alternatives were considered and
/// rejected: declaring only <c>AuthResponse</c> lies about the 2FA branch instead of the ordinary one;
/// introducing a merged "login response" record with every field nullable would change the wire (the
/// server sends one shape or the other, never a union of their fields) and is exactly the kind of
/// DTO-level surgery scope decision 1 rules out; and splitting login into two routes is a breaking API
/// change for a documentation problem. A transformer touches metadata only - no handler, no DTO and no
/// byte on the wire changes.
///
/// <b>Scope.</b> Deliberately driven by an explicit table rather than by "any operation that looks
/// ambiguous". A transformer that guesses would quietly reshape responses across the whole document
/// the next time somebody adds a second <c>.Produces</c> by accident - and that accident is precisely
/// what the coverage guard should be surfacing, not what this should be papering over.
/// </summary>
public sealed class MultiShapeResponseTransformer : IOpenApiOperationTransformer
{
    /// <summary>
    /// (method, path, status) -> the component schema names that status can really carry. Adding a
    /// row here is a deliberate statement that a route has more than one success shape, and it
    /// belongs next to the comment on that route explaining why.
    /// </summary>
    private static readonly Dictionary<(string Method, string Path, string Status), string[]> MultiShaped = new()
    {
        [("POST", "/api/auth/login", "200")] = ["AuthResponse", "TwoFactorRequiredResponse"],
        [("POST", "/api/auth/external/exchange", "200")] = ["ExternalExchangeResponse", "TwoFactorRequiredResponse"],
    };

    public Task TransformAsync(
        OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        var method = context.Description.HttpMethod?.ToUpperInvariant();
        var path = "/" + context.Description.RelativePath?.TrimEnd('/');
        if (method is null || operation.Responses is null)
            return Task.CompletedTask;

        foreach (var ((m, p, status), schemaNames) in MultiShaped)
        {
            if (m != method || p != path) continue;
            if (!operation.Responses.TryGetValue(status, out var response)) continue;
            if (response.Content is null ||
                !response.Content.TryGetValue("application/json", out var mediaType))
                continue;

            mediaType.Schema = new OpenApiSchema
            {
                OneOf = schemaNames
                    .Select(IOpenApiSchema (name) => new OpenApiSchemaReference(name))
                    .ToList(),
            };
        }

        return Task.CompletedTask;
    }
}
