using KiriChess.Server.Data.Entities;
using KiriChess.Server.Infrastructure.OpenApi;

namespace KiriChess.Server.Moderation;

/// <summary>
/// Submit-a-report request. <see cref="Reason"/> crosses the wire as a string (parsed against
/// <c>ReportReason</c> by the endpoint) so an unknown value is a clean 400 instead of a raw-integer
/// footgun, same shape as <c>GameEndpoints.BuildCustomConfig</c> handles <c>MoveCostMode</c>. It carries
/// <c>[StringEnum]</c> too (Phase 9d.7, Task C): it is the same closed vocabulary <see cref="ReportDto.Reason"/>
/// documents on the response side, and the request side was simply missed by the original sweep.
/// </summary>
public sealed record CreateReportRequest(
    Guid ReportedUserId, Guid? GameId,
    [property: StringEnum(typeof(ReportReason))] string Reason,
    string? Details);

/// <summary>A report as returned to its own reporter. There is no DTO for reports filed against you.</summary>
public sealed record ReportDto(
    Guid Id, Guid ReportedUserId, Guid? GameId,
    [property: StringEnum(typeof(ReportReason))] string Reason,
    string? Details,
    [property: StringEnum(typeof(ReportStatus))] string Status,
    DateTimeOffset CreatedAt);
