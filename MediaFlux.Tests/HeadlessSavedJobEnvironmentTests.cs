using System.Diagnostics;
using System.Security.Cryptography;
using MediaFlux.Models;
using MediaFlux.Services;
using MediaFlux.Services.Encoders;
using Xunit;

namespace MediaFlux.Tests;

public sealed class HeadlessSavedJobEnvironmentTests : IDisposable
{
    private readonly string _fixture = Path.Combine(Path.GetTempPath(), "MediaFlux-headless-environment", Guid.NewGuid().ToString("N"));
    private readonly string _isolated;
    private readonly MediaFluxStoragePathService _normal;
    private readonly Guid _jobId = Guid.NewGuid();

    public HeadlessSavedJobEnvironmentTests()
    {
        _isolated = Path.Combine(_fixture, "Isolated");
        _normal = new(Path.Combine(_fixture, "Default"), Path.Combine(_fixture, "storage-location.json"));
        Prepare(_normal.Root, "_DEFAULT", savings: false);
        string relocated = Path.Combine(_fixture, "Relocated");
        Prepare(relocated, "_NORMAL", savings: false);
        _normal.WriteConfiguredRoot(relocated);
        Prepare(_isolated, "_ISOLATED", savings: true);
        File.WriteAllText(Path.Combine(_normal.Data, "history.json"), "[]");
        File.WriteAllText(Path.Combine(_normal.Data, "prediction-shadow-observations.jsonl"), "normal research sentinel");
        new EncodingStatisticsService(Path.Combine(_normal.Data, "encoding-statistics.jsonl"))
            .AppendFinalized(new() { Id = "normal-only", StartUtc = DateTime.UtcNow, EndUtc = DateTime.UtcNow });
    }

    public void Dispose() => Directory.Delete(_fixture, recursive: true);

    [Fact]
    public void NoOverridePreservesConfiguredPointerConfigAndJobLookup()
    {
        MediaFluxStoragePathService paths = HeadlessSavedJobEnvironment.ResolvePaths(Command(null), _normal);
        var environment = HeadlessSavedJobEnvironment.Load(paths);
        Assert.Same(_normal, paths);
        Assert.False(paths.IsProcessScoped);
        Assert.Equal("_NORMAL", environment.Config.OutputSuffix);
        Assert.False(environment.Config.StorageSavings.Enabled);
        Assert.Equal(Path.Combine(_normal.Root, "source.fixture"), Assert.Single(environment.Jobs).Files[0].SourcePath);
    }

    [Fact]
    public void NoOverrideRetainsDefaultsForMissingConfigAndJobStore()
    {
        var paths = new MediaFluxStoragePathService(Path.Combine(_fixture, "EmptyNormal"), Path.Combine(_fixture, "missing-pointer.json"));
        var environment = HeadlessSavedJobEnvironment.Load(HeadlessSavedJobEnvironment.ResolvePaths(Command(null), paths));
        Assert.Equal(new Config().OutputSuffix, environment.Config.OutputSuffix);
        Assert.Empty(environment.Jobs);
        Assert.False(Directory.Exists(paths.Root));
    }

    [Fact]
    public void NoOverridePreservesLegacyNullConfigAndJobStoreBehavior()
    {
        File.WriteAllText(_normal.Config, "null");
        File.WriteAllText(Path.Combine(_normal.Data, "encode-jobs.json"), "null");
        var environment = HeadlessSavedJobEnvironment.Load(_normal);
        Assert.Equal(new Config().OutputSuffix, environment.Config.OutputSuffix);
        Assert.Empty(environment.Jobs);
    }

    [Fact]
    public void IsolatedStateWinsOverNormalAndLocalPointersEvenWhenNormalStateIsCorrupt()
    {
        string normalPointer = File.ReadAllText(_normal.LocationFile);
        File.WriteAllText(Path.Combine(_isolated, "storage-location.json"), normalPointer);
        File.WriteAllText(_normal.Config, "not-json");
        File.WriteAllText(_normal.LocationFile, "not-json");
        var before = NormalSnapshot();

        var environment = LoadIsolated();
        Assert.Equal("_ISOLATED", environment.Config.OutputSuffix);
        Assert.True(environment.Config.StorageSavings.Enabled);
        Assert.Equal(Path.Combine(_isolated, "source.fixture"), Assert.Single(environment.Jobs).Files[0].SourcePath);
        Assert.Empty(new EncodingStatisticsService(Path.Combine(environment.Paths.Data, "encoding-statistics.jsonl")).GetAll());
        Assert.Throws<InvalidOperationException>(() => environment.Paths.WriteConfiguredRoot(_normal.DefaultRoot));
        Assert.Equal(normalPointer, File.ReadAllText(Path.Combine(_isolated, "storage-location.json")));
        AssertNormalUnchanged(before);
        Assert.Throws<InvalidDataException>(() => HeadlessSavedJobEnvironment.Load(_normal));
    }

    [Theory]
    [InlineData("config.json")]
    [InlineData("data/encode-jobs.json")]
    public void MissingRequiredIsolatedStateFailsClearlyWithoutInitializingOrFallingBack(string relative)
    {
        string missing = Path.Combine(_isolated, relative);
        File.Delete(missing);
        var before = NormalSnapshot();
        var error = Assert.Throws<HeadlessSavedJobValidationException>(() => LoadIsolated());
        Assert.Contains("normal UserData was not used", error.Message);
        Assert.IsType<FileNotFoundException>(error.InnerException);
        Assert.False(File.Exists(missing));
        Assert.False(Directory.Exists(Path.Combine(_isolated, "temp")));
        AssertNormalUnchanged(before);
    }

    [Theory]
    [InlineData("config.json", "null")]
    [InlineData("config.json", "not-json")]
    [InlineData("data/encode-jobs.json", "null")]
    [InlineData("data/encode-jobs.json", "{}")]
    public void CorruptIsolatedStateIsAnErrorAndNeverUsesNormalDefaults(string relative, string content)
    {
        string path = Path.Combine(_isolated, relative);
        File.WriteAllText(path, content);
        var before = NormalSnapshot();
        Assert.Throws<HeadlessSavedJobValidationException>(() => LoadIsolated());
        Assert.Equal(content, File.ReadAllText(path));
        AssertNormalUnchanged(before);
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative")]
    [InlineData("C:relative")]
    [InlineData("C:\\bad\0path")]
    public void InvalidRootCannotFallBackToNormalData(string root)
    {
        Assert.ThrowsAny<ArgumentException>(() => HeadlessSavedJobEnvironment.ResolvePaths(Command(root), _normal));
    }

    [Fact]
    public void MissingDirectoryOrFileRootIsRejectedWithoutCreation()
    {
        string missing = Path.Combine(_fixture, "NotPrepared");
        Assert.Throws<DirectoryNotFoundException>(() => HeadlessSavedJobEnvironment.ResolvePaths(Command(missing), _normal));
        Assert.False(Directory.Exists(missing));
        string file = Path.Combine(_fixture, "not-directory");
        File.WriteAllText(file, "keep");
        Assert.Throws<DirectoryNotFoundException>(() => HeadlessSavedJobEnvironment.ResolvePaths(Command(file), _normal));
        Assert.Equal("keep", File.ReadAllText(file));
        string volumeRoot = Path.GetPathRoot(_isolated)!;
        Assert.Equal(volumeRoot, MediaFluxStoragePathService.NormalizeIsolatedRoot(volumeRoot));
    }

    [Fact]
    public void StartupFailureDisposesProcessScopeWithoutChangingNormalResolution()
    {
        var previous = AppPaths.StoragePaths;
        var paths = HeadlessSavedJobEnvironment.ResolvePaths(Command(_isolated), _normal);
        File.Delete(paths.Config);
        Assert.Throws<HeadlessSavedJobValidationException>(() =>
        {
            using var scope = AppPaths.UseIsolatedHeadlessStorage(paths);
            HeadlessSavedJobEnvironment.Load(paths);
        });
        Assert.Same(previous, AppPaths.StoragePaths);
    }

    [Fact]
    public void ProcessScopeCoversStaticPathsAndCannotInitializeGuiOrMigrateState()
    {
        var previous = AppPaths.StoragePaths;
        var paths = LoadIsolated().Paths;
        using (AppPaths.UseIsolatedHeadlessStorage(paths))
        {
            Assert.Same(paths, AppPaths.StoragePaths);
            string[] runtimePaths =
            [
                AppPaths.RootDirectory, AppPaths.UserDataDirectory, AppPaths.ConfigFile, AppPaths.EncodeJobsFile,
                AppPaths.HistoryFile, AppPaths.EncodingStatisticsFile, AppPaths.PredictionShadowObservationsFile,
                AppPaths.LogsDirectory, AppPaths.TempDirectory, AppPaths.BackupDirectory,
                AppPaths.AiIntermediatesDirectory, AppPaths.TensorRtEnginesDirectory,
                AppPaths.AiBenchmarkDatabaseFile, AppPaths.NcnnPerformanceTuningCacheFile,
                AppPaths.RestorationPreviewsDirectory, AppPaths.FramePreviewsDirectory,
                AppPaths.RuntimeTemporaryDirectory("AdaptiveSamples"), AppPaths.RuntimeTemporaryDirectory("PredictionShadow")
            ];
            Assert.All(runtimePaths, path => Assert.True(
                MediaFluxStoragePathService.Same(_isolated, path) || MediaFluxStoragePathService.IsWithin(path, _isolated), path));
            Assert.Throws<InvalidOperationException>(AppPaths.Initialize);
            Assert.Throws<InvalidOperationException>(() => AppPaths.UseIsolatedHeadlessStorage(paths));
            Assert.False(Directory.Exists(paths.Backups));
            Assert.False(Directory.Exists(paths.Temp));
            Assert.False(File.Exists(Path.Combine(_isolated, ".legacy-install-data-migrated-v1")));
        }
        Assert.Same(previous, AppPaths.StoragePaths);
        Assert.Equal(Path.Combine(Path.GetTempPath(), "MediaFlux", "AdaptiveSamples"), AppPaths.RuntimeTemporaryDirectory("AdaptiveSamples"));
    }

    [Fact]
    public void StaticPersistenceUsesOnlyIsolatedStateAndNormalFilesStayByteIdentical()
    {
        var before = NormalSnapshot();
        var paths = LoadIsolated().Paths;
        using (AppPaths.UseIsolatedHeadlessStorage(paths))
        {
            // Fail before any persistence operation if static routing regresses.
            Assert.Equal(paths.Root, AppPaths.UserDataDirectory);
            Assert.Equal(Path.Combine(paths.Data, "encoding-statistics.jsonl"), AppPaths.EncodingStatisticsFile);
            Assert.Equal(Path.Combine(paths.Data, "history.json"), AppPaths.HistoryFile);
            Assert.Equal(Path.Combine(paths.Data, "prediction-shadow-observations.jsonl"), AppPaths.PredictionShadowObservationsFile);
            Assert.Equal(paths.Logs, AppPaths.LogsDirectory);
            Assert.Equal(Path.Combine(paths.Data, "ncnn-performance-tuning.json"), AppPaths.NcnnPerformanceTuningCacheFile);
            var statistics = new EncodingStatisticsService(AppPaths.EncodingStatisticsFile);
            Assert.Empty(statistics.GetAll());
            Assert.True(statistics.AppendFinalized(new() { Id = "isolated-only", StartUtc = DateTime.UtcNow, EndUtc = DateTime.UtcNow }));
            var history = new HistoryService(AppPaths.HistoryFile);
            Assert.Empty(history.LoadAll());
            history.Append(new() { Id = "isolated-history", EndUtc = DateTime.UtcNow, Log = "isolated log" });
            Assert.Equal("isolated-history", Assert.Single(history.LoadAll()).Id);
            string log = ErrorLogService.Append("ignored-install-directory", "isolated diagnostic");
            Assert.True(MediaFluxStoragePathService.IsWithin(log, paths.Logs));
            Assert.Contains("isolated diagnostic", File.ReadAllText(log));
            var research = new PredictionShadowObservationJournal(AppPaths.PredictionShadowObservationsFile);
            Assert.Empty(research.ReadEvents());
            research.AppendFrozen(new() { ObservationId = "isolated-observation", SourceFamilyKey = "isolated-family", FrozenUtc = DateTime.UtcNow });
            Assert.True(File.Exists(AppPaths.PredictionShadowObservationsFile));
            var freezes = new PredictionShadowExperimentFreezeStore();
            Assert.True(MediaFluxStoragePathService.IsWithin(freezes.GetPath("fixture"), paths.Data));
            Assert.Null(freezes.Load("fixture"));
            var tuning = new NcnnPerformanceTuningCacheService();
            var key = new NcnnTuningCacheKey("isolated-key");
            Assert.False(tuning.TryGet(key, out _));
            tuning.Store(key, NcnnRuntimeConfiguration.SafeDefault);
            Assert.True(tuning.TryGet(key, out _));
            Assert.True(File.Exists(AppPaths.NcnnPerformanceTuningCacheFile));
            Assert.Single(new PredictionShadowResearchHistoryReader(AppPaths.EncodingStatisticsFile).ReadFinalizedStatistics());
        }
        AssertNormalUnchanged(before);
    }

    [Theory]
    [InlineData(HeadlessSavedJobMode.Preflight, "accepted", 0, HeadlessSavedJobExitCode.Success)]
    [InlineData(HeadlessSavedJobMode.Run, "accepted", 1, HeadlessSavedJobExitCode.Success)]
    [InlineData(HeadlessSavedJobMode.Run, "rejected", 1, HeadlessSavedJobExitCode.StoragePolicyRejected)]
    [InlineData(HeadlessSavedJobMode.Run, "skipped", 0, HeadlessSavedJobExitCode.AdaptiveStorageSavingsSkipped)]
    public async Task BothModesUseTheSameIsolatedSnapshotAndSharedGuardrails(
        HeadlessSavedJobMode mode, string outcome, int fullEncodeCount, HeadlessSavedJobExitCode expected)
    {
        var before = NormalSnapshot();
        var command = Command(_isolated, mode);
        var paths = HeadlessSavedJobEnvironment.ResolvePaths(command, _normal);
        using (AppPaths.UseIsolatedHeadlessStorage(paths))
        {
            var environment = HeadlessSavedJobEnvironment.Load(paths);
            var pipeline = new FixturePipeline(environment, outcome);
            var report = await HeadlessSavedJobItemRunner.RunAsync(command, environment.Jobs, pipeline, _ => { }, CancellationToken.None);
            Assert.Equal(expected, report.ExitCode);
            var snapshot = pipeline.LastSnapshot!;
            Assert.True(snapshot.AdaptiveStorageSavingsEnabled);
            Assert.True(snapshot.StorageSavingsContract.Applies);
            Assert.Equal(EncodingQualityIntent.Automatic(QualityTarget.Balanced), snapshot.QualityIntent);
            Assert.False(snapshot.DeleteSourceAfterCompression);
            Assert.Equal(VideoEncoderIds.Nvenc, snapshot.Encoder.EncoderId);
            Assert.Equal(fullEncodeCount, pipeline.Executor.FullEncodeCount);
            Assert.Equal(mode == HeadlessSavedJobMode.Run ? 1 : 0, pipeline.Executor.Calls);
            var stats = pipeline.Statistics.GetAll();
            if (mode == HeadlessSavedJobMode.Preflight) Assert.Empty(stats);
            else
            {
                var record = Assert.Single(stats);
                Assert.NotNull(record.AdaptiveSelection);
                Assert.Equal(outcome switch
                {
                    "accepted" => EncodingStatisticsOutcome.Success,
                    "rejected" => EncodingStatisticsOutcome.StoragePolicyRejected,
                    _ => EncodingStatisticsOutcome.AdaptiveStorageSavingsSkipped
                }, record.Outcome);
            }
            Assert.False(File.Exists(AppPaths.PredictionShadowObservationsFile));
            Assert.Equal(1_000_000, new FileInfo(snapshot.SourceFilePath).Length);
        }
        AssertNormalUnchanged(before);
    }

    [Fact]
    public async Task MissingIsolatedJobDoesNotSearchOrModifyNormalSavedJobs()
    {
        new EncodeJobService(Path.Combine(_isolated, "data", "encode-jobs.json")).Save([]);
        var before = NormalSnapshot();
        var environment = LoadIsolated();
        var report = await HeadlessSavedJobItemRunner.RunAsync(Command(_isolated), environment.Jobs,
            new FixturePipeline(environment, "accepted"), _ => { }, CancellationToken.None);
        Assert.Equal(HeadlessSavedJobExitCode.CommandOrSelectorError, report.ExitCode);
        Assert.Null(report.Snapshot);
        AssertNormalUnchanged(before);
    }

    [Fact]
    public async Task AdaptiveAndResearchSampleTemporaryDirectoriesAreIsolatedAndCleaned()
    {
        var paths = LoadIsolated().Paths;
        using var scope = AppPaths.UseIsolatedHeadlessStorage(paths);
        string? adaptiveOutput = null;
        var adaptive = new AdaptiveVideoSampleRunner("unused", "unused", new NeverProbe(), (_, _, _, output) => output,
            runOverride: (output, _, _) =>
            {
                adaptiveOutput = output;
                throw new IOException("injected failure before any media process");
            });
        var request = AdaptiveStorageSavingsTests.Request();
        await Assert.ThrowsAsync<IOException>(() => adaptive.MeasureAsync(request, request.Envelope.PreferredQuality,
            StorageSavingsSampleSelector.Positions(request.SourceDuration)[0], CancellationToken.None));
        Assert.True(MediaFluxStoragePathService.IsWithin(adaptiveOutput!, Path.Combine(paths.Temp, "AdaptiveSamples")));
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(paths.Temp, "AdaptiveSamples")));

        string executable = Path.Combine(_isolated, "unused-ffmpeg.exe");
        File.WriteAllText(executable, "fixture; never executed");
        var runner = new FailingSampleProcessRunner();
        await new PredictionShadowComplexitySamplingService(executable, runner)
            .AnalyzeAsync(Path.Combine(_isolated, "source.fixture"), 100);
        Assert.NotEmpty(runner.Outputs);
        Assert.All(runner.Outputs, output => Assert.True(MediaFluxStoragePathService.IsWithin(output, Path.Combine(paths.Temp, "PredictionShadow"))));
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(paths.Temp, "PredictionShadow")));
    }

    [Fact]
    public void StartupDiagnosticsIdentifySelectedRootAndPersistenceLocations()
    {
        var paths = LoadIsolated().Paths;
        var lines = new List<string>();
        HeadlessSavedJobEnvironment.WriteDiagnostics(paths, lines.Add);
        Assert.Contains(lines, line => line.Contains(_isolated) && line.Contains("storage pointers ignored"));
        Assert.Contains(lines, line => line.Contains(paths.Config));
        Assert.Contains(lines, line => line.Contains(Path.Combine(paths.Data, "encode-jobs.json")));
        Assert.Contains(lines, line => line.Contains(Path.Combine(paths.Data, "encoding-statistics.jsonl")));
    }

    [Theory]
    [InlineData("--user-data", "relative")]
    [InlineData("--user-data", "")]
    [InlineData("--user-data=C:\\isolated", "C:\\isolated")]
    public async Task ExecutableRejectsMalformedIsolationBeforeGuiOrStateLoading(string option, string value)
    {
        var before = NormalSnapshot();
        var result = await InvokeExecutableAsync(["--run-saved-job-item", _jobId.ToString(), "--item", "path=C:\\missing.fixture", option, value]);
        Assert.Equal(2, result.ExitCode);
        Assert.Contains("Command rejected", result.Stderr);
        Assert.DoesNotContain("Headless UserData:", result.Stdout);
        AssertNormalUnchanged(before);
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> InvokeExecutableAsync(string[] args)
    {
        string executable = Path.Combine(Path.GetDirectoryName(typeof(Config).Assembly.Location)!, "MediaFlux.exe");
        Assert.True(File.Exists(executable), executable);
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (string argument in args) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Headless fixture process did not start.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await stdout, await stderr);
    }

    private HeadlessSavedJobEnvironment LoadIsolated() =>
        HeadlessSavedJobEnvironment.Load(HeadlessSavedJobEnvironment.ResolvePaths(Command(_isolated), _normal));

    private HeadlessSavedJobCommand Command(string? root, HeadlessSavedJobMode mode = HeadlessSavedJobMode.Run) =>
        new(mode, _jobId, "path=" + Path.Combine(root ?? _normal.Root, "source.fixture"), root);

    private void Prepare(string root, string suffix, bool savings)
    {
        Directory.CreateDirectory(root);
        new Config { OutputSuffix = suffix, StorageSavings = new() { Enabled = savings } }.Save(Path.Combine(root, "config.json"));
        string source = Path.Combine(root, "source.fixture");
        File.WriteAllBytes(source, new byte[1_000_000]);
        new EncodeJobService(Path.Combine(root, "data", "encode-jobs.json")).Save([new EncodeJob
        {
            Id = _jobId, Name = suffix, Files = [new() { SourcePath = source }],
            Settings = new()
            {
                OutputFolder = root, CompressionProfile = "Medium Quality (Default)", EncoderId = VideoEncoderIds.Nvenc,
                VideoCodec = nameof(VideoCodecFamily.Hevc), VideoFormat = "H.265 / HEVC (x265)", EncoderPreset = "p5",
                OutputContainer = nameof(OutputContainerSelection.Auto), QualityMode = "Automatic", QualityValue = 22,
                QualityTarget = nameof(QualityTarget.Balanced), AudioChannels = "Keep source layout", Resolution = "None",
                AutoTargetSize = true, DeleteSourceAfterCompression = false
            }
        }]);
    }

    private Dictionary<string, string> NormalSnapshot() => Directory.EnumerateFiles(_fixture, "*", SearchOption.AllDirectories)
        .Where(path => !MediaFluxStoragePathService.IsWithin(path, _isolated))
        .ToDictionary(path => path, path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));

    private void AssertNormalUnchanged(Dictionary<string, string> before) =>
        Assert.Equal(before.OrderBy(pair => pair.Key), NormalSnapshot().OrderBy(pair => pair.Key));

    private sealed class FixturePipeline : IHeadlessSavedJobItemPipeline
    {
        private readonly HeadlessSavedJobEnvironment _environment;
        private readonly EncodeExecutionOrchestrator _orchestrator;
        public FixtureExecutor Executor { get; }
        public EncodingStatisticsService Statistics { get; }
        public EncodeExecutionSnapshot? LastSnapshot { get; private set; }

        public FixturePipeline(HeadlessSavedJobEnvironment environment, string outcome)
        {
            _environment = environment;
            Executor = new(outcome);
            Statistics = new(Path.Combine(environment.Paths.Data, "encoding-statistics.jsonl"));
            _orchestrator = new(Executor, new NvencQualityModePredictionShadowService(
                new PredictionShadowObservationJournal(Path.Combine(environment.Paths.Data, "prediction-shadow-observations.jsonl")),
                new PredictionShadowComplexitySamplingService("unused", new FailingSampleProcessRunner())), Statistics,
                hardwareKey: () => "fixture", experimentFreezeStore: new(environment.Paths.Root));
        }

        public Task<EncodeExecutionSnapshot> BuildSnapshotAsync(EncodeJob job, EncodeJobFile item, CancellationToken token) =>
            Task.FromResult(LastSnapshot = new EncodeExecutionSnapshotBuilder().Build(new()
            {
                OperationId = Guid.NewGuid().ToString("N"), StatisticsStartUtc = DateTime.UtcNow, MediaFluxVersion = "fixture",
                StatisticsPath = Path.Combine(_environment.Paths.Data, "encoding-statistics.jsonl"), ContainerCompatibilityConfirmed = false
            }, HeadlessSavedJobItemPipeline.ResolveSettingsForItem(job.Settings, _environment.Config, item.SourcePath), new()
            {
                SourceFilePath = item.SourcePath, LogicalSourcePath = item.SourcePath,
                SourceSizeBytes = new FileInfo(item.SourcePath).Length, MediaDurationSeconds = 100
            }));

        public void ValidateForPreflight(EncodeExecutionSnapshot snapshot) => _orchestrator.ValidateForPreflight(_orchestrator.CreateAttempt(snapshot));

        public async Task<string> ExecuteOneAsync(EncodeExecutionSnapshot snapshot, CancellationToken token)
        {
            var attempt = _orchestrator.CreateAttempt(snapshot);
            try
            {
                var result = await _orchestrator.ExecuteAsync(attempt, new(), null, token);
                _orchestrator.RecordSuccessfulExecution(attempt, DateTime.UtcNow, result.EncodedOutput.FinalOutputSizeBytes, 1, "fixture", null, false, null);
                return "fixture success";
            }
            catch (Exception error)
            {
                _orchestrator.RecordFailedExecution(attempt, DateTime.UtcNow, false,
                    (error as EncodeFinalizationException)?.Result.FailureKind, "", 1, "fixture", null, retryQueued: false);
                throw;
            }
        }
    }

    private sealed class FixtureExecutor(string outcome) : IEncodeRequestExecutor
    {
        public int Calls { get; private set; }
        public int FullEncodeCount { get; private set; }
        public async Task<EncodingService.EncodeResult> EncodeWithResultAsync(EncodingRequest request)
        {
            Calls++;
            Assert.True(request.AdaptiveStorageSavingsEnabled);
            var context = AdaptiveStorageSavingsTests.Context() with { Input = request.Input };
            var plan = EncodingPlanService.Create(context);
            Assert.True(AdaptiveStorageSavingsPolicy.TryCreateRequest(true, false, request.StorageSavingsContract, context, plan, out var sampling, out _));
            var selection = await new StorageSavingsSampleSelector(new AdaptiveStorageSavingsTests.FakeSamples((_, _) => outcome == "skipped" ? 10_000 : 4_000))
                .SelectAsync(sampling!, null, request.CancellationToken);
            plan = EncodingPlanService.FreezeAdaptiveSelection(plan, selection);
            request.EncodingPlanSnapshotCallback?.Invoke(new(plan.PlanId, plan));
            request.AdaptiveSelectionCallback?.Invoke(selection);
            if (selection.Disposition == AdaptiveSelectionDisposition.Skipped)
                throw new AdaptiveStorageSavingsSkippedException(selection);
            FullEncodeCount++;
            long bytes = outcome == "rejected" ? request.StorageSavingsContract.MaximumAcceptedOutputBytes!.Value + 1 : 800_000;
            var actual = StorageSavingsContractService.Evaluate(request.StorageSavingsContract, bytes);
            if (actual.Acceptance == StorageSavingsAcceptance.Rejected)
                throw new EncodeFinalizationException(new() { FailureKind = EncodeFinalizationFailureKind.StoragePolicyRejected, StorageSavings = actual });
            return new(true, Path.Combine(request.OutputFolder!, "fixture-output.mp4"), finalizationSucceeded: true,
                finalOutputSizeBytes: bytes, storageSavingsContract: request.StorageSavingsContract, storageSavings: actual);
        }
    }

    private sealed class NeverProbe : IMediaProbeService
    {
        public Task<MediaProbeResult> ProbeAsync(string path, CancellationToken token = default) => throw new InvalidOperationException("No media probe expected.");
    }

    private sealed class FailingSampleProcessRunner : IMediaToolProcessRunner
    {
        public List<string> Outputs { get; } = [];
        public Task<MediaToolProcessResult> RunAsync(MediaToolProcessRequest request, CancellationToken token = default)
        {
            Outputs.Add(request.Arguments.Last());
            return Task.FromResult(new MediaToolProcessResult { ExitCode = 1, StandardError = "fixture; no process executed" });
        }
    }
}
