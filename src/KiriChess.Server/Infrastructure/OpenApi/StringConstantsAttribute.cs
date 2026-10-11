using System.Reflection;

namespace KiriChess.Server.Infrastructure.OpenApi;

/// <summary>
/// Marks a <c>string</c> DTO property whose value is always one of a fixed set of string constants, so
/// the OpenAPI document can say so (Phase 9d.7, Task C). This is <see cref="StringEnumAttribute"/>'s
/// sibling for the closed vocabularies that are not C# enums: <c>DetectionVerdict</c> and
/// <c>DormancyReasons</c> are <c>static class</c>es of <c>public const string</c> fields, not
/// <c>enum</c>s, because their values are the wire format itself (persisted, compared, and in
/// <c>DetectionVerdict</c>'s case, thresholds computed against), so an <c>enum</c> plus a
/// <c>.ToString()</c> mapping would just be a second, driftable definition of the same vocabulary.
/// <see cref="StringEnumAttribute"/> cannot describe them, its constructor throws on a non-enum type,
/// correctly, so this attribute exists rather than that check being loosened.
///
/// Validates in its constructor for the same reason <see cref="StringEnumAttribute"/> does: a typo in
/// the vocabulary type is a startup failure, not a silently-missing enum list in the document.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter, AllowMultiple = false)]
public sealed class StringConstantsAttribute(Type vocabularyType) : Attribute
{
    public Type VocabularyType { get; } = HasPublicConstStringFields(vocabularyType)
        ? vocabularyType
        : throw new ArgumentException(
            $"{vocabularyType} declares no public const string fields.", nameof(vocabularyType));

    private static bool HasPublicConstStringFields(Type type) =>
        type.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Any(f => f.IsLiteral && !f.IsInitOnly && f.FieldType == typeof(string));
}
