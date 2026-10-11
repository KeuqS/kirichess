using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace KiriChess.Server.Infrastructure.OpenApi;

/// <summary>
/// Strips the input-only <c>"string"</c> alternative that .NET 10's document generator adds to every
/// numeric schema (Phase 9d.7, Task C). Minimal APIs configure <c>System.Text.Json</c> with
/// <c>JsonSerializerDefaults.Web</c>, which sets <c>JsonNumberHandling.AllowReadingFromString</c>, and
/// the generator honestly documents that deserializer tolerance by widening every <c>int</c>,
/// <c>long</c> and <c>double</c> schema to <c>"type": ["integer", "string"]</c> (or
/// <c>["number", "string"]</c>) with a digit-string <c>pattern</c>. That is not wrong about what the
/// server will *accept*, but it is a strictly worse description of what the server *emits*: every
/// response field the server only ever serializes as a real JSON number - <c>whiteTime</c>,
/// <c>moveCount</c>, <c>rating</c>, every <c>count</c> and <c>percent</c> - would generate as
/// <c>number | string</c> in the TypeScript client, forcing a cast or a <c>Number()</c> call at every
/// arithmetic site in <c>ui/</c> for no reason a caller of these endpoints would ever hit in practice.
///
/// Two alternatives were considered and rejected (phase README, scope decision 3's reasoning, restated
/// here because it is the same principle):
/// - Setting <c>JsonNumberHandling.Strict</c> globally would make the generator's output correct
///   without a transformer, but it is a real wire change: a request body that today sends the digit
///   string <c>"5"</c> for an <c>int</c> would start being rejected with a 400. This phase changes what
///   the document *says*, never what the server *does*.
/// - Leaving it as-is and accepting <c>number | string</c> in the generated client was rejected because
///   the phase README's whole premise is that a generated type must be at least as precise as the
///   hand-written one it replaces, and the hand-written TypeScript for every one of these fields is a
///   bare <c>number</c>.
///
/// So this is metadata only, exactly like <see cref="StringEnumSchemaTransformer"/>: it clears the
/// <see cref="JsonSchemaType.String"/> flag (and the pattern that only existed to describe it) wherever
/// a numeric flag is present alongside it, and leaves the deserializer's actual behaviour, and every
/// genuine <c>string</c>-only schema, untouched.
///
/// Registered once via <c>OpenApiOptions.AddSchemaTransformer&lt;NumericSchemaTransformer&gt;()</c> in
/// Program.cs, so it runs for every schema the document generator produces.
/// </summary>
public sealed class NumericSchemaTransformer : IOpenApiSchemaTransformer
{
    public Task TransformAsync(OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken cancellationToken)
    {
        if (schema.Type is not { } type)
            return Task.CompletedTask;

        var hasNumericFlag = type.HasFlag(JsonSchemaType.Integer) || type.HasFlag(JsonSchemaType.Number);
        if (!hasNumericFlag || !type.HasFlag(JsonSchemaType.String))
            return Task.CompletedTask;

        // Clear only the String flag: Integer/Number stay, and Null stays too (a nullable int? arrives
        // as Integer | String | Null and must come out as Integer | Null, not Integer, or a nullable
        // numeric field would stop documenting that it can be null).
        schema.Type = type & ~JsonSchemaType.String;

        // The pattern (`^-?(?:0|[1-9]\d*)$`) exists only to constrain the string alternative this
        // transformer just removed; leaving it behind would describe a numeric schema as also matching
        // a regex, which no JSON Schema consumer expects and openapi-typescript would not honour anyway.
        schema.Pattern = null;

        return Task.CompletedTask;
    }
}
