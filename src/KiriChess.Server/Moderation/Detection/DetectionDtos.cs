using KiriChess.Server.Infrastructure.OpenApi;

namespace KiriChess.Server.Moderation.Detection;

/// <summary>
/// One measured signal inside a detection report (Phase 8.5). <see cref="Evidence"/> exists so a
/// moderator reads the working rather than a bare number: an unvalidated heuristic that cannot show
/// what it saw is not evidence, it is an accusation.
/// </summary>
public sealed record DetectionSignalDto(
    string Name, string Label, double Value, double Contribution, string Evidence);

/// <summary>
/// A detector's advisory verdict on one account (Phase 8.5). Advisory is the whole contract: nothing
/// in this codebase reads a DetectionReportDto and acts on it - see Phase 8.5
/// scope decision 1. Task A defines the shape; Tasks B and C fill it in.
/// </summary>
public sealed record DetectionReportDto(
    Guid UserId, string DisplayName, string Detector, string? Pool,
    // DetectionVerdict is a static class of const strings, not an enum (see its doc comment for why),
    // so [StringEnum] cannot describe it; [StringConstants] is that attribute's sibling for exactly
    // this shape (Phase 9d.7, Task C).
    [property: StringConstants(typeof(DetectionVerdict))] string Verdict,
    double Score, int SampleSize,
    DetectionSignalDto[] Signals, string[] Caveats, DateTimeOffset ComputedAt);

/// <summary>
/// The shared verdict vocabulary. Insufficient is not a clean bill of health - it means the detector
/// had too little to say anything at all, which is a different claim from "nothing stands out".
/// </summary>
public static class DetectionVerdict
{
    public const string Insufficient = "Insufficient";
    public const string Unremarkable = "Unremarkable";
    public const string Watch = "Watch";
    public const string Elevated = "Elevated";

    /// <summary>
    /// Score to verdict. The only place these two cutoffs are written down. Both are unvalidated
    /// guesses; what would tune them is a labelled set of real cheaters, which does not exist yet.
    /// </summary>
    public static string For(double score) =>
        score >= 0.70 ? Elevated : score >= 0.40 ? Watch : Unremarkable;
}
