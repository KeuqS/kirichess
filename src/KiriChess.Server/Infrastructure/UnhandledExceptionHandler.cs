using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace KiriChess.Server.Infrastructure;

/// <summary>
/// Turns an unhandled request exception into an RFC 9457 ProblemDetails carrying the same trace id
/// that appears in the log line, so a user-reported failure can be found in the logs by that id
/// alone. The exception message is never sent to the client, only the trace id is.
///
/// Sentry's ASP.NET Core integration captures unhandled request exceptions itself when a DSN is
/// configured, so this deliberately does not also call <c>SentrySdk.CaptureException</c>, doing so
/// would double-report the same exception.
/// </summary>
public sealed class UnhandledExceptionHandler : IExceptionHandler
{
    private readonly ILogger<UnhandledExceptionHandler> _logger;

    public UnhandledExceptionHandler(ILogger<UnhandledExceptionHandler> logger) => _logger = logger;

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var traceId = httpContext.TraceIdentifier;
        _logger.LogError(exception, "Unhandled exception for {Method} {Path} (trace {TraceId})",
            httpContext.Request.Method, httpContext.Request.Path, traceId);

        httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await httpContext.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Status = StatusCodes.Status500InternalServerError,
            Title = "An unexpected error occurred.",
            Instance = httpContext.Request.Path,
            Extensions = { ["traceId"] = traceId }
        }, cancellationToken);

        return true;
    }
}
