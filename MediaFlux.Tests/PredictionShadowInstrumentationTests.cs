using System.Globalization;
using System.Diagnostics;
using System.Text;
using MediaFlux.Models;
using MediaFlux.Services;
using Xunit;
using Xunit.Abstractions;

namespace MediaFlux.Tests;

public sealed class PredictionShadowInstrumentationTests : IDisposable
{
    private static readonly DateTime Cutoff = new(2026, 9, 28, 12, 0, 0, DateTimeKind.Utc);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MediaFlux-PredictionShadowTests", Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper _output;

    public PredictionShadowInstrumentationTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public async Task SamplerUsesDeterministicRepresentativeWindowsAndNormalizedFeatures()
    {
        string ffmpeg = CreateFile("ffmpeg.exe");
        string source = CreateFile("source.mp4");
        var runner = new PgmWritingRunner();
        var sampler = new PredictionShadowComplexitySamplingService(ffmpeg, runner);

        PredictionShadowSamplingObservation first = await sampler.AnalyzeAsync(source, 120);
        PredictionShadowSamplingObservation second = await sampler.AnalyzeAsync(source, 120);

        Assert.Equal(6, runner.Calls);
        Assert.Equal(PredictionShadowSamplingStatus.Succeeded, first.Status);
        Assert.Equal(15, first.FrameCount);
        Assert.Equal(12, first.TemporalPairCount);
        Assert.InRange(first.TemporalFrameDifference!.Value, 0, 1);
        Assert.InRange(first.SpatialGradientEnergy!.Value, 0, 1);
        Assert.Equal(first.TemporalFrameDifference, second.TemporalFrameDifference);
        Assert.Equal(first.SpatialGradientEnergy, second.SpatialGradientEnergy);
        Assert.Equal(first.Windows.Select(window => (window.Label, window.StartSeconds)),
            second.Windows.Select(window => (window.Label, window.StartSeconds)));
        Assert.Equal(3, first.WindowCount);
        Assert.False(first.UsedHardwareDecode);
        Assert.Equal("OmittedNotValidated", first.NoiseGrainProxyStatus);
        Assert.All(first.Windows, window => Assert.Equal(5, window.FramesAnalyzed));
    }

    [Fact]
    public async Task ShortSourceUsesOneFullDurationWindowAndCancellationStopsSampling()
    {
        string ffmpeg = CreateFile("ffmpeg.exe");
        string source = CreateFile("short.mp4");
        var runner = new PgmWritingRunner();
        var sampler = new PredictionShadowComplexitySamplingService(ffmpeg, runner);

        PredictionShadowSamplingObservation result = await sampler.AnalyzeAsync(source, 2.4);

        Assert.Equal(1, runner.Calls);
        PredictionShadowSampleWindow window = Assert.Single(result.Windows);
        Assert.Equal(0, window.StartSeconds);
        Assert.Equal(2.4, window.DurationSeconds, 3);
        Assert.Equal(PredictionShadowSamplingStatus.Succeeded, result.Status);

        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sampler.AnalyzeAsync(source, 2.4, canceled.Token));
        Assert.Equal(1, runner.Calls);
    }

    [Fact]
    public async Task CancellationDuringFfmpegWindowPropagatesAndStopsCapture()
    {
        string ffmpeg = CreateFile("ffmpeg.exe");
        string source = CreateFile("cancel-mid-sample.mp4");
        var runner = new BlockingRunner();
        var sampler = new PredictionShadowComplexitySamplingService(ffmpeg, runner);
        using var canceled = new CancellationTokenSource();

        Task<PredictionShadowSamplingObservation> sampling = sampler.AnalyzeAsync(source, 120, canceled.Token);
        await runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        canceled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sampling);
        Assert.Equal(1, runner.Calls);
    }

    [Fact]
    public void MetricKernelSeparatesTemporalDifferenceFromSpatialGradientAndBoundsBoth()
    {
        var flat = new byte[25];
        var edge = new byte[25];
        for (int y = 0; y < 5; y++)
        for (int x = 0; x < 5; x++)
            edge[y * 5 + x] = x < 2 ? (byte)0 : (byte)255;
        var temporalWindows = new IReadOnlyList<PredictionShadowComplexitySamplingService.GrayFrame>[]
        {
            new[]
            {
                new PredictionShadowComplexitySamplingService.GrayFrame(5, 5, flat),
                new PredictionShadowComplexitySamplingService.GrayFrame(5, 5, Enumerable.Repeat((byte)255, 25).ToArray())
            }
        };
        var spatialWindows = new IReadOnlyList<PredictionShadowComplexitySamplingService.GrayFrame>[]
        {
            new[]
            {
                new PredictionShadowComplexitySamplingService.GrayFrame(5, 5, edge),
                new PredictionShadowComplexitySamplingService.GrayFrame(5, 5, edge),
                new PredictionShadowComplexitySamplingService.GrayFrame(5, 5, edge)
            }
        };

        var temporalMetrics = PredictionShadowComplexitySamplingService.Measure(temporalWindows);
        var spatialMetrics = PredictionShadowComplexitySamplingService.Measure(spatialWindows);

        Assert.Equal(1, temporalMetrics.Temporal);
        Assert.InRange(temporalMetrics.Temporal!.Value, 0, 1);
        Assert.InRange(spatialMetrics.Spatial!.Value, 0, 1);
        Assert.True(spatialMetrics.Spatial > 0);
        Assert.Equal(2, temporalMetrics.FrameCount);
        Assert.Equal(1, temporalMetrics.PairCount);
        Assert.Equal(3, spatialMetrics.FrameCount);
    }

    [Fact]
    public async Task CaptureFreezesPairedPredictionsAndExactPeersBeforeOutcome()
    {
        NvencQualityModePredictionShadowService service = CreateService(out PgmWritingRunner runner);
        EncodingPlanSnapshot snapshot = MakeSnapshot();
        string source = CreateFile("target.mp4");
        string signature = Signature;
        string targetFamily = NvencQualityModeVideoBitratePredictionService.GetSourceFamilyKey(
            snapshot.Plan.SourceAdaptiveShadow,
            snapshot.Plan.Source!.DurationSeconds,
            source);
        EncodingStatisticsRecord[] history =
        [
            MakeRecord("same-family", targetFamily, 8000, 9000, 800_000_000, Cutoff.AddMinutes(-3)),
            MakeRecord("prior-a", "family-a", 5000, 8000, 400_000_000, Cutoff.AddMinutes(-2)),
            MakeRecord("prior-b", "family-b", 8000, 6000, 700_000_000, Cutoff.AddMinutes(-1)),
            MakeRecord("future", "future-family", 9000, 2000, 900_000_000, Cutoff.AddMinutes(1))
        ];

        PredictionShadowFrozenObservation observation = (await service.CaptureAsync(
            snapshot, source, signature, history, "1.7.3", CancellationToken.None))!;

        Assert.Equal(snapshot.PlanId.ToString("N"), observation.ObservationId);
        Assert.Equal(2, observation.SchemaVersion);
        Assert.Equal(Cutoff, observation.PredictionEvidenceCutoffUtc);
        Assert.Equal(Cutoff, observation.FrozenUtc);
        Assert.Equal(2, observation.AdmittedPeerSourceFamilyKeys.Count);
        string[] expectedPeers = history.Skip(1).Take(2)
            .Select(record => NvencQualityModeVideoBitratePredictionService.GetSourceFamilyKey(
                record.SourceAdaptiveShadow!.Decision, record.MediaDurationSeconds, record.SourcePath))
            .OrderBy(key => key, StringComparer.OrdinalIgnoreCase).ThenBy(key => key, StringComparer.Ordinal).ToArray();
        Assert.Equal(expectedPeers, observation.AdmittedPeerSourceFamilyKeys);
        Assert.DoesNotContain(targetFamily, observation.AdmittedPeerSourceFamilyKeys);
        Assert.DoesNotContain(NvencQualityModeVideoBitratePredictionService.GetSourceFamilyKey(
            history[3].SourceAdaptiveShadow!.Decision, history[3].MediaDurationSeconds, history[3].SourcePath),
            observation.AdmittedPeerSourceFamilyKeys);
        Assert.True(observation.RatioAndDirectSharePeers);
        Assert.Equal(2, observation.Ratio.IndependentPeerCount);
        Assert.Equal(2, observation.Direct.IndependentPeerCount);
        Assert.Equal("ComparableHistory", observation.Ratio.Reason);
        Assert.Equal("ComparableHistory", observation.Direct.Reason);
        Assert.NotNull(observation.Ratio.PredictedVideoBitrateKbps);
        Assert.NotNull(observation.Direct.PredictedVideoBitrateKbps);
        Assert.Equal("OmittedNotValidated", observation.Complexity.NoiseGrainProxyStatus);
        Assert.Equal("temporal-neighbor-k2-v1", observation.TemporalNeighbor!.ComparatorVersion);
        Assert.Equal(2, observation.TemporalNeighbor.K);
        Assert.Equal("HistoricalJournalUnavailableOrCorrupt", observation.TemporalNeighbor.AbstentionReason);

        var journal = new PredictionShadowObservationJournal(Path.Combine(_root, "prediction-shadow-observations.jsonl"));
        Assert.Single(journal.ReadEvents());
        PredictionShadowJournalEvent frozenEvent = Assert.Single(journal.ReadEvents());
        Assert.Equal("Frozen", frozenEvent.EventType);
        Assert.Equal(2, frozenEvent.SchemaVersion);
        Assert.Null(frozenEvent.Outcome);
        Assert.Equal(observation.Direct.PredictedVideoBitrateKbps, frozenEvent.Frozen!.Direct.PredictedVideoBitrateKbps);

        Assert.True(service.RecordOutcome(observation.ObservationId, "Completed", "Completed", "Passed", "Passed",
            recoveredSuccessful: false, actualOutputVideoBitrateKbps: 7000, outputBytes: 900_000_000,
            recordedUtc: Cutoff.AddSeconds(10)));
        Assert.False(service.RecordOutcome(observation.ObservationId, "Completed", "Completed", "Passed", "Passed",
            recoveredSuccessful: false, actualOutputVideoBitrateKbps: 1000, outputBytes: 2,
            recordedUtc: Cutoff.AddSeconds(11)));
        PredictionShadowJournalEvent[] events = journal.ReadEvents().ToArray();
        PredictionShadowOutcome finalOutcome = events[1].Outcome!;
        Assert.Equal(2, events.Length);
        Assert.Equal(observation.Direct.PredictedVideoBitrateKbps, events[0].Frozen!.Direct.PredictedVideoBitrateKbps);
        Assert.Equal(observation.Direct.PredictedVideoBitrateKbps!.Value - 7000,
            finalOutcome.DirectSignedErrorKbps!.Value);
        Assert.Equal((observation.Direct.PredictedVideoBitrateKbps.Value - 7000) / 7000d * 100d,
            finalOutcome.DirectSignedErrorPercent!.Value, 6);
        Assert.Equal(100_000_000L, finalOutcome.SizeChangeBytes);
        Assert.Equal(12.5, finalOutcome.SizeChangePercent!.Value, 6);
        Assert.Equal(3, runner.Calls);
    }

    [Theory]
    [InlineData(0, "NoComparableHistory")]
    [InlineData(1, "InsufficientIndependentSources")]
    public async Task CapturePreservesExistingNoHistoryAndOnePeerAbstentions(int peerCount, string expectedReason)
    {
        NvencQualityModePredictionShadowService service = CreateService(out _);
        string source = CreateFile($"target-{peerCount}.mp4");
        EncodingStatisticsRecord[] history = Enumerable.Range(0, peerCount)
            .Select(index => MakeRecord($"peer-{index}", $"family-{index}", 5000 + index * 1000,
                7000 + index * 1000, 300_000_000 + index, Cutoff.AddMinutes(-index - 1)))
            .ToArray();

        PredictionShadowFrozenObservation observation = (await service.CaptureAsync(
            MakeSnapshot(), source, Signature, history, "1.7.3"))!;

        Assert.Equal(expectedReason, observation.Ratio.Reason);
        Assert.Equal(expectedReason, observation.Direct.Reason);
        Assert.Null(observation.Ratio.PredictedVideoBitrateKbps);
        Assert.Null(observation.Direct.PredictedVideoBitrateKbps);
        Assert.Equal(peerCount, observation.Ratio.IndependentPeerCount);
        Assert.Equal(peerCount, observation.Direct.IndependentPeerCount);
    }

    [Fact]
    public async Task SamplingFailureStillCapturesForecastAndEncodeFailureAppendsOutcome()
    {
        string ffmpeg = CreateFile("ffmpeg-failing.exe");
        string source = CreateFile("failure-source.mp4");
        var runner = new PgmWritingRunner(exitCode: 1);
        var journal = new PredictionShadowObservationJournal(Path.Combine(_root, "failure-journal.jsonl"));
        var service = new NvencQualityModePredictionShadowService(
            journal,
            new PredictionShadowComplexitySamplingService(ffmpeg, runner),
            utcNow: () => Cutoff);
        EncodingPlanSnapshot snapshot = MakeSnapshot();
        var peers = new[]
        {
            MakeRecord("prior1", "family-one", 4000, 8000, 400_000_000, Cutoff.AddMinutes(-2)),
            MakeRecord("prior2", "family-two", 8000, 6000, 700_000_000, Cutoff.AddMinutes(-1))
        };

        PredictionShadowFrozenObservation observation = (await service.CaptureAsync(
            snapshot, source, Signature, peers, "1.7.3"))!;
        Assert.Equal(PredictionShadowSamplingStatus.Unavailable, observation.Complexity.Status);
        Assert.Equal("ComparableHistory", observation.Direct.Reason);
        Assert.Single(journal.ReadEvents());

        Assert.True(service.RecordOutcome(observation.ObservationId, "Failed", "EncodeFailed", "Failed", "NotRun",
            recoveredSuccessful: false, actualOutputVideoBitrateKbps: null, outputBytes: null,
            recordedUtc: Cutoff.AddSeconds(15)));
        PredictionShadowJournalEvent[] events = journal.ReadEvents().ToArray();
        Assert.Equal(2, events.Length);
        Assert.Equal("Outcome", events[1].EventType);
        Assert.Equal("Failed", events[1].Outcome!.State);
        PredictionShadowOutcome failedOutcome = events[1].Outcome!;
        Assert.Null(failedOutcome.ActualOutputVideoBitrateKbps);
        Assert.Null(failedOutcome.DirectAbsoluteErrorPercent);
    }

    [Fact]
    public async Task PersistenceFailureDoesNotEscapeResearchCapture()
    {
        string ffmpeg = CreateFile("ffmpeg.exe");
        string source = CreateFile("persistence-source.mp4");
        var runner = new PgmWritingRunner();
        string blockedPath = Path.Combine(_root, "is-a-directory");
        Directory.CreateDirectory(blockedPath);
        var journal = new PredictionShadowObservationJournal(blockedPath);
        var service = new NvencQualityModePredictionShadowService(
            journal,
            new PredictionShadowComplexitySamplingService(ffmpeg, runner),
            utcNow: () => Cutoff);

        PredictionShadowFrozenObservation? observation = await service.CaptureAsync(
            MakeSnapshot(), source, Signature, Array.Empty<EncodingStatisticsRecord>(), "1.7.3");

        Assert.Null(observation);
        Assert.Equal(3, runner.Calls);
    }

    [Fact]
    public void ProductionStatisticsRecordDoesNotGainResearchFields()
    {
        string statsPath = Path.Combine(_root, "encoding-statistics.jsonl");
        var stats = new EncodingStatisticsService(statsPath);
        Assert.True(stats.AppendFinalized(new EncodingStatisticsRecord
        {
            Id = "schema-stability",
            StartUtc = Cutoff.AddMinutes(-1),
            EndUtc = Cutoff,
            Outcome = EncodingStatisticsOutcome.Success
        }));
        string json = File.ReadAllText(statsPath);
        Assert.DoesNotContain("TemporalFrameDifference", json);
        Assert.DoesNotContain("SpatialGradientEnergy", json);
        Assert.DoesNotContain("AdmittedPeerSourceFamilyKeys", json);
        Assert.DoesNotContain("PredictionShadow", json);
    }

    [Fact]
    [Trait("Category", "Performance")]
    public async Task OptionalSynthetic1080pFixtureReportsSamplerRuntime()
    {
        string? ffmpegPath = Environment.GetEnvironmentVariable("MEDIAFLUX_RESEARCH_FFMPEG_PATH");
        if (string.IsNullOrWhiteSpace(ffmpegPath) || !File.Exists(ffmpegPath))
            return;

        string fixtureRoot = Path.Combine(Path.GetTempPath(), "MediaFlux-PredictionShadowPerf", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixtureRoot);
        try
        {
            string sourcePath = Path.Combine(fixtureRoot, "synthetic-1080p.mp4");
            var start = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };
            foreach (string argument in new[]
            {
                "-hide_banner", "-nostdin", "-loglevel", "error", "-f", "lavfi", "-i",
                "testsrc2=size=1920x1080:rate=30", "-t", "30", "-an", "-c:v", "libx264",
                "-preset", "ultrafast", "-crf", "24", "-pix_fmt", "yuv420p", "-y", sourcePath
            })
                start.ArgumentList.Add(argument);

            using (var process = Process.Start(start) ?? throw new InvalidOperationException("FFmpeg fixture process did not start."))
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                try
                {
                    await process.WaitForExitAsync(timeout.Token);
                }
                catch (OperationCanceledException)
                {
                    process.Kill(entireProcessTree: true);
                    throw;
                }
                string error = await process.StandardError.ReadToEndAsync();
                Assert.True(process.ExitCode == 0, $"Synthetic fixture generation failed: {error}");
            }

            var sampler = new PredictionShadowComplexitySamplingService(ffmpegPath, new MediaToolProcessRunner());
            PredictionShadowSamplingObservation result = await sampler.AnalyzeAsync(sourcePath, 30);
            _output.WriteLine(
                $"status={result.Status}; frames={result.FrameCount}; pairs={result.TemporalPairCount}; " +
                $"windows={result.WindowCount}; wall_ms={result.WallClockMilliseconds:0.##}; " +
                $"ffmpeg_cpu_ms={result.FfmpegCpuMilliseconds?.ToString("0.##", CultureInfo.InvariantCulture) ?? "n/a"}; " +
                $"temp_bytes={result.TemporaryBytesWritten}; hw_decode={result.UsedHardwareDecode}; " +
                $"fixture_bytes={new FileInfo(sourcePath).Length}");
            Assert.NotEqual(PredictionShadowSamplingStatus.Unavailable, result.Status);
            Assert.True(result.FrameCount > 0);
        }
        finally
        {
            if (Directory.Exists(fixtureRoot))
                Directory.Delete(fixtureRoot, recursive: true);
        }
    }

    private NvencQualityModePredictionShadowService CreateService(out PgmWritingRunner runner)
    {
        string ffmpeg = CreateFile("ffmpeg.exe");
        runner = new PgmWritingRunner();
        var journal = new PredictionShadowObservationJournal(Path.Combine(_root, "prediction-shadow-observations.jsonl"));
        return new NvencQualityModePredictionShadowService(
            journal,
            new PredictionShadowComplexitySamplingService(ffmpeg, runner),
            utcNow: () => Cutoff);
    }

    private EncodingPlanSnapshot MakeSnapshot()
    {
        var decision = new SourceAdaptiveShadowCalibration
        {
            Status = SourceAdaptiveShadowStatus.CalibrationCandidate,
            SourceCodec = "h264",
            OutputCodec = "hevc_nvenc",
            EncoderId = VideoEncoderIds.Nvenc,
            Preset = "p5",
            FinalExecutionCq = 22,
            SourceVideoBitrateKbps = 8000,
            SourceTotalBytes = 800_000_000,
            PlannedWidth = 1920,
            PlannedHeight = 1080,
            PlannedFps = 29.97002997002997,
            MaterialTransformationActive = false
        };
        var plan = new EncodingPlan
        {
            IsAvailable = true,
            PlanId = Guid.NewGuid(),
            Source = new EncodingPlanSource("h264", 1920, 1080, 29.97002997002997, 120)
            {
                SizeBytes = 800_000_000,
                BitrateKbps = 8000
            },
            Video = new EncodingPlanVideo("Reencode", "hevc_nvenc", VideoEncoderIds.Nvenc, 1920, 1080, 1920, 1080, "p010le"),
            Validation = new EncodingPlanValidation("Production", true, false),
            SourceAdaptiveShadow = decision
        };
        return new EncodingPlanSnapshot(plan.PlanId, plan);
    }

    private static EncodingStatisticsRecord MakeRecord(
        string id,
        string familyOrPath,
        double sourceKbps,
        double outputKbps,
        long sourceBytes,
        DateTime endUtc)
    {
        SourceAdaptiveShadowCalibration decision = new()
        {
            Status = SourceAdaptiveShadowStatus.CalibrationCandidate,
            SourceCodec = "h264",
            OutputCodec = "hevc_nvenc",
            EncoderId = VideoEncoderIds.Nvenc,
            Preset = "p5",
            FinalExecutionCq = 22,
            SourceVideoBitrateKbps = sourceKbps,
            SourceTotalBytes = sourceBytes,
            PlannedWidth = 1920,
            PlannedHeight = 1080,
            PlannedFps = 29.97002997002997
        };
        return new EncodingStatisticsRecord
        {
            Id = id,
            SourcePath = familyOrPath,
            StartUtc = endUtc.AddMinutes(-1),
            EndUtc = endUtc,
            Outcome = EncodingStatisticsOutcome.Success,
            MediaDurationSeconds = 120,
            QualityModeSettingsSignature = Signature,
            SourceAdaptiveShadow = new SourceAdaptiveShadowOutcome
            {
                Decision = decision,
                ActualOutputCodec = "hevc",
                ActualOutputVideoBitrateKbps = outputKbps
            }
        };
    }

    private static string Signature => NvencQualityModeVideoBitratePredictionService.EffectiveSettingsSignature(
        VideoEncoderIds.Nvenc, "hevc_nvenc", "p5", 10, true);

    private string CreateFile(string name)
    {
        string path = Path.Combine(_root, name);
        File.WriteAllText(path, "fixture");
        return path;
    }

    private sealed class PgmWritingRunner(int exitCode = 0) : IMediaToolProcessRunner
    {
        public int Calls { get; private set; }

        public Task<MediaToolProcessResult> RunAsync(MediaToolProcessRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            if (exitCode == 0)
            {
                string pattern = request.Arguments[request.Arguments.Count - 1];
                for (int frame = 1; frame <= 5; frame++)
                {
                    string path = pattern.Replace("%03d", frame.ToString("D3", CultureInfo.InvariantCulture), StringComparison.Ordinal);
                    byte[] pixels = new byte[16];
                    for (int y = 0; y < 4; y++)
                    for (int x = 0; x < 4; x++)
                        pixels[y * 4 + x] = (byte)Math.Clamp(x * 55 + frame * 5, 0, 255);
                    File.WriteAllBytes(path, Encoding.ASCII.GetBytes("P5\n4 4\n255\n").Concat(pixels).ToArray());
                }
            }
            return Task.FromResult(new MediaToolProcessResult { ExitCode = exitCode });
        }
    }

    private sealed class BlockingRunner : IMediaToolProcessRunner
    {
        public int Calls { get; private set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<MediaToolProcessResult> RunAsync(MediaToolProcessRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new MediaToolProcessResult { ExitCode = 0 };
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
