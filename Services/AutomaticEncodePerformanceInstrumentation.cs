using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using MediaFlux.Models;

namespace MediaFlux.Services;

public enum AutomaticPerformanceProcessTool { Ffmpeg, Ffprobe, Other }

public enum AutomaticPerformanceProcessPurpose
{
    AdaptiveSampleFfmpeg,
    SourceMetadata,
    SourceTiming,
    AdaptiveSampleMetadata,
    AdaptiveSamplePacketAccounting,
    OutputValidation,
    ProductionFfmpeg,
    Other
}

public sealed record AutomaticEncodePerformanceStage(
    string Name,
    double? DurationSeconds,
    string Status,
    double? StartOffsetSeconds = null,
    double? EndOffsetSeconds = null);

public sealed record AutomaticEncodeProcessCount(
    AutomaticPerformanceProcessTool Tool,
    AutomaticPerformanceProcessPurpose Purpose,
    int Count,
    double? TotalLaunchSeconds,
    int LaunchTimingsAvailable);

public sealed record AutomaticEncodePreviewTiming(
    bool? Active,
    bool? CancellationRequested,
    double? DrainSeconds,
    string Status);

public sealed record AutomaticEncodeSampleTiming(
    int CandidateQuality,
    int Ordinal,
    string Label,
    double SourceStartSeconds,
    double RequestedDurationSeconds,
    double? MeasuredMediaSeconds,
    double? FfmpegProcessLaunchSeconds,
    double? FfmpegEncodeSeconds,
    double? MetadataProbeSeconds,
    double? PacketByteScanSeconds,
    double TotalWallSeconds,
    long? VideoPayloadBytes,
    double? MeasuredBytesPerSecond,
    int FfmpegProcessCount,
    int FfprobeMetadataProcessCount,
    int FfprobePacketAccountingProcessCount,
    double? SourceVideoStartOffsetSeconds = null,
    double? EffectiveInputStartSeconds = null,
    double? ExpectedAvailableDurationSeconds = null,
    double? OutputFrameRate = null,
    double? DurationToleranceSeconds = null,
    bool? DurationValidationPassed = null);

public sealed record AutomaticEncodeCandidateTiming(
    int Quality,
    double? DurationSeconds,
    double? StartOffsetSeconds,
    double? EndOffsetSeconds,
    AdaptiveSampleClassification? Classification,
    double? ProjectedLowerBytes,
    double? ProjectedTypicalBytes,
    double? ProjectedUpperBytes,
    long? MaximumAcceptedOutputBytes,
    int EvidenceSampleCount,
    int AttemptedSampleCount);

/// <summary>Transient headless diagnostics. This model is never written to configuration or plan identity.</summary>
public sealed record AutomaticEncodePerformanceDiagnostics(
    int SchemaVersion,
    string OperationId,
    string Mode,
    string SourcePath,
    DateTime StartedUtc,
    DateTime EndedUtc,
    double TotalWallSeconds,
    double? AdaptiveSelectionSeconds,
    double? SelectorExecutionSeconds,
    double? AdaptiveOverheadPercent,
    int CandidateCount,
    int SampleCount,
    int EvidenceSampleCount,
    double TotalMeasuredSampleMediaSeconds,
    string? AdaptiveDisposition,
    string? QualityMechanism,
    int? SelectedQuality,
    long? MaximumAcceptedOutputBytes,
    long? ActualOutputBytes,
    double? ProjectedTypicalOutputBytes,
    double? ProjectedTypicalOutputErrorPercent,
    bool? StorageContractAccepted,
    double? ProductionFfmpegProcessSeconds,
    double? ProductionFfmpegStartupSeconds,
    int ProductionFfmpegProcessCount,
    double? ProductionProcessDrainSeconds,
    double? TimeToProductionFfmpegStartSeconds,
    double? TimeFromAdaptiveSelectionToProductionStartSeconds,
    double? SnapshotPreparationSeconds,
    double? HistoryStatisticsRecordingSeconds,
    string TerminalResult,
    AutomaticEncodePreviewTiming Preview,
    IReadOnlyList<AutomaticEncodePerformanceStage> Stages,
    IReadOnlyList<AutomaticEncodeCandidateTiming> Candidates,
    IReadOnlyList<AutomaticEncodeSampleTiming> Samples,
    IReadOnlyList<AutomaticEncodeProcessCount> ProcessCounts,
    IReadOnlyList<string> Limitations)
{
    public bool SevenFirstEarlyPositive { get; init; }
}

/// <summary>Low-overhead, per-headless-job timing collection using monotonic elapsed time.</summary>
internal sealed class AutomaticEncodePerformanceInstrumentation
{
    private static readonly AsyncLocal<AmbientState?> Ambient = new();
    private readonly object _gate = new();
    private readonly long _startedAt = Stopwatch.GetTimestamp();
    private readonly DateTime _startedUtc;
    private string _mode;
    private readonly string _sourcePath;
    private readonly Dictionary<string, List<TimingEntry>> _stages = new(StringComparer.Ordinal);
    private readonly Dictionary<(AutomaticPerformanceProcessTool Tool, AutomaticPerformanceProcessPurpose Purpose), ProcessAggregate> _processes = new();
    private readonly Dictionary<int, int> _sampleOrdinals = new();
    private readonly List<SampleRecord> _samples = [];
    private readonly List<CandidateWindow> _candidateWindows = [];
    private readonly List<double> _productionEncodeSeconds = [];
    private readonly List<double> _productionDrainSeconds = [];
    private readonly List<string> _limitations = [];
    private string _operationId = "";
    private string _terminalResult = "Unknown";
    private long? _snapshotPreparationStartedAt;
    private long? _historyRecordingStartedAt;
    private long? _adaptiveStartedAt;
    private long? _adaptiveEndedAt;
    private long? _activeCandidateStartedAt;
    private int? _activeCandidateQuality;
    private AdaptiveQualitySelectionEvidence? _selection;
    private long? _maximumAcceptedOutputBytes;
    private int? _resolvedQuality;
    private string? _qualityMechanism;
    private PreviewRecord? _preview;
    private long? _firstProductionFfmpegStartedAt;
    private long? _activeProductionFfmpegStartedAt;
    private long? _activeProductionFfmpegExitedAt;
    private long? _productionStdoutCompletedAt;
    private long? _productionStderrCompletedAt;
    private int _productionFfmpegProcessCount;

    public AutomaticEncodePerformanceInstrumentation(string mode, string sourcePath, DateTime startedUtc)
    {
        _mode = string.IsNullOrWhiteSpace(mode) ? "Unknown" : mode;
        _sourcePath = sourcePath ?? "";
        _startedUtc = startedUtc.ToUniversalTime();
    }

    public static AutomaticEncodePerformanceInstrumentation? Current => Ambient.Value?.Owner;

    public IDisposable Activate()
    {
        AmbientState? previous = Ambient.Value;
        Ambient.Value = new(this, null, null, null);
        return new RestoreAmbient(previous);
    }

    public void SetOperationId(string? operationId)
    {
        lock (_gate) _operationId = operationId ?? "";
    }

    public void SetMode(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode)) return;
        lock (_gate) _mode = mode;
    }

    public void RecordQualityResolution(EncodingQualityResolution resolution)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        lock (_gate)
        {
            _resolvedQuality = resolution.EffectiveQuality;
            _qualityMechanism = resolution.Mechanism.ToString();
        }
    }

    public void BeginSnapshotPreparation() => _snapshotPreparationStartedAt = Stopwatch.GetTimestamp();

    public void EndSnapshotPreparation()
    {
        long? startedAt;
        lock (_gate)
        {
            startedAt = _snapshotPreparationStartedAt;
            _snapshotPreparationStartedAt = null;
        }
        if (startedAt is long value)
            RecordNamedStage("snapshotPreparation", value, Stopwatch.GetElapsedTime(value), completed: true);
    }

    public void BeginHistoryStatisticsRecording() => _historyRecordingStartedAt = Stopwatch.GetTimestamp();

    public void EndHistoryStatisticsRecording(bool completed = true)
    {
        long? startedAt;
        lock (_gate)
        {
            startedAt = _historyRecordingStartedAt;
            _historyRecordingStartedAt = null;
        }
        if (startedAt is long value)
            RecordNamedStage("historyStatisticsRecording", value, Stopwatch.GetElapsedTime(value), completed);
    }

    public void ObserveProgress(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        long now = Stopwatch.GetTimestamp();
        lock (_gate)
        {
            if (line.StartsWith("Analyzing compression", StringComparison.OrdinalIgnoreCase))
            {
                _adaptiveStartedAt ??= now;
                return;
            }

            if (!line.StartsWith("Testing compression", StringComparison.OrdinalIgnoreCase)) return;
            int? quality = ParseQuality(line);
            CloseActiveCandidate(now);
            _activeCandidateQuality = quality;
            _activeCandidateStartedAt = now;
            if (_adaptiveStartedAt is null) _adaptiveStartedAt = now;
        }
    }

    public void RecordAdaptiveSelection(AdaptiveQualitySelectionEvidence evidence, long? maximumAcceptedOutputBytes)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        long now = Stopwatch.GetTimestamp();
        lock (_gate)
        {
            _selection = evidence;
            _maximumAcceptedOutputBytes = maximumAcceptedOutputBytes;
            _qualityMechanism = evidence.Mechanism.ToString();
            _adaptiveStartedAt ??= now;
            CloseActiveCandidate(now);
            _adaptiveEndedAt = now;
        }
    }

    public IDisposable? MeasurePlanFreeze()
    {
        if (!ReferenceEquals(Current, this)) return null;
        return new NamedStageScope(this, "planFreeze", Stopwatch.GetTimestamp());
    }

    public SampleScope? BeginAdaptiveSample(int quality, RepresentativeSample sample, long? startedAt = null)
    {
        ArgumentNullException.ThrowIfNull(sample);
        if (!ReferenceEquals(Current, this)) return null;
        int ordinal;
        lock (_gate)
        {
            ordinal = _sampleOrdinals.GetValueOrDefault(quality) + 1;
            _sampleOrdinals[quality] = ordinal;
        }
        return new SampleScope(this, quality, ordinal, sample, startedAt ?? Stopwatch.GetTimestamp());
    }

    public void RecordAdaptiveSampleSeekMapping(double? sourceVideoStartOffsetSeconds, TimeSpan effectiveInputStart)
    {
        AmbientState? state = Ambient.Value;
        if (state?.Owner == this && state.Sample is { } sample)
            sample.RecordSeekMapping(sourceVideoStartOffsetSeconds, effectiveInputStart.TotalSeconds);
    }

    internal IDisposable? EnterPerformanceStage(PerformanceTimingStage stage)
    {
        if (!ReferenceEquals(Current, this)) return null;
        AmbientState? previous = Ambient.Value;
        Ambient.Value = new(this, previous?.Purpose, stage, previous?.Sample);
        return new RestoreAmbient(previous);
    }

    internal static IDisposable? EnterProcessPurpose(AutomaticPerformanceProcessPurpose purpose)
    {
        AmbientState? previous = Ambient.Value;
        if (previous is null) return null;
        Ambient.Value = previous with { Purpose = purpose };
        return new RestoreAmbient(previous);
    }

    internal void RecordPerformanceStage(PerformanceTimingStage stage, long startedAt, TimeSpan elapsed, bool completed)
    {
        RecordNamedStage(stage.ToString(), startedAt, elapsed, completed);
    }

    internal void RecordNamedStage(string name, long startedAt, TimeSpan elapsed, bool completed)
    {
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        double startOffset = Math.Max(0, Stopwatch.GetElapsedTime(_startedAt, startedAt).TotalSeconds);
        double endOffset = Math.Max(startOffset, startOffset + elapsed.TotalSeconds);
        lock (_gate)
        {
            if (!_stages.TryGetValue(name, out List<TimingEntry>? entries))
                _stages.Add(name, entries = []);
            entries.Add(new(elapsed.TotalSeconds, completed, startOffset, endOffset));
        }
    }

    internal void RecordPreviewDrain(bool active, bool cancellationRequested, long startedAt, bool completed)
    {
        double? elapsed = active
            ? Math.Max(0, Stopwatch.GetElapsedTime(startedAt).TotalSeconds)
            : null;
        lock (_gate) _preview = new(active, cancellationRequested, elapsed, !active ? "inactive" : completed ? "drained" : "interrupted");
        if (active)
            RecordNamedStage("previewDrain", startedAt, TimeSpan.FromSeconds(elapsed ?? 0), completed);
    }

    internal void RecordExternalProcessStart(
        string fileName,
        FfmpegDiagnosticComponent component,
        TimeSpan? launchElapsed,
        AutomaticPerformanceProcessPurpose? explicitPurpose = null)
    {
        AmbientState? state = Ambient.Value;
        if (state?.Owner != this) return;

        AutomaticPerformanceProcessPurpose purpose = explicitPurpose ?? state.Purpose ?? PurposeForStage(state.Stage, component);
        AutomaticPerformanceProcessTool tool = ToolFor(fileName, component, state.Stage, purpose);
        AddProcess(tool, purpose, launchElapsed, state.Sample);
    }

    internal void RecordProductionLifecycleEvent(EncodeLifecycleEvent lifecycleEvent)
    {
        long now = Stopwatch.GetTimestamp();
        lock (_gate)
        {
            switch (lifecycleEvent)
            {
                case EncodeLifecycleEvent.FfmpegStart:
                    _productionFfmpegProcessCount++;
                    _firstProductionFfmpegStartedAt ??= now;
                    _activeProductionFfmpegStartedAt = now;
                    _activeProductionFfmpegExitedAt = null;
                    _productionStdoutCompletedAt = null;
                    _productionStderrCompletedAt = null;
                    AddProcess(AutomaticPerformanceProcessTool.Ffmpeg,
                        AutomaticPerformanceProcessPurpose.ProductionFfmpeg, null, null);
                    break;
                case EncodeLifecycleEvent.FfmpegExit:
                    if (_activeProductionFfmpegStartedAt is long processStart && _activeProductionFfmpegExitedAt is null)
                    {
                        _activeProductionFfmpegExitedAt = now;
                        _productionEncodeSeconds.Add(Math.Max(0, Stopwatch.GetElapsedTime(processStart, now).TotalSeconds));
                        TryRecordProductionDrain();
                    }
                    break;
                case EncodeLifecycleEvent.StdoutComplete:
                    _productionStdoutCompletedAt = now;
                    TryRecordProductionDrain();
                    break;
                case EncodeLifecycleEvent.StderrComplete:
                    _productionStderrCompletedAt = now;
                    TryRecordProductionDrain();
                    break;
            }
        }
    }

    public void SetTerminalResult(string? terminalResult)
    {
        lock (_gate) _terminalResult = string.IsNullOrWhiteSpace(terminalResult) ? "Unknown" : terminalResult;
    }

    public AutomaticEncodePerformanceDiagnostics BuildReport(string? operationId = null, string? terminalResult = null)
    {
        long now = Stopwatch.GetTimestamp();
        DateTime endedUtc = DateTime.UtcNow;
        lock (_gate)
        {
            if (_activeCandidateStartedAt.HasValue)
                CloseActiveCandidate(now);
            if (!string.IsNullOrWhiteSpace(operationId)) _operationId = operationId;
            if (!string.IsNullOrWhiteSpace(terminalResult)) _terminalResult = terminalResult;

            double totalSeconds = Math.Max(0, Stopwatch.GetElapsedTime(_startedAt, now).TotalSeconds);
            double? adaptiveSeconds = _adaptiveStartedAt is long adaptiveStart
                ? Math.Max(0, Stopwatch.GetElapsedTime(adaptiveStart, _adaptiveEndedAt ?? now).TotalSeconds)
                : null;
            double? selectorSeconds = ValidNonnegative(_selection?.SamplingSeconds);
            var candidateByQuality = _selection?.Candidates
                .GroupBy(candidate => candidate.Quality)
                .ToDictionary(group => group.Key, group => group.Last()) ?? new Dictionary<int, AdaptiveCandidateEvidence>();
            int[] qualities = _candidateWindows.Select(window => window.Quality)
                .Concat(candidateByQuality.Keys).Distinct().ToArray();
            AutomaticEncodeCandidateTiming[] candidates = qualities.Select(quality =>
            {
                CandidateWindow? window = _candidateWindows.LastOrDefault(item => item.Quality == quality);
                candidateByQuality.TryGetValue(quality, out AdaptiveCandidateEvidence? evidence);
                return new AutomaticEncodeCandidateTiming(
                    quality,
                    window?.DurationSeconds,
                    window?.StartOffsetSeconds,
                    window?.EndOffsetSeconds,
                    evidence?.Classification,
                    ValidNonnegative(evidence?.ProjectedLowerBytes),
                    ValidNonnegative(evidence?.ProjectedTypicalBytes),
                    ValidNonnegative(evidence?.ProjectedUpperBytes),
                    _maximumAcceptedOutputBytes,
                    evidence?.Samples.Count ?? 0,
                    _samples.Count(sample => sample.Quality == quality));
            }).ToArray();

            AutomaticEncodeSampleTiming[] sampleTimings = _samples.Select(sample => sample.ToModel()).ToArray();
            int evidenceSampleCount = _selection?.Candidates.Sum(candidate => candidate.Samples.Count) ?? 0;
            double measuredMediaSeconds = _selection is not null
                ? _selection.Candidates.SelectMany(candidate => candidate.Samples).Sum(sample => Math.Max(0, sample.MeasuredSeconds))
                : _samples.Sum(sample => sample.MeasuredMediaSeconds ?? 0);
            IReadOnlyList<AutomaticEncodeProcessCount> processes = _processes
                .OrderBy(pair => pair.Key.Tool).ThenBy(pair => pair.Key.Purpose)
                .Select(pair => new AutomaticEncodeProcessCount(
                    pair.Key.Tool,
                    pair.Key.Purpose,
                    pair.Value.Count,
                    pair.Value.LaunchTimingCount == 0 ? null : pair.Value.LaunchSeconds,
                    pair.Value.LaunchTimingCount))
                .ToArray();
            IReadOnlyList<AutomaticEncodePerformanceStage> stages = BuildStages(now, totalSeconds, adaptiveSeconds);
            double? productionSeconds = _productionEncodeSeconds.Count > 0
                ? _productionEncodeSeconds.Sum()
                : StageSeconds("FinalEncode");
            double? drainSeconds = _productionDrainSeconds.Count > 0
                ? _productionDrainSeconds.Sum()
                : null;
            double? firstProductionOffset = _firstProductionFfmpegStartedAt is long firstStart
                ? Math.Max(0, Stopwatch.GetElapsedTime(_startedAt, firstStart).TotalSeconds)
                : null;
            double? afterAdaptiveToProduction = _adaptiveEndedAt is long adaptiveEnd && _firstProductionFfmpegStartedAt is long productionStart && productionStart >= adaptiveEnd
                ? Math.Max(0, Stopwatch.GetElapsedTime(adaptiveEnd, productionStart).TotalSeconds)
                : null;
            if (IsAutomaticMode(_mode))
                _limitations.Add("Production FFmpeg startup latency is combined with encode execution; the direct Process.Start boundary exposes no separate startup timer.");
            _limitations.Add("Storage-contract evaluation has no isolated timing boundary; it remains inside the measured validation/finalization stages.");

            double? overhead = adaptiveSeconds is { } adaptive && totalSeconds > 0
                ? Math.Clamp(adaptive / totalSeconds * 100d, 0, 100)
                : null;
            long? maximumAccepted = _maximumAcceptedOutputBytes;
            return new AutomaticEncodePerformanceDiagnostics(
                SchemaVersion: 1,
                OperationId: _operationId,
                Mode: _mode,
                SourcePath: _sourcePath,
                StartedUtc: _startedUtc,
                EndedUtc: endedUtc,
                TotalWallSeconds: totalSeconds,
                AdaptiveSelectionSeconds: adaptiveSeconds,
                SelectorExecutionSeconds: selectorSeconds,
                AdaptiveOverheadPercent: overhead,
                CandidateCount: candidates.Length,
                SampleCount: sampleTimings.Length,
                EvidenceSampleCount: evidenceSampleCount,
                TotalMeasuredSampleMediaSeconds: Math.Max(0, measuredMediaSeconds),
                AdaptiveDisposition: _selection?.Disposition.ToString(),
                QualityMechanism: _qualityMechanism,
                SelectedQuality: _selection is not null ? _selection.SelectedQuality : _resolvedQuality,
                MaximumAcceptedOutputBytes: maximumAccepted,
                ActualOutputBytes: null,
                ProjectedTypicalOutputBytes: null,
                ProjectedTypicalOutputErrorPercent: null,
                StorageContractAccepted: null,
                ProductionFfmpegProcessSeconds: productionSeconds,
                ProductionFfmpegStartupSeconds: null,
                ProductionFfmpegProcessCount: _productionFfmpegProcessCount,
                ProductionProcessDrainSeconds: drainSeconds,
                TimeToProductionFfmpegStartSeconds: firstProductionOffset,
                TimeFromAdaptiveSelectionToProductionStartSeconds: afterAdaptiveToProduction,
                SnapshotPreparationSeconds: StageSeconds("snapshotPreparation"),
                HistoryStatisticsRecordingSeconds: StageSeconds("historyStatisticsRecording"),
                TerminalResult: _terminalResult,
                Preview: _preview is null
                    ? new(null, null, null, "not-observed")
                    : new(_preview.Active, _preview.CancellationRequested, _preview.DrainSeconds, _preview.Status),
                Stages: stages,
                Candidates: candidates,
                Samples: sampleTimings,
                ProcessCounts: processes,
                Limitations: new ReadOnlyCollection<string>(_limitations.Distinct(StringComparer.Ordinal).ToArray()))
            {
                SevenFirstEarlyPositive = _selection?.SevenFirstEarlyPositive ?? false
            };
        }
    }

    public static string FormatHumanReadable(AutomaticEncodePerformanceDiagnostics diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        var text = new StringBuilder()
            .AppendLine(IsAutomaticMode(diagnostics.Mode)
                ? "Automatic Source Adaptive Performance"
                : "Manual Encode Performance")
            .AppendLine("-------------------------------------");
        AppendStage(text, diagnostics, "snapshotPreparation", "Snapshot/preflight");
        AppendStage(text, diagnostics, "SourceProbe", "Source probe");
        AppendStage(text, diagnostics, "SourceTimingAnalysis", "Source timing analysis");
        text.Append("Preview drain".PadRight(31)).AppendLine(diagnostics.Preview.DrainSeconds is { } drain
            ? Seconds(drain)
            : diagnostics.Preview.Status == "inactive" ? "not applicable" : "not measured");
        text.Append("Adaptive selection".PadRight(31)).AppendLine(diagnostics.AdaptiveSelectionSeconds is { } adaptive
            ? Seconds(adaptive)
            : "not applicable");
        foreach (AutomaticEncodeCandidateTiming candidate in diagnostics.Candidates)
        {
            text.Append("  Candidate ").Append(diagnostics.QualityMechanism?.Contains("Crf", StringComparison.OrdinalIgnoreCase) == true ? "CRF " : "CQ ")
                .Append(candidate.Quality.ToString(CultureInfo.InvariantCulture).PadRight(20))
                .AppendLine(candidate.DurationSeconds is { } duration ? Seconds(duration) : "partial / not measured");
            text.Append("    Samples".PadRight(33)).AppendLine(candidate.AttemptedSampleCount.ToString(CultureInfo.InvariantCulture));
            text.Append("    Media sampled".PadRight(33)).AppendLine(Seconds(diagnostics.Samples
                .Where(sample => sample.CandidateQuality == candidate.Quality)
                .Sum(sample => sample.MeasuredMediaSeconds ?? 0)));
            text.Append("    FFmpeg sample processes".PadRight(33)).AppendLine(SampleProcessCount(diagnostics, candidate.Quality, AutomaticPerformanceProcessPurpose.AdaptiveSampleFfmpeg));
            text.Append("    FFprobe metadata processes".PadRight(33)).AppendLine(SampleProcessCount(diagnostics, candidate.Quality, AutomaticPerformanceProcessPurpose.AdaptiveSampleMetadata));
            text.Append("    FFprobe packet processes".PadRight(33)).AppendLine(SampleProcessCount(diagnostics, candidate.Quality, AutomaticPerformanceProcessPurpose.AdaptiveSamplePacketAccounting));
            if (candidate.Classification is { } classification)
                text.Append("    Classification".PadRight(33)).AppendLine(classification.ToString());
            if (candidate.ProjectedTypicalBytes is { } typical)
                text.Append("    Projected typical / maximum".PadRight(33)).Append(FormatMebibytes(typical)).Append(" / ")
                    .AppendLine(candidate.MaximumAcceptedOutputBytes is { } maximum ? FormatMebibytes(maximum) : "not applicable");
        }
        AppendStage(text, diagnostics, "planFreeze", "Plan freeze");
        AppendStage(text, diagnostics, "FfmpegInitialization", "FFmpeg initialization");
        text.Append("Production FFmpeg".PadRight(31)).AppendLine(diagnostics.ProductionFfmpegProcessSeconds is { } encode
            ? Seconds(encode) + " (process + encode combined)"
            : "not started / not measured");
        AppendStage(text, diagnostics, "OutputValidation", "Validation");
        AppendStage(text, diagnostics, "Finalization", "Finalization");
        AppendStage(text, diagnostics, "TemporaryFileCleanup", "Temporary cleanup");
        text.Append("Production FFmpeg processes".PadRight(31)).AppendLine(diagnostics.ProductionFfmpegProcessCount.ToString(CultureInfo.InvariantCulture));
        text.Append("Actual output".PadRight(31)).AppendLine(diagnostics.ActualOutputBytes is { } actual ? FormatMebibytes(actual) : "not produced");
        text.Append("Projected typical error".PadRight(31)).AppendLine(diagnostics.ProjectedTypicalOutputErrorPercent is { } error
            ? error.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture) + "%"
            : "not applicable");
        text.Append("History/statistics".PadRight(31)).AppendLine(diagnostics.HistoryStatisticsRecordingSeconds is { } history
            ? Seconds(history)
            : "not measured");
        text.Append("Total".PadRight(31)).AppendLine(Seconds(diagnostics.TotalWallSeconds));
        text.Append("Adaptive overhead".PadRight(31)).AppendLine(diagnostics.AdaptiveOverheadPercent is { } percent
            ? percent.ToString("0.0", CultureInfo.InvariantCulture) + "%"
            : "not applicable");
        text.Append("Candidates / samples".PadRight(31)).Append(diagnostics.CandidateCount.ToString(CultureInfo.InvariantCulture))
            .Append(" / ").AppendLine(diagnostics.SampleCount.ToString(CultureInfo.InvariantCulture));
        text.Append("Seven-first early positive".PadRight(31)).AppendLine(diagnostics.SevenFirstEarlyPositive ? "yes" : "no");
        text.Append("Measured sample media".PadRight(31)).AppendLine(Seconds(diagnostics.TotalMeasuredSampleMediaSeconds));
        text.Append("Selected quality".PadRight(31)).AppendLine(diagnostics.SelectedQuality?.ToString(CultureInfo.InvariantCulture) ?? "none");
        text.Append("Terminal result".PadRight(31)).AppendLine(diagnostics.TerminalResult);
        return text.ToString().TrimEnd();
    }

    private void AddProcess(
        AutomaticPerformanceProcessTool tool,
        AutomaticPerformanceProcessPurpose purpose,
        TimeSpan? launchElapsed,
        SampleScope? sample)
    {
        lock (_gate)
        {
            var key = (tool, purpose);
            if (!_processes.TryGetValue(key, out ProcessAggregate? aggregate))
                _processes.Add(key, aggregate = new ProcessAggregate());
            aggregate.Count++;
            if (launchElapsed is { } elapsed && elapsed >= TimeSpan.Zero)
            {
                aggregate.LaunchSeconds += elapsed.TotalSeconds;
                aggregate.LaunchTimingCount++;
            }
            sample?.RecordProcess(tool, purpose, launchElapsed);
        }
    }

    private void TryRecordProductionDrain()
    {
        if (_activeProductionFfmpegExitedAt is not long exited ||
            _productionStdoutCompletedAt is not long stdout ||
            _productionStderrCompletedAt is not long stderr) return;
        long completed = Math.Max(stdout, stderr);
        if (completed < exited) return;
        _productionDrainSeconds.Add(Math.Max(0, Stopwatch.GetElapsedTime(exited, completed).TotalSeconds));
        _productionStdoutCompletedAt = null;
        _productionStderrCompletedAt = null;
    }

    private IReadOnlyList<AutomaticEncodePerformanceStage> BuildStages(long now, double totalSeconds, double? adaptiveSeconds)
    {
        var result = new List<AutomaticEncodePerformanceStage>();
        foreach ((string name, List<TimingEntry> entries) in _stages)
        {
            result.Add(new(name,
                Math.Max(0, entries.Sum(entry => entry.DurationSeconds)),
                entries.All(entry => entry.Completed) ? "completed" : "partial",
                entries.Min(entry => entry.StartOffsetSeconds),
                entries.Max(entry => entry.EndOffsetSeconds)));
        }
        if (_adaptiveStartedAt is long adaptiveStart)
        {
            long adaptiveEnd = _adaptiveEndedAt ?? now;
            result.Add(new("adaptiveSelection", adaptiveSeconds,
                _adaptiveEndedAt is null ? "partial" : "completed",
                Math.Max(0, Stopwatch.GetElapsedTime(_startedAt, adaptiveStart).TotalSeconds),
                Math.Max(0, Stopwatch.GetElapsedTime(_startedAt, adaptiveEnd).TotalSeconds)));
        }
        result.Add(new("totalJob", Math.Max(0, totalSeconds), "completed", 0, totalSeconds));
        return result.OrderBy(stage => stage.StartOffsetSeconds ?? double.MaxValue)
            .ThenBy(stage => stage.EndOffsetSeconds ?? double.MaxValue)
            .ToArray();
    }

    private double? StageSeconds(string name)
    {
        if (!_stages.TryGetValue(name, out List<TimingEntry>? entries) || entries.Count == 0) return null;
        return Math.Max(0, entries.Sum(entry => entry.DurationSeconds));
    }

    private void CloseActiveCandidate(long endedAt)
    {
        if (_activeCandidateStartedAt is not long startedAt) return;
        int quality = _activeCandidateQuality ?? -1;
        double startOffset = Math.Max(0, Stopwatch.GetElapsedTime(_startedAt, startedAt).TotalSeconds);
        double endOffset = Math.Max(startOffset, Stopwatch.GetElapsedTime(_startedAt, endedAt).TotalSeconds);
        _candidateWindows.Add(new(quality, Math.Max(0, Stopwatch.GetElapsedTime(startedAt, endedAt).TotalSeconds), startOffset, endOffset));
        _activeCandidateStartedAt = null;
        _activeCandidateQuality = null;
    }

    private static int? ParseQuality(string line)
    {
        string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 && int.TryParse(parts[^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int quality)
            ? quality
            : null;
    }

    private static double? ValidNonnegative(double? value) => value is { } number && double.IsFinite(number) && number >= 0 ? number : null;

    private static AutomaticPerformanceProcessPurpose PurposeForStage(PerformanceTimingStage? stage, FfmpegDiagnosticComponent component) => stage switch
    {
        PerformanceTimingStage.SourceProbe => AutomaticPerformanceProcessPurpose.SourceMetadata,
        PerformanceTimingStage.SourceTimingAnalysis => AutomaticPerformanceProcessPurpose.SourceTiming,
        PerformanceTimingStage.OutputValidation or PerformanceTimingStage.Finalization => AutomaticPerformanceProcessPurpose.OutputValidation,
        PerformanceTimingStage.FinalEncode when component == FfmpegDiagnosticComponent.Ffmpeg => AutomaticPerformanceProcessPurpose.ProductionFfmpeg,
        _ => AutomaticPerformanceProcessPurpose.Other
    };

    private static AutomaticPerformanceProcessTool ToolFor(
        string fileName,
        FfmpegDiagnosticComponent component,
        PerformanceTimingStage? stage,
        AutomaticPerformanceProcessPurpose purpose)
    {
        if (purpose is AutomaticPerformanceProcessPurpose.AdaptiveSampleFfmpeg or AutomaticPerformanceProcessPurpose.ProductionFfmpeg)
            return AutomaticPerformanceProcessTool.Ffmpeg;
        if (purpose is AutomaticPerformanceProcessPurpose.SourceMetadata or AutomaticPerformanceProcessPurpose.SourceTiming or
            AutomaticPerformanceProcessPurpose.AdaptiveSampleMetadata or AutomaticPerformanceProcessPurpose.AdaptiveSamplePacketAccounting or
            AutomaticPerformanceProcessPurpose.OutputValidation)
            return AutomaticPerformanceProcessTool.Ffprobe;
        if (stage == PerformanceTimingStage.SourceTimingAnalysis) return AutomaticPerformanceProcessTool.Ffprobe;
        string executable = Path.GetFileNameWithoutExtension(fileName);
        if (executable.Contains("ffprobe", StringComparison.OrdinalIgnoreCase)) return AutomaticPerformanceProcessTool.Ffprobe;
        if (executable.Contains("ffmpeg", StringComparison.OrdinalIgnoreCase)) return AutomaticPerformanceProcessTool.Ffmpeg;
        return component switch
        {
            FfmpegDiagnosticComponent.Ffprobe => AutomaticPerformanceProcessTool.Ffprobe,
            FfmpegDiagnosticComponent.Ffmpeg => AutomaticPerformanceProcessTool.Ffmpeg,
            _ => AutomaticPerformanceProcessTool.Other
        };
    }

    private static bool IsAutomaticMode(string mode) => mode.Contains("Automatic", StringComparison.OrdinalIgnoreCase);

    private static string FormatMebibytes(double bytes) =>
        (bytes / (1024d * 1024d)).ToString("0.##", CultureInfo.InvariantCulture) + " MiB";

    private static void AppendStage(StringBuilder builder, AutomaticEncodePerformanceDiagnostics diagnostics, string stageName, string label)
    {
        AutomaticEncodePerformanceStage? stage = diagnostics.Stages.FirstOrDefault(item => item.Name == stageName);
        builder.Append(label.PadRight(31)).AppendLine(stage?.DurationSeconds is { } elapsed ? Seconds(elapsed) : "not applicable / not measured");
    }

    private static string SampleProcessCount(
        AutomaticEncodePerformanceDiagnostics diagnostics,
        int quality,
        AutomaticPerformanceProcessPurpose purpose)
    {
        int count = diagnostics.Samples.Where(sample => sample.CandidateQuality == quality).Sum(sample => purpose switch
        {
            AutomaticPerformanceProcessPurpose.AdaptiveSampleFfmpeg => sample.FfmpegProcessCount,
            AutomaticPerformanceProcessPurpose.AdaptiveSampleMetadata => sample.FfprobeMetadataProcessCount,
            AutomaticPerformanceProcessPurpose.AdaptiveSamplePacketAccounting => sample.FfprobePacketAccountingProcessCount,
            _ => 0
        });
        return count.ToString(CultureInfo.InvariantCulture);
    }

    private static string Seconds(double seconds) => Math.Max(0, seconds).ToString("0.00", CultureInfo.InvariantCulture) + " s";

    private sealed record AmbientState(
        AutomaticEncodePerformanceInstrumentation Owner,
        AutomaticPerformanceProcessPurpose? Purpose,
        PerformanceTimingStage? Stage,
        SampleScope? Sample);

    private sealed record TimingEntry(double DurationSeconds, bool Completed, double StartOffsetSeconds, double EndOffsetSeconds);
    private sealed record CandidateWindow(int Quality, double DurationSeconds, double StartOffsetSeconds, double EndOffsetSeconds);
    private sealed record PreviewRecord(bool Active, bool CancellationRequested, double? DrainSeconds, string Status);

    private sealed class ProcessAggregate
    {
        public int Count { get; set; }
        public int LaunchTimingCount { get; set; }
        public double LaunchSeconds { get; set; }
    }

    private sealed class RestoreAmbient(AmbientState? previous) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) Ambient.Value = previous;
        }
    }

    private sealed class NamedStageScope(AutomaticEncodePerformanceInstrumentation owner, string name, long startedAt) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            owner.RecordNamedStage(name, startedAt, Stopwatch.GetElapsedTime(startedAt), completed: true);
        }
    }

    internal sealed class SampleScope : IDisposable
    {
        private readonly AutomaticEncodePerformanceInstrumentation _owner;
        private readonly int _quality;
        private readonly int _ordinal;
        private readonly RepresentativeSample _sample;
        private readonly long _startedAt;
        private readonly AmbientState? _previousAmbient;
        private readonly object _gate = new();
        private int _disposed;
        private double? _encodeSeconds;
        private double? _ffmpegLaunchSeconds;
        private double? _metadataProbeSeconds;
        private double? _packetScanSeconds;
        private double? _measuredSeconds;
        private double? _sourceVideoStartOffsetSeconds;
        private double? _effectiveInputStartSeconds;
        private double? _expectedAvailableDurationSeconds;
        private double? _outputFrameRate;
        private double? _durationToleranceSeconds;
        private bool? _durationValidationPassed;
        private long? _videoBytes;
        private int _ffmpegProcessCount;
        private int _metadataProcessCount;
        private int _packetProcessCount;

        internal SampleScope(AutomaticEncodePerformanceInstrumentation owner, int quality, int ordinal, RepresentativeSample sample, long startedAt)
        {
            _owner = owner;
            _quality = quality;
            _ordinal = ordinal;
            _sample = sample;
            _startedAt = startedAt;
            _previousAmbient = Ambient.Value;
            if (_previousAmbient?.Owner == owner)
                Ambient.Value = _previousAmbient with { Sample = this };
        }

        public void RecordFfmpegEncode(TimeSpan elapsed) { lock (_gate) _encodeSeconds = SafeSeconds(elapsed); }
        public void RecordMetadataProbe(TimeSpan elapsed) { lock (_gate) _metadataProbeSeconds = SafeSeconds(elapsed); }
        public void RecordPacketScan(TimeSpan elapsed) { lock (_gate) _packetScanSeconds = SafeSeconds(elapsed); }
        public void RecordSeekMapping(double? sourceVideoStartOffsetSeconds, double effectiveInputStartSeconds)
        {
            lock (_gate)
            {
                _sourceVideoStartOffsetSeconds = IsFinite(sourceVideoStartOffsetSeconds) ? sourceVideoStartOffsetSeconds : null;
                _effectiveInputStartSeconds = double.IsFinite(effectiveInputStartSeconds) ? effectiveInputStartSeconds : null;
            }
        }
        public void RecordDurationValidation(double expectedAvailableSeconds, double measuredSeconds,
            double? outputFrameRate, double? toleranceSeconds, bool? passed)
        {
            lock (_gate)
            {
                _expectedAvailableDurationSeconds = IsFinite(expectedAvailableSeconds) ? expectedAvailableSeconds : null;
                _measuredSeconds = IsFinite(measuredSeconds) ? measuredSeconds : null;
                _outputFrameRate = outputFrameRate is double frameRate && double.IsFinite(frameRate) && frameRate > 0
                    ? frameRate
                    : null;
                _durationToleranceSeconds = toleranceSeconds is double tolerance && double.IsFinite(tolerance) && tolerance >= 0
                    ? tolerance
                    : null;
                _durationValidationPassed = passed;
            }
        }
        public void RecordEvidence(long videoBytes, double measuredSeconds)
        {
            lock (_gate)
            {
                _videoBytes = videoBytes > 0 ? videoBytes : null;
                _measuredSeconds = double.IsFinite(measuredSeconds) && measuredSeconds > 0 ? measuredSeconds : null;
            }
        }

        internal void RecordProcess(AutomaticPerformanceProcessTool tool, AutomaticPerformanceProcessPurpose purpose, TimeSpan? launchElapsed)
        {
            lock (_gate)
            {
                if (tool == AutomaticPerformanceProcessTool.Ffmpeg && purpose == AutomaticPerformanceProcessPurpose.AdaptiveSampleFfmpeg)
                {
                    _ffmpegProcessCount++;
                    if (launchElapsed is { } ffmpegLaunch && ffmpegLaunch >= TimeSpan.Zero)
                        _ffmpegLaunchSeconds = (_ffmpegLaunchSeconds ?? 0) + ffmpegLaunch.TotalSeconds;
                }
                else if (tool == AutomaticPerformanceProcessTool.Ffprobe && purpose == AutomaticPerformanceProcessPurpose.AdaptiveSampleMetadata)
                    _metadataProcessCount++;
                else if (tool == AutomaticPerformanceProcessTool.Ffprobe && purpose == AutomaticPerformanceProcessPurpose.AdaptiveSamplePacketAccounting)
                    _packetProcessCount++;
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            AmbientState? state = Ambient.Value;
            if (state?.Sample == this) Ambient.Value = _previousAmbient;
            SampleRecord record;
            lock (_gate)
            {
                record = new SampleRecord(
                    _quality, _ordinal, _sample.Label, _sample.Start.TotalSeconds, _sample.Duration.TotalSeconds,
                    _measuredSeconds, _ffmpegLaunchSeconds, _encodeSeconds, _metadataProbeSeconds, _packetScanSeconds,
                    Math.Max(0, Stopwatch.GetElapsedTime(_startedAt).TotalSeconds), _videoBytes,
                    _measuredSeconds is > 0 && _videoBytes is > 0 ? _videoBytes.Value / _measuredSeconds.Value : null,
                    _ffmpegProcessCount, _metadataProcessCount, _packetProcessCount,
                    _sourceVideoStartOffsetSeconds, _effectiveInputStartSeconds,
                    _expectedAvailableDurationSeconds, _outputFrameRate, _durationToleranceSeconds,
                    _durationValidationPassed);
            }
            lock (_owner._gate) _owner._samples.Add(record);
        }

        private static double? SafeSeconds(TimeSpan elapsed) => elapsed < TimeSpan.Zero ? 0 : elapsed.TotalSeconds;
        private static bool IsFinite(double? value) => value is double number && double.IsFinite(number);
    }

    private sealed record SampleRecord(
        int Quality, int Ordinal, string Label, double StartSeconds, double RequestedSeconds,
        double? MeasuredMediaSeconds, double? FfmpegLaunchSeconds, double? FfmpegEncodeSeconds, double? MetadataProbeSeconds,
        double? PacketScanSeconds, double TotalWallSeconds, long? VideoBytes, double? BytesPerSecond,
        int FfmpegProcessCount, int MetadataProcessCount, int PacketProcessCount,
        double? SourceVideoStartOffsetSeconds, double? EffectiveInputStartSeconds,
        double? ExpectedAvailableDurationSeconds, double? OutputFrameRate, double? DurationToleranceSeconds,
        bool? DurationValidationPassed)
    {
        public AutomaticEncodeSampleTiming ToModel() => new(
            Quality, Ordinal, Label, StartSeconds, RequestedSeconds, MeasuredMediaSeconds,
            FfmpegLaunchSeconds, FfmpegEncodeSeconds, MetadataProbeSeconds, PacketScanSeconds, TotalWallSeconds,
            VideoBytes, BytesPerSecond, FfmpegProcessCount, MetadataProcessCount, PacketProcessCount,
            SourceVideoStartOffsetSeconds, EffectiveInputStartSeconds, ExpectedAvailableDurationSeconds,
            OutputFrameRate, DurationToleranceSeconds, DurationValidationPassed);
    }
}
