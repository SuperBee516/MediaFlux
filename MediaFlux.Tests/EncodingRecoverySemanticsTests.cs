using MediaFlux.Models;
using MediaFlux.Services;
using Xunit;
using System.Collections.Concurrent;

namespace MediaFlux.Tests;

public sealed class EncodingRecoverySemanticsTests
{
    [Fact]
    public void ProcessSuccessDoesNotImplyMediaRecoverySuccess()
    {
        var outcome = new EncodingRecoveryOutcome(
            EncodingRecoveryKind.VideoDecode,
            EncodingRecoveryFailureClass.SourceVideoCorruption,
            EncodingRecoveryMode.Strict,
            EncodingRecoveryMode.Tolerant,
            1, 1, EncodingRecoveryResult.Succeeded,
            "FFmpeg completed; validation rejected material frame loss.",
            EncodingRecoveryProcessResult.Succeeded,
            EncodingRecoveryDisposition.Rejected,
            SourceDurationSeconds: 100,
            ProducedDurationSeconds: 100,
            ExpectedFrameCount: 3000,
            ProducedFrameCount: 2500,
            DroppedFrameOrPacketCount: 500,
            DurationDeltaSeconds: 0,
            DiagnosticReason: "Material source frame loss remained after tolerant decode.");

        Assert.Equal(EncodingRecoveryProcessResult.Succeeded, outcome.ProcessResult);
        Assert.Equal(EncodingRecoveryDisposition.Rejected, outcome.MediaDisposition);
        Assert.Equal(500, outcome.ExpectedFrameCount - outcome.ProducedFrameCount);
    }

    [Theory]
    [InlineData("[h264] Invalid NAL unit size\nError submitting packet to decoder: Invalid data found when processing input", EncodingSourceFailureType.VideoBitstreamCorruption, true)]
    [InlineData("Non-monotonic presentation timestamps were detected", EncodingSourceFailureType.TimelineCorruption, true)]
    [InlineData("Error while decoding stream #0:1: channel element", EncodingSourceFailureType.AudioBitstreamCorruption, true)]
    [InlineData("NV_ENC_ERR_INVALID_PARAM", EncodingSourceFailureType.GpuEncoderFailure, false)]
    public void SourceFailureClassificationIsConservative(string diagnostic, EncodingSourceFailureType type, bool candidate)
    {
        EncodingSourceFailureClassification result = EncodingSourceFailureClassifier.Classify(diagnostic);
        Assert.Equal(type, result.Type);
        Assert.Equal(candidate, result.IsRecoveryCandidate);
    }

    [Theory]
    [InlineData("h264")]
    [InlineData("audio")]
    [InlineData("NV_ENC_ERR_INVALID_PARAM")]
    [InlineData("Error writing trailer of output.mp4")]
    [InlineData("Permission denied while writing output.mp4")]
    public void GenericOrOutputFailuresDoNotEnableSourceRepair(string diagnostic)
    {
        Assert.False(EncodingSourceFailureClassifier.Classify(diagnostic).IsRecoveryCandidate);
    }

    [Fact]
    public void RepairedProbeWithShorterDurationIsNotEquivalentToOriginal()
    {
        MediaProbeResult original = Probe(100, 3000, "video", "audio");
        MediaProbeResult repaired = Probe(98, 2940, "video", "audio");
        Assert.False(SourceTimelineRecoveryService.IsEquivalent(original, repaired, out string reason));
        Assert.Contains("duration", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RepairedProbeWithMissingRequiredStreamIsNotEquivalentToOriginal()
    {
        Assert.False(SourceTimelineRecoveryService.IsEquivalent(Probe(100, 3000, "video", "audio"), Probe(100, 3000, "video"), out string reason));
        Assert.Contains("topology", reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("audio=1→0", reason, StringComparison.Ordinal);
        Assert.Contains("Missing-after=audio/aac", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Mp4ToMatroskaTopologyIgnoresUnspecifiedLanguageIdentityChanges()
    {
        MediaProbeResult original = new()
        {
            Success = true, DurationSeconds = 100,
            Streams = new[]
            {
                new MediaProbeStreamInfo { Index = 0, Id = "1", CodecType = "video", CodecName = "H264", DurationSeconds = 100, FrameCount = 3000 },
                new MediaProbeStreamInfo { Index = 1, Id = "2", CodecType = "audio", CodecName = "AAC", Language = "und", DurationSeconds = 100 }
            }
        };
        MediaProbeResult normalized = new()
        {
            Success = true, DurationSeconds = 100,
            Streams = new[]
            {
                new MediaProbeStreamInfo { Index = 0, Id = "V_MPEG4/ISO/AVC", CodecType = "video", CodecName = "h264", DurationSeconds = 100, FrameCount = 3000 },
                new MediaProbeStreamInfo { Index = 1, Id = "A_AAC", CodecType = "audio", CodecName = "aac", Language = "", DurationSeconds = 100 }
            }
        };

        Assert.True(SourceTimelineRecoveryService.IsEquivalent(original, normalized, out string reason), reason);
    }

    [Theory]
    [InlineData("subtitle")]
    [InlineData("attachment")]
    public void MissingRequiredAncillaryStreamRemainsRejected(string missingType)
    {
        MediaProbeResult original = Probe(100, 3000, "video", "audio", missingType);
        MediaProbeResult normalized = Probe(100, 3000, "video", "audio");

        Assert.False(SourceTimelineRecoveryService.IsEquivalent(original, normalized, out string reason));
        Assert.Contains($"{missingType}=1→0", reason, StringComparison.Ordinal);
        Assert.Contains("Missing-after=", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void LostRequiredVideoTopologyRemainsRejected()
    {
        Assert.False(SourceTimelineRecoveryService.IsEquivalent(
            Probe(100, 3000, "video", "video", "audio"),
            Probe(100, 3000, "video", "audio"),
            out string reason));
        Assert.Contains("video=2→1", reason, StringComparison.Ordinal);
    }

    private static MediaProbeResult Probe(double duration, long frames, params string[] types) => new()
    {
        Success = true, DurationSeconds = duration,
        Streams = types.Select((type, index) => new MediaProbeStreamInfo
        {
            Index = index, CodecType = type, CodecName = type == "video" ? "h264" : "aac",
            DurationSeconds = duration, FrameCount = type == "video" ? frames : null
        }).ToArray()
    };

    [Fact]
    public async Task CancellationRemovesPartialAndNeverPromotesRepair()
    {
        using TempFiles files = new();
        using var cts = new CancellationTokenSource();
        var runner = new BlockingRunner();
        Task<SourceTimelineRecoveryResult> operation = new SourceTimelineRecoveryService("ffmpeg", files.FfprobePath, runner)
            .TryNormalizeAsync(files.SourcePath, files.OutputPath, files.Probe, cts.Token);
        await runner.Started.Task;
        Assert.True(File.Exists(files.OutputPath + ".partial"));
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.False(File.Exists(files.OutputPath + ".partial"));
        Assert.False(File.Exists(files.OutputPath));
        Assert.True(File.Exists(files.SourcePath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrThrowingRepairRemovesPartialAndDoesNotPromote(bool throws)
    {
        using TempFiles files = new();
        var runner = new FailingRunner(throws);
        SourceTimelineRecoveryResult result = await new SourceTimelineRecoveryService("ffmpeg", files.FfprobePath, runner)
            .TryNormalizeAsync(files.SourcePath, files.OutputPath, files.Probe, CancellationToken.None);
        Assert.False(result.Success);
        Assert.False(File.Exists(files.OutputPath + ".partial"));
        Assert.False(File.Exists(files.OutputPath));
        Assert.True(File.Exists(files.SourcePath));
    }

    [Fact]
    public async Task SuccessfulRepairPromotesOnlyAfterProbeAndEquivalence()
    {
        using TempFiles files = new();
        var runner = new SuccessfulRunner();
        SourceTimelineRecoveryResult result = await new SourceTimelineRecoveryService("ffmpeg", files.FfprobePath, runner)
            .TryNormalizeAsync(files.SourcePath, files.OutputPath, files.Probe, CancellationToken.None);
        Assert.True(result.Success);
        Assert.True(File.Exists(files.OutputPath));
        Assert.False(File.Exists(files.OutputPath + ".partial"));
        Assert.NotNull(result.RepairedProbe);
        Assert.True(File.Exists(files.SourcePath));
    }

    [Fact]
    public async Task TimelineRepairUsesExplicitMatroskaMuxerAndApprovedStreamMaps()
    {
        using TempFiles files = new();
        var runner = new SuccessfulRunner();
        SourceTimelineRecoveryResult result = await new SourceTimelineRecoveryService("ffmpeg", files.FfprobePath, runner)
            .TryNormalizeAsync(files.SourcePath, files.OutputPath, files.Probe, CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(runner.FfmpegRequest);
        IReadOnlyList<string> arguments = runner.FfmpegRequest!.Arguments;
        Assert.Equal("matroska", arguments[arguments.ToList().IndexOf("-f") + 1]);
        Assert.Contains("0:v:0", arguments);
        Assert.Contains("0:a?", arguments);
        Assert.Contains("0:s?", arguments);
        Assert.Contains("0:t?", arguments);
        Assert.Contains("-dn", arguments);
        Assert.Contains("-map_metadata", arguments);
        Assert.Contains("-map_chapters", arguments);
        Assert.DoesNotContain("0:d?", arguments);
        Assert.DoesNotContain("0", arguments.Where((value, index) => index > 0 && arguments[index - 1] == "-map"));
    }

    [Fact]
    public async Task StreamCopyNormalizationThatPreservesNonMonotonicTimingIsNotAccepted()
    {
        using TempFiles files = new();
        SourceTimelineRecoveryResult result = await new SourceTimelineRecoveryService("ffmpeg", files.FfprobePath, new NonMonotonicTimingRunner())
            .TryNormalizeAsync(files.SourcePath, files.OutputPath, files.Probe, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(SourceTimelineRecoveryFailureKind.StreamCopyPreservedUnsafeTiming, result.FailureKind);
        Assert.Contains("safe monotonic timeline", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(files.OutputPath));
    }

    [Fact]
    public async Task RecoverableBaselineRequiresCompleteDecodeCoverageBeforeItCanBeUsed()
    {
        var runner = new BaselineRunner("frame=2900\nout_time=00:01:40.000000\nprogress=end\n");
        RecoverableSourceBaselineResult result = await new RecoverableSourceBaselineService("ffmpeg", runner)
            .MeasureAsync("corrupt-source.mp4", 100, CancellationToken.None);

        Assert.True(result.Success, result.Reason);
        Assert.Equal(2900, result.Baseline!.DecodedVideoFrameCount);
        Assert.Equal(100, result.Baseline.TailPresentationSeconds);
        Assert.Contains("0:v:0", runner.Request!.Arguments);
        Assert.Contains("-fps_mode", runner.Request.Arguments);
        Assert.Contains("passthrough", runner.Request.Arguments);
        Assert.Contains("null", runner.Request.Arguments);
    }

    [Fact]
    public async Task RecoverableBaselineRejectsPrematureDecodeTail()
    {
        var runner = new BaselineRunner("frame=2900\nout_time=00:01:38.700000\nprogress=end\n");
        RecoverableSourceBaselineResult result = await new RecoverableSourceBaselineService("ffmpeg", runner)
            .MeasureAsync("corrupt-source.mp4", 100, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("does not reach", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConcurrentRepairsUseIsolatedPartialAndPromotedPaths()
    {
        using TempFiles first = new();
        using TempFiles second = new();
        using var firstCancellation = new CancellationTokenSource();
        using var secondCancellation = new CancellationTokenSource();
        var runner = new ConcurrentBlockingRunner(2);
        Task<SourceTimelineRecoveryResult> firstTask = new SourceTimelineRecoveryService("ffmpeg", first.FfprobePath, runner)
            .TryNormalizeAsync(first.SourcePath, first.OutputPath, first.Probe, firstCancellation.Token);
        Task<SourceTimelineRecoveryResult> secondTask = new SourceTimelineRecoveryService("ffmpeg", second.FfprobePath, runner)
            .TryNormalizeAsync(second.SourcePath, second.OutputPath, second.Probe, secondCancellation.Token);
        await runner.AllStarted.Task;
        string[] partials = runner.PartialPaths.ToArray();
        Assert.Equal(2, partials.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.DoesNotContain(partials, path => path.Equals(first.SourcePath, StringComparison.OrdinalIgnoreCase) || path.Equals(second.SourcePath, StringComparison.OrdinalIgnoreCase));
        firstCancellation.Cancel();
        secondCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => firstTask);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => secondTask);
        Assert.All(partials, path => Assert.False(File.Exists(path)));
        Assert.False(File.Exists(first.OutputPath));
        Assert.False(File.Exists(second.OutputPath));
    }

    private sealed class TempFiles : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "mediaflux-recovery-" + Guid.NewGuid().ToString("N"));
        public TempFiles()
        {
            Directory.CreateDirectory(_directory);
            SourcePath = Path.Combine(_directory, "source.mkv");
            OutputPath = Path.Combine(_directory, "repaired.mkv");
            FfprobePath = Path.Combine(_directory, "ffprobe.exe");
            File.WriteAllText(SourcePath, "original");
            File.WriteAllText(FfprobePath, "test");
            Probe = Probe(10, 300, "video", "audio");
        }
        public string SourcePath { get; }
        public string OutputPath { get; }
        public string FfprobePath { get; }
        public MediaProbeResult Probe { get; }
        public void Dispose() { try { Directory.Delete(_directory, true); } catch { } }
    }

    private sealed class BlockingRunner : IMediaToolProcessRunner
    {
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<MediaToolProcessResult> RunAsync(MediaToolProcessRequest request, CancellationToken token = default)
        {
            string output = request.Arguments[^1];
            File.WriteAllText(output, "partial");
            Started.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new MediaToolProcessResult();
        }
    }

    private sealed class FailingRunner(bool throws) : IMediaToolProcessRunner
    {
        public Task<MediaToolProcessResult> RunAsync(MediaToolProcessRequest request, CancellationToken token = default)
        {
            File.WriteAllText(request.Arguments[^1], "partial");
            if (throws) throw new InvalidOperationException("synthetic repair failure");
            return Task.FromResult(new MediaToolProcessResult { ExitCode = 1, StandardError = "synthetic repair failure" });
        }
    }

    private sealed class SuccessfulRunner : IMediaToolProcessRunner
    {
        public MediaToolProcessRequest? FfmpegRequest { get; private set; }

        public Task<MediaToolProcessResult> RunAsync(MediaToolProcessRequest request, CancellationToken token = default)
        {
            if (request.FileName.Equals("ffmpeg", StringComparison.OrdinalIgnoreCase))
            {
                FfmpegRequest = request;
                File.WriteAllText(request.Arguments[^1], "partial");
                return Task.FromResult(new MediaToolProcessResult { ExitCode = 0 });
            }
            if (request.Arguments.Any(argument => argument.Contains("frame=best_effort_timestamp_time", StringComparison.OrdinalIgnoreCase)))
                return Task.FromResult(new MediaToolProcessResult { ExitCode = 0, StandardOutput = "0\n0.033\n0.066\n0.099\n" });
            if (request.Arguments.Contains("-print_format"))
                return Task.FromResult(new MediaToolProcessResult
                {
                    ExitCode = 0,
                    StandardOutput = "{\"format\":{\"duration\":\"10\"},\"streams\":[{\"index\":0,\"codec_type\":\"video\",\"codec_name\":\"h264\",\"duration\":\"10\",\"nb_frames\":\"300\",\"r_frame_rate\":\"30/1\",\"avg_frame_rate\":\"30/1\"},{\"index\":1,\"codec_type\":\"audio\",\"codec_name\":\"aac\",\"duration\":\"10\"}],\"chapters\":[]}"
                });
            return Task.FromResult(new MediaToolProcessResult { ExitCode = 0, StandardOutput = "r_frame_rate=30/1\navg_frame_rate=30/1\ntime_base=1/1000\nstart_time=0\nduration=10\n" });
        }
    }

    private sealed class ConcurrentBlockingRunner(int expected) : IMediaToolProcessRunner
    {
        private readonly ConcurrentBag<string> _partials = new();
        private int _started;
        public TaskCompletionSource<bool> AllStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyCollection<string> PartialPaths => _partials;
        public async Task<MediaToolProcessResult> RunAsync(MediaToolProcessRequest request, CancellationToken token = default)
        {
            string output = request.Arguments[^1];
            File.WriteAllText(output, "partial");
            _partials.Add(output);
            if (Interlocked.Increment(ref _started) == expected) AllStarted.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new MediaToolProcessResult();
        }
    }

    private sealed class NonMonotonicTimingRunner : IMediaToolProcessRunner
    {
        public Task<MediaToolProcessResult> RunAsync(MediaToolProcessRequest request, CancellationToken token = default)
        {
            if (request.FileName.Equals("ffmpeg", StringComparison.OrdinalIgnoreCase))
            {
                File.WriteAllText(request.Arguments[^1], "partial");
                return Task.FromResult(new MediaToolProcessResult { ExitCode = 0 });
            }
            if (request.Arguments.Any(argument => argument.Contains("frame=best_effort_timestamp_time", StringComparison.OrdinalIgnoreCase)))
                return Task.FromResult(new MediaToolProcessResult { ExitCode = 0, StandardOutput = "0\n0.033\n0.066\n0.020\n" });
            if (request.Arguments.Contains("-print_format"))
                return Task.FromResult(new MediaToolProcessResult
                {
                    ExitCode = 0,
                    StandardOutput = "{\"format\":{\"duration\":\"10\"},\"streams\":[{\"index\":0,\"codec_type\":\"video\",\"codec_name\":\"h264\",\"duration\":\"10\",\"nb_frames\":\"300\",\"r_frame_rate\":\"30/1\",\"avg_frame_rate\":\"30/1\"},{\"index\":1,\"codec_type\":\"audio\",\"codec_name\":\"aac\",\"duration\":\"10\"}],\"chapters\":[]}"
                });
            return Task.FromResult(new MediaToolProcessResult { ExitCode = 0, StandardOutput = "r_frame_rate=30/1\navg_frame_rate=30/1\ntime_base=1/1000\nstart_time=0\nduration=10\n" });
        }
    }

    private sealed class BaselineRunner(string progress) : IMediaToolProcessRunner
    {
        public MediaToolProcessRequest? Request { get; private set; }
        public Task<MediaToolProcessResult> RunAsync(MediaToolProcessRequest request, CancellationToken token = default)
        {
            Request = request;
            foreach (string line in progress.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                request.StandardOutputLineCallback?.Invoke(line);
            return Task.FromResult(new MediaToolProcessResult { ExitCode = 0, StandardOutput = progress });
        }
    }
}
