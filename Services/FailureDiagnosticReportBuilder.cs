using System.Globalization;
using System.Text;
using MediaFlux.Models;

namespace MediaFlux.Services;

/// <summary>Immutable, observational input to the failure-report formatter.</summary>
public sealed record FailureDiagnosticReportContext(
    string Operation, string SourcePath, string OutputPath, int? FfmpegExitCode,
    string TerminalFailure, FfmpegDiagnosticSummary? Diagnostics, string RawCapturedStandardError,
    EncodingPlan? Plan = null, EncodingExecutionOutcome? Execution = null,
    string? JobId = null, string? SourceContainer = null, long? SourceSizeBytes = null,
    IReadOnlyList<FfmpegAttemptDiagnostic>? Attempts = null);

/// <summary>Bounded, attempt-scoped FFmpeg evidence retained for failure reporting.</summary>
public sealed record FfmpegAttemptDiagnostic(
    int Attempt, string Name, string Command, int ExitCode, string StandardError,
    FfmpegDiagnosticSummary? Diagnostics, bool IsTerminal = false);

public sealed record FailureDiagnosticReportArtifact(string ReportPath, string RawEvidencePath);

/// <summary>Formats already-authoritative encode facts; it never participates in encode decisions.</summary>
public sealed class FailureDiagnosticReportBuilder
{
    private const int MaxFamilies = 16, MaxSamplesPerFamily = 3, MaxTerminalLines = 20, MaxRawLineCharacters = 1000, MaxReportCharacters = 96 * 1024;

    public string Build(FailureDiagnosticReportContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var report = new StringBuilder(12 * 1024);
        Header(report);
        Section(report, "FAILURE SUMMARY");
        Line(report, "Job", Value(context.Operation));
        Line(report, "Result", "FAILED");
        Line(report, "Failed stage", Value(context.TerminalFailure));
        Line(report, "FFmpeg exit", context.FfmpegExitCode?.ToString(CultureInfo.InvariantCulture) ?? "Not available");
        if (context.Diagnostics is { } diagnostic)
        {
            Line(report, "Diagnostic interpretation", DiagnosticCause(diagnostic));
            Line(report, "Confidence", diagnostic.Classification.Confidence.ToString());
        }

        Section(report, "JOB / SOURCE INFORMATION");
        Line(report, "Job ID", Value(context.JobId));
        Line(report, "Source", Value(context.SourcePath));
        Line(report, "Destination", Value(context.OutputPath));
        Line(report, "Source container", Value(context.SourceContainer));
        Line(report, "Source size", context.SourceSizeBytes is > 0 ? $"{context.SourceSizeBytes.Value:N0} bytes" : "Not available");
        RenderPlan(report, context.Plan);
        RenderTimeline(report, context.Execution);
        RenderAttempts(report, context.Attempts);
        RenderClassification(report, context.Diagnostics);
        RenderDiagnosticSummary(report, context.Diagnostics);
        RenderEvidence(report, context.Diagnostics);
        RenderTerminalContext(report, context.RawCapturedStandardError);
        RenderValidation(report, context.Execution);
        RenderStatistics(report, context.Diagnostics, context.RawCapturedStandardError);
        report.AppendLine(new string('=', 60));
        if (report.Length <= MaxReportCharacters)
            return report.ToString();

        int cut = report.ToString().LastIndexOf('\n', MaxReportCharacters - 1);
        return (cut > 0 ? report.ToString(0, cut + 1) : report.ToString(0, MaxReportCharacters)) +
               "[Curated report reached its 96 KiB bound; remaining content was omitted. Raw captured stderr remains available in the companion artifact.]" + Environment.NewLine;
    }

    private static void Header(StringBuilder report)
    {
        report.AppendLine(new string('=', 60));
        report.AppendLine("MEDIAFLUX FAILURE DIAGNOSTIC REPORT");
        report.AppendLine(new string('=', 60));
    }
    private static void Section(StringBuilder report, string name) { report.AppendLine(); report.AppendLine(name); report.AppendLine(new string('-', name.Length)); }
    private static void Line(StringBuilder report, string label, string value) => report.Append(label.PadRight(22)).Append(": ").AppendLine(value);
    private static string Value(string? value) => string.IsNullOrWhiteSpace(value) ? "Not available" : value;

    private static void RenderPlan(StringBuilder report, EncodingPlan? plan)
    {
        Section(report, "ENCODING PLAN");
        if (plan is not { IsAvailable: true }) { report.AppendLine("Plan: Not available."); return; }
        if (plan.Container is { } container) Line(report, "Output container", $"{container.Effective} ({container.Reason})");
        if (plan.Video is { } video)
        {
            Line(report, "Video", $"{video.Action}: {plan.Source?.Codec ?? "unknown"} -> {video.Codec}; encoder={video.Encoder}");
            Line(report, "Geometry", video.EffectiveWidth is > 0 && video.EffectiveHeight is > 0 ? $"{video.EffectiveWidth}x{video.EffectiveHeight}" : "Not available");
        }
        if (plan.Hardware is { } hardware) Line(report, "Hardware", hardware.UseGpu ? $"GPU ({hardware.EncoderId})" : "CPU");
        if (plan.Quality is { } quality) Line(report, "Quality", $"intent={quality.Intent}; effective={quality.EffectiveQuality?.ToString(CultureInfo.InvariantCulture) ?? "Not available"}; mechanism={quality.Mechanism}");
        if (plan.Estimates.EstimatedOutputSizeMb is { } target) Line(report, "Target size", $"{target:0.###} MB");
        RenderStreams(report, "Audio", plan.Audio);
        RenderStreams(report, "Subtitles", plan.Subtitles);
        if (plan.SourceHealth is { } health) Line(report, "Source assessment", $"{health.Type}: {health.Reason}");
    }
    private static void RenderStreams(StringBuilder report, string label, IReadOnlyList<EncodingPlanStream> streams)
    {
        if (streams.Count == 0) { Line(report, label, "None"); return; }
        Line(report, label, string.Join("; ", streams.Take(6).Select(x => $"#{x.StreamIndex} {x.Codec}: {x.Action}{(string.IsNullOrWhiteSpace(x.TargetCodec) ? "" : " -> " + x.TargetCodec)}")) + (streams.Count > 6 ? $"; {streams.Count - 6} additional stream(s)" : ""));
    }
    private static void RenderTimeline(StringBuilder report, EncodingExecutionOutcome? execution)
    {
        Section(report, "EXECUTION / RECOVERY TIMELINE");
        if (execution is null) { report.AppendLine("Authoritative execution outcome: Not available."); return; }
        foreach (EncodingPreflightOutcome item in execution.Preflight) report.AppendLine($"Preflight {item.Kind}: {item.Status}{(string.IsNullOrWhiteSpace(item.Detail) ? "" : " — " + item.Detail)}");
        foreach (EncodingRecoveryOutcome item in execution.Recovery) report.AppendLine($"Recovery attempt {item.Attempt}/{item.MaximumAttempts}: {item.Kind}/{item.RecoveryMode}; process={item.ProcessResult}; media={item.MediaDisposition}; result={item.Result}{(string.IsNullOrWhiteSpace(item.Detail) ? "" : " — " + item.Detail)}");
        report.AppendLine($"Terminal result: {execution.TerminalResult}");
    }
    private static void RenderAttempts(StringBuilder report, IReadOnlyList<FfmpegAttemptDiagnostic>? attempts)
    {
        if (attempts is not { Count: > 0 })
            return;

        Section(report, "FFMPEG ATTEMPTS");
        foreach (FfmpegAttemptDiagnostic attempt in attempts.OrderBy(x => x.Attempt))
        {
            report.AppendLine($"Attempt {attempt.Attempt}: {Value(attempt.Name)}{(attempt.IsTerminal ? " [terminal]" : "")}");
            Line(report, "Command", Value(attempt.Command));
            Line(report, "Exit", attempt.ExitCode.ToString(CultureInfo.InvariantCulture));
            if (attempt.Diagnostics is { } diagnostics)
                Line(report, "Interpretation", DiagnosticCause(diagnostics));

            string[] lines = attempt.StandardError.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries)
                .TakeLast(MaxTerminalLines).ToArray();
            report.AppendLine(lines.Length == 0
                ? "Captured stderr: none"
                : $"Captured stderr (last {lines.Length} retained line(s)):");
            foreach (string line in lines)
                report.AppendLine("  " + LimitLine(line));
        }
    }
    private static void RenderClassification(StringBuilder report, FfmpegDiagnosticSummary? diagnostics)
    {
        Section(report, "DIAGNOSTIC CLASSIFICATION");
        if (diagnostics is null) { report.AppendLine("Structured diagnostic interpretation: Not available."); return; }
        Line(report, "Primary category", diagnostics.Classification.PrimaryCategory.ToString());
        Line(report, "Probable cause", diagnostics.Classification.ProbableCause);
        Line(report, "Confidence", diagnostics.Classification.Confidence.ToString());
        Line(report, "Supporting families", diagnostics.Classification.SupportingFamilies.Count == 0 ? "None" : string.Join(", ", diagnostics.Classification.SupportingFamilies));
        if (HasCompetingTerminalEvidence(diagnostics))
            report.AppendLine("Terminal-cause note: source anomalies and a separate output/hardware failure were both observed; this interpretation does not establish which caused process termination.");
    }
    private static string DiagnosticCause(FfmpegDiagnosticSummary diagnostics) =>
        HasCompetingTerminalEvidence(diagnostics)
            ? diagnostics.Classification.ProbableCause + " Source anomalies were observed, but a separate output or hardware failure prevents assigning the terminal cause from diagnostics alone."
            : diagnostics.Classification.ProbableCause;

    private static bool HasCompetingTerminalEvidence(FfmpegDiagnosticSummary diagnostics) =>
        (diagnostics.Classification.PrimaryCategory is FfmpegDiagnosticCategory.SourceDecode or FfmpegDiagnosticCategory.SourceIntegrity) &&
        diagnostics.Families.Any(f => f.Category is FfmpegDiagnosticCategory.DiskIo or FfmpegDiagnosticCategory.PermissionAccess or FfmpegDiagnosticCategory.HardwareAcceleration);
    private static void RenderDiagnosticSummary(StringBuilder report, FfmpegDiagnosticSummary? diagnostics)
    {
        Section(report, "DIAGNOSTIC SUMMARY");
        if (diagnostics is null || diagnostics.Families.Count == 0) { report.AppendLine("No structured diagnostic families were retained."); return; }
        foreach (FfmpegDiagnosticFamilySummary family in diagnostics.Families.Take(MaxFamilies))
        {
            string ranges = family.ValueRanges.Count == 0 ? "" : "; " + string.Join(", ", family.ValueRanges.Select(x => $"{x.Key}={x.Value.Minimum}–{x.Value.Maximum}"));
            report.AppendLine($"{family.Family}: {family.Occurrences:N0} occurrence(s); category={family.Category}; severity={family.Severity}; first={family.FirstOccurrence:O}; last={family.LastOccurrence:O}; samples={family.RepresentativeRawMessages.Count}{ranges}");
        }
        if (diagnostics.Families.Count > MaxFamilies) report.AppendLine($"{diagnostics.Families.Count - MaxFamilies} additional diagnostic family/families summarized but not rendered.");
        if (diagnostics.DroppedNewFamilies > 0) report.AppendLine($"Fingerprint aggregation overflow: {diagnostics.DroppedNewFamilies:N0} new family/families were not retained.");
    }
    private static void RenderEvidence(StringBuilder report, FfmpegDiagnosticSummary? diagnostics)
    {
        Section(report, "REPRESENTATIVE FFMPEG EVIDENCE");
        if (diagnostics is null || diagnostics.Families.Count == 0) { report.AppendLine("No representative FFmpeg evidence is available."); return; }
        foreach (FfmpegDiagnosticFamilySummary family in diagnostics.Families.Take(MaxFamilies))
        {
            report.AppendLine($"{family.Family} ({family.Occurrences:N0} occurrence(s)):");
            foreach (string sample in family.RepresentativeRawMessages.Take(MaxSamplesPerFamily)) report.AppendLine("  " + LimitLine(sample));
            long summarized = Math.Max(0, family.Occurrences - Math.Min(family.Occurrences, MaxSamplesPerFamily));
            if (summarized > 0) report.AppendLine($"  {summarized:N0} similar occurrence(s) summarized.");
        }
    }
    private static void RenderTerminalContext(StringBuilder report, string raw)
    {
        Section(report, "TERMINAL FAILURE CONTEXT");
        string[] lines = raw.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries).TakeLast(MaxTerminalLines).ToArray();
        if (lines.Length == 0) { report.AppendLine("No retained raw stderr lines are available."); return; }
        report.AppendLine($"Last {lines.Length} retained raw stderr line(s), in original order:");
        foreach (string line in lines) report.AppendLine(LimitLine(line));
    }
    private static void RenderValidation(StringBuilder report, EncodingExecutionOutcome? execution)
    {
        Section(report, "VALIDATION / FINALIZATION");
        if (execution?.Validation is { } validation) Line(report, "Validation", $"{validation.Status}; {Value(validation.Detail)}"); else Line(report, "Validation", "Not run or not available");
        if (execution?.Finalization is { } finalization) { Line(report, "Finalization", $"{finalization.Status}; {Value(finalization.Detail)}"); Line(report, "Output disposition", finalization.StagedOutputAccepted == true ? "Accepted" : "Rejected or not accepted"); } else Line(report, "Finalization", "Not run or not available");
    }
    private static void RenderStatistics(StringBuilder report, FfmpegDiagnosticSummary? diagnostics, string raw)
    {
        Section(report, "LOG STATISTICS");
        long samples = diagnostics?.Families.Sum(x => x.RepresentativeRawMessages.Count) ?? 0;
        Line(report, "Diagnostic occurrences", diagnostics?.TotalEvents.ToString("N0", CultureInfo.InvariantCulture) ?? "Not available");
        Line(report, "Diagnostic families", diagnostics?.Families.Count.ToString(CultureInfo.InvariantCulture) ?? "Not available");
        Line(report, "Representative messages", samples.ToString("N0", CultureInfo.InvariantCulture));
        Line(report, "Unknown occurrences", diagnostics?.Families.Where(x => x.Category == FfmpegDiagnosticCategory.Unknown).Sum(x => x.Occurrences).ToString("N0", CultureInfo.InvariantCulture) ?? "0");
        Line(report, "Terminal context lines", raw.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries).TakeLast(MaxTerminalLines).Count().ToString(CultureInfo.InvariantCulture));
        Line(report, "Raw evidence", "Bounded raw captured stderr; the existing process capture limit remains 512 KiB per stream.");
        if (diagnostics?.AnalysisHadErrors == true) Line(report, "Diagnostic parser", "One or more diagnostic lines could not be analyzed; raw evidence was retained.");
    }
    private static string LimitLine(string line) => line.Length <= MaxRawLineCharacters ? line : line[..MaxRawLineCharacters] + " [line truncated in curated report]";
}
