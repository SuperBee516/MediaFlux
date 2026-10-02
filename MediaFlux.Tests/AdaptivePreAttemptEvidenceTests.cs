using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Forms;
using MediaFlux.Models;
using MediaFlux.Services;
using Xunit;
using Xunit.Abstractions;

namespace MediaFlux.Tests;

public sealed class AdaptivePreAttemptEvidenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MediaFlux-PreAttemptEvidence", Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper _output;
    private string Source => Path.Combine(_root, "source.mkv");
    public AdaptivePreAttemptEvidenceTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_root);
        File.WriteAllBytes(Source, new byte[1_000_000]);
        File.WriteAllText(Path.Combine(_root, "ffmpeg.exe"), "unused test tool");
        File.WriteAllText(Path.Combine(_root, "ffprobe.exe"), "unused test tool");
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"AdaptivePreAttemptResearchCapture\":null}")]
    [InlineData("{\"AdaptivePreAttemptResearchCapture\":{\"Enabled\":false}}")]
    public async Task LegacyAbsentNullAndFalseConfigNeverCapture(string json)
    {
        string path = Path.Combine(_root, "config.json");
        File.WriteAllText(path, json);
        Config config = Config.Load(path);
        Assert.False(config.AdaptivePreAttemptResearchCapture.Enabled);
        Assert.False(config.StorageSavings.ExperimentalPolicyCRetryEnabled);
        var fixture = await FixtureAsync();
        var executor = new SimulatedExecutor(fixture.Observation);
        var orchestrator = Orchestrator(executor);
        var attempt = orchestrator.CreateAttempt(Snapshot() with { AdaptivePreAttemptResearchCapture = config.AdaptivePreAttemptResearchCapture });
        await orchestrator.ExecuteAsync(attempt, new(), null, CancellationToken.None);
        Assert.Null(executor.Request!.AdaptivePreAttemptCaptureCallback);
        Assert.Null(attempt.PreAttemptCapture);
        Assert.Equal(1, executor.LaunchCount);
        Assert.False(Directory.Exists(Store().DirectoryPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CaptureIsIndependentOfRetryGateAndLeavesFrozenPlanEvidenceAndTerminalCountsUnchanged(bool retryGate)
    {
        var fixture = await FixtureAsync();
        var executor = new SimulatedExecutor(fixture.Observation);
        var orchestrator = Orchestrator(executor);
        var attempt = orchestrator.CreateAttempt(Snapshot() with { ExperimentalPolicyCRetryEnabled = retryGate });
        executor.BeforeLaunch = () =>
        {
            Assert.NotNull(attempt.PreAttemptCapture);
            Assert.True(File.Exists(attempt.PreAttemptCapture!.RecordPath));
        };
        byte[] original = JsonSerializer.SerializeToUtf8Bytes(fixture.Observation.Plan.Plan);
        await orchestrator.ExecuteAsync(attempt, new(), null, CancellationToken.None);
        Assert.Equal(1, executor.LaunchCount);
        Assert.Equal(retryGate, executor.Request!.ExperimentalPolicyCRetryEnabled);
        Assert.Equal(original, JsonSerializer.SerializeToUtf8Bytes(fixture.Observation.Plan.Plan));
        Assert.Same(fixture.Observation.Plan.Plan, attempt.Plan);
        Assert.Same(fixture.Selection, attempt.AdaptiveSelection);
        var ack = attempt.PreAttemptCapture!;
        byte[] persisted = File.ReadAllBytes(ack.RecordPath);
        using var doc = JsonDocument.Parse(persisted);
        var root = doc.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(attempt.Snapshot.OperationId, root.GetProperty("operationId").GetString());
        Assert.Equal(attempt.Snapshot.SavedJobId!.Value, root.GetProperty("savedJobId").GetGuid());
        Assert.Equal(42, root.GetProperty("queueRowId").GetInt64());
        Assert.Equal("case-1", root.GetProperty("source").GetProperty("campaignCaseId").GetString());
        Assert.Equal("opaque-source", root.GetProperty("source").GetProperty("stableSourceId").GetString());
        Assert.Equal(new string('a', 64), root.GetProperty("source").GetProperty("sha256").GetString());
        Assert.Equal(new FileInfo(Source).Length, root.GetProperty("source").GetProperty("byteLength").GetInt64());
        Assert.Equal(new FileInfo(Source).LastWriteTimeUtc, root.GetProperty("source").GetProperty("lastWriteUtc").GetDateTime());
        Assert.False(root.GetProperty("productionAttempt1Started").GetBoolean());
        Assert.Equal(0, root.GetProperty("productionAttemptCount").GetInt32());
        Assert.Equal(ack.CapturedUtc, root.GetProperty("capturedUtc").GetDateTime());
        Assert.Equal(retryGate, root.GetProperty("experimentalPolicyCRetryEnabled").GetBoolean());
        Assert.False(root.TryGetProperty("naturalPolicyCTrigger", out _));
        Assert.False(root.TryGetProperty("phaseOneResult", out _));
        Assert.False(root.TryGetProperty("retryCandidateEligibility", out _));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(persisted)).ToLowerInvariant(), ack.Sha256);
        Assert.True(ack.Success);
        Assert.True(ack.SerializationSeconds >= 0 && ack.DurableWriteSeconds >= 0 && ack.TotalSeconds >= 0);
        _output.WriteLine($"Serialization={ack.SerializationSeconds:R}s; durable write={ack.DurableWriteSeconds:R}s; total={ack.TotalSeconds:R}s; bytes={persisted.Length}");

        AssertJsonCopy(fixture.Request.Contract, root.GetProperty("contract"));
        AssertJsonCopy(fixture.Request.Envelope, root.GetProperty("envelope"));
        AssertJsonCopy(fixture.Request.Video, root.GetProperty("video"));
        AssertJsonCopy(fixture.Selection, root.GetProperty("selection"));
        AssertJsonCopy(EncodingPlanService.GetExecutionValues(fixture.Observation.Plan.Plan).ContainerDecision,
            root.GetProperty("streamPlan").GetProperty("containerDecision"));
        Assert.Equal(fixture.Selection.Candidates.Select(c => c.Quality), root.GetProperty("selection").GetProperty("candidates").EnumerateArray().Select(c => c.GetProperty("quality").GetInt32()));
        Assert.Equal(fixture.Selection.SelectedQuality, root.GetProperty("selection").GetProperty("selectedQuality").GetInt32());
        foreach (var pair in fixture.Selection.Candidates.Zip(root.GetProperty("selection").GetProperty("candidates").EnumerateArray()))
        {
            Assert.Equal(pair.First.ProjectedLowerBytes, pair.Second.GetProperty("projectedLowerBytes").GetDouble());
            Assert.Equal(pair.First.ProjectedUpperBytes, pair.Second.GetProperty("projectedUpperBytes").GetDouble());
            Assert.Equal(pair.First.Classification.ToString(), pair.Second.GetProperty("classification").GetString());
            Assert.Equal(pair.First.Samples.Select(s => s.Sample.Label), pair.Second.GetProperty("samples").EnumerateArray().Select(s => s.GetProperty("sample").GetProperty("label").GetString()));
        }

        Assert.True(orchestrator.RecordSuccessfulExecution(attempt, DateTime.UtcNow, 100, 1, "test", null, false, null));
        Assert.False(orchestrator.RecordSuccessfulExecution(attempt, DateTime.UtcNow, 100, 1, "test", null, false, null));
        Assert.Single(new EncodingStatisticsService(attempt.Snapshot.StatisticsPath).GetAll());
        var history = new HistoryService(Path.Combine(_root, "history.json"));
        history.AppendEncodingOutcome(new() { Id = attempt.Snapshot.OperationId, Status = JobStatus.Success,
            AdaptiveSelection = attempt.AdaptiveSelection, TerminalResult = EncodingTerminalResult.Completed }, attempt.ExecutionOutcome);
        Assert.Equal(1, Assert.Single(history.LoadAll()).ProductionEncodeCount);
        Assert.Equal(persisted, File.ReadAllBytes(ack.RecordPath));
        Assert.Single(Directory.GetFiles(Store().DirectoryPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisabledCapturePreservesOneAttemptIncludingPolicyCEnabled(bool retryGate)
    {
        var fixture = await FixtureAsync();
        var executor = new SimulatedExecutor(fixture.Observation);
        var orchestrator = Orchestrator(executor, new ThrowingSink());
        var attempt = orchestrator.CreateAttempt(Snapshot() with
        {
            AdaptivePreAttemptResearchCapture = new(), ExperimentalPolicyCRetryEnabled = retryGate
        });
        await orchestrator.ExecuteAsync(attempt, new(), null, CancellationToken.None);
        Assert.Equal(1, executor.LaunchCount);
        Assert.Null(executor.Request!.AdaptivePreAttemptCaptureCallback);
        Assert.Equal(retryGate, executor.Request.ExperimentalPolicyCRetryEnabled);
        Assert.Same(fixture.Selection, attempt.AdaptiveSelection);
        Assert.False(Directory.Exists(Store().DirectoryPath));
        Assert.True(orchestrator.RecordSuccessfulExecution(attempt, DateTime.UtcNow, 100, 1, "test", null, false, null));
        Assert.Single(new EncodingStatisticsService(attempt.Snapshot.StatisticsPath).GetAll());
    }

    [Fact]
    public async Task DurableAcknowledgmentMustCompleteBeforeSimulatedLaunch()
    {
        var fixture = await FixtureAsync();
        var sink = new BlockingSink(Store());
        var executor = new SimulatedExecutor(fixture.Observation);
        var orchestrator = Orchestrator(executor, sink);
        var attempt = orchestrator.CreateAttempt(Snapshot());
        Task run = orchestrator.ExecuteAsync(attempt, new(), null, CancellationToken.None);
        await sink.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, executor.LaunchCount);
        Assert.Null(attempt.PreAttemptCapture);
        Assert.False(Directory.Exists(Store().DirectoryPath));
        sink.Release.SetResult(true);
        await run;
        Assert.Equal(1, executor.LaunchCount);
        Assert.NotNull(attempt.PreAttemptCapture);
    }

    [Theory]
    [InlineData("write")]
    [InlineData("negative-ack")]
    [InlineData("missing-selection")]
    public async Task CaptureFailureIsExplicitNotRunWithZeroLaunchesAndNoRetry(string mode)
    {
        var fixture = await FixtureAsync();
        var observation = mode == "missing-selection" ? fixture.Observation with { SelectionRequest = null } : fixture.Observation;
        IAdaptivePreAttemptEvidenceSink? sink = mode == "negative-ack" ? new NegativeSink() : null;
        if (mode == "write") File.WriteAllText(Path.Combine(_root, "data"), "directory blocker");
        var executor = new SimulatedExecutor(observation);
        var orchestrator = Orchestrator(executor, sink);
        var attempt = orchestrator.CreateAttempt(Snapshot() with { ExperimentalPolicyCRetryEnabled = true });
        var ex = await Assert.ThrowsAsync<AdaptivePreAttemptEvidenceCaptureException>(() => orchestrator.ExecuteAsync(attempt, new(), null, CancellationToken.None));
        Assert.Contains("Research pre-attempt evidence capture failed", ex.Message);
        Assert.Contains("production attempt 1 was not started", ex.Message);
        Assert.Equal(0, executor.LaunchCount);
        Assert.Equal(EncodingTerminalResult.NotRun, attempt.ExecutionOutcome!.TerminalResult);
        Assert.Equal(0, attempt.ExecutionOutcome.ProductionEncodeCount);
        Assert.Empty(attempt.ExecutionOutcome.Recovery);
        Assert.Null(attempt.ExecutionOutcome.StorageSavings);
        Assert.Null(attempt.ExecutionOutcome.AdaptiveStorageSavingsRetry);
        Assert.Null(attempt.EncodeResult);
        Assert.Null(attempt.PreAttemptCapture);
        Assert.True(File.Exists(Source));
    }

    [Fact]
    public async Task SameOperationCannotOverwriteAndFailedPublicationLeavesNoTemporaryFile()
    {
        var fixture = await FixtureAsync();
        var executor = new SimulatedExecutor(fixture.Observation);
        var orchestrator = Orchestrator(executor);
        var snapshot = Snapshot();
        var first = orchestrator.CreateAttempt(snapshot);
        await orchestrator.ExecuteAsync(first, new(), null, CancellationToken.None);
        byte[] before = File.ReadAllBytes(first.PreAttemptCapture!.RecordPath);
        var second = orchestrator.CreateAttempt(snapshot with { OperationId = Guid.Parse(snapshot.OperationId).ToString("D") });
        await Assert.ThrowsAsync<AdaptivePreAttemptEvidenceCaptureException>(() => orchestrator.ExecuteAsync(second, new(), null, CancellationToken.None));
        Assert.Equal(1, executor.LaunchCount);
        Assert.Equal(before, File.ReadAllBytes(first.PreAttemptCapture.RecordPath));
        Assert.Single(Directory.GetFiles(Store().DirectoryPath));
    }

    [Fact]
    public void SuppliedSourceContextFailsClosedWhenStale()
    {
        var snapshot = Snapshot();
        var orchestrator = Orchestrator(new SimulatedExecutor(null!));
        Assert.Throws<AdaptivePreAttemptEvidenceCaptureException>(() => orchestrator.CreateAttempt(snapshot with
        {
            AdaptivePreAttemptResearchCapture = snapshot.AdaptivePreAttemptResearchCapture with
            {
                Source = snapshot.AdaptivePreAttemptResearchCapture.Source! with { ExpectedByteLength = 999 }
            }
        }));
        Assert.False(Directory.Exists(Store().DirectoryPath));
    }

    [Fact]
    public async Task ConcurrentPublicationHasOneWinnerAndNoTemporaryArtifacts()
    {
        var fixture = await FixtureAsync();
        var recording = new RecordingSink(Store());
        var orchestrator = Orchestrator(new SimulatedExecutor(fixture.Observation), recording);
        await orchestrator.ExecuteAsync(orchestrator.CreateAttempt(Snapshot()), new(), null, CancellationToken.None);
        var record = recording.Record! with { OperationId = Guid.NewGuid().ToString("N") };
        async Task<bool> Publish()
        {
            try { await Store().CaptureAsync(record, CancellationToken.None); return true; }
            catch (AdaptivePreAttemptEvidenceCaptureException) { return false; }
        }
        Assert.Single(await Task.WhenAll(Publish(), Publish()), success => success);
        Assert.Equal(2, Directory.GetFiles(Store().DirectoryPath, "*.json").Length);
        Assert.Empty(Directory.GetFiles(Store().DirectoryPath, "*.tmp"));
        await Assert.ThrowsAsync<AdaptivePreAttemptEvidenceCaptureException>(() => Store().CaptureAsync(record with { OperationId = "../escape" }, CancellationToken.None));
    }

    [Fact]
    public async Task CancellationWhileAwaitingCaptureCannotLaunchProduction()
    {
        var fixture = await FixtureAsync();
        var sink = new BlockingSink(Store());
        var executor = new SimulatedExecutor(fixture.Observation);
        var orchestrator = Orchestrator(executor, sink);
        var attempt = orchestrator.CreateAttempt(Snapshot());
        using var cancellation = new CancellationTokenSource();
        Task run = orchestrator.ExecuteAsync(attempt, new(), null, cancellation.Token);
        await sink.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Equal(0, executor.LaunchCount);
        Assert.Null(attempt.PreAttemptCapture);
        Assert.False(Directory.Exists(Store().DirectoryPath));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task RealEncodingServiceAwaitsGateAfterFrozenCallbacksBeforeProductionRunner(bool failCapture, bool skipped)
    {
        var events = new List<string>();
        int launches = 0;
        // Native read-only stand-in: FFprobe's arguments are rejected, so the existing
        // timing service returns Unknown. Metadata uses ProbeRunner; no media tool runs.
        File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "whoami.exe"),
            Path.Combine(_root, "ffprobe.exe"), overwrite: true);
        var service = new EncodingService(_root, _ => { }, null,
            Path.Combine(_root, "ffmpeg.exe"), Path.Combine(_root, "ffprobe.exe"), null,
            new AdaptiveStorageSavingsTests.FakeSamples((_, _) => skipped ? 50_000 : 10),
            (_, _, _, _) =>
            {
                events.Add("production");
                launches++;
                throw new ProductionBoundaryException();
            });
        // Existing probe/process seams keep this test entirely synthetic. The fake tool is never an encoder.
        typeof(EncodingService).GetField("_ffprobeService", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(service, new FfprobeService(Path.Combine(_root, "ffprobe.exe"), new ProbeRunner()));
        EncodingPlanSnapshot? frozen = null;
        AdaptiveQualitySelectionEvidence? selection = null;
        var request = new EncodingRequest
        {
            Input = EncodingInputSource.FromFile(Source), OutputFolder = _root, Suffix = "_test",
            Encoder = new(VideoEncoderIds.Libx265, VideoCodecFamily.Hevc, "libx265"), UseGpu = false, EncoderPreset = "medium",
            Restoration = new(), QualityIntent = EncodingQualityIntent.Automatic(QualityTarget.Balanced),
            StorageSavingsContract = AdaptiveStorageSavingsTests.Contract, AdaptiveStorageSavingsEnabled = true,
            ExperimentalPolicyCRetryEnabled = true, OutputContainer = OutputContainerSelection.Matroska,
            AdaptiveSelectionCallback = value => { events.Add("selection"); selection = value; },
            EncodingPlanSnapshotCallback = value => { events.Add("plan"); frozen = value; },
            AdaptivePreAttemptCaptureCallback = async (observation, _) =>
            {
                events.Add("capture");
                Assert.Same(selection, observation.Plan.Plan.AdaptiveSelection);
                Assert.Same(frozen!.Plan, observation.Plan.Plan);
                Assert.Equal(0, launches);
                Assert.NotNull(observation.SelectionRequest);
                await Task.Yield();
                if (failCapture) throw new AdaptivePreAttemptEvidenceCaptureException("synthetic disk failure");
                events.Add("acknowledged");
            }
        };
        if (failCapture)
            await Assert.ThrowsAsync<AdaptivePreAttemptEvidenceCaptureException>(() => service.EncodeWithResultAsync(request));
        else if (skipped)
            await Assert.ThrowsAsync<AdaptiveStorageSavingsSkippedException>(() => service.EncodeWithResultAsync(request));
        else
            await Assert.ThrowsAsync<ProductionBoundaryException>(() => service.EncodeWithResultAsync(request));
        Assert.Equal(failCapture || skipped ? 0 : 1, launches);
        Assert.Equal(failCapture ? ["selection", "plan", "capture"] : skipped ? ["selection", "plan", "capture", "acknowledged"] : new[] { "selection", "plan", "capture", "acknowledged", "production" }, events);
    }

    [Fact]
    public void MainFormSavedJobSettingsNaturallyCaptureResearchOptInThroughExistingConstruction()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            MainForm? main = null;
            string? configPath = null;
            try
            {
                main = new MainForm();
                configPath = QueueWorkspaceTestSupport.UseIsolatedConfig(main);
                var options = Snapshot().AdaptivePreAttemptResearchCapture;
                var config = (Config)typeof(MainForm).GetField("_config", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
                config.AdaptivePreAttemptResearchCapture = options;
                config.StorageSavings.Enabled = true;
                var job = new EncodeJobSettings
                {
                    OutputFolder = _root, EncoderId = VideoEncoderIds.Nvenc, VideoCodec = "HEVC",
                    CompressionProfile = "Medium Quality (Default)", EncoderPreset = "p5", OutputContainer = "Matroska",
                    VideoFormat = "H.265 / HEVC (x265)", Resolution = "Original", AudioChannels = "Keep Original",
                    QualityMode = "Automatic", QualityTarget = "Balanced"
                };
                typeof(MainForm).GetMethod("ApplyJobSettings", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, [job]);
                var settings = (EncodeExecutionSnapshotSettings)typeof(MainForm)
                    .GetMethod("CaptureEncodeExecutionSnapshotSettings", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, [OutputContainerSelection.Matroska])!;
                Assert.Equal(options, settings.AdaptivePreAttemptResearchCapture);
                Assert.Equal(AppPaths.UserDataDirectory, settings.ResearchRoot);
                Assert.False(settings.StorageSavings.ExperimentalPolicyCRetryEnabled);
                var saved = EncodeExecutionSnapshotSettings.FromSavedJob(job, config);
                Assert.Equal(options, saved.AdaptivePreAttemptResearchCapture);
                var snapshot = new EncodeExecutionSnapshotBuilder().Build(new()
                {
                    OperationId = Guid.NewGuid().ToString("N"), StatisticsStartUtc = DateTime.UtcNow,
                    MediaFluxVersion = "1.7.3", StatisticsPath = Path.Combine(_root, "stats.jsonl"),
                    ContainerCompatibilityConfirmed = false, SavedJobId = Guid.NewGuid(), QueueRowId = 7
                }, settings, new() { SourceFilePath = Source, LogicalSourcePath = Source, EstimatedTargetMb = 100 });
                Assert.Equal(options, snapshot.AdaptivePreAttemptResearchCapture);
                Assert.Equal(settings.ResearchRoot, snapshot.ResearchRoot);
                Assert.NotNull(snapshot.SavedJobId);
                Assert.Equal(7, snapshot.QueueRowId);
                Assert.False(Directory.Exists(Store().DirectoryPath));
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                WinFormsTestLifecycle.CloseAndDispose(main);
                QueueWorkspaceTestSupport.DeleteIsolatedConfig(configPath);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Research capture MainForm construction test timed out.");
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    private AdaptivePreAttemptEvidenceStore Store() => new(_root);
    private EncodeExecutionSnapshot Snapshot() => new()
    {
        OperationId = Guid.NewGuid().ToString("N"), StatisticsStartUtc = DateTime.UtcNow,
        SourceFilePath = Source, LogicalSourcePath = Source, Input = EncodingInputSource.FromFile(Source),
        OutputFolder = _root, Suffix = "_test", Encoder = AdaptiveStorageSavingsTests.Context().Encoder,
        UseGpu = true, Restoration = new(), OutputContainer = OutputContainerSelection.Matroska,
        EncoderText = "NVENC", Codec = "hevc_nvenc", MediaFluxVersion = "1.7.3",
        StatisticsPath = Path.Combine(_root, "stats.jsonl"), ResearchRoot = _root,
        AdaptiveStorageSavingsEnabled = true, StorageSavingsContract = AdaptiveStorageSavingsTests.Contract,
        SavedJobId = Guid.NewGuid(), QueueRowId = 42,
        AdaptivePreAttemptResearchCapture = new()
        {
            Enabled = true, CaptureRevision = "test-revision", ImplementationId = "test-commit",
            ExecutableSha256 = new string('b', 64), FfmpegSha256 = new string('c', 64),
            FfprobeSha256 = new string('d', 64), ResearchConfigSha256 = new string('e', 64),
            Source = new() { CanonicalPath = Source, CampaignCaseId = "case-1", StableSourceId = "opaque-source",
                Sha256 = new string('a', 64), ExpectedByteLength = new FileInfo(Source).Length,
                ExpectedLastWriteUtc = new FileInfo(Source).LastWriteTimeUtc }
        }
    };

    private async Task<(AdaptivePreAttemptObservation Observation, AdaptiveQualitySelectionRequest Request, AdaptiveQualitySelectionEvidence Selection)> FixtureAsync()
    {
        var context = AdaptiveStorageSavingsTests.Context() with { Input = EncodingInputSource.FromFile(Source) };
        var plan = EncodingPlanService.Create(context);
        Assert.True(AdaptiveStorageSavingsPolicy.TryCreateRequest(true, false, AdaptiveStorageSavingsTests.Contract, context, plan, out var request, out _));
        int preferred = request!.Envelope.PreferredQuality;
        var selection = await new StorageSavingsSampleSelector(new AdaptiveStorageSavingsTests.FakeSamples((quality, position) =>
            quality == preferred ? 50_000 + position : 1000 + position)).SelectAsync(request, null, CancellationToken.None);
        Assert.True(selection.Candidates.Count > 1);
        Assert.Equal(AdaptiveSelectionDisposition.Selected, selection.Disposition);
        plan = EncodingPlanService.FreezeAdaptiveSelection(plan, selection);
        return (new(new(plan.PlanId, plan), request, context.Restoration.Clone()), request, selection);
    }

    private EncodeExecutionOrchestrator Orchestrator(IEncodeRequestExecutor executor, IAdaptivePreAttemptEvidenceSink? sink = null)
    {
        var shadow = new NvencQualityModePredictionShadowService(new PredictionShadowObservationJournal(Path.Combine(_root, "shadow.jsonl")),
            new PredictionShadowComplexitySamplingService(Path.Combine(_root, "ffmpeg.exe"), new ProbeRunner()));
        return new(executor, shadow, new EncodingStatisticsService(Path.Combine(_root, "stats.jsonl")),
            hardwareKey: () => "synthetic", preAttemptEvidenceSink: sink);
    }

    private static void AssertJsonCopy<T>(T expected, JsonElement actual)
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(allowIntegerValues: false) } };
        Assert.Equal(JsonSerializer.Serialize(expected, options), JsonSerializer.Serialize(actual));
    }

    private sealed class SimulatedExecutor(AdaptivePreAttemptObservation observation) : IEncodeRequestExecutor
    {
        public EncodingRequest? Request { get; private set; }
        public int LaunchCount { get; private set; }
        public Action? BeforeLaunch { get; set; }
        public async Task<EncodingService.EncodeResult> EncodeWithResultAsync(EncodingRequest request)
        {
            Request = request;
            request.AdaptiveSelectionCallback?.Invoke(observation.Plan.Plan.AdaptiveSelection!);
            request.EncodingPlanSnapshotCallback?.Invoke(observation.Plan);
            request.PreEncodeExecutionValidationCallback?.Invoke(observation.Plan);
            if (request.AdaptivePreAttemptCaptureCallback is { } capture) await capture(observation, request.CancellationToken);
            BeforeLaunch?.Invoke();
            LaunchCount++;
            request.EncodingExecutionOutcomeCallback?.Invoke(new(observation.Plan.PlanId, [], [], TerminalResult: EncodingTerminalResult.Completed, ProductionEncodeCount: 1));
            return new(success: true, outputPath: "synthetic-final.mkv", finalizationSucceeded: true, finalOutputSizeBytes: 100,
                storageSavingsContract: request.StorageSavingsContract, storageSavings: StorageSavingsContractService.Evaluate(request.StorageSavingsContract, 100));
        }
    }

    private sealed class BlockingSink(IAdaptivePreAttemptEvidenceSink store) : IAdaptivePreAttemptEvidenceSink
    {
        public TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<AdaptivePreAttemptCaptureAcknowledgment> CaptureAsync(AdaptivePreAttemptEvidenceRecord record, CancellationToken token)
        {
            Entered.SetResult(true);
            await Release.Task.WaitAsync(token);
            return await store.CaptureAsync(record, token);
        }
    }
    private sealed class RecordingSink(IAdaptivePreAttemptEvidenceSink store) : IAdaptivePreAttemptEvidenceSink
    {
        public AdaptivePreAttemptEvidenceRecord? Record { get; private set; }
        public Task<AdaptivePreAttemptCaptureAcknowledgment> CaptureAsync(AdaptivePreAttemptEvidenceRecord record, CancellationToken token)
        {
            Record = record;
            return store.CaptureAsync(record, token);
        }
    }
    private sealed class ThrowingSink : IAdaptivePreAttemptEvidenceSink
    {
        public Task<AdaptivePreAttemptCaptureAcknowledgment> CaptureAsync(AdaptivePreAttemptEvidenceRecord record, CancellationToken token)
            => throw new Xunit.Sdk.XunitException("Disabled capture invoked a sink.");
    }
    private sealed class NegativeSink : IAdaptivePreAttemptEvidenceSink
    {
        public Task<AdaptivePreAttemptCaptureAcknowledgment> CaptureAsync(AdaptivePreAttemptEvidenceRecord record, CancellationToken token)
            => Task.FromResult(new AdaptivePreAttemptCaptureAcknowledgment(false, "", "", record.CapturedUtc, 0, 0, 0));
    }
    private sealed class ProbeRunner : IMediaToolProcessRunner
    {
        public Task<MediaToolProcessResult> RunAsync(MediaToolProcessRequest request, CancellationToken token = default) => Task.FromResult(new MediaToolProcessResult
        {
            ExitCode = 0,
            StandardOutput = """
                {"format":{"duration":"100","size":"1000000","format_name":"matroska"},"streams":[{"index":0,"codec_type":"video","codec_name":"h264","width":1920,"height":1080,"pix_fmt":"yuv420p","r_frame_rate":"30/1","avg_frame_rate":"30/1","duration":"100","nb_frames":"3000"}]}
                """
        });
    }
    private sealed class ProductionBoundaryException : Exception;
    public void Dispose() => Directory.Delete(_root, recursive: true);
}
