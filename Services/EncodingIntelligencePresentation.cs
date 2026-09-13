using MediaFlux.Models;

namespace MediaFlux.Services;

/// <summary>Formats existing planning and execution facts for concise user-facing UI surfaces.</summary>
public static class EncodingIntelligencePresentation
{
    public readonly record struct PresentationKey(Guid PlanId, string Lifecycle);

    public sealed record Model(
        IReadOnlyList<EncodingPlanItem> Summary,
        IReadOnlyList<EncodingPlanItem> Reasons,
        IReadOnlyList<EncodingPlanItem> Technical);

    public static Model Create(EncodingPlan plan, EncodingExecutionOutcome? outcome = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.IsAvailable)
            return new([new("Status", string.IsNullOrWhiteSpace(plan.UnavailableReason) ? "Not available" : plan.UnavailableReason)], [], []);

        var summary = new List<EncodingPlanItem>
        {
            new("Planned output", PlannedOutput(plan)),
            new("Expected result", ExpectedResult(plan)),
            new("Source health", RiskSummary(plan.Risks)),
            new("Recovery", RecoverySummary(plan, outcome)),
            new("Lifecycle", LifecycleSummary(outcome))
        };
        if (plan.Estimates.EstimatedOutputSizeMb is > 0)
            summary.Insert(1, new("Target", FormatSize(plan.Estimates.EstimatedOutputSizeMb.Value)));

        EncodingHistoricalPrediction? prediction = plan.Estimates.HistoricalPrediction;
        var technical = new List<EncodingPlanItem>();
        if (prediction?.IsAvailable == true)
            technical.Add(new("History match", $"Tier {prediction.MatchTier} · {prediction.SampleCount} comparable completed jobs"));
        if (outcome != null)
        {
            technical.Add(new("Terminal result", outcome.TerminalResult.ToString()));
            if (outcome.Validation != null)
                technical.Add(new("Validation", outcome.Validation.Status.ToString()));
            if (outcome.Finalization != null)
                technical.Add(new("Finalization", outcome.Finalization.Status.ToString()));
        }

        return new(summary,
            plan.DecisionReasons.Select(reason => new EncodingPlanItem("Why MediaFlux chose this", DescribeReason(reason))).ToArray(),
            technical);
    }

    public static PresentationKey GetKey(EncodingPlan plan, EncodingExecutionOutcome? outcome = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        string lifecycle = outcome == null
            ? "none"
            : string.Join("|",
                outcome.TerminalResult,
                string.Join(",", outcome.Preflight.Select(item => $"{item.Kind}:{item.Status}")),
                string.Join(",", outcome.Recovery.Select(item => $"{item.Kind}:{item.FailureClass}:{item.Result}:{item.Attempt}")),
                outcome.Validation?.Status,
                outcome.Validation?.OutputProbe,
                outcome.Finalization?.Status,
                outcome.Finalization?.StagedOutputAccepted);
        return new(plan.PlanId, lifecycle);
    }

    private static string PlannedOutput(EncodingPlan plan)
    {
        var lines = new List<string>();
        if (plan.Source is { } source && plan.Video is { } video)
        {
            string sourceResolution = Resolution(source.Width, source.Height);
            string outputResolution = Resolution(video.EffectiveWidth, video.EffectiveHeight);
            lines.Add($"Video: {DisplayCodec(source.Codec)} {sourceResolution} → {DisplayCodec(video.Codec)} {outputResolution}");
        }
        if (plan.Container is { } container)
            lines.Add(container.Configured == OutputContainerSelection.Auto
                ? $"Container: Auto → {container.Effective}"
                : $"Container: {container.Effective}");
        if (plan.Hardware is { } hardware && plan.Video is { } encoderVideo)
            lines.Add($"Encoder: {hardware.EncoderId} ({DisplayCodec(encoderVideo.Codec)}) · {(hardware.UseGpu ? "GPU" : "CPU")}");
        AddStreamSummary(lines, "Audio", plan.Audio);
        AddStreamSummary(lines, "Subtitles", plan.Subtitles);
        return lines.Count == 0 ? "Not available" : string.Join(Environment.NewLine, lines);
    }

    private static void AddStreamSummary(List<string> lines, string type, IReadOnlyList<EncodingPlanStream> streams)
    {
        if (streams.Count == 0)
            return;
        IEnumerable<IGrouping<StreamCompatibilityAction, EncodingPlanStream>> groups = streams.GroupBy(stream => stream.Action);
        string value = string.Join(", ", groups.Select(group => $"{Action(group.Key)} {group.Count()}"));
        lines.Add($"{type}: {value}");
    }

    private static string ExpectedResult(EncodingPlan plan)
    {
        EncodingHistoricalPrediction? prediction = plan.Estimates.HistoricalPrediction;
        if (prediction?.IsAvailable != true)
            return "Not enough comparable history yet";

        var values = new List<string>();
        if (prediction.SpeedLow is > 0 && prediction.SpeedHigh is > 0)
            values.Add($"Expected speed: {prediction.SpeedLow.Value:0.##}–{prediction.SpeedHigh.Value:0.##}×");
        else if (prediction.PredictedSpeedX is > 0)
            values.Add($"Expected speed: {prediction.PredictedSpeedX.Value:0.##}×");
        if (prediction.DurationLow is { } durationLow && prediction.DurationHigh is { } durationHigh)
            values.Add($"Expected duration: {Duration(durationLow)}–{Duration(durationHigh)}");
        if (prediction.OutputSizeLowMb is > 0 && prediction.OutputSizeHighMb is > 0)
            values.Add($"Historical size: {FormatSize(prediction.OutputSizeLowMb.Value)}–{FormatSize(prediction.OutputSizeHighMb.Value)}");
        if (prediction.PredictedCompressionRatio is > 0 and < 1)
            values.Add($"About {(1 - prediction.PredictedCompressionRatio.Value) * 100:0.#}% smaller");
        values.Add($"{prediction.Confidence} confidence · {prediction.SampleCount} comparable jobs");
        return string.Join(Environment.NewLine, values);
    }

    private static string RiskSummary(IReadOnlyList<EncodingRisk> risks)
    {
        EncodingRisk[] warnings = risks.Where(risk => risk.Severity == EncodingRiskSeverity.Warning).ToArray();
        return warnings.Length == 0
            ? "No known source issues detected"
            : warnings.Length == 1 ? $"1 warning: {warnings[0].Description}" : $"{warnings.Length} warnings";
    }

    private static string RecoverySummary(EncodingPlan plan, EncodingExecutionOutcome? outcome)
    {
        EncodingRecoveryOutcome? recovered = outcome?.Recovery.FirstOrDefault(item => item.Result == EncodingRecoveryResult.Succeeded);
        if (recovered != null)
            return $"Recovered from {Failure(recovered.FailureClass)}";
        EncodingRecoveryCapability? video = plan.RecoveryCapabilities?.Items.FirstOrDefault(item => item.Kind == EncodingRecoveryKind.VideoDecode);
        return video?.Permitted == true
            ? "Intelligent video decode recovery available"
            : "Standard strict decode; automatic tolerant recovery disabled";
    }

    private static string LifecycleSummary(EncodingExecutionOutcome? outcome)
    {
        if (outcome == null)
            return "Encode not started";
        if (outcome.TerminalResult == EncodingTerminalResult.NotRun)
        {
            if (outcome.Recovery.Count > 0)
                return "Recovery attempted";
            if (outcome.Finalization != null)
                return "Finalizing output";
            if (outcome.Validation != null)
                return outcome.Validation.Status == EncodingLifecycleStatus.Passed ? "Validating output" : "Validation in progress";
            return outcome.Preflight.Count > 0 && outcome.Preflight.All(item => item.Status is EncodingPreflightStatus.Passed or EncodingPreflightStatus.Skipped)
                ? "Preflight passed"
                : "Encoding";
        }
        return outcome.TerminalResult switch
        {
        EncodingTerminalResult.Completed => "Completed · validation passed · finalization succeeded",
        EncodingTerminalResult.CompletedAfterRecovery => "Completed after recovery · validation passed · finalization succeeded",
        EncodingTerminalResult.ValidationFailed => "Validation failed",
        EncodingTerminalResult.FinalizationFailed => "Finalization failed",
        EncodingTerminalResult.PreflightRejected => "Preflight rejected",
        EncodingTerminalResult.Canceled => "Canceled",
        _ => outcome.TerminalResult.ToString()
        };
    }

    private static string DescribeReason(EncodingDecisionReason reason) => reason.Code switch
    {
        EncodingDecisionReasonCode.ContainerAutoResolved => $"Container resolved automatically: {reason.Description}",
        EncodingDecisionReasonCode.CompatibleAudioPassthrough => "Audio will be copied because it is compatible with the selected container.",
        EncodingDecisionReasonCode.AudioConversionRequired => "Audio will be converted for container compatibility.",
        EncodingDecisionReasonCode.SubtitleConversionRequired => "Subtitles need a compatible conversion or omission.",
        EncodingDecisionReasonCode.GeometryNormalized => $"Output dimensions were normalized for codec compatibility: {reason.Description}",
        EncodingDecisionReasonCode.HardwareEncoderSelected => "The configured video profile selected hardware encoding.",
        EncodingDecisionReasonCode.TargetSizeBudget => "The configured target size is the authoritative output-size budget.",
        EncodingDecisionReasonCode.IntelligentRecoveryAvailable => "Intelligent recovery is available only for corroborated source-video failures.",
        _ => string.IsNullOrWhiteSpace(reason.Description)
            ? "A configured planning decision applies."
            : reason.Description
    };

    private static string Action(StreamCompatibilityAction action) => action switch
    {
        StreamCompatibilityAction.Copy => "copy",
        StreamCompatibilityAction.Transcode => "convert",
        StreamCompatibilityAction.Omit => "omit",
        _ => "process"
    };
    private static string Failure(EncodingRecoveryFailureClass failure) => failure switch
    {
        EncodingRecoveryFailureClass.SourceVideoCorruption => "localized source video corruption",
        EncodingRecoveryFailureClass.SourceAudioCorruption => "source audio corruption",
        _ => failure.ToString()
    };
    private static string Resolution(int? width, int? height) => width is > 0 && height is > 0 ? $"{width}×{height}" : "unknown resolution";
    private static string Duration(TimeSpan value) => value.TotalHours >= 1 ? value.ToString("h\\:mm") + " hr" : value.TotalMinutes >= 1 ? $"{Math.Round(value.TotalMinutes):0} min" : $"{Math.Round(value.TotalSeconds):0} sec";
    private static string FormatSize(double megabytes) => megabytes >= 1024 ? $"{megabytes / 1024d:0.##} GB" : $"{megabytes:0.#} MB";
    private static string DisplayCodec(string value) => value.Trim().ToLowerInvariant() switch { "h264" or "libx264" => "H.264", "hevc" or "h265" or "libx265" or "hevc_nvenc" => "HEVC", _ => value };
}
