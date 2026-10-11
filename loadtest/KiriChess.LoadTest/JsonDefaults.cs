using System.Text.Json;

namespace KiriChess.LoadTest;

/// <summary>
/// SignalR's JsonHubProtocol defaults to PropertyNamingPolicy = CamelCase and case-insensitive
/// matching, which is why the wire records in WireDtos.cs deserialize hub payloads with no extra
/// configuration (README § "SignalR's JsonHubProtocol is camelCase on both ends"). Plain
/// System.Net.Http.Json calls do NOT inherit that - System.Text.Json's own default is
/// PropertyNameCaseInsensitive = false - so every HTTP-side deserialization in this project uses
/// these options explicitly. Without this, AuthResponse's PascalCase properties silently bind to
/// null/default against the server's camelCase JSON instead of throwing, which is worse than a crash.
/// </summary>
internal static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true
    };
}
