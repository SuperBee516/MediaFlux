using MediaFlux.Models;
using MediaFlux.Services;
using MediaFlux.Services.Encoders;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace MediaFlux.Tests;

public sealed class HeadlessSavedJobItemCommandTests
{
    [Theory]
    [InlineData("--run-saved-job-item", HeadlessSavedJobMode.Run)]
    [InlineData("--preflight-saved-job-item", HeadlessSavedJobMode.Preflight)]
    public void ParserAcceptsExplicitModeJobAndOneSelector(string mode, HeadlessSavedJobMode expected)
    {
        bool parsed = HeadlessSavedJobCommand.TryParse(
            [mode, JobId.ToString(), "--item", "path=C:\\media\\clip.mp4"],
            out HeadlessSavedJobCommand? command,
            out string error);

        Assert.True(parsed, error);
        Assert.Equal(expected, command!.Mode);
        Assert.Equal(JobId, command.JobId);
        Assert.Equal("path=C:\\media\\clip.mp4", command.ItemSelector);
        Assert.Null(command.UserDataRoot);
    }

    [Theory]
    [InlineData("--run-saved-job-item", HeadlessSavedJobMode.Run)]
    [InlineData("--preflight-saved-job-item", HeadlessSavedJobMode.Preflight)]
    public void ParserAcceptsOneTrailingNormalizedUserDataRoot(string mode, HeadlessSavedJobMode expected)
    {
        string root = Path.Combine(Path.GetTempPath(), "MediaFlux-isolated", "child", "..", "UserData");
        Assert.True(HeadlessSavedJobCommand.TryParse(
            [mode, JobId.ToString(), "--item", "path=C:\\media\\clip.mp4", "--user-data", root],
            out var command, out string error), error);
        Assert.Equal(expected, command!.Mode);
        Assert.Equal(Path.GetFullPath(root), command.UserDataRoot);
        Assert.Equal(JobId, command.JobId);
        Assert.Equal("path=C:\\media\\clip.mp4", command.ItemSelector);
    }

    [Fact]
    public void ParserRejectsMalformedDuplicateOrUnsupportedIsolationOptions()
    {
        string[] prefix = ["--run-saved-job-item", JobId.ToString(), "--item", "path=C:\\media\\clip.mp4"];
        string[][] tails =
        [
            ["--user-data"], ["--user-data", ""], ["--user-data", "relative"],
            ["--user-data", "C:relative"], ["--user-data", "C:\\bad\0path"],
            ["--config", "C:\\config.json"], ["--user-data=C:\\isolated"],
            ["--user-data", "C:\\one", "--user-data", "C:\\two"],
            ["--user-data", "C:\\one", "extra"], ["--USER-DATA", "C:\\one"]
        ];
        foreach (string[] tail in tails)
        {
            Assert.False(HeadlessSavedJobCommand.TryParse([.. prefix, .. tail], out _, out string error));
            Assert.NotEmpty(error);
        }
    }

    [Theory]
    [InlineData("--user-data")]
    [InlineData("--user-data=C:\\isolated")]
    [InlineData("--USER-DATA")]
    public void IsolationCannotFallThroughToGuiDispatch(string option)
    {
        string[] args = ["--enqueue-file", "C:\\media\\clip.mp4", option, "C:\\isolated"];
        Assert.True(HeadlessSavedJobCommand.IsRequested(args));
        Assert.False(HeadlessSavedJobCommand.TryParse(args, out _, out _));
        Assert.False(HeadlessSavedJobCommand.IsRequested([]));
        Assert.False(HeadlessSavedJobCommand.IsRequested(["--enqueue-file", "C:\\media\\clip.mp4"]));
    }

    [Fact]
    public void ParserRejectsMissingOrMalformedExplicitSelectors()
    {
        string[][] invalid =
        [
            ["--run-saved-job-item"],
            ["--run-saved-job-item", "bad", "--item", "path=x"],
            ["--run-saved-job-item", "00000000-0000-0000-0000-000000000001", "--item"],
            ["--run-saved-job-item", "00000000-0000-0000-0000-000000000001", "--item", "path=x", "extra"]
        ];
        foreach (string[] args in invalid)
        {
            Assert.False(HeadlessSavedJobCommand.TryParse(args, out _, out string error));
            Assert.NotEmpty(error);
        }
    }

    [Fact]
    public void JobSelectionRequiresExactlyOneIdMatch()
    {
        HeadlessSavedJobCommand command = Command("path=C:\\media\\clip.mp4");
        Assert.Contains("No saved job", Assert.Throws<HeadlessSavedJobSelectionException>(
            () => HeadlessSavedJobSelector.Select(Array.Empty<EncodeJob>(), command)).Message);
        Assert.Contains("ambiguous", Assert.Throws<HeadlessSavedJobSelectionException>(
            () => HeadlessSavedJobSelector.Select([Job(), Job()], command)).Message);
    }

    [Fact]
    public void OrdinaryPathSelectionMustMatchExactlyOnePersistedItem()
    {
        var job = Job();
        Assert.Equal("C:\\media\\clip.mp4", HeadlessSavedJobSelector.Select([job], Command("path=c:\\MEDIA\\clip.mp4")).Item.SourcePath);
        job.Files.Add(new EncodeJobFile { SourcePath = "C:\\media\\clip.mp4" });
        Assert.Contains("matches 2", Assert.Throws<HeadlessSavedJobSelectionException>(
            () => HeadlessSavedJobSelector.Select([job], Command("path=C:\\media\\clip.mp4"))).Message);
    }

    [Fact]
    public void ExperimentSelectorMatchesExperimentAndSlotAndAttemptWhenProvided()
    {
        EncodeJob job = Job();
        job.Files[0].PredictionShadowExperimentAssignment = Binding("exp-a", 2, 1);
        job.Files.Add(new EncodeJobFile { SourcePath = "C:\\media\\other.mp4", PredictionShadowExperimentAssignment = Binding("exp-a", 2, 2) });

        Assert.Equal("C:\\media\\clip.mp4", HeadlessSavedJobSelector.Select([job], Command("experiment=exp-a,slot=2,attempt=1")).Item.SourcePath);
        Assert.Contains("matches 2", Assert.Throws<HeadlessSavedJobSelectionException>(
            () => HeadlessSavedJobSelector.Select([job], Command("experiment=exp-a,slot=2"))).Message);
        Assert.Contains("No item", Assert.Throws<HeadlessSavedJobSelectionException>(
            () => HeadlessSavedJobSelector.Select([job], Command("experiment=exp-a,slot=9"))).Message);
    }

    [Fact]
    public async Task DisabledManualJobCanRunOneExplicitItemWithoutBeingRewritten()
    {
        EncodeJob job = Job();
        job.Files.Add(new EncodeJobFile { SourcePath = "C:\\media\\second.mp4" });
        var pipeline = new FakePipeline();
        var output = new List<string>();

        HeadlessSavedJobReport result = await HeadlessSavedJobItemRunner.RunAsync(
            Command("path=C:\\media\\clip.mp4"), [job], pipeline, output.Add, CancellationToken.None);

        Assert.Equal(HeadlessSavedJobExitCode.Success, result.ExitCode);
        Assert.Equal("C:\\media\\clip.mp4", result.SourcePath);
        Assert.Equal(1, pipeline.BuildCount);
        Assert.Equal(1, pipeline.ExecuteCount);
        Assert.False(job.Enabled);
        Assert.Equal(EncodeJobScheduleType.Manual, job.ScheduleType);
        Assert.Null(job.LastRunUtc);
        Assert.Contains("[disabled, Manual]", output[0]);
    }

    [Fact]
    public async Task MissingOrAmbiguousItemsNeverReachThePipeline()
    {
        EncodeJob job = Job();
        var pipeline = new FakePipeline();
        HeadlessSavedJobReport missing = await HeadlessSavedJobItemRunner.RunAsync(
            Command("path=C:\\missing.mp4"), [job], pipeline, _ => { }, CancellationToken.None);
        Assert.Equal(HeadlessSavedJobExitCode.CommandOrSelectorError, missing.ExitCode);
        Assert.Equal(0, pipeline.BuildCount);

        job.Files.Add(new EncodeJobFile { SourcePath = job.Files[0].SourcePath });
        HeadlessSavedJobReport ambiguous = await HeadlessSavedJobItemRunner.RunAsync(
            Command("path=C:\\media\\clip.mp4"), [job], pipeline, _ => { }, CancellationToken.None);
        Assert.Equal(HeadlessSavedJobExitCode.CommandOrSelectorError, ambiguous.ExitCode);
        Assert.Equal(0, pipeline.BuildCount);
    }

    [Fact]
    public async Task PreflightBuildsAndValidatesWithoutExecutingOrRecording()
    {
        var pipeline = new FakePipeline();
        HeadlessSavedJobReport result = await HeadlessSavedJobItemRunner.RunAsync(
            Command("path=C:\\media\\clip.mp4", HeadlessSavedJobMode.Preflight), [Job()], pipeline, _ => { }, CancellationToken.None);

        Assert.Equal(HeadlessSavedJobExitCode.Success, result.ExitCode);
        Assert.Equal(1, pipeline.BuildCount);
        Assert.Equal(1, pipeline.PreflightCount);
        Assert.Equal(0, pipeline.ExecuteCount);
        Assert.Equal(0, pipeline.OutcomeCount);
        Assert.Equal(0, pipeline.StatisticsCount);
    }

    [Fact]
    public async Task TemporaryPersistedJobSourceAndJournalsRemainUnchangedByPreflight()
    {
        string root = Path.Combine(Path.GetTempPath(), "MediaFlux-headless-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string source = Path.Combine(root, "source.mp4");
            string jobsPath = Path.Combine(root, "encode-jobs.json");
            string userData = Path.Combine(root, "UserData");
            string data = Path.Combine(userData, "data");
            Directory.CreateDirectory(data);
            File.WriteAllText(source, "test-source-bytes");
            File.WriteAllText(Path.Combine(data, "prediction-shadow-observations.jsonl"), "frozen-fixture-row\n");
            File.WriteAllText(Path.Combine(data, "encoding-statistics.jsonl"), "statistics-fixture-row\n");
            var job = Job();
            job.Files[0].SourcePath = source;
            File.WriteAllText(jobsPath, JsonSerializer.Serialize(new[] { job }));
            byte[] jobsBefore = SHA256.HashData(File.ReadAllBytes(jobsPath));
            byte[] observationsBefore = SHA256.HashData(File.ReadAllBytes(Path.Combine(data, "prediction-shadow-observations.jsonl")));
            byte[] statisticsBefore = SHA256.HashData(File.ReadAllBytes(Path.Combine(data, "encoding-statistics.jsonl")));

            List<EncodeJob> loaded = new EncodeJobService(jobsPath).LoadStrict();
            var pipeline = new FakePipeline();
            HeadlessSavedJobReport result = await HeadlessSavedJobItemRunner.RunAsync(
                Command("path=" + source, HeadlessSavedJobMode.Preflight), loaded, pipeline, _ => { }, CancellationToken.None);

            Assert.Equal(HeadlessSavedJobExitCode.Success, result.ExitCode);
            Assert.Equal(jobsBefore, SHA256.HashData(File.ReadAllBytes(jobsPath)));
            Assert.Equal(observationsBefore, SHA256.HashData(File.ReadAllBytes(Path.Combine(data, "prediction-shadow-observations.jsonl"))));
            Assert.Equal(statisticsBefore, SHA256.HashData(File.ReadAllBytes(Path.Combine(data, "encoding-statistics.jsonl"))));
            Assert.False(loaded[0].Enabled);
            Assert.Equal(0, pipeline.OutcomeCount);
            Assert.Equal(0, pipeline.StatisticsCount);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task StaleAssignmentFailsClosedAndBindingIsPreservedInSnapshot()
    {
        EncodeJob job = Job();
        PredictionShadowExperimentAssignmentBinding stale = Binding("exp-stale", 3, 1) with { SourcePath = "C:\\old.mp4" };
        job.Files[0].PredictionShadowExperimentAssignment = stale;
        var pipeline = new FakePipeline { RejectAssigned = true };
        HeadlessSavedJobReport result = await HeadlessSavedJobItemRunner.RunAsync(
            Command("experiment=exp-stale,slot=3"), [job], pipeline, _ => { }, CancellationToken.None);

        Assert.Equal(HeadlessSavedJobExitCode.PreflightOrValidationRejected, result.ExitCode);
        Assert.Equal(stale, pipeline.LastSnapshot!.PredictionShadowExperimentAssignment);
        Assert.Equal(1, pipeline.ExecuteCount);
        Assert.Equal(0, pipeline.EncodeExecutorCount);
        Assert.Equal(0, pipeline.OutcomeCount);
        Assert.Equal(0, pipeline.StatisticsCount);
    }

    [Fact]
    public async Task ExecutionFailureDoesNotAdvanceToAnotherItem()
    {
        EncodeJob job = Job();
        job.Files.Add(new EncodeJobFile { SourcePath = "C:\\media\\second.mp4" });
        var pipeline = new FakePipeline { ExecuteException = new IOException("encode failed") };

        HeadlessSavedJobReport result = await HeadlessSavedJobItemRunner.RunAsync(
            Command("path=C:\\media\\clip.mp4"), [job], pipeline, _ => { }, CancellationToken.None);

        Assert.Equal(HeadlessSavedJobExitCode.EncodeFailure, result.ExitCode);
        Assert.Equal(4, (int)result.ExitCode);
        Assert.Equal(1, pipeline.ExecuteCount);
        Assert.Equal("C:\\media\\clip.mp4", pipeline.LastSnapshot!.SourceFilePath);
    }

    [Fact]
    public async Task StoragePolicyRejectionHasDedicatedHeadlessExitCode()
    {
        var pipeline = new FakePipeline
        {
            ExecuteException = new EncodeFinalizationException(new EncodeFinalizationResult
            {
                FailureKind = EncodeFinalizationFailureKind.StoragePolicyRejected,
                StorageSavings = StorageSavingsContractService.Evaluate(StorageSavingsContractService.Resolve(true, 1000), 901),
                ErrorMessage = "Insufficient savings."
            })
        };
        var report = await HeadlessSavedJobItemRunner.RunAsync(
            Command("path=C:\\media\\clip.mp4"), [Job()], pipeline, _ => { }, CancellationToken.None);
        Assert.Equal(HeadlessSavedJobExitCode.StoragePolicyRejected, report.ExitCode);
        Assert.Equal(1, pipeline.ExecuteCount);
        Assert.Equal(0, pipeline.OutcomeCount);
    }

    [Fact]
    public async Task CancellationAndValidationUseStableExitCodes()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelledPipeline = new FakePipeline { HonorCancellation = true };
        HeadlessSavedJobReport cancelled = await HeadlessSavedJobItemRunner.RunAsync(
            Command("path=C:\\media\\clip.mp4"), [Job()], cancelledPipeline, _ => { }, cancellation.Token);
        Assert.Equal(HeadlessSavedJobExitCode.Cancelled, cancelled.ExitCode);
        Assert.Equal(1, cancelledPipeline.BuildCount);
        Assert.Equal(0, cancelledPipeline.ExecuteCount);

        var invalidPipeline = new FakePipeline { BuildException = new HeadlessSavedJobValidationException("missing saved settings") };
        HeadlessSavedJobReport invalid = await HeadlessSavedJobItemRunner.RunAsync(
            Command("path=C:\\media\\clip.mp4"), [Job()], invalidPipeline, _ => { }, CancellationToken.None);
        Assert.Equal(HeadlessSavedJobExitCode.PreflightOrValidationRejected, invalid.ExitCode);
        Assert.Equal(0, invalidPipeline.ExecuteCount);
    }

    [Fact]
    public void IncompleteSavedJobSettingsAreRejectedWithoutGuiFallback()
    {
        Assert.Throws<InvalidDataException>(() => EncodeExecutionSnapshotSettings.FromSavedJob(new EncodeJobSettings(), new Config()));
    }

    [Fact]
    public void BlankOutputFolderResolvesPerItemAndUsesTheSharedSnapshotBuilder()
    {
        string root = Path.Combine(Path.GetTempPath(), "MediaFlux-headless-output-" + Guid.NewGuid().ToString("N"));
        string firstDirectory = Path.Combine(root, "first");
        string secondDirectory = Path.Combine(root, "second");
        Directory.CreateDirectory(firstDirectory);
        Directory.CreateDirectory(secondDirectory);
        try
        {
            string firstSource = Path.Combine(firstDirectory, "clip-one.mp4");
            string secondSource = Path.Combine(secondDirectory, "clip-two.mp4");
            File.WriteAllText(firstSource, "fixture");
            File.WriteAllText(secondSource, "fixture");
            EncodeJobSettings saved = CompleteSettings(outputFolder: "");

            EncodeExecutionSnapshotSettings firstSettings = HeadlessSavedJobItemPipeline.ResolveSettingsForItem(saved, new Config(), firstSource);
            EncodeExecutionSnapshotSettings secondSettings = HeadlessSavedJobItemPipeline.ResolveSettingsForItem(saved, new Config(), secondSource);
            Assert.Equal(firstDirectory, firstSettings.OutputFolder);
            Assert.Equal(secondDirectory, secondSettings.OutputFolder);
            Assert.NotEqual(firstSettings.OutputFolder, secondSettings.OutputFolder);
            Assert.Equal("", saved.OutputFolder);

            var builder = new EncodeExecutionSnapshotBuilder();
            EncodeExecutionSnapshot firstSnapshot = builder.Build(
                TestIdentity(), firstSettings, TestItem(firstSource));
            EncodeExecutionSnapshot secondSnapshot = builder.Build(
                TestIdentity(), secondSettings, TestItem(secondSource));

            Assert.Equal(firstDirectory, firstSnapshot.OutputFolder);
            Assert.Equal(secondDirectory, secondSnapshot.OutputFolder);
            Assert.Equal("clip-one", firstSnapshot.Input.OutputBaseName);
            Assert.Equal(OutputContainerSelection.Auto, firstSnapshot.OutputContainer);
            Assert.Equal(" [HEVC] [SourceRelative]", firstSnapshot.Suffix);

            // Snapshot construction leaves final naming and collision allocation to
            // the existing production output-path service.
            string requestedPath = Path.Combine(
                firstSnapshot.OutputFolder,
                firstSnapshot.Input.OutputBaseName + firstSnapshot.Suffix + ".mp4");
            File.WriteAllText(requestedPath, "existing-output-fixture");
            Assert.Equal(
                "clip-one [HEVC] [SourceRelative] (1).mp4",
                Path.GetFileName(OutputPathService.GetCollisionSafePath(requestedPath)));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ExplicitOutputFolderStillRequiresAnAbsoluteExistingDirectory()
    {
        string root = Path.Combine(Path.GetTempPath(), "MediaFlux-headless-explicit-output-" + Guid.NewGuid().ToString("N"));
        string sourceDirectory = Path.Combine(root, "source");
        string outputDirectory = Path.Combine(root, "output");
        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(outputDirectory);
        string source = Path.Combine(sourceDirectory, "clip.mp4");
        File.WriteAllText(source, "fixture");
        try
        {
            EncodeExecutionSnapshotSettings explicitSettings = HeadlessSavedJobItemPipeline.ResolveSettingsForItem(
                CompleteSettings(outputDirectory), new Config(), source);
            Assert.Equal(outputDirectory, explicitSettings.OutputFolder);

            HeadlessSavedJobValidationException relative = Assert.Throws<HeadlessSavedJobValidationException>(() =>
                HeadlessSavedJobItemPipeline.ResolveSettingsForItem(
                    CompleteSettings("relative-output"), new Config(), source));
            Assert.Contains("absolute path", relative.Message, StringComparison.OrdinalIgnoreCase);

            HeadlessSavedJobValidationException malformed = Assert.Throws<HeadlessSavedJobValidationException>(() =>
                HeadlessSavedJobItemPipeline.ResolveSettingsForItem(
                    CompleteSettings(Path.Combine(root, "bad<output>")), new Config(), source));
            Assert.Contains("malformed", malformed.Message, StringComparison.OrdinalIgnoreCase);

            HeadlessSavedJobValidationException missing = Assert.Throws<HeadlessSavedJobValidationException>(() =>
                HeadlessSavedJobItemPipeline.ResolveSettingsForItem(
                    CompleteSettings(Path.Combine(root, "missing")), new Config(), source));
            Assert.Contains("does not currently exist", missing.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void OutputFolderResolutionDoesNotRelaxOtherStrictSavedJobSettings()
    {
        string root = Path.Combine(Path.GetTempPath(), "MediaFlux-headless-strict-output-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string source = Path.Combine(root, "clip.mp4");
        File.WriteAllText(source, "fixture");
        try
        {
            EncodeJobSettings incomplete = CompleteSettings("");
            incomplete.AudioChannels = "";
            HeadlessSavedJobValidationException error = Assert.Throws<HeadlessSavedJobValidationException>(() =>
                HeadlessSavedJobItemPipeline.ResolveSettingsForItem(incomplete, new Config(), source));
            Assert.Contains("execution settings are incomplete", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    private static readonly Guid JobId = Guid.Parse("7ec5e11c-c122-4932-8b40-e620024ca8c9");

    [Fact]
    public async Task AdaptivePreEncodeSkipHasDistinctStableHeadlessExitAndNoSuccessfulOutput()
    {
        var evidence = await new StorageSavingsSampleSelector(new AdaptiveStorageSavingsTests.FakeSamples((_, _) => 10_000))
            .SelectAsync(AdaptiveStorageSavingsTests.Request(), null, CancellationToken.None);
        var pipeline = new FakePipeline { ExecuteException = new AdaptiveStorageSavingsSkippedException(evidence) };
        var report = await HeadlessSavedJobItemRunner.RunAsync(Command("path=C:\\media\\clip.mp4"), [Job()], pipeline, _ => { }, CancellationToken.None);
        Assert.Equal(7, (int)report.ExitCode);
        Assert.Equal(HeadlessSavedJobExitCode.AdaptiveStorageSavingsSkipped, report.ExitCode);
        Assert.Equal(1, pipeline.ExecuteCount);
        Assert.Equal(0, pipeline.EncodeExecutorCount);
        Assert.Equal(0, pipeline.StatisticsCount);
        Assert.Contains("acceptable quality", report.Message);
    }

    [Fact]
    public void SavedAndScheduledSettingsReachSharedAdaptiveSnapshotPolicy()
    {
        string root = Path.Combine(Path.GetTempPath(), "MediaFlux-headless-adaptive-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string source = Path.Combine(root, "clip.mp4");
        File.WriteAllText(source, "fixture");
        try
        {
            var saved = CompleteSettings(root);
            saved.QualityMode = "Automatic";
            var config = new Config { StorageSavings = new() { Enabled = true } };
            var fromSaved = EncodeExecutionSnapshotSettings.FromSavedJob(saved, config);
            var fromHeadless = HeadlessSavedJobItemPipeline.ResolveSettingsForItem(saved, config, source);
            var builder = new EncodeExecutionSnapshotBuilder();
            var scheduled = builder.Build(TestIdentity(), fromSaved, TestItem(source));
            var headless = builder.Build(TestIdentity(), fromHeadless, TestItem(source));
            Assert.True(scheduled.AdaptiveStorageSavingsEnabled);
            Assert.True(headless.AdaptiveStorageSavingsEnabled);
            Assert.Equal(scheduled.QualityIntent, headless.QualityIntent);
            Assert.Equal(scheduled.StorageSavingsContract, headless.StorageSavingsContract);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static HeadlessSavedJobCommand Command(string selector, HeadlessSavedJobMode mode = HeadlessSavedJobMode.Run) =>
        new(mode, JobId, selector);

    private static EncodeJob Job() => new()
    {
        Id = JobId,
        Name = "Fixture",
        Enabled = false,
        ScheduleType = EncodeJobScheduleType.Manual,
        Settings = CompleteSettings(""),
        Files = [new EncodeJobFile { SourcePath = "C:\\media\\clip.mp4" }]
    };

    private static EncodeJobSettings CompleteSettings(string outputFolder) => new()
    {
        OutputFolder = outputFolder,
        CompressionProfile = "Medium Quality (Default)",
        EncoderId = VideoEncoderIds.Nvenc,
        VideoCodec = nameof(VideoCodecFamily.Hevc),
        EncoderPreset = "p5",
        OutputContainer = nameof(OutputContainerSelection.Auto),
        QualityValue = 22,
        QualityMode = "Manual",
        QualityTarget = nameof(QualityTarget.Balanced),
        AudioChannels = "Keep source layout",
        VideoFormat = "H.265 / HEVC (x265)",
        AutoTargetSize = true,
        Resolution = "None",
        EnableOutputSuffix = true,
        EnableCodecSuffix = true,
        OutputSuffix = "SourceRelative"
    };

    private static EncodeExecutionSnapshotIdentity TestIdentity() => new()
    {
        OperationId = Guid.NewGuid().ToString("N"),
        StatisticsStartUtc = DateTime.UtcNow,
        MediaFluxVersion = "1.7.3",
        StatisticsPath = "C:\\UserData\\stats.jsonl",
        ContainerCompatibilityConfirmed = false
    };

    private static EncodeExecutionSnapshotItem TestItem(string source) => new()
    {
        SourceFilePath = source,
        LogicalSourcePath = source,
        MediaDurationSeconds = 10,
        SourceSizeBytes = 100,
        EstimatedTargetMb = 1
    };

    private static PredictionShadowExperimentAssignmentBinding Binding(string id, int slot, int attempt) => new(
        new PredictionShadowExperimentAssignment(id, slot, attempt, PredictionShadowExperimentStratum.Control, PredictionShadowExperimentRole.Target),
        "C:\\media\\clip.mp4", 100, 200);

    private sealed class FakePipeline : IHeadlessSavedJobItemPipeline
    {
        private readonly EncodeExecutionSnapshotBuilder _builder = new();
        public int BuildCount { get; private set; }
        public int PreflightCount { get; private set; }
        public int ExecuteCount { get; private set; }
        public int EncodeExecutorCount { get; private set; }
        public int OutcomeCount { get; private set; }
        public int StatisticsCount { get; private set; }
        public bool RejectAssigned { get; init; }
        public bool HonorCancellation { get; init; }
        public Exception? BuildException { get; init; }
        public Exception? ExecuteException { get; init; }
        public EncodeExecutionSnapshot? LastSnapshot { get; private set; }

        public Task<EncodeExecutionSnapshot> BuildSnapshotAsync(EncodeJob job, EncodeJobFile item, CancellationToken cancellationToken)
        {
            BuildCount++;
            if (BuildException is not null) throw BuildException;
            cancellationToken.ThrowIfCancellationRequested();
            EncodeExecutionSnapshotSettings settings = HeadlessSavedJobItemPipeline.ResolveSettingsForItem(
                job.Settings, new Config(), item.SourcePath);
            LastSnapshot = _builder.Build(
                new EncodeExecutionSnapshotIdentity
                {
                    OperationId = "test-op",
                    StatisticsStartUtc = DateTime.UtcNow,
                    MediaFluxVersion = "1.7.3",
                    StatisticsPath = "C:\\UserData\\stats.jsonl",
                    ContainerCompatibilityConfirmed = false
                },
                settings,
                new EncodeExecutionSnapshotItem
                {
                    SourceFilePath = item.SourcePath,
                    LogicalSourcePath = item.SourcePath,
                    MediaDurationSeconds = 10,
                    SourceSizeBytes = 100,
                    EstimatedTargetMb = 1,
                    PredictionShadowExperimentAssignment = item.PredictionShadowExperimentAssignment
                });
            return Task.FromResult(LastSnapshot);
        }

        public void ValidateForPreflight(EncodeExecutionSnapshot snapshot)
        {
            PreflightCount++;
            if (RejectAssigned && snapshot.PredictionShadowExperimentAssignment is { } binding)
                throw new EncodeExecutionAssignmentValidationException(binding.Assignment, "stale source binding");
        }

        public Task<string> ExecuteOneAsync(EncodeExecutionSnapshot snapshot, CancellationToken cancellationToken)
        {
            ExecuteCount++;
            if (HonorCancellation) cancellationToken.ThrowIfCancellationRequested();
            if (RejectAssigned && snapshot.PredictionShadowExperimentAssignment is { } binding)
                throw new EncodeExecutionAssignmentValidationException(binding.Assignment, "stale source binding");
            if (ExecuteException is not null) throw ExecuteException;
            EncodeExecutorCount++;
            StatisticsCount++;
            OutcomeCount++;
            return Task.FromResult("success");
        }
    }

}
