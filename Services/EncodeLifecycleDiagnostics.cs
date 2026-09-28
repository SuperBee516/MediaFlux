using System.Runtime.InteropServices;
using System.Text;

namespace MediaFlux.Services;

/// <summary>Observational milestones for one queued encode. Missing milestones stay null.</summary>
public enum EncodeLifecycleEvent
{
    QueueDispatch, FfmpegStart, FirstProgress, LastProgress, FaststartStart,
    FfmpegExit, StdoutComplete, StderrComplete, VerificationStart,
    StagedVerificationEnd, FinalizationStart, FinalizationEnd,
    PromotedVerificationStart, VerificationEnd, QueueCompleted
}

public sealed class EncodeLifecycleDiagnostics
{
    public const string FaststartStatus = "Finalizing MP4…";
    public const string FaststartProgress = "Frames done";
    public static bool ShouldApplyFrameProgress(string? currentStatus) =>
        !string.Equals(currentStatus, FaststartStatus, StringComparison.Ordinal) &&
        !string.Equals(currentStatus, "Verifying output", StringComparison.Ordinal) &&
        !string.Equals(currentStatus, "Finalizing", StringComparison.Ordinal) &&
        !string.Equals(currentStatus, "Verifying final output", StringComparison.Ordinal);
    private readonly object _gate = new();
    private readonly Dictionary<EncodeLifecycleEvent, DateTime> _times = new();
    private long? _processReadBytes, _processWriteBytes;
    private DateTime? _previousQueueCompletedUtc;

    public bool FaststartActive { get { lock (_gate) return _times.ContainsKey(EncodeLifecycleEvent.FaststartStart) && !_times.ContainsKey(EncodeLifecycleEvent.FfmpegExit); } }
    public bool IsActiveEncodePeriod { get { lock (_gate) return _times.ContainsKey(EncodeLifecycleEvent.FirstProgress) && !_times.ContainsKey(EncodeLifecycleEvent.FaststartStart) && !_times.ContainsKey(EncodeLifecycleEvent.FfmpegExit); } }

    public void Record(EncodeLifecycleEvent milestone, DateTime? utc = null)
    {
        lock (_gate)
        {
            DateTime time = (utc ?? DateTime.UtcNow).ToUniversalTime();
            if (milestone == EncodeLifecycleEvent.FfmpegStart && _times.ContainsKey(milestone))
            {
                // A recovery attempt supersedes earlier per-process boundaries.
                foreach (EncodeLifecycleEvent previous in new[]
                {
                    EncodeLifecycleEvent.FfmpegStart, EncodeLifecycleEvent.FirstProgress,
                    EncodeLifecycleEvent.LastProgress, EncodeLifecycleEvent.FaststartStart,
                    EncodeLifecycleEvent.FfmpegExit, EncodeLifecycleEvent.StdoutComplete,
                    EncodeLifecycleEvent.StderrComplete, EncodeLifecycleEvent.VerificationStart,
                    EncodeLifecycleEvent.StagedVerificationEnd, EncodeLifecycleEvent.FinalizationStart,
                    EncodeLifecycleEvent.FinalizationEnd, EncodeLifecycleEvent.PromotedVerificationStart,
                    EncodeLifecycleEvent.VerificationEnd
                }) _times.Remove(previous);
                _processReadBytes = _processWriteBytes = null;
            }
            if (milestone == EncodeLifecycleEvent.LastProgress)
            {
                if (!_times.ContainsKey(EncodeLifecycleEvent.FaststartStart)) _times[milestone] = time;
            }
            else if (!_times.ContainsKey(milestone)) _times[milestone] = time;
        }
    }

    public void RecordProcessIo(long? readBytes, long? writeBytes)
    {
        lock (_gate) { _processReadBytes = readBytes; _processWriteBytes = writeBytes; }
    }

    public void RecordPreviousQueueCompletion(DateTime utc)
    {
        lock (_gate) _previousQueueCompletedUtc = utc.ToUniversalTime();
    }

    public EncodeLifecycleTiming Snapshot(string sourcePath, string outputPath, long? outputBytes)
    {
        lock (_gate)
        {
            DateTime? At(EncodeLifecycleEvent key) => _times.GetValueOrDefault(key) is { } value && value != default ? value : null;
            double? Gap(EncodeLifecycleEvent start, EncodeLifecycleEvent end) =>
                At(start) is { } a && At(end) is { } b && b >= a ? (b - a).TotalSeconds : null;
            DateTime? stdout = At(EncodeLifecycleEvent.StdoutComplete);
            DateTime? stderr = At(EncodeLifecycleEvent.StderrComplete);
            DateTime? drain = stdout.HasValue && stderr.HasValue
                ? (stdout > stderr ? stdout : stderr) : null;
            double? drainSeconds = At(EncodeLifecycleEvent.FfmpegExit) is { } exit && drain is { } drained && drained >= exit
                ? (drained - exit).TotalSeconds : null;
            double? containerSeconds = Gap(EncodeLifecycleEvent.FaststartStart, EncodeLifecycleEvent.FfmpegExit);
            double? stagedVerification = Gap(EncodeLifecycleEvent.VerificationStart, EncodeLifecycleEvent.StagedVerificationEnd);
            double? promotedVerification = Gap(EncodeLifecycleEvent.PromotedVerificationStart, EncodeLifecycleEvent.VerificationEnd);
            return new EncodeLifecycleTiming
            {
                QueueDispatchUtc = At(EncodeLifecycleEvent.QueueDispatch),
                PreviousQueueCompletedUtc = _previousQueueCompletedUtc,
                FfmpegStartUtc = At(EncodeLifecycleEvent.FfmpegStart),
                FirstProgressUtc = At(EncodeLifecycleEvent.FirstProgress),
                LastProgressUtc = At(EncodeLifecycleEvent.LastProgress),
                FaststartStartUtc = At(EncodeLifecycleEvent.FaststartStart),
                FfmpegExitUtc = At(EncodeLifecycleEvent.FfmpegExit),
                StdoutCompleteUtc = At(EncodeLifecycleEvent.StdoutComplete),
                StderrCompleteUtc = At(EncodeLifecycleEvent.StderrComplete),
                VerificationStartUtc = At(EncodeLifecycleEvent.VerificationStart),
                StagedVerificationEndUtc = At(EncodeLifecycleEvent.StagedVerificationEnd),
                VerificationEndUtc = At(EncodeLifecycleEvent.VerificationEnd),
                FinalizationStartUtc = At(EncodeLifecycleEvent.FinalizationStart),
                FinalizationEndUtc = At(EncodeLifecycleEvent.FinalizationEnd),
                PromotedVerificationStartUtc = At(EncodeLifecycleEvent.PromotedVerificationStart),
                QueueCompletedUtc = At(EncodeLifecycleEvent.QueueCompleted),
                ActiveEncodeSeconds = Gap(EncodeLifecycleEvent.FirstProgress, EncodeLifecycleEvent.LastProgress),
                LastProgressToFaststartSeconds = Gap(EncodeLifecycleEvent.LastProgress, EncodeLifecycleEvent.FaststartStart),
                ContainerFinalizeSeconds = containerSeconds,
                ProcessDrainSeconds = drainSeconds,
                ReaderCompletionToVerificationSeconds = drain is { } d && At(EncodeLifecycleEvent.VerificationStart) is { } verify && verify >= d ? (verify - d).TotalSeconds : null,
                // Sum only the two verification windows; promotion is separate.
                OutputVerificationSeconds = stagedVerification is { } staged && promotedVerification is { } promoted
                    ? staged + promoted
                    : At(EncodeLifecycleEvent.FinalizationStart) is null
                        ? Gap(EncodeLifecycleEvent.VerificationStart, EncodeLifecycleEvent.VerificationEnd)
                        : null,
                StagedVerificationSeconds = stagedVerification,
                PromotedVerificationSeconds = promotedVerification,
                FinalizationStageSeconds = Gap(EncodeLifecycleEvent.FinalizationStart, EncodeLifecycleEvent.FinalizationEnd),
                VerificationEndToQueueCompletedSeconds = Gap(EncodeLifecycleEvent.VerificationEnd, EncodeLifecycleEvent.QueueCompleted),
                PreviousCompletionToDispatchSeconds = _previousQueueCompletedUtc is { } previous && At(EncodeLifecycleEvent.QueueDispatch) is { } dispatch && dispatch >= previous ? (dispatch - previous).TotalSeconds : null,
                SourceStorage = EncodeStorageLocation.Describe(sourcePath),
                DestinationStorage = EncodeStorageLocation.Describe(outputPath),
                OutputSizeBytes = outputBytes,
                FfmpegReadBytes = _processReadBytes,
                FfmpegWrittenBytes = _processWriteBytes,
                ContainerFinalizeBytesPerSecond = outputBytes is > 0 && containerSeconds is > 0
                    ? outputBytes.Value / containerSeconds.Value : null
            };
        }
    }

    public static bool IsFaststartMarker(string? line) =>
        !string.IsNullOrWhiteSpace(line) &&
        line.Contains("second pass", StringComparison.OrdinalIgnoreCase) &&
        line.Contains("moov atom", StringComparison.OrdinalIgnoreCase) &&
        line.Contains("beginning", StringComparison.OrdinalIgnoreCase);

    public static void TryRecordProcessIo(System.Diagnostics.Process process, EncodeLifecycleDiagnostics? diagnostics)
    {
        if (diagnostics == null || !OperatingSystem.IsWindows()) return;
        try
        {
            if (GetProcessIoCounters(process.Handle, out IoCounters counters))
                diagnostics.RecordProcessIo((long)Math.Min(counters.ReadTransferCount, (ulong)long.MaxValue),
                    (long)Math.Min(counters.WriteTransferCount, (ulong)long.MaxValue));
        }
        catch { /* Process I/O counters are optional. */ }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessIoCounters(IntPtr process, out IoCounters counters);
}

public sealed record EncodeLifecycleTiming
{
    public DateTime? QueueDispatchUtc { get; init; }
    public DateTime? PreviousQueueCompletedUtc { get; init; }
    public DateTime? FfmpegStartUtc { get; init; }
    public DateTime? FirstProgressUtc { get; init; }
    public DateTime? LastProgressUtc { get; init; }
    public DateTime? FaststartStartUtc { get; init; }
    public DateTime? FfmpegExitUtc { get; init; }
    public DateTime? StdoutCompleteUtc { get; init; }
    public DateTime? StderrCompleteUtc { get; init; }
    public DateTime? VerificationStartUtc { get; init; }
    public DateTime? StagedVerificationEndUtc { get; init; }
    public DateTime? VerificationEndUtc { get; init; }
    public DateTime? FinalizationStartUtc { get; init; }
    public DateTime? FinalizationEndUtc { get; init; }
    public DateTime? PromotedVerificationStartUtc { get; init; }
    public DateTime? QueueCompletedUtc { get; init; }
    public double? ActiveEncodeSeconds { get; init; }
    public double? LastProgressToFaststartSeconds { get; init; }
    public double? ContainerFinalizeSeconds { get; init; }
    public double? ProcessDrainSeconds { get; init; }
    public double? ReaderCompletionToVerificationSeconds { get; init; }
    public double? OutputVerificationSeconds { get; init; }
    public double? StagedVerificationSeconds { get; init; }
    public double? PromotedVerificationSeconds { get; init; }
    public double? FinalizationStageSeconds { get; init; }
    public double? VerificationEndToQueueCompletedSeconds { get; init; }
    public double? PreviousCompletionToDispatchSeconds { get; init; }
    public EncodeStorageLocation? SourceStorage { get; init; }
    public EncodeStorageLocation? DestinationStorage { get; init; }
    public long? OutputSizeBytes { get; init; }
    public long? FfmpegReadBytes { get; init; }
    public long? FfmpegWrittenBytes { get; init; }
    public double? ContainerFinalizeBytesPerSecond { get; init; }
}

public sealed record EncodeStorageLocation(string Root, bool IsNetwork, string? SharePath)
{
    public static EncodeStorageLocation Describe(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return new("", false, null);
        string root = Path.GetPathRoot(path) ?? "";
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
            return new(root, true, root);
        if (!OperatingSystem.IsWindows() || root.Length < 2) return new(root, false, null);
        // A mapped drive may be disconnected; the mapping can still be resolved
        // even when DriveInfo cannot query its current media state.
        try
        {
            var buffer = new StringBuilder(512);
            int length = buffer.Capacity;
            if (WNetGetConnection(root[..2], buffer, ref length) == 0)
                return new(root, true, buffer.ToString());
        }
        catch { /* Fall back to the drive type if the mapping query is unavailable. */ }
        try { return new(root, new DriveInfo(root).DriveType == DriveType.Network, null); }
        catch { return new(root, false, null); }
    }

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetGetConnection(string localName, StringBuilder remoteName, ref int length);
}
