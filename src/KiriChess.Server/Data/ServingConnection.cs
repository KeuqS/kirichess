namespace KiriChess.Server.Data;

/// <summary>
/// C-12-4. The long-lived process and <c>--migrate</c> can use different database roles.
/// The serving string is what the process keeps. When a migrator string is set, only the
/// migrate process uses it, so the serving role does not need to be able to change the schema.
/// </summary>
internal static class ServingConnection
{
    public static string Select(string? serving, string? migrator, bool migrating)
    {
        if (migrating && !string.IsNullOrWhiteSpace(migrator))
            return migrator;
        return serving ?? "";
    }
}
