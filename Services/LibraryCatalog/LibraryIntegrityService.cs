using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace MediaFlux.Services.LibraryCatalog;

public sealed class LibraryIntegrityScrubService
{
    public const int CurrentMethodVersion = 1;
    private const double QuickWindowSeconds = 1.0;
    private readonly string _ffmpegPath;
    private readonly IMediaToolProcessRunner _runner;
    private readonly string _toolVersion;

    public LibraryIntegrityScrubService(string ffmpegPath, IMediaToolProcessRunner? runner = null, string toolVersion = "")
    {
        _ffmpegPath = ffmpegPath ?? ""; _runner = runner ?? new MediaToolProcessRunner();
        _toolVersion = string.IsNullOrWhiteSpace(toolVersion) ? DescribeTool(_ffmpegPath) : toolVersion;
    }

    public async Task<LibraryIntegrityRunResult> ScrubAsync(LibraryIntegrityQueueItem item,
        Action<LibraryIntegrityProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        DateTime checkedUtc = DateTime.UtcNow; var timer = Stopwatch.StartNew();
        if (!File.Exists(item.FullPath)) return Result(item, LibraryIntegrityResultState.Unavailable,
            LibraryIntegrityErrorCategory.FileDisappeared, "The file disappeared before integrity verification.", checkedUtc, timer.Elapsed, 0, 0);
        if (string.IsNullOrWhiteSpace(item.VideoCodec)) return Result(item, LibraryIntegrityResultState.Failed,
            LibraryIntegrityErrorCategory.MissingVideoStream, "Current catalog metadata does not contain a primary video stream.", checkedUtc, timer.Elapsed, 0, 0);
        if (!File.Exists(_ffmpegPath)) return Result(item, LibraryIntegrityResultState.Unavailable,
            LibraryIntegrityErrorCategory.ToolFailure, "FFmpeg is unavailable for integrity verification.", checkedUtc, timer.Elapsed, 0, 0);

        FileVersion before;
        try { before = ReadVersion(item.FullPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return Result(item, LibraryIntegrityResultState.Unavailable, LibraryIntegrityErrorCategory.StorageUnavailable, Concise(ex.Message), checkedUtc, timer.Elapsed, 0, 0); }
        if (!MatchesCatalog(before, item)) return Result(item, LibraryIntegrityResultState.Stale,
            LibraryIntegrityErrorCategory.FileChanged, "The file changed after it was cataloged. Re-analyze it before scrubbing.", checkedUtc, timer.Elapsed, 0, 0);

        try
        {
            LibraryIntegrityRunResult result = item.ScrubType == LibraryIntegrityScrubType.Quick
                ? await QuickAsync(item, before, checkedUtc, timer, progress, cancellationToken).ConfigureAwait(false)
                : await FullAsync(item, before, checkedUtc, timer, progress, cancellationToken).ConfigureAwait(false);
            FileVersion after = ReadVersion(item.FullPath);
            if (!before.Equals(after)) return Result(item, LibraryIntegrityResultState.Stale,
                LibraryIntegrityErrorCategory.FileChanged, "The file changed while integrity verification was running. The result was discarded.", checkedUtc, timer.Elapsed, 0, 0);
            return result;
        }
        catch (OperationCanceledException)
        {
            return Result(item, LibraryIntegrityResultState.Cancelled, LibraryIntegrityErrorCategory.Cancelled,
                "Integrity verification was cancelled. No media file was changed.", checkedUtc, timer.Elapsed, 0, 0);
        }
        catch (FileNotFoundException)
        { return Result(item, LibraryIntegrityResultState.Unavailable, LibraryIntegrityErrorCategory.FileDisappeared, "The file disappeared during integrity verification.", checkedUtc, timer.Elapsed, 0, 0); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return Result(item, LibraryIntegrityResultState.Unavailable, LibraryIntegrityErrorCategory.StorageUnavailable, Concise(ex.Message), checkedUtc, timer.Elapsed, 0, 0); }
    }

    internal static IReadOnlyList<double> BuildQuickPositions(double? durationSeconds)
    {
        if (durationSeconds is not > 0 || !double.IsFinite(durationSeconds.Value)) return new[] { 0d };
        double duration = durationSeconds.Value;
        double end = Math.Max(0, duration - Math.Min(QuickWindowSeconds, duration));
        return new[] { 0d, Math.Max(0, duration / 2d - QuickWindowSeconds / 2d), end }
            .DistinctBy(value => Math.Round(value, 2)).OrderBy(value => value).ToArray();
    }

    private async Task<LibraryIntegrityRunResult> QuickAsync(LibraryIntegrityQueueItem item, FileVersion version,
        DateTime checkedUtc, Stopwatch timer, Action<LibraryIntegrityProgress>? progress, CancellationToken token)
    {
        IReadOnlyList<double> positions = BuildQuickPositions(item.DurationSeconds); double checkedDuration = 0;
        for (int index = 0; index < positions.Count; index++)
        {
            token.ThrowIfCancellationRequested(); double position = positions[index];
            progress?.Invoke(new(item.Id, item.FileId, item.FullPath, item.ScrubType, index * 100d / positions.Count,
                timer.Elapsed, null, $"Decoding representative window {index + 1} of {positions.Count}"));
            MediaToolProcessResult process = await _runner.RunAsync(new MediaToolProcessRequest
            {
                FileName = _ffmpegPath, Timeout = TimeSpan.FromMinutes(2), SendQuitOnCancellation = true,
                Arguments = QuickArguments(item.FullPath, position)
            }, token).ConfigureAwait(false);
            if (process.TimedOut) return Result(item, LibraryIntegrityResultState.Failed, LibraryIntegrityErrorCategory.ToolFailure,
                $"Representative decode timed out near {position:0.##} seconds.", checkedUtc, timer.Elapsed, EstimateQuickBytes(version.Size, item.DurationSeconds, checkedDuration), checkedDuration, positions);
            if (process.ExitCode != 0)
            {
                (LibraryIntegrityErrorCategory category, string detail) = Classify(process.StandardError, audioIncluded: false);
                return Result(item, LibraryIntegrityResultState.Failed, category,
                    $"Representative decode failed near {position:0.##} seconds. {detail}".Trim(), checkedUtc, timer.Elapsed,
                    EstimateQuickBytes(version.Size, item.DurationSeconds, checkedDuration), checkedDuration, positions);
            }
            int decodedFrames = Regex.Matches(process.StandardOutput ?? "", @"(?m)^frame=(\d+)\r?$")
                .Cast<Match>().Select(match => int.TryParse(match.Groups[1].Value, out int frames) ? frames : 0).DefaultIfEmpty().Max();
            if (decodedFrames <= 0)
            {
                return Result(item, LibraryIntegrityResultState.Failed, LibraryIntegrityErrorCategory.TruncatedMedia,
                    $"No video frame could be decoded in the representative region near {position:0.##} seconds.", checkedUtc, timer.Elapsed,
                    EstimateQuickBytes(version.Size, item.DurationSeconds, checkedDuration), checkedDuration, positions);
            }
            checkedDuration += Math.Min(QuickWindowSeconds, Math.Max(0, (item.DurationSeconds ?? QuickWindowSeconds) - position));
        }
        progress?.Invoke(new(item.Id, item.FileId, item.FullPath, item.ScrubType, 100, timer.Elapsed, TimeSpan.Zero, "Quick Scrub passed"));
        return Result(item, LibraryIntegrityResultState.Passed, LibraryIntegrityErrorCategory.None,
            $"Decoded {positions.Count} representative video region(s) without fatal errors.", checkedUtc, timer.Elapsed,
            EstimateQuickBytes(version.Size, item.DurationSeconds, checkedDuration), checkedDuration, positions);
    }

    private async Task<LibraryIntegrityRunResult> FullAsync(LibraryIntegrityQueueItem item, FileVersion version,
        DateTime checkedUtc, Stopwatch timer, Action<LibraryIntegrityProgress>? progress, CancellationToken token)
    {
        double lastMediaSeconds = 0;
        void ParseProgress(string line)
        {
            if (!line.StartsWith("out_time_us=", StringComparison.Ordinal) || !long.TryParse(line[12..], out long micros)) return;
            lastMediaSeconds = Math.Max(lastMediaSeconds, micros / 1_000_000d);
            double? percent = item.DurationSeconds is > 0 ? Math.Clamp(lastMediaSeconds * 100 / item.DurationSeconds.Value, 0, 100) : null;
            TimeSpan? remaining = percent is > 0 and < 100 ? TimeSpan.FromSeconds(timer.Elapsed.TotalSeconds * (100 - percent.Value) / percent.Value) : null;
            progress?.Invoke(new(item.Id, item.FileId, item.FullPath, item.ScrubType, percent, timer.Elapsed, remaining, "Decoding complete media streams"));
        }
        MediaToolProcessResult process = await _runner.RunAsync(new MediaToolProcessRequest
        {
            FileName = _ffmpegPath, Timeout = Timeout.InfiniteTimeSpan, SendQuitOnCancellation = true,
            Arguments = FullArguments(item.FullPath, item.AudioStreamCount > 0), StandardOutputLineCallback = ParseProgress
        }, token).ConfigureAwait(false);
        if (process.ExitCode != 0 || process.TimedOut)
        {
            (LibraryIntegrityErrorCategory category, string detail) = process.TimedOut
                ? (LibraryIntegrityErrorCategory.ToolFailure, "Full decode timed out.") : Classify(process.StandardError, item.AudioStreamCount > 0);
            return Result(item, LibraryIntegrityResultState.Failed, category, detail, checkedUtc, timer.Elapsed,
                EstimateFullBytes(version.Size, item.DurationSeconds, lastMediaSeconds), lastMediaSeconds);
        }
        if (item.DurationSeconds is > 0 && lastMediaSeconds > 0)
        {
            double allowedShortfall = Math.Max(1.0, item.DurationSeconds.Value * .03);
            if (lastMediaSeconds < item.DurationSeconds.Value - allowedShortfall)
            {
                return Result(item, LibraryIntegrityResultState.Failed, LibraryIntegrityErrorCategory.TruncatedMedia,
                    $"Full decode ended at {lastMediaSeconds:0.##} seconds, materially before the catalog duration of {item.DurationSeconds:0.##} seconds.",
                    checkedUtc, timer.Elapsed, EstimateFullBytes(version.Size, item.DurationSeconds, lastMediaSeconds), lastMediaSeconds);
            }
        }
        progress?.Invoke(new(item.Id, item.FileId, item.FullPath, item.ScrubType, 100, timer.Elapsed, TimeSpan.Zero, "Full Scrub passed"));
        return Result(item, LibraryIntegrityResultState.Passed, LibraryIntegrityErrorCategory.None,
            item.AudioStreamCount > 0 ? "Decoded the complete primary video and available audio streams without fatal errors." : "Decoded the complete primary video stream without fatal errors.",
            checkedUtc, timer.Elapsed, version.Size, item.DurationSeconds ?? lastMediaSeconds);
    }

    internal static IReadOnlyList<string> QuickArguments(string path, double position) => new[]
    {
        "-hide_banner", "-v", "error", "-xerror", "-err_detect", "explode", "-ss", position.ToString("0.###", CultureInfo.InvariantCulture),
        "-i", path, "-map", "0:v:0", "-t", QuickWindowSeconds.ToString("0.###", CultureInfo.InvariantCulture), "-an", "-sn", "-dn",
        "-progress", "pipe:1", "-nostats", "-f", "null", "-"
    };

    internal static IReadOnlyList<string> FullArguments(string path, bool includeAudio)
    {
        var args = new List<string> { "-hide_banner", "-v", "error", "-xerror", "-err_detect", "explode", "-i", path, "-map", "0:v:0" };
        if (includeAudio) args.AddRange(new[] { "-map", "0:a?" }); else args.Add("-an");
        args.AddRange(new[] { "-sn", "-dn", "-progress", "pipe:1", "-nostats", "-f", "null", "-" }); return args;
    }

    internal static (LibraryIntegrityErrorCategory Category, string Detail) Classify(string diagnostics, bool audioIncluded)
    {
        string value = diagnostics ?? ""; string lower = value.ToLowerInvariant();
        LibraryIntegrityErrorCategory category =
            lower.Contains("no such file") ? LibraryIntegrityErrorCategory.FileDisappeared :
            lower.Contains("moov atom not found") || lower.Contains("invalid data found when processing input") ? LibraryIntegrityErrorCategory.ContainerReadFailure :
            lower.Contains("end of file") || lower.Contains("truncat") || lower.Contains("partial file") ? LibraryIntegrityErrorCategory.TruncatedMedia :
            lower.Contains("non-monoton") || lower.Contains("invalid pts") || lower.Contains("invalid dts") || lower.Contains("timestamp") ? LibraryIntegrityErrorCategory.InvalidTimestamps :
            audioIncluded && (lower.Contains("audio") || lower.Contains("channel")) ? LibraryIntegrityErrorCategory.AudioDecodeError :
            lower.Contains("decod") || lower.Contains("corrupt") || lower.Contains("invalid nal") ? LibraryIntegrityErrorCategory.VideoDecodeError :
            LibraryIntegrityErrorCategory.ContainerReadFailure;
        string detail = Regex.Split(value.Trim(), "[\r\n]+").Select(line => line.Trim()).LastOrDefault(line => line.Length > 0) ?? "FFmpeg reported an ambiguous media read/decode failure.";
        return (category, Concise(detail));
    }

    private LibraryIntegrityRunResult Result(LibraryIntegrityQueueItem item, LibraryIntegrityResultState state,
        LibraryIntegrityErrorCategory category, string details, DateTime checkedUtc, TimeSpan elapsed, long bytes, double duration,
        IReadOnlyList<double>? positions = null) => new(new LibraryIntegrityResultWrite(item.FileId, CurrentMethodVersion, item.ScrubType,
            state, checkedUtc, item.SizeBytes, item.LastWriteUtc, item.VolumeId, item.FileIdentity, Math.Max(0, bytes), Math.Max(0, duration),
            Math.Max(0, elapsed.TotalSeconds), category, Concise(details), _toolVersion), positions ?? Array.Empty<double>());

    private static bool MatchesCatalog(FileVersion version, LibraryIntegrityQueueItem item) =>
        version.Size == item.SizeBytes && version.LastWriteUtc.Ticks == item.LastWriteUtc.Ticks;
    private static FileVersion ReadVersion(string path) { var info = new FileInfo(path); return new(info.Length, info.LastWriteTimeUtc); }
    private static long EstimateQuickBytes(long size, double? totalDuration, double checkedDuration) => totalDuration is > 0 ? (long)Math.Min(size, size * checkedDuration / totalDuration.Value) : 0;
    private static long EstimateFullBytes(long size, double? duration, double checkedDuration) => duration is > 0 ? (long)Math.Min(size, size * checkedDuration / duration.Value) : 0;
    private static string Concise(string? value) { string text = Regex.Replace(value ?? "", "\\s+", " ").Trim(); return text.Length <= 600 ? text : text[..600]; }
    private static string DescribeTool(string path)
    {
        try
        {
            if (!File.Exists(path)) return "";
            FileVersionInfo info = FileVersionInfo.GetVersionInfo(path);
            string version = info.ProductVersion ?? info.FileVersion ?? "";
            return string.IsNullOrWhiteSpace(version) ? $"ffmpeg-file:{File.GetLastWriteTimeUtc(path):O}" : $"ffmpeg:{version}";
        }
        catch { return "ffmpeg"; }
    }
    private sealed record FileVersion(long Size, DateTime LastWriteUtc);
}

public sealed class LibraryIntegrityCoordinator : IDisposable
{
    private readonly ILibraryIntegrityCatalog _catalog; private readonly LibraryIntegrityScrubService _scrubber;
    private readonly LibraryStorageScheduler _scheduler; private readonly Func<bool> _isEncodingActive;
    private readonly SemaphoreSlim _signal = new(0, 1); private readonly CancellationTokenSource _shutdown = new();
    private CancellationTokenSource _activeCancellation = new(); private readonly Task _worker; private bool _disposed;
    public event Action<LibraryIntegrityProgress>? ProgressChanged;

    public LibraryIntegrityCoordinator(ILibraryIntegrityCatalog catalog, LibraryIntegrityScrubService scrubber,
        LibraryStorageScheduler scheduler, Func<bool>? isEncodingActive = null)
    {
        _catalog = catalog; _scrubber = scrubber; _scheduler = scheduler; _isEncodingActive = isEncodingActive ?? (() => false);
        _catalog.RecoverInterruptedIntegrity(); _worker = Task.Run(() => WorkerAsync(_shutdown.Token)); Signal();
    }

    public IReadOnlyList<long> QueueFiles(IEnumerable<long> fileIds, LibraryIntegrityScrubType type, string batchId = "")
    {
        string batch = string.IsNullOrWhiteSpace(batchId) ? Guid.NewGuid().ToString("N") : batchId;
        long[] ids = fileIds.Distinct().Select(id => _catalog.EnqueueIntegrity(id, type, batch, type == LibraryIntegrityScrubType.Full ? 1 : 3)).ToArray(); Signal(); return ids;
    }
    public void CancelRunning() { _activeCancellation.Cancel(); Signal(); }

    private async Task WorkerAsync(CancellationToken shutdown)
    {
        while (!shutdown.IsCancellationRequested)
        {
            if (_isEncodingActive()) { try { await Task.Delay(1500, shutdown).ConfigureAwait(false); } catch { break; } continue; }
            IReadOnlyList<LibraryIntegrityQueueItem> items = _catalog.ClaimIntegrityBatch(2, DateTime.UtcNow);
            if (items.Count == 0) { try { await _signal.WaitAsync(TimeSpan.FromSeconds(10), shutdown).ConfigureAwait(false); } catch { break; } continue; }
            var batch = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
            CancellationTokenSource previous = Interlocked.Exchange(ref _activeCancellation, batch);
            if (!ReferenceEquals(previous, batch)) previous.Dispose();
            await Task.WhenAll(items.Select(item => ProcessAsync(item, batch.Token))).ConfigureAwait(false);
        }
    }

    private async Task ProcessAsync(LibraryIntegrityQueueItem item, CancellationToken token)
    {
        try
        {
            await using IAsyncDisposable lease = await _scheduler.AcquireAsync(item.FullPath, item.VolumeId, token).ConfigureAwait(false);
            if (_isEncodingActive()) { await Task.Delay(1500, token).ConfigureAwait(false); }
            LibraryIntegrityRunResult run = await _scrubber.ScrubAsync(item, value => ProgressChanged?.Invoke(value), token).ConfigureAwait(false);
            if (run.Result.State == LibraryIntegrityResultState.Cancelled) _catalog.CancelIntegrityItem(item.Id, run.Result);
            else _catalog.CompleteIntegrityItem(item.Id, run.Result, run.Result.State is LibraryIntegrityResultState.Failed or LibraryIntegrityResultState.Unavailable ? run.Result.Details : "");
        }
        catch (OperationCanceledException)
        {
            var result = new LibraryIntegrityResultWrite(item.FileId, LibraryIntegrityScrubService.CurrentMethodVersion, item.ScrubType,
                LibraryIntegrityResultState.Cancelled, DateTime.UtcNow, item.SizeBytes, item.LastWriteUtc, item.VolumeId, item.FileIdentity,
                0, 0, 0, LibraryIntegrityErrorCategory.Cancelled, "Integrity verification was cancelled.", "");
            _catalog.CancelIntegrityItem(item.Id, result);
        }
        catch (Exception ex)
        {
            var result = new LibraryIntegrityResultWrite(item.FileId, LibraryIntegrityScrubService.CurrentMethodVersion, item.ScrubType,
                LibraryIntegrityResultState.Unavailable, DateTime.UtcNow, item.SizeBytes, item.LastWriteUtc, item.VolumeId, item.FileIdentity,
                0, 0, 0, LibraryIntegrityErrorCategory.StorageUnavailable, ex.Message, "");
            _catalog.CompleteIntegrityItem(item.Id, result, ex.Message);
        }
    }

    private void Signal() { try { _signal.Release(); } catch (SemaphoreFullException) { } }
    public void Dispose() { if (_disposed) return; _disposed = true; _shutdown.Cancel(); try { _activeCancellation.Cancel(); } catch (ObjectDisposedException) { } Signal(); try { _worker.Wait(TimeSpan.FromSeconds(10)); } catch { } _activeCancellation.Dispose(); _shutdown.Dispose(); _signal.Dispose(); }
}
