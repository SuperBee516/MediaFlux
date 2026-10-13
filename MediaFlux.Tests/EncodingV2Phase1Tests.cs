using MediaFlux.Models;
using MediaFlux.Services;
using MediaFlux.Services.EncodingV2;
using Xunit;

namespace MediaFlux.Tests;

public sealed class EncodingV2Phase1Tests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MediaFlux-V2-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _source;
    private readonly string _output;
    private readonly string _ffmpeg;

    public EncodingV2Phase1Tests()
    {
        Directory.CreateDirectory(_root);
        _source = Path.Combine(_root, "source.mp4");
        _output = Path.Combine(_root, "output.mp4");
        _ffmpeg = Path.Combine(_root, "ffmpeg.exe");
        File.WriteAllBytes(_source, Enumerable.Range(0, 1024).Select(value => (byte)value).ToArray());
        File.WriteAllText(_ffmpeg, "test executable placeholder");
    }

    [Fact]
    public async Task PlannerFreezesResolvedInvocationAndArgumentsCannotBeChanged()
    {
        V2MediaFacts facts = await Metadata().ProbeAsync(_source);
        FrozenEncodePlan plan = new V2EncodePlanner().CreatePlan(Request(), facts, _ffmpeg);

        Assert.Equal("libx265", plan.Encoder);
        Assert.Equal("hevc", plan.Codec);
        Assert.Equal("mp4", plan.Container);
        Assert.Equal("crf", plan.QualityMode);
        Assert.Equal(27, plan.QualityValue);
        Assert.Equal(64, plan.Width);
        Assert.Equal(32, plan.Height);
        Assert.Contains("libx265", plan.Arguments);
        Assert.Contains("-crf", plan.Arguments);
        Assert.Contains("27", plan.Arguments);
        Assert.Contains(plan.StagingPath, plan.Arguments);
        Assert.Equal(plan.StagingPath, plan.Arguments[^1]);
        plan.EnsureExecutable();

        IList<string> arguments = Assert.IsAssignableFrom<IList<string>>(plan.Arguments);
        Assert.Throws<NotSupportedException>(() => arguments[0] = "-n" );
        Assert.Equal("-y", plan.Arguments[0]);
        plan.EnsureExecutable();
    }

    [Fact]
    public async Task V2EstimateAndFrozenProductionPlanShareTheCanonicalFingerprint()
    {
        V2MediaFacts mediaFacts = await Metadata().ProbeAsync(_source);
        FrozenEncodePlan plan = new V2EncodePlanner().CreatePlan(Request(), mediaFacts, _ffmpeg);
        V2FastEstimateSourceFacts estimateFacts = V2FastEstimateSourceFacts.FromMediaFacts(mediaFacts);
        V2FastEstimateResult estimate = V2FastEstimateService.Estimate(new V2FastEstimateRequest
        {
            SourceFacts = estimateFacts,
            Encoder = plan.Encoder,
            Codec = plan.Codec,
            Container = plan.Container,
            QualityMode = plan.QualityMode,
            QualityValue = plan.QualityValue,
            Preset = plan.Preset,
            GeometryMode = plan.GeometryMode,
            AudioMode = plan.AudioMode,
            AudioBitrateKbps = plan.AudioBitrateKbps,
            SubtitleMode = plan.SubtitleMode
        });

        Assert.NotNull(estimate.ProductionPlan);
        Assert.Equal(plan.ProductionPlan.SourceIdentity, estimate.ProductionPlan.SourceIdentity);
        Assert.Equal(plan.ProductionPlan.PlanFingerprint, estimate.ProductionPlan.PlanFingerprint);
    }

    [Fact]
    public async Task InvalidPlanNeverReachesProductionExecutor()
    {
        var executor = new FakeExecutor();
        V2ExecutionResult result = await Core(executor).RunAsync(WithQuality(Request(), quality: 80));

        Assert.Equal(V2LifecycleState.Failed, result.State);
        Assert.Equal("InvalidQuality", result.Failure?.Code);
        Assert.Equal(0, result.ProductionAttemptCount);
        Assert.Equal(0, executor.Calls);
        Assert.True(File.Exists(_source));
    }

    [Fact]
    public async Task ProductionExecutorMakesExactlyOneCallWithFrozenArguments()
    {
        var runner = new RecordingProcessRunner();
        var executor = new V2ProductionExecutor(runner);
        V2MediaFacts facts = await Metadata().ProbeAsync(_source);
        FrozenEncodePlan plan = new V2EncodePlanner().CreatePlan(Request(), facts, _ffmpeg);

        V2ProcessOutcome outcome = await executor.ExecuteAsync(plan, _ => { });

        Assert.Equal(0, outcome.ExitCode);
        MediaToolProcessRequest invocation = Assert.Single(runner.Requests);
        Assert.Equal(plan.FfmpegPath, invocation.FileName);
        Assert.Equal(plan.Arguments, invocation.Arguments);
        Assert.True(invocation.SendQuitOnCancellation);
    }

    [Fact]
    public async Task FailedEncodePreservesSourceAndCleansOnlyItsStagingFile()
    {
        string unrelated = Path.Combine(_root, "keep.txt");
        File.WriteAllText(unrelated, "keep me");
        var executor = new FakeExecutor { ExitCode = 1, Error = "encoder failed" };

        V2ExecutionResult result = await Core(executor).RunAsync(Request());

        Assert.Equal(V2LifecycleState.Failed, result.State);
        Assert.Equal("FfmpegExitCode", result.Failure?.Code);
        Assert.Equal(1, result.ProductionAttemptCount);
        Assert.True(File.Exists(_source));
        Assert.False(File.Exists(result.StagingPath));
        Assert.False(File.Exists(_output));
        Assert.Equal("keep me", File.ReadAllText(unrelated));
    }

    [Fact]
    public async Task CancellationPreservesSourceAndRemovesOwnedStagingFile()
    {
        using var cancellation = new CancellationTokenSource();
        var executor = new FakeExecutor { CancelOnExecute = cancellation };

        V2ExecutionResult result = await Core(executor).RunAsync(Request(), cancellationToken: cancellation.Token);

        Assert.Equal(V2LifecycleState.Canceled, result.State);
        Assert.Equal(1, result.ProductionAttemptCount);
        Assert.True(File.Exists(_source));
        Assert.False(File.Exists(result.StagingPath));
        Assert.False(File.Exists(_output));
    }

    [Fact]
    public async Task ValidationFailurePreservesSourceAndDoesNotPromoteCandidate()
    {
        var executor = new FakeExecutor { WriteOutput = true };
        var validator = new FakeValidator { Success = false, ErrorCode = "BadCandidate", Summary = "bad candidate" };

        V2ExecutionResult result = await Core(executor, validator).RunAsync(Request());

        Assert.Equal(V2LifecycleState.Failed, result.State);
        Assert.Equal("BadCandidate", result.Failure?.Code);
        Assert.True(File.Exists(_source));
        Assert.False(File.Exists(_output));
        Assert.False(File.Exists(result.StagingPath));
    }

    [Fact]
    public async Task PromotionCollisionPreservesPreexistingDestinationAndSource()
    {
        byte[] preexisting = [1, 2, 3, 4];
        var executor = new FakeExecutor { WriteOutput = true };
        var events = new List<V2LifecycleEvent>();
        V2ExecutionResult? result = null;
        result = await Core(executor).RunAsync(Request(), item =>
        {
            events.Add(item);
            if (item.Stage == "output.promotion.started") File.WriteAllBytes(_output, preexisting);
        });

        Assert.Equal(V2LifecycleState.Failed, result.State);
        Assert.Equal("DestinationExists", result.Failure?.Code);
        Assert.True(File.Exists(_source));
        Assert.Equal(preexisting, File.ReadAllBytes(_output));
        Assert.False(File.Exists(result.StagingPath));
        Assert.Contains(events, item => item.Stage == "staging.cleanup.completed");
    }

    [Fact]
    public async Task DeleteSourceOffCompletesAndRetainsSource()
    {
        var executor = new FakeExecutor { WriteOutput = true };
        var validator = new FakeValidator { Success = true };
        V2ExecutionResult result = await Core(executor, validator).RunAsync(Request());

        Assert.Equal(V2LifecycleState.Completed, result.State);
        Assert.False(result.SourceDeletionRequested);
        Assert.False(result.SourceDeleted);
        Assert.True(File.Exists(_source));
        Assert.True(File.Exists(_output));
        Assert.Equal(1, executor.Calls);
        Assert.Equal(1, result.ProductionAttemptCount);
    }

    [Fact]
    public async Task DeleteSourceOnSuccessRunsOnlyAfterValidationAndPromotion()
    {
        var executor = new FakeExecutor { WriteOutput = true };
        var validator = new FakeValidator { Success = true, RequireOutputAbsent = true };
        var promoter = new RecordingPromoter(_source);
        EncodingV2Request request = Request(deleteSource: true);

        V2ExecutionResult result = await Core(executor, validator, promoter).RunAsync(request);

        Assert.Equal(V2LifecycleState.Completed, result.State);
        Assert.True(result.SourceDeleted);
        Assert.False(File.Exists(_source));
        Assert.True(File.Exists(_output));
        Assert.True(promoter.Called);
        Assert.Contains(result.Events, item => item.Stage == "source.deletion.completed");
    }

    [Fact]
    public async Task SourceDeletionFailureKeepsValidOutputAndCompletesWithWarning()
    {
        var executor = new FakeExecutor { WriteOutput = true };
        var validator = new FakeValidator { Success = true };
        using var sourceLock = new FileStream(_source, FileMode.Open, FileAccess.Read, FileShare.None);

        V2ExecutionResult result = await Core(executor, validator).RunAsync(Request(deleteSource: true));

        Assert.Equal(V2LifecycleState.Completed, result.State);
        Assert.False(result.SourceDeleted);
        Assert.NotEmpty(result.Warnings);
        Assert.True(File.Exists(_source));
        Assert.True(File.Exists(_output));
    }

    [Fact]
    public async Task CancellationBeforeSourceDeletionRetainsSourceAfterPromotion()
    {
        using var cancellation = new CancellationTokenSource();
        var executor = new FakeExecutor { WriteOutput = true };
        var validator = new FakeValidator { Success = true };
        V2ExecutionResult result = await Core(executor, validator).RunAsync(Request(deleteSource: true), item =>
        {
            if (item.Stage == "source.deletion.requested") cancellation.Cancel();
        }, cancellation.Token);

        Assert.Equal(V2LifecycleState.Canceled, result.State);
        Assert.True(File.Exists(_source));
        Assert.True(File.Exists(_output));
        Assert.True(result.OutputPromoted);
        Assert.False(result.SourceDeleted);
    }

    [Fact]
    public async Task ExistingDestinationIsNeverOverwritten()
    {
        byte[] before = [8, 9, 10];
        File.WriteAllBytes(_output, before);
        var executor = new FakeExecutor();

        V2ExecutionResult result = await Core(executor).RunAsync(Request());

        Assert.Equal("DestinationExists", result.Failure?.Code);
        Assert.Equal(0, executor.Calls);
        Assert.Equal(before, File.ReadAllBytes(_output));
        Assert.True(File.Exists(_source));
    }

    [Fact]
    public async Task PreexistingPlannedStagingCollisionIsNotDeleted()
    {
        string jobId = Guid.NewGuid().ToString("N");
        string stage = Path.Combine(_root, $".output.mediaflux-v2-{jobId}.partial.mp4");
        byte[] before = [5, 4, 3, 2, 1];
        File.WriteAllBytes(stage, before);
        var executor = new FakeExecutor();

        V2ExecutionResult result = await Core(executor).RunAsync(Request(jobId: jobId));

        Assert.Equal("StagingPathExists", result.Failure?.Code);
        Assert.Equal(0, executor.Calls);
        Assert.Equal(before, File.ReadAllBytes(stage));
        Assert.True(File.Exists(_source));
    }

    [Fact]
    public async Task SuccessUsesLegalLifecycleOrderingAndOneProductionAttempt()
    {
        var executor = new FakeExecutor { WriteOutput = true };
        var validator = new FakeValidator { Success = true };

        V2ExecutionResult result = await Core(executor, validator).RunAsync(Request());

        V2LifecycleState[] states = result.Events.Select(item => item.State).Distinct().ToArray();
        Assert.Equal(new[]
        {
            V2LifecycleState.Queued, V2LifecycleState.Probing, V2LifecycleState.Planned,
            V2LifecycleState.Encoding, V2LifecycleState.Validating,
            V2LifecycleState.Promoting, V2LifecycleState.Completed
        }, states);
        Assert.Equal(1, result.ProductionAttemptCount);
        Assert.Equal(1, executor.Calls);
        Assert.True(result.OutputValidated);
        Assert.True(result.OutputPromoted);
        Assert.True(result.OutputSizeBytes > 0);
        Assert.All(result.Events, item => Assert.Equal(_source, item.SourcePath));
        Assert.All(result.Events, item => Assert.Equal(_output, item.OutputPath));
        Assert.True(result.Timings.TotalMilliseconds >= result.Timings.EncodeMilliseconds);
    }

    [Fact]
    public void HeadlessCommandsAcceptOnlyVersionedRequestFileForm()
    {
        Assert.True(EncodingV2HeadlessCommand.TryParse(["--v2-probe", "probe.json"], out var probe, out string probePath, out _));
        Assert.Equal(EncodingV2HeadlessOperation.Probe, probe);
        Assert.Equal("probe.json", probePath);
        Assert.True(EncodingV2HeadlessCommand.TryParse(["--v2-plan", "plan.json"], out var plan, out _, out _));
        Assert.Equal(EncodingV2HeadlessOperation.Plan, plan);
        Assert.True(EncodingV2HeadlessCommand.TryParse(["--v2-run", "run.json"], out var run, out _, out _));
        Assert.Equal(EncodingV2HeadlessOperation.Run, run);
        Assert.False(EncodingV2HeadlessCommand.TryParse(["--v2-run", "run.json", "--delete-source"], out _, out _, out _));
    }

    [Fact]
    public void V2ProductionSourceHasNoProhibitedOrchestrationDependencies()
    {
        string root = FindRepositoryRoot();
        string[] forbidden = [
            "EncodingService", "EncodingRequest", "EncodingPlanService", "AdaptiveStorageSavings",
            "PolicyC", "PredictionShadow", "ResearchCapture", "MainForm", "StorageSavings"
        ];
        foreach (string file in Directory.GetFiles(Path.Combine(root, "Services", "EncodingV2"), "*.cs"))
        {
            string contents = File.ReadAllText(file);
            foreach (string name in forbidden)
                Assert.DoesNotContain(name, contents, StringComparison.OrdinalIgnoreCase);
        }
        string program = File.ReadAllText(Path.Combine(root, "Program.cs"));
        Assert.True(program.IndexOf("EncodingV2HeadlessCommand.IsRequested", StringComparison.Ordinal) <
                    program.IndexOf("VelopackApp.Build().Run()", StringComparison.Ordinal));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private V2EncodingCore Core(FakeExecutor executor, FakeValidator? validator = null, IV2OutputPromoter? promoter = null)
    {
        var metadata = Metadata();
        return new V2EncodingCore(metadata, new V2EncodePlanner(), executor,
            validator ?? new FakeValidator { Success = true }, promoter ?? new V2FileOutputPromoter(), _ffmpeg);
    }

    private FakeMetadataProvider Metadata() => new(_source);

    private EncodingV2Request Request(bool deleteSource = false, string? jobId = null) => new()
    {
        JobId = jobId ?? Guid.NewGuid().ToString("N"),
        SourcePath = _source,
        OutputPath = _output,
        Encoder = "libx265",
        Codec = "hevc",
        Container = "mp4",
        QualityMode = "crf",
        QualityValue = 27,
        AudioMode = "copy",
        SubtitleMode = "none",
        DeleteSourceOnSuccess = deleteSource
    };

    private string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "MediaFlux.csproj")) &&
                Directory.Exists(Path.Combine(current.FullName, "Services", "EncodingV2"))) return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Could not find the MediaFlux repository root from the test output directory.");
    }

    private static EncodingV2Request WithQuality(EncodingV2Request request, double quality) => new()
    {
        SchemaVersion = request.SchemaVersion,
        JobId = request.JobId,
        SourcePath = request.SourcePath,
        OutputPath = request.OutputPath,
        Encoder = request.Encoder,
        Codec = request.Codec,
        Container = request.Container,
        QualityMode = request.QualityMode,
        QualityValue = quality,
        Preset = request.Preset,
        PixelFormat = request.PixelFormat,
        GeometryMode = request.GeometryMode,
        MaxWidth = request.MaxWidth,
        MaxHeight = request.MaxHeight,
        AudioMode = request.AudioMode,
        AudioBitrateKbps = request.AudioBitrateKbps,
        SubtitleMode = request.SubtitleMode,
        DeleteSourceOnSuccess = request.DeleteSourceOnSuccess,
        FfmpegPath = request.FfmpegPath,
        FfprobePath = request.FfprobePath,
        EventLogPath = request.EventLogPath
    };

    private sealed class FakeMetadataProvider(string sourcePath) : IV2SourceMetadataProvider
    {
        public Task<V2MediaFacts> ProbeAsync(string path, CancellationToken cancellationToken = default)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("fake media missing", path);
            var info = new FileInfo(path);
            bool isSource = string.Equals(Path.GetFullPath(path), Path.GetFullPath(sourcePath), StringComparison.OrdinalIgnoreCase);
            var video = new MediaProbeStreamInfo
            {
                Index = 0, CodecType = "video", CodecName = isSource ? "h264" : "hevc",
                Width = 64, Height = 32, PixelFormat = "yuv420p", DurationSeconds = 1
            };
            var probe = new MediaProbeResult
            {
                Success = true,
                FormatName = "mov,mp4,m4a,3gp,3g2,mj2",
                DurationSeconds = 1,
                SizeBytes = info.Length,
                Streams = [video]
            };
            return Task.FromResult(new V2MediaFacts
            {
                SourcePath = Path.GetFullPath(path),
                Probe = probe,
                SourceLength = info.Length,
                SourceLastWriteUtcTicks = info.LastWriteTimeUtc.Ticks,
                PrimaryVideoCodec = video.CodecName,
                SourceWidth = 64,
                SourceHeight = 32
            });
        }
    }

    private sealed class FakeExecutor : IV2ProductionExecutor
    {
        public int Calls { get; private set; }
        public int ExitCode { get; init; }
        public string Error { get; init; } = "";
        public bool WriteOutput { get; init; }
        public CancellationTokenSource? CancelOnExecute { get; init; }

        public Task<V2ProcessOutcome> ExecuteAsync(FrozenEncodePlan plan, Action<TimeSpan>? processStarted = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            if (WriteOutput) File.WriteAllBytes(plan.StagingPath, Enumerable.Repeat((byte)0x42, 1024).ToArray());
            processStarted?.Invoke(TimeSpan.FromMilliseconds(1));
            if (CancelOnExecute is not null)
            {
                CancelOnExecute.Cancel();
                return Task.FromCanceled<V2ProcessOutcome>(cancellationToken);
            }
            return Task.FromResult(new V2ProcessOutcome(ExitCode, false, TimeSpan.FromMilliseconds(1),
                TimeSpan.FromMilliseconds(1), Error));
        }
    }

    private sealed class FakeValidator : IV2CandidateValidator
    {
        public bool Success { get; init; }
        public string ErrorCode { get; init; } = "BadCandidate";
        public string Summary { get; init; } = "fake validation result";
        public bool RequireOutputAbsent { get; init; }
        public Task<V2ValidationOutcome> ValidateAsync(FrozenEncodePlan plan, V2MediaFacts source,
            string candidatePath, CancellationToken cancellationToken = default)
        {
            Assert.True(File.Exists(candidatePath));
            if (RequireOutputAbsent) Assert.False(File.Exists(plan.OutputPath));
            var info = new FileInfo(candidatePath);
            return Task.FromResult(Success
                ? new V2ValidationOutcome(true, Summary, info.Length, info.LastWriteTimeUtc.Ticks, "")
                : new V2ValidationOutcome(false, Summary, ErrorCode: ErrorCode));
        }
    }

    private sealed class RecordingPromoter(string sourcePath) : IV2OutputPromoter
    {
        public bool Called { get; private set; }
        public void Promote(string stagingPath, string outputPath)
        {
            Called = true;
            Assert.True(File.Exists(sourcePath));
            File.Move(stagingPath, outputPath, overwrite: false);
        }
    }

    private sealed class RecordingProcessRunner : IMediaToolProcessRunner
    {
        public List<MediaToolProcessRequest> Requests { get; } = [];
        public Task<MediaToolProcessResult> RunAsync(MediaToolProcessRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            request.ProcessStartedCallback?.Invoke(new MediaToolProcessLaunchInfo(42, request.FileName, request.WorkingDirectory, request.Arguments));
            return Task.FromResult(new MediaToolProcessResult
            {
                ExitCode = 0,
                ProcessLaunchElapsed = TimeSpan.FromMilliseconds(2)
            });
        }
    }
}
