using MediaFlux.Models;
using MediaFlux.Services;
using MediaFlux.Services.Encoders;
using System.Windows.Forms;
using Xunit;

namespace MediaFlux.Tests;

public sealed class EncoderBenchmarkTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MediaFlux-EncoderBenchmarkTests", Guid.NewGuid().ToString("N"));

    public EncoderBenchmarkTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task CurrentSettingsAndMultiplePresetsUseOneRepresentativeSourceWindow()
    {
        EncoderBenchmarkDefinition definition = Definition();
        var runner = new FakeRunner();
        var service = Service(runner);

        EncoderBenchmarkReport report = await service.RunAsync(new EncoderBenchmarkRequest(
            definition, new[] { definition.Settings.CurrentPreset, "p1" }, new[] { 1 }, 25));

        Assert.Equal(new[] { "p5", "p1" }, report.Results.Select(x => x.Preset));
        Assert.All(runner.Requests, request =>
        {
            Assert.Equal(TimeSpan.FromSeconds(32.5), request.Sample.Start);
            Assert.Equal(TimeSpan.FromSeconds(25), request.Sample.Duration);
            Assert.Equal(definition.Settings.QualityValue, request.Definition.Settings.QualityValue);
        });
    }

    [Fact]
    public void RepresentativeSamplingAvoidsOpeningAndUsesWholeShortFile()
    {
        EncoderBenchmarkSample normal = EncoderBenchmarkService.SelectRepresentativeSample(TimeSpan.FromSeconds(100), 25);
        Assert.Equal("Representative middle section", normal.Label);
        Assert.Equal(TimeSpan.FromSeconds(32.5), normal.Start);
        Assert.Equal(TimeSpan.FromSeconds(25), normal.Duration);

        EncoderBenchmarkSample shortFile = EncoderBenchmarkService.SelectRepresentativeSample(TimeSpan.FromSeconds(4), 25);
        Assert.Equal("Full video", shortFile.Label);
        Assert.Equal(TimeSpan.Zero, shortFile.Start);
        Assert.Equal(TimeSpan.FromSeconds(4), shortFile.Duration);
    }

    [Fact]
    public void EstimatedCompletionUsesMeasuredRealtimeMultiplier()
    {
        Assert.Equal(TimeSpan.FromMinutes(20),
            EncoderBenchmarkService.EstimateFullFileTime(TimeSpan.FromHours(1), 3));
        Assert.Null(EncoderBenchmarkService.EstimateFullFileTime(TimeSpan.FromHours(1), 0));
    }

    [Fact]
    public async Task ConcurrencyReportsPerJobAndAggregateThroughput()
    {
        var runner = new FakeRunner(delay: TimeSpan.FromMilliseconds(25));
        EncoderBenchmarkReport report = await Service(runner).RunAsync(new EncoderBenchmarkRequest(
            Definition(), new[] { "p5" }, new[] { 1, 2 }, 10));

        EncoderBenchmarkConfigurationResult two = report.Results.Single(x => x.Concurrency == 2);
        Assert.Equal(2, two.Jobs.Count);
        Assert.Equal(60, two.AverageJobFps);
        Assert.Equal(120, two.AggregateFps);
        Assert.True(two.AggregateRealtimeMultiplier > two.AverageJobRealtimeMultiplier);
    }

    [Fact]
    public async Task CancellationTerminatesWorkAndCleansTemporaryArtifacts()
    {
        string temp = Path.Combine(_root, "cancel-temp");
        var runner = new FakeRunner(blockUntilCanceled: true);
        var service = new EncoderBenchmarkService(runner, new UnavailableTelemetry(), temp, TimeSpan.FromMilliseconds(5));
        using var cancellation = new CancellationTokenSource();
        Task<EncoderBenchmarkReport> run = service.RunAsync(new EncoderBenchmarkRequest(
            Definition(), new[] { "p5" }, new[] { 1 }, 10), cancellationToken: cancellation.Token);
        await runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Single(runner.Requests);
        Assert.False(Directory.Exists(temp) && Directory.EnumerateFileSystemEntries(temp).Any());
    }

    [Fact]
    public async Task FfmpegFailureAndUnavailableTelemetryProduceDiagnosticResult()
    {
        var runner = new FakeRunner(fail: true);
        EncoderBenchmarkReport report = await Service(runner).RunAsync(new EncoderBenchmarkRequest(
            Definition(), new[] { "p5" }, new[] { 1 }, 10));
        EncoderBenchmarkConfigurationResult result = Assert.Single(report.Results);

        Assert.False(result.Success);
        Assert.Contains("simulated ffmpeg failure", result.Error);
        Assert.Null(result.GpuEncodePercent);
        Assert.Contains("unavailable", result.TelemetryStatus, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("exit 1", EncoderBenchmarkService.BuildTechnicalDetails(report.Definition, report.Sample, result), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HealthyBenchmarkUsesOnlyTheExistingStrictSampleAttempt()
    {
        var runner = new ScriptedRunner(_ => SuccessfulMeasurement());
        EncoderBenchmarkReport report = await Service(runner).RunAsync(new EncoderBenchmarkRequest(
            Definition(), new[] { "p5" }, new[] { 1 }, 10));

        Assert.Single(runner.Requests);
        Assert.False(runner.Requests[0].TolerantVideoDecode);
        EncoderBenchmarkConfigurationResult result = Assert.Single(report.Results);
        Assert.Equal("Completed", result.Status);
        Assert.Equal(1, Assert.Single(result.Jobs).AttemptCount);
    }

    [Fact]
    public async Task RecognizedVideoDecodeFailureRetriesSameSampleOnceWithTolerantDecode()
    {
        var runner = new ScriptedRunner(request => request.TolerantVideoDecode
            ? SuccessfulMeasurement()
            : FailedDecodeMeasurement());
        EncoderBenchmarkReport report = await Service(runner).RunAsync(new EncoderBenchmarkRequest(
            Definition(), new[] { "p6" }, new[] { 1 }, 10));

        Assert.Equal(2, runner.Requests.Count);
        Assert.All(runner.Requests, x => Assert.Equal(TimeSpan.FromSeconds(40), x.Sample.Start));
        Assert.False(runner.Requests[0].TolerantVideoDecode);
        Assert.True(runner.Requests[1].TolerantVideoDecode);
        EncoderBenchmarkConfigurationResult result = Assert.Single(report.Results);
        Assert.Equal("Completed (decode recovery)", result.Status);
        Assert.Equal(2, Assert.Single(result.Jobs).AttemptCount);
        Assert.Contains("tolerant", EncoderBenchmarkService.BuildTechnicalDetails(report.Definition, report.Sample, result));
    }

    [Fact]
    public async Task PersistentPrimaryDecodeFailureUsesBoundedAlternateSample()
    {
        var runner = new ScriptedRunner(request => request.Sample.Start == TimeSpan.FromSeconds(55) && !request.TolerantVideoDecode
            ? SuccessfulMeasurement()
            : FailedDecodeMeasurement());
        EncoderBenchmarkReport report = await Service(runner).RunAsync(new EncoderBenchmarkRequest(
            Definition(), new[] { "p5" }, new[] { 1 }, 10));

        Assert.Equal(new[] { 40d, 40d, 55d }, runner.Requests.Select(x => x.Sample.Start.TotalSeconds));
        EncoderBenchmarkConfigurationResult result = Assert.Single(report.Results);
        Assert.Equal("Completed (alternate sample)", result.Status);
        Assert.True(Assert.Single(result.Jobs).UsedAlternateSample);
    }

    [Fact]
    public async Task PersistentVideoCorruptionTerminatesWithSourceSampleStatus()
    {
        var runner = new ScriptedRunner(_ => FailedDecodeMeasurement());
        EncoderBenchmarkReport report = await Service(runner).RunAsync(new EncoderBenchmarkRequest(
            Definition(), new[] { "p5" }, new[] { 1 }, 10));

        Assert.Equal(6, runner.Requests.Count);
        EncoderBenchmarkConfigurationResult result = Assert.Single(report.Results);
        Assert.Equal("Source sample decode failed", result.Status);
        Assert.Contains("Source unsuitable for benchmarking", result.Error);
    }

    [Fact]
    public async Task EncoderFailureDoesNotTriggerDecodeRecovery()
    {
        var runner = new ScriptedRunner(_ => new EncoderBenchmarkJobMeasurement(1, false, TimeSpan.Zero, 0, 0, 0, "", "", 1, "NV_ENC_ERR_INVALID_PARAM"));
        EncoderBenchmarkReport report = await Service(runner).RunAsync(new EncoderBenchmarkRequest(
            Definition(), new[] { "p5" }, new[] { 1 }, 10));

        Assert.Single(runner.Requests);
        Assert.Equal("Encoder failed", Assert.Single(report.Results).Status);
    }

    [Fact]
    public async Task CascadingDecoderFailureTakesPrecedenceOverNvencTeardown()
    {
        const string cascading = "[h264] mmco: unref short failure\n" +
            "[dec:h264] Error submitting packet to decoder: Invalid data found when processing input\n" +
            "[dec:h264] Error processing packet in decoder: Invalid data found when processing input\n" +
            "[dec:h264] Task finished with error code: -1094995529\n" +
            "[hevc_nvenc] Could not open encoder before EOF\n" +
            "[hevc_nvenc] Task finished with error code: -22 (Invalid argument)\n" +
            "Nothing was written into output file\nConversion failed";
        var runner = new ScriptedRunner(request => request.TolerantVideoDecode
            ? SuccessfulMeasurement()
            : new EncoderBenchmarkJobMeasurement(1, false, TimeSpan.Zero, 0, 0, 0, "", "", 1, cascading));

        EncoderBenchmarkReport report = await Service(runner).RunAsync(new EncoderBenchmarkRequest(
            Definition(), new[] { "p5" }, new[] { 1 }, 10));

        Assert.Equal(2, runner.Requests.Count);
        Assert.True(runner.Requests[1].TolerantVideoDecode);
        Assert.Equal("Completed (decode recovery)", Assert.Single(report.Results).Status);
    }

    [Theory]
    [InlineData("No space left on device while writing output.mkv")]
    [InlineData("unclassified ffmpeg failure")]
    public async Task StorageAndUnknownFailuresDoNotTriggerDecodeRecovery(string error)
    {
        var runner = new ScriptedRunner(_ => new EncoderBenchmarkJobMeasurement(1, false, TimeSpan.Zero, 0, 0, 0, "", "", 1, error));
        await Service(runner).RunAsync(new EncoderBenchmarkRequest(Definition(), new[] { "p5" }, new[] { 1 }, 10));

        Assert.Single(runner.Requests);
    }

    [Fact]
    public void SuccessfulFpsFallsBackToEncodedFramesOverSuccessfulElapsedTime()
    {
        Assert.Equal(24, EncoderBenchmarkService.CalculateSuccessfulFps(
            Array.Empty<double>(), 600, TimeSpan.FromSeconds(25)), 3);
        Assert.Equal(60, EncoderBenchmarkService.CalculateSuccessfulFps(
            new[] { 60d }, 0, TimeSpan.FromSeconds(25)), 3);
        Assert.Equal(0, EncoderBenchmarkService.CalculateSuccessfulFps(
            Array.Empty<double>(), 0, TimeSpan.FromSeconds(25)));
    }

    [Fact]
    public void TechnicalDetailsForResultsIncludesEveryConfigurationAndAttemptHistory()
    {
        EncoderBenchmarkDefinition definition = Definition();
        EncoderBenchmarkSample sample = EncoderBenchmarkService.SelectRepresentativeSample(definition.SourceDuration, 10);
        var first = new EncoderBenchmarkConfigurationResult(
            "p5", 1, true,
            new[] { new EncoderBenchmarkJobMeasurement(1, true, TimeSpan.FromSeconds(1), 60, 2, 1000, "-xerror -err_detect explode", "NVENC", 0, "", 2, true, false, sample, "Video decode corruption recovered", "40 (strict): failed; 40 (tolerant): succeeded") },
            60, 2, 60, 2, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(50), null, null, null, null, null, null, null, "Unavailable", "Completed (decode recovery)", "");
        var second = first with { Preset = "p6", Status = "Source sample decode failed" };

        string text = EncoderBenchmarkService.BuildTechnicalDetailsForResults(definition, sample, new[] { first, second });

        Assert.Contains("Encoder / preset: GPU (NVENC) / p5", text);
        Assert.Contains("Encoder / preset: GPU (NVENC) / p6", text);
        Assert.Contains("Benchmark attempts: 2", text);
        Assert.Contains("40 (tolerant): succeeded", text);
        Assert.Contains("Completed (decode recovery)", text);
        Assert.Contains("Source sample decode failed", text);
    }

    [Fact]
    public async Task BenchmarkDoesNotModifySourceOrSettingsAndDiscardsOutputs()
    {
        EncoderBenchmarkDefinition definition = Definition();
        byte[] before = File.ReadAllBytes(definition.SourcePath);
        DateTime modified = File.GetLastWriteTimeUtc(definition.SourcePath);
        string temp = Path.Combine(_root, "safe-temp");
        var service = new EncoderBenchmarkService(new FakeRunner(), new UnavailableTelemetry(), temp, TimeSpan.FromMilliseconds(5));

        await service.RunAsync(new EncoderBenchmarkRequest(definition, new[] { "p5" }, new[] { 1 }, 10));

        Assert.Equal(before, File.ReadAllBytes(definition.SourcePath));
        Assert.Equal(modified, File.GetLastWriteTimeUtc(definition.SourcePath));
        Assert.Equal("p5", definition.Settings.CurrentPreset);
        Assert.False(Directory.Exists(temp) && Directory.EnumerateFileSystemEntries(temp).Any());
    }

    [Fact]
    public void ActiveProductionWorkBlocksBenchmarkWithoutChangingIt()
    {
        Assert.False(MainForm.CanStartEncoderBenchmark(true, false, false, out string reason));
        Assert.Contains("production encode", reason, StringComparison.OrdinalIgnoreCase);
        Assert.True(MainForm.CanStartEncoderBenchmark(false, false, false, out reason));
        Assert.Empty(reason);
    }

    [Fact]
    public void NormalCommandBuilderAddsBenchmarkWindowOnlyWhenRequested()
    {
        ResolvedVideoEncoder resolved = EncoderRegistry.Default.Resolve(VideoEncoderIds.Nvenc, VideoCodecFamily.Hevc);
        var builder = new FfmpegCommandBuilder(EncoderRegistry.Default, _ => 192);
        FfmpegCommandRequest Request(TimeSpan? start, TimeSpan? duration) => new()
        {
            Input = EncodingInputSource.FromFile(@"P:\media\movie.mkv"), OutputPath = @"P:\temp\out.mkv",
            Encoder = resolved.Selection, UseGpu = true, TargetMb = null, ScaleMode = EncodingService.ScaleMode.None,
            EncoderPreset = "p5", QualityValue = 24, TenBit = false, AudioChannels = null,
            ConcurrentEncoderSessions = false, MapMode = EncodingService.StreamMapMode.KeepAll,
            CopySubtitles = true, CopyDataStreams = true, CopyAttachments = true,
            ContainerDecision = new OutputContainerDecision { Requested = OutputContainerSelection.Matroska, Resolved = OutputContainer.Matroska, Reason = "test", CopySubtitles = true, CopyDataStreams = true, CopyAttachments = true },
            ForceMp4CompatibleAudio = false, KnownDuration = TimeSpan.FromSeconds(25),
            SampleStart = start, SampleDuration = duration
        };

        string benchmark = builder.Build(Request(TimeSpan.FromSeconds(32.5), TimeSpan.FromSeconds(25)));
        string normal = builder.Build(Request(null, null));

        Assert.Contains("-ss 32.5", benchmark);
        Assert.Contains("-t 25", benchmark);
        Assert.DoesNotContain("-ss 32.5", normal);
        Assert.DoesNotContain("-t 25", normal);
    }

    private EncoderBenchmarkService Service(IEncoderBenchmarkJobRunner runner) => new(
        runner, new UnavailableTelemetry(), Path.Combine(_root, Guid.NewGuid().ToString("N")), TimeSpan.FromMilliseconds(5));

    private EncoderBenchmarkDefinition Definition()
    {
        string source = Path.Combine(_root, "source.mkv");
        if (!File.Exists(source)) File.WriteAllBytes(source, Enumerable.Repeat((byte)7, 10_000).ToArray());
        ResolvedVideoEncoder encoder = EncoderRegistry.Default.Resolve(VideoEncoderIds.Nvenc, VideoCodecFamily.Hevc);
        return new EncoderBenchmarkDefinition(source, TimeSpan.FromSeconds(100), 10_000, "h264", "matroska", "1920×1080", 30,
            new EncoderBenchmarkSettings(encoder.Selection, "GPU (NVENC)", true, null, EncodingService.ScaleMode.None,
                "p5", 24, false, null, EncodingService.StreamMapMode.KeepAll, true, true, true,
                OutputContainerSelection.Matroska, true),
            new[] { new EncoderPresetOption("p1", "Fastest (p1)"), new EncoderPresetOption("p5", "Slow (p5)") },
            new[] { 1, 2 });
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private sealed class UnavailableTelemetry : IEncodingSystemTelemetryProvider
    {
        public EncodingSystemTelemetry Sample() => new(25, 5, null, 0, null, null, null, null, "GPU telemetry unavailable.");
    }

    private sealed class FakeRunner : IEncoderBenchmarkJobRunner
    {
        private readonly bool _fail;
        private readonly bool _blockUntilCanceled;
        private readonly TimeSpan _delay;
        public List<EncoderBenchmarkJobRequest> Requests { get; } = new();
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public FakeRunner(bool fail = false, bool blockUntilCanceled = false, TimeSpan? delay = null)
        { _fail = fail; _blockUntilCanceled = blockUntilCanceled; _delay = delay ?? TimeSpan.Zero; }
        public async Task<EncoderBenchmarkJobMeasurement> RunAsync(EncoderBenchmarkJobRequest request, IProgress<string>? progress, CancellationToken cancellationToken)
        {
            lock (Requests) Requests.Add(request);
            Directory.CreateDirectory(request.OutputFolder);
            File.WriteAllBytes(Path.Combine(request.OutputFolder, $"job-{request.JobNumber}.tmp"), new byte[] { 1, 2, 3 });
            Started.TrySetResult();
            if (_blockUntilCanceled) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            if (_delay > TimeSpan.Zero) await Task.Delay(_delay, cancellationToken);
            return _fail
                ? new(request.JobNumber, false, _delay, 0, 0, 0, "", "Unavailable", 1, "simulated ffmpeg failure")
                : new(request.JobNumber, true, _delay, 60, 2, 1_000_000, "-safe benchmark args", "NVDEC -> NVENC", 0, "");
        }
    }

    private sealed class ScriptedRunner : IEncoderBenchmarkJobRunner
    {
        private readonly Func<EncoderBenchmarkJobRequest, EncoderBenchmarkJobMeasurement> _run;
        public List<EncoderBenchmarkJobRequest> Requests { get; } = new();
        public ScriptedRunner(Func<EncoderBenchmarkJobRequest, EncoderBenchmarkJobMeasurement> run) => _run = run;
        public Task<EncoderBenchmarkJobMeasurement> RunAsync(EncoderBenchmarkJobRequest request, IProgress<string>? progress, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(_run(request) with { JobNumber = request.JobNumber });
        }
    }

    private static EncoderBenchmarkJobMeasurement SuccessfulMeasurement() =>
        new(1, true, TimeSpan.FromSeconds(1), 60, 2, 1_000, "args", "NVENC", 0, "");

    private static EncoderBenchmarkJobMeasurement FailedDecodeMeasurement() =>
        new(1, false, TimeSpan.FromSeconds(1), 0, 0, 0, "args", "", 1,
            "[h264] Invalid NAL unit size\nError submitting packet to decoder: Invalid data found when processing input");
}

[Collection("LibraryAnalyzerUi")]
public sealed class EncoderBenchmarkUiTests
{
    [Fact]
    public void BenchmarkEntryPointsAndComparisonControlsAreAvailable()
    {
        if (!OperatingSystem.IsWindows()) return;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            string source = Path.Combine(Path.GetTempPath(), $"benchmark-ui-{Guid.NewGuid():N}.mkv");
            try
            {
                File.WriteAllBytes(source, new byte[] { 1, 2, 3 });
                using var main = new MainForm();
                DataGridView queue = (DataGridView)(main.GetType().GetField("dgvEncodeQueue", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(main)
                    ?? throw new MissingFieldException("dgvEncodeQueue"));
                Assert.Contains(AllMenuItems(queue.ContextMenuStrip!.Items), item => item.Text == "Benchmark Encode Performance");
                MenuStrip menu = main.Controls.OfType<MenuStrip>().First();
                Assert.Contains(AllMenuItems(menu.Items), item => item.Text == "Encoder Benchmark");

                ResolvedVideoEncoder encoder = EncoderRegistry.Default.Resolve(VideoEncoderIds.Nvenc, VideoCodecFamily.Hevc);
                var definition = new EncoderBenchmarkDefinition(source, TimeSpan.FromMinutes(10), 3, "h264", "matroska", "1920×1080", 30,
                    new EncoderBenchmarkSettings(encoder.Selection, "GPU (NVENC)", true, null, EncodingService.ScaleMode.None, "p5", 24, false, null,
                        EncodingService.StreamMapMode.KeepAll, true, true, true, OutputContainerSelection.Matroska, true),
                    new[] { new EncoderPresetOption("p1", "Fastest (p1)"), new EncoderPresetOption("p5", "Slow (p5)") }, new[] { 1, 2 });
                using var dialog = new EncoderBenchmarkForm(definition,
                    new EncoderBenchmarkService(new UiRunner(), new UiTelemetry(), Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))));
                CheckedListBox[] checkedLists = Descendants<CheckedListBox>(dialog).ToArray();
                Assert.Equal(2, checkedLists.Length);
                Assert.Contains(checkedLists, list => list.CheckedItems.Cast<object>().Any(item => item.ToString()!.Contains("p5", StringComparison.OrdinalIgnoreCase)));
                Assert.Contains(Descendants<NumericUpDown>(dialog), value => value.Value == 25);
                DataGridView results = Assert.Single(Descendants<DataGridView>(dialog));
                Assert.All(results.Columns.Cast<DataGridViewColumn>(), column => Assert.Equal(DataGridViewColumnSortMode.Automatic, column.SortMode));
            }
            catch (Exception ex) { failure = ex; }
            finally { try { File.Delete(source); } catch { } }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    private static IEnumerable<ToolStripItem> AllMenuItems(ToolStripItemCollection items)
    {
        foreach (ToolStripItem item in items)
        {
            yield return item;
            if (item is ToolStripDropDownItem dropDown)
                foreach (ToolStripItem child in AllMenuItems(dropDown.DropDownItems)) yield return child;
        }
    }

    private static IEnumerable<T> Descendants<T>(Control parent) where T : Control
    {
        foreach (Control child in parent.Controls)
        {
            if (child is T match) yield return match;
            foreach (T descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private sealed class UiTelemetry : IEncodingSystemTelemetryProvider
    { public EncodingSystemTelemetry Sample() => new(null, null, null, 0, null, null, null, null, "Unavailable"); }
    private sealed class UiRunner : IEncoderBenchmarkJobRunner
    {
        public Task<EncoderBenchmarkJobMeasurement> RunAsync(EncoderBenchmarkJobRequest request, IProgress<string>? progress, CancellationToken cancellationToken) =>
            Task.FromResult(new EncoderBenchmarkJobMeasurement(1, true, TimeSpan.FromSeconds(1), 30, 1, 1, "args", "pipeline", 0, ""));
    }
}
