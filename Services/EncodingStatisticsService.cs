using System.Text.Json;
using System.Text.Json.Serialization;
using MediaFlux.Models;

namespace MediaFlux.Services
{
    public enum EncodingStatisticsOutcome
    {
        Success = 0,
        Failed = 1,
        Skipped = 2,
        Cancelled = 3,
        ValidationFailed = 4,
        PromotionFailed = 5,
        FinalVerificationFailed = 6
    }

    public enum EncodingStatisticsPeriod
    {
        Today,
        ThisWeek,
        ThisMonth,
        ThisYear,
        AllTime,
        Custom
    }

    public sealed record EncodingStatisticsRecord
    {
        public int SchemaVersion { get; set; } = 4;
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public DateTime StartUtc { get; set; }
        public DateTime EndUtc { get; set; }
        public EncodingStatisticsOutcome Outcome { get; set; }
        public string SourcePath { get; set; } = "";
        public string OutputPath { get; set; } = "";
        public string Codec { get; set; } = "";
        public string Encoder { get; set; } = "";
        public long? SourceSizeBytes { get; set; }
        public long? OutputSizeBytes { get; set; }
        public double? MediaDurationSeconds { get; set; }
        public double ProcessingSeconds { get; set; }
        public string EncoderId { get; set; } = "";
        public string EncoderPreset { get; set; } = "";
        public string SourceResolutionTier { get; set; } = "";
        public string OutputResolutionTier { get; set; } = "";
        public int? OutputBitDepth { get; set; }
        public bool? ScalingApplied { get; set; }
        public bool? ConcurrentEncoderSessions { get; set; }
        public bool IsSampleJob { get; set; }
        public EncodingDiagnosticSummary? DiagnosticSummary { get; set; }
        public string Notes { get; set; } = "";
        public string HardwareKey { get; set; } = "";
        public bool RecoveredSuccessful { get; set; }
        // Frozen before execution. These nullable fields are observational only and
        // intentionally share the bounded finalized-statistics journal.
        public string PredictionPlanId { get; set; } = "";
        public string PredictionSourceCodec { get; set; } = "";
        public string PredictionTargetCodec { get; set; } = "";
        public bool? PredictionSameCodec { get; set; }
        public long? PredictedOutputSizeBytes { get; set; }
        public long? BasePredictedOutputSizeBytes { get; set; }
        public bool? CalibrationApplied { get; set; }
        public double? CalibrationCorrectionPercent { get; set; }
        public double? CalibrationMedianSignedErrorPercent { get; set; }
        public string CalibrationConfidence { get; set; } = "";
        public int? CalibrationSampleCount { get; set; }
        public string CalibrationCohortKey { get; set; } = "";
        public string CalibrationReason { get; set; } = "";
        public long? HypotheticalCalibratedOutputSizeBytes { get; set; }
        public DateTime? CalibrationEvidenceCutoffUtc { get; set; }
        public DateTime? CalibrationDecisionUtc { get; set; }
        public string CalibrationDecision { get; set; } = "";
        public string CalibrationEffectivenessState { get; set; } = "";
        public int? CalibrationEvaluationCount { get; set; }
        public double? CalibrationMedianImprovementPercent { get; set; }
        public double? CalibrationImprovedRatePercent { get; set; }
        public double? CalibrationWorsenedRatePercent { get; set; }
        public DateTime? CalibrationEffectivenessSinceUtc { get; set; }
        public string CalibrationPolicyId { get; set; } = "";
        public double? CalibrationLearningStrength { get; set; }
        public double? CalibrationRawHistoricalCorrectionPercent { get; set; }
        public double? CalibrationAppliedCorrectionPercent { get; set; }
        public double? PredictedCompressionRatio { get; set; }
        public double? PredictedProcessingSeconds { get; set; }
        public string PredictionConfidence { get; set; } = "";
        public string PredictionRecommendation { get; set; } = "";
        public string PredictionQuality { get; set; } = "";
        public string PredictionAssessment { get; set; } = "";
        public string TerminalResult { get; set; } = "";
        // Additive JSONL field; older schema versions deserialize with null.
        public SourceAdaptiveShadowOutcome? SourceAdaptiveShadow { get; set; }
        // Versioned effective NVENC quality-mode settings; absent on legacy records.
        public string QualityModeSettingsSignature { get; set; } = "";
    }

    public readonly record struct EncodingStatisticsUtcRange(
        DateTime? StartUtc,
        DateTime? EndUtcExclusive,
        string Description);

    public sealed class EncodingStatisticsSnapshot
    {
        public int FilesProcessed { get; init; }
        public int Successful { get; init; }
        public int Failed { get; init; }
        public int Skipped { get; init; }
        public int Cancelled { get; init; }
        public int FinalizationFailed { get; init; }
        public int FilesWithSizeData { get; init; }
        public long OriginalBytes { get; init; }
        public long OutputBytes { get; init; }
        public long SpaceSavedBytes { get; init; }
        public double ReductionPercent { get; init; }
        public double AverageSpaceSavedBytes { get; init; }
        public double AverageEncodingSpeed { get; init; }
        public double AverageProcessingSeconds { get; init; }
        public IReadOnlyList<EncodingStatisticsGroup> Groups { get; init; } =
            Array.Empty<EncodingStatisticsGroup>();
    }

    public sealed class EncodingStatisticsGroup
    {
        public string Codec { get; init; } = "";
        public string Encoder { get; init; } = "";
        public int FilesProcessed { get; init; }
        public int Successful { get; init; }
        public int Failed { get; init; }
        public int Skipped { get; init; }
        public int Cancelled { get; init; }
        public int FinalizationFailed { get; init; }
        public long OriginalBytes { get; init; }
        public long OutputBytes { get; init; }
        public long SpaceSavedBytes { get; init; }
        public double ReductionPercent { get; init; }
        public double AverageEncodingSpeed { get; init; }
    }

    public sealed class EncodingStatisticsService
    {
        private readonly string _path;
        private readonly object _sync = new();
        private readonly List<EncodingStatisticsRecord> _records = new();
        private readonly HashSet<string> _recordIds =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        private const int MaximumHistoricalRecords = 3000;

        public EncodingStatisticsService(string storagePath)
        {
            if (string.IsNullOrWhiteSpace(storagePath))
                throw new ArgumentException("A statistics storage path is required.", nameof(storagePath));

            _path = Path.GetFullPath(storagePath);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            LoadExistingRecords();
        }

        public bool AppendFinalized(EncodingStatisticsRecord record)
        {
            ArgumentNullException.ThrowIfNull(record);
            if (string.IsNullOrWhiteSpace(record.Id))
                throw new ArgumentException("A stable statistics record ID is required.", nameof(record));

            NormalizeRecord(record);
            string line = JsonSerializer.Serialize(record, _jsonOptions);

            lock (_sync)
            {
                if (_recordIds.Contains(record.Id))
                    return false;

                File.AppendAllText(_path, line + Environment.NewLine);
                _records.Add(record);
                _recordIds.Add(record.Id);
                if (_records.Count > MaximumHistoricalRecords)
                {
                    EncodingStatisticsRecord[] retained = _records.OrderByDescending(value => value.EndUtc).Take(MaximumHistoricalRecords).ToArray();
                    File.WriteAllLines(_path, retained.Select(value => JsonSerializer.Serialize(value, _jsonOptions)));
                    _records.Clear(); _records.AddRange(retained); _recordIds.Clear();
                    foreach (EncodingStatisticsRecord value in retained) _recordIds.Add(value.Id);
                }
                return true;
            }
        }

        public IReadOnlyList<EncodingStatisticsRecord> GetAll()
        {
            lock (_sync)
            {
                return _records
                    .OrderByDescending(record => record.EndUtc)
                    .ToArray();
            }
        }

        private void LoadExistingRecords()
        {
            if (!File.Exists(_path))
                return;

            foreach (string line in File.ReadLines(_path))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                try
                {
                    EncodingStatisticsRecord? record =
                        JsonSerializer.Deserialize<EncodingStatisticsRecord>(
                            line,
                            _jsonOptions);
                    if (record == null || string.IsNullOrWhiteSpace(record.Id))
                        continue;

                    NormalizeRecord(record);
                    if (_recordIds.Add(record.Id))
                        _records.Add(record);
                }
                catch
                {
                    // One damaged append must not hide the remaining lifetime totals.
                }
            }
        }

        private static void NormalizeRecord(EncodingStatisticsRecord record)
        {
            record.StartUtc = NormalizeUtc(record.StartUtc);
            record.EndUtc = NormalizeUtc(record.EndUtc);
            if (record.EndUtc < record.StartUtc)
                record.StartUtc = record.EndUtc;

            record.SourcePath ??= "";
            record.OutputPath ??= "";
            record.Codec = NormalizeGroupValue(record.Codec);
            record.Encoder = NormalizeGroupValue(record.Encoder);
            record.EncoderId = record.EncoderId?.Trim() ?? "";
            record.EncoderPreset = record.EncoderPreset?.Trim() ?? "";
            record.SourceResolutionTier = record.SourceResolutionTier?.Trim() ?? "";
            record.OutputResolutionTier = record.OutputResolutionTier?.Trim() ?? "";
            if (record.OutputBitDepth is not (8 or 10 or 12 or 16)) record.OutputBitDepth = null;
            record.Notes ??= "";
            record.HardwareKey = record.HardwareKey?.Trim() ?? "";
            record.PredictionPlanId = record.PredictionPlanId?.Trim() ?? "";
            record.PredictionSourceCodec = record.PredictionSourceCodec?.Trim() ?? "";
            record.PredictionTargetCodec = record.PredictionTargetCodec?.Trim() ?? "";
            record.PredictionConfidence = record.PredictionConfidence?.Trim() ?? "";
            record.PredictionRecommendation = record.PredictionRecommendation?.Trim() ?? "";
            record.PredictionQuality = record.PredictionQuality?.Trim() ?? "";
            record.PredictionAssessment = record.PredictionAssessment?.Trim() ?? "";
            record.CalibrationConfidence = record.CalibrationConfidence?.Trim() ?? "";
            record.CalibrationCohortKey = record.CalibrationCohortKey?.Trim() ?? "";
            record.CalibrationReason = record.CalibrationReason?.Trim() ?? "";
            record.CalibrationDecision = record.CalibrationDecision?.Trim() ?? "";
            record.CalibrationEffectivenessState = record.CalibrationEffectivenessState?.Trim() ?? "";
            record.CalibrationPolicyId = record.CalibrationPolicyId?.Trim() ?? "";
            record.TerminalResult = record.TerminalResult?.Trim() ?? "";
            record.QualityModeSettingsSignature = record.QualityModeSettingsSignature?.Trim() ?? "";
            if (record.DiagnosticSummary is { } diagnostic)
            {
                record.DiagnosticSummary = diagnostic with
                {
                    PeakConcurrentJobs = Math.Clamp(diagnostic.PeakConcurrentJobs, 0, 64),
                    MaintenanceOverlapSeconds = Math.Max(0, diagnostic.MaintenanceOverlapSeconds),
                    SameDeviceMaintenanceSeconds = Math.Max(0, diagnostic.SameDeviceMaintenanceSeconds),
                    StorageWaitSeconds = diagnostic.StorageWaitSeconds is >= 0 ? diagnostic.StorageWaitSeconds : null,
                    FinalizationSeconds = Math.Max(0, diagnostic.FinalizationSeconds),
                    Samples = Math.Clamp(diagnostic.Samples, 0, EncodingDiagnosticsService.MaximumSamplesPerSession),
                    Observation = string.IsNullOrWhiteSpace(diagnostic.Observation) ? "No diagnostic observation recorded." : diagnostic.Observation.Trim()
                };
            }
            record.ProcessingSeconds =
                double.IsFinite(record.ProcessingSeconds)
                    ? Math.Max(0, record.ProcessingSeconds)
                    : 0;
            if (record.MediaDurationSeconds is not > 0 ||
                !double.IsFinite(record.MediaDurationSeconds.Value))
            {
                record.MediaDurationSeconds = null;
            }

            if (record.SourceSizeBytes is < 0)
                record.SourceSizeBytes = null;
            if (record.OutputSizeBytes is < 0)
                record.OutputSizeBytes = null;
            if (record.PredictedOutputSizeBytes is < 0)
                record.PredictedOutputSizeBytes = null;
            if (record.BasePredictedOutputSizeBytes is < 0)
                record.BasePredictedOutputSizeBytes = null;
            if (record.HypotheticalCalibratedOutputSizeBytes is < 0)
                record.HypotheticalCalibratedOutputSizeBytes = null;
            if (record.CalibrationCorrectionPercent is { } correction && !double.IsFinite(correction))
                record.CalibrationCorrectionPercent = null;
            if (record.CalibrationMedianSignedErrorPercent is { } bias && !double.IsFinite(bias))
                record.CalibrationMedianSignedErrorPercent = null;
            if (record.CalibrationSampleCount is < 0)
                record.CalibrationSampleCount = null;
            if (record.CalibrationEvaluationCount is < 0)
                record.CalibrationEvaluationCount = null;
            if (record.CalibrationMedianImprovementPercent is { } improvement && !double.IsFinite(improvement))
                record.CalibrationMedianImprovementPercent = null;
            if (record.CalibrationImprovedRatePercent is { } improvedRate && !double.IsFinite(improvedRate))
                record.CalibrationImprovedRatePercent = null;
            if (record.CalibrationWorsenedRatePercent is { } worsenedRate && !double.IsFinite(worsenedRate))
                record.CalibrationWorsenedRatePercent = null;
            if (record.CalibrationLearningStrength is { } learningStrength && !double.IsFinite(learningStrength))
                record.CalibrationLearningStrength = null;
            if (record.CalibrationRawHistoricalCorrectionPercent is { } rawCorrection && !double.IsFinite(rawCorrection))
                record.CalibrationRawHistoricalCorrectionPercent = null;
            if (record.CalibrationAppliedCorrectionPercent is { } appliedCorrection && !double.IsFinite(appliedCorrection))
                record.CalibrationAppliedCorrectionPercent = null;
            if (record.PredictedCompressionRatio is not >= 0 ||
                !double.IsFinite(record.PredictedCompressionRatio.Value))
                record.PredictedCompressionRatio = null;
            if (record.PredictedProcessingSeconds is not > 0 ||
                !double.IsFinite(record.PredictedProcessingSeconds.Value))
                record.PredictedProcessingSeconds = null;

            // Incomplete failed/cancelled output files are not durable encoded output.
            if (record.Outcome != EncodingStatisticsOutcome.Success)
                record.OutputSizeBytes = null;
        }

        private static DateTime NormalizeUtc(DateTime value)
        {
            if (value == default)
                return DateTime.UtcNow;

            return value.Kind switch
            {
                DateTimeKind.Utc => value,
                DateTimeKind.Local => value.ToUniversalTime(),
                _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
            };
        }

        private static string NormalizeGroupValue(string? value) =>
            string.IsNullOrWhiteSpace(value) ? "Unknown" : value.Trim();
    }

    public static class EncodingStatisticsCalculator
    {
        public static EncodingStatisticsSnapshot Aggregate(
            IEnumerable<EncodingStatisticsRecord> records,
            DateTime? startUtc = null,
            DateTime? endUtcExclusive = null)
        {
            ArgumentNullException.ThrowIfNull(records);

            EncodingStatisticsRecord[] filtered = records
                .Where(record =>
                    (!startUtc.HasValue || record.EndUtc >= startUtc.Value) &&
                    (!endUtcExclusive.HasValue || record.EndUtc < endUtcExclusive.Value))
                .ToArray();

            EncodingStatisticsSnapshot totals = BuildSnapshot(filtered);
            EncodingStatisticsGroup[] groups = filtered
                .GroupBy(
                    record => new
                    {
                        Codec = FriendlyCodec(record.Codec),
                        Encoder = FriendlyValue(record.Encoder)
                    })
                .Select(group =>
                {
                    EncodingStatisticsSnapshot snapshot = BuildSnapshot(group);
                    return new EncodingStatisticsGroup
                    {
                        Codec = group.Key.Codec,
                        Encoder = group.Key.Encoder,
                        FilesProcessed = snapshot.FilesProcessed,
                        Successful = snapshot.Successful,
                        Failed = snapshot.Failed,
                        Skipped = snapshot.Skipped,
                        Cancelled = snapshot.Cancelled,
                        FinalizationFailed = snapshot.FinalizationFailed,
                        OriginalBytes = snapshot.OriginalBytes,
                        OutputBytes = snapshot.OutputBytes,
                        SpaceSavedBytes = snapshot.SpaceSavedBytes,
                        ReductionPercent = snapshot.ReductionPercent,
                        AverageEncodingSpeed = snapshot.AverageEncodingSpeed
                    };
                })
                .OrderByDescending(group => group.FilesProcessed)
                .ThenBy(group => group.Codec, StringComparer.OrdinalIgnoreCase)
                .ThenBy(group => group.Encoder, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return new EncodingStatisticsSnapshot
            {
                FilesProcessed = totals.FilesProcessed,
                Successful = totals.Successful,
                Failed = totals.Failed,
                Skipped = totals.Skipped,
                Cancelled = totals.Cancelled,
                FinalizationFailed = totals.FinalizationFailed,
                FilesWithSizeData = totals.FilesWithSizeData,
                OriginalBytes = totals.OriginalBytes,
                OutputBytes = totals.OutputBytes,
                SpaceSavedBytes = totals.SpaceSavedBytes,
                ReductionPercent = totals.ReductionPercent,
                AverageSpaceSavedBytes = totals.AverageSpaceSavedBytes,
                AverageEncodingSpeed = totals.AverageEncodingSpeed,
                AverageProcessingSeconds = totals.AverageProcessingSeconds,
                Groups = groups
            };
        }

        public static EncodingStatisticsUtcRange GetUtcRange(
            EncodingStatisticsPeriod period,
            DateTime customStartDate,
            DateTime customEndDate,
            DateTimeOffset now,
            TimeZoneInfo timeZone)
        {
            ArgumentNullException.ThrowIfNull(timeZone);

            DateTime localNow = TimeZoneInfo.ConvertTime(now, timeZone).DateTime;
            DateTime localStart;
            DateTime localEndExclusive;

            switch (period)
            {
                case EncodingStatisticsPeriod.Today:
                    localStart = localNow.Date;
                    localEndExclusive = localStart.AddDays(1);
                    break;
                case EncodingStatisticsPeriod.ThisWeek:
                    int daysFromMonday =
                        ((int)localNow.DayOfWeek + 6) % 7;
                    localStart = localNow.Date.AddDays(-daysFromMonday);
                    localEndExclusive = localStart.AddDays(7);
                    break;
                case EncodingStatisticsPeriod.ThisMonth:
                    localStart = new DateTime(localNow.Year, localNow.Month, 1);
                    localEndExclusive = localStart.AddMonths(1);
                    break;
                case EncodingStatisticsPeriod.ThisYear:
                    localStart = new DateTime(localNow.Year, 1, 1);
                    localEndExclusive = localStart.AddYears(1);
                    break;
                case EncodingStatisticsPeriod.AllTime:
                    return new EncodingStatisticsUtcRange(
                        null,
                        null,
                        "All recorded time");
                case EncodingStatisticsPeriod.Custom:
                    localStart = customStartDate.Date;
                    DateTime localEnd = customEndDate.Date;
                    if (localEnd < localStart)
                        (localStart, localEnd) = (localEnd, localStart);
                    localEndExclusive = localEnd.AddDays(1);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(period));
            }

            DateTime startUtc = TimeZoneInfo.ConvertTimeToUtc(
                DateTime.SpecifyKind(localStart, DateTimeKind.Unspecified),
                timeZone);
            DateTime endUtc = TimeZoneInfo.ConvertTimeToUtc(
                DateTime.SpecifyKind(localEndExclusive, DateTimeKind.Unspecified),
                timeZone);
            string description = period == EncodingStatisticsPeriod.Custom
                ? $"{localStart:d} through {localEndExclusive.AddDays(-1):d}"
                : $"{FriendlyPeriod(period)} · {localStart:d} through {localEndExclusive.AddDays(-1):d}";

            return new EncodingStatisticsUtcRange(
                startUtc,
                endUtc,
                description);
        }

        public static string FormatBytes(long bytes)
        {
            string sign = bytes < 0 ? "-" : "";
            double value = Math.Abs((double)bytes);
            string[] units = { "B", "KB", "MB", "GB", "TB", "PB" };
            int unit = 0;
            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }

            string format = unit == 0 ? "0" : value >= 100 ? "0" : "0.##";
            return $"{sign}{value.ToString(format)} {units[unit]}";
        }

        private static EncodingStatisticsSnapshot BuildSnapshot(
            IEnumerable<EncodingStatisticsRecord> records)
        {
            EncodingStatisticsRecord[] all = records.ToArray();
            EncodingStatisticsRecord[] successful = all
                .Where(record => record.Outcome == EncodingStatisticsOutcome.Success)
                .ToArray();
            EncodingStatisticsRecord[] withSizes = successful
                .Where(record =>
                    record.SourceSizeBytes is >= 0 &&
                    record.OutputSizeBytes is >= 0)
                .ToArray();

            long originalBytes = withSizes.Sum(record => record.SourceSizeBytes!.Value);
            long outputBytes = withSizes.Sum(record => record.OutputSizeBytes!.Value);
            long savedBytes = originalBytes - outputBytes;
            double reduction = originalBytes > 0
                ? savedBytes * 100d / originalBytes
                : 0;

            EncodingStatisticsRecord[] timedAttempts = all
                .Where(record =>
                    record.Outcome != EncodingStatisticsOutcome.Skipped &&
                    record.ProcessingSeconds > 0)
                .ToArray();
            EncodingStatisticsRecord[] speedSamples = successful
                .Where(record =>
                    record.MediaDurationSeconds is > 0 &&
                    record.ProcessingSeconds > 0)
                .ToArray();
            double processingSeconds = speedSamples.Sum(record => record.ProcessingSeconds);
            double mediaSeconds = speedSamples.Sum(record => record.MediaDurationSeconds!.Value);

            return new EncodingStatisticsSnapshot
            {
                FilesProcessed = all.Length,
                Successful = all.Count(record => record.Outcome == EncodingStatisticsOutcome.Success),
                Failed = all.Count(record => record.Outcome == EncodingStatisticsOutcome.Failed),
                Skipped = all.Count(record => record.Outcome == EncodingStatisticsOutcome.Skipped),
                Cancelled = all.Count(record => record.Outcome == EncodingStatisticsOutcome.Cancelled),
                FinalizationFailed = all.Count(record =>
                    record.Outcome is
                        EncodingStatisticsOutcome.ValidationFailed or
                        EncodingStatisticsOutcome.PromotionFailed or
                        EncodingStatisticsOutcome.FinalVerificationFailed),
                FilesWithSizeData = withSizes.Length,
                OriginalBytes = originalBytes,
                OutputBytes = outputBytes,
                SpaceSavedBytes = savedBytes,
                ReductionPercent = reduction,
                AverageSpaceSavedBytes = withSizes.Length > 0
                    ? savedBytes / (double)withSizes.Length
                    : 0,
                AverageEncodingSpeed = processingSeconds > 0
                    ? mediaSeconds / processingSeconds
                    : 0,
                AverageProcessingSeconds = timedAttempts.Length > 0
                    ? timedAttempts.Average(record => record.ProcessingSeconds)
                    : 0
            };
        }

        private static string FriendlyPeriod(EncodingStatisticsPeriod period) =>
            period switch
            {
                EncodingStatisticsPeriod.Today => "Today",
                EncodingStatisticsPeriod.ThisWeek => "This week",
                EncodingStatisticsPeriod.ThisMonth => "This month",
                EncodingStatisticsPeriod.ThisYear => "This year",
                _ => "Selected period"
            };

        private static string FriendlyValue(string? value) =>
            string.IsNullOrWhiteSpace(value) ? "Unknown" : value.Trim();

        private static string FriendlyCodec(string? codec)
        {
            string value = FriendlyValue(codec);
            return value.ToLowerInvariant() switch
            {
                "hevc" or "h265" or "libx265" or "hevc_nvenc" or "hevc_qsv" =>
                    "H.265 / HEVC",
                "h264" or "avc" or "libx264" or "h264_nvenc" or "h264_qsv" =>
                    "H.264 / AVC",
                "av1" or "av01" or "libsvtav1" or "av1_nvenc" or "av1_qsv" =>
                    "AV1",
                _ => value
            };
        }
    }
}
