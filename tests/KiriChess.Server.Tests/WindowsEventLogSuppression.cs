using System.Runtime.CompilerServices;

namespace KiriChess.Server.Tests;

/// <summary>
/// Silences the Windows EventLog logging provider for the whole test assembly, before any host is
/// built. Nothing in this suite asserts on EventLog output, and leaving it attached made local runs
/// on Windows fail 2 to 7 tests at random with
/// <c>AggregateException: ... Cannot access a disposed object. Object name: 'EventLogInternal'</c>,
/// thrown out of <see cref="Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory{T}"/> teardown
/// rather than out of any assertion.
///
/// Why the provider is there at all: Program.cs only calls <c>ClearProviders()</c> outside
/// Development (Phase 6d.2), and every fixture here builds its host with
/// <c>UseEnvironment("Development")</c>, so the default provider set survives. On Windows that set
/// includes EventLog; a shared handle to it is disposed by whichever host tears down first, while a
/// concurrently stopping host is still writing a shutdown log line through it. CI runs on
/// <c>ubuntu-latest</c>, where the provider does not exist, which is why this was only ever a local
/// symptom.
///
/// Done as configuration rather than as <c>ClearProviders()</c> in each fixture so that it covers
/// fixtures written later too, and so console output stays available when a test needs diagnosing.
/// </summary>
internal static class WindowsEventLogSuppression
{
    [ModuleInitializer]
    internal static void Disable() =>
        Environment.SetEnvironmentVariable("Logging__EventLog__LogLevel__Default", "None");
}
