namespace KiriChess.Server.Infrastructure.OpenApi;

/// <summary>
/// Marks a <c>string</c> DTO property whose value is always the name of a C# enum member, so the
/// OpenAPI document can say so (Phase 9d.7).
///
/// This exists because the DTOs deliberately carry <c>string</c>, not the enum type. The same records
/// are serialized by two different serializers - minimal APIs' JsonOptions and SignalR's own
/// AddJsonProtocol payload options (Program.cs) - and an options-level JsonStringEnumConverter reaches
/// one and not the other, so switching to real enums would start sending 0/1 over the game hub. See
/// the phase README, scope decision 3: the wire format must not move, only the document's description
/// of it.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter, AllowMultiple = false)]
public sealed class StringEnumAttribute(Type enumType) : Attribute
{
    public Type EnumType { get; } = enumType.IsEnum
        ? enumType
        : throw new ArgumentException($"{enumType} is not an enum.", nameof(enumType));
}
