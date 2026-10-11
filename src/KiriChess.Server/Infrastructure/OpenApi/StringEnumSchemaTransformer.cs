using System.Reflection;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace KiriChess.Server.Infrastructure.OpenApi;

/// <summary>
/// Restores the enum information <see cref="StringEnumAttribute"/> and <see cref="StringConstantsAttribute"/>
/// mark and the DTOs' own <c>string</c> types deliberately do not carry (Phase 9d.7, phase README scope
/// decision 3). Reads whichever attribute is present off the property that produced this schema via
/// <see cref="OpenApiSchemaTransformerContext.JsonPropertyInfo"/> and writes the vocabulary's values
/// into <see cref="OpenApiSchema.Enum"/>, so a nullable string property such as
/// <c>GameStateResponse.Winner</c> documents itself as e.g. <c>"White" | "Black" | null</c> instead of
/// a bare, useless <c>string</c>.
///
/// Covers two shapes of closed vocabulary (Task C added the second one):
/// - <see cref="StringEnumAttribute"/>: the values are <c>Enum.GetNames(...)</c> of a real C# enum.
/// - <see cref="StringConstantsAttribute"/>: the values are the <c>public const string</c> fields of a
///   <c>static class</c> such as <c>DetectionVerdict</c> or <c>DormancyReasons</c>, read by reflection
///   (<c>IsLiteral &amp;&amp; !IsInitOnly</c>, the same filter .NET's own tooling uses to distinguish a
///   compile-time constant from a mere <c>static readonly</c> field) rather than hardcoded here, so the
///   vocabulary keeps exactly one definition, the const fields themselves.
///
/// Registered once via <c>OpenApiOptions.AddSchemaTransformer&lt;StringEnumSchemaTransformer&gt;()</c>
/// in Program.cs, so it runs for every schema the document generator produces: it is a no-op for any
/// property that carries neither attribute.
/// </summary>
public sealed class StringEnumSchemaTransformer : IOpenApiSchemaTransformer
{
    public Task TransformAsync(OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken cancellationToken)
    {
        var attributeProvider = context.JsonPropertyInfo?.AttributeProvider;
        if (attributeProvider is null)
            return Task.CompletedTask;

        var values = ReadVocabulary(attributeProvider);
        if (values is null)
            return Task.CompletedTask;

        schema.Enum = values.Select(value => (JsonNode)JsonValue.Create(value)).ToList();

        // Nullable properties (e.g. GameStateResponse.Winner) already permit null in the schema, so the
        // enum list must say so too, or a strict validator rejects the null the server really sends.
        if (schema.Type is not null && schema.Type.Value.HasFlag(JsonSchemaType.Null))
            schema.Enum.Add(null!);

        return Task.CompletedTask;
    }

    private static IReadOnlyList<string>? ReadVocabulary(ICustomAttributeProvider attributeProvider)
    {
        var stringEnum = attributeProvider
            .GetCustomAttributes(typeof(StringEnumAttribute), inherit: false)
            .Cast<StringEnumAttribute>()
            .FirstOrDefault();
        if (stringEnum is not null)
            return Enum.GetNames(stringEnum.EnumType);

        var stringConstants = attributeProvider
            .GetCustomAttributes(typeof(StringConstantsAttribute), inherit: false)
            .Cast<StringConstantsAttribute>()
            .FirstOrDefault();
        if (stringConstants is not null)
        {
            return stringConstants.VocabularyType
                .GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(f => f.IsLiteral && !f.IsInitOnly && f.FieldType == typeof(string))
                .Select(f => (string)f.GetRawConstantValue()!)
                .ToArray();
        }

        return null;
    }
}
