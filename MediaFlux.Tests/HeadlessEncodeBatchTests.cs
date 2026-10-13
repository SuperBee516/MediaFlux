using MediaFlux.Models;
using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class HeadlessEncodeBatchTests
{
    [Fact]
    public void ParserKeepsRepeatedSourceOrderAndRejectsRelativePaths()
    {
        string first = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "first.mp4"));
        string second = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "second.mp4"));
        Assert.True(HeadlessEncodeBatchCommand.TryParse(
            ["--headless-encode", "--source", first, "--source", second, "--quality-mode", "Automatic"],
            out HeadlessEncodeBatchCommand? command, out string error), error);
        Assert.Equal(new[] { first, second }, command!.SourcePaths);
        Assert.Equal("Automatic", command.QualityModeOverride);
        Assert.False(command.DeleteSourceAfterCompression);
        Assert.False(command.RequireStorageSavingsContract);

        Assert.True(HeadlessEncodeBatchCommand.TryParse(
            ["--headless-encode", "--source", first, "--storage-savings"],
            out command, out error), error);
        Assert.True(command!.RequireStorageSavingsContract);
        Assert.False(HeadlessEncodeBatchCommand.TryParse(
            ["--headless-encode", "--source", first, "--storage-savings", "--storage-savings"], out _, out error));

        string isolatedUserData = Path.Combine(Path.GetTempPath(), "MediaFlux-Isolated-Campaign");
        Assert.True(HeadlessEncodeBatchCommand.TryParse(
            ["--headless-encode", "--source", first, "--user-data", isolatedUserData],
            out command, out error), error);
        Assert.Equal(Path.GetFullPath(isolatedUserData), command!.UserDataRoot);
        Assert.False(HeadlessEncodeBatchCommand.TryParse(
            ["--headless-encode", "--source", first, "--user-data", isolatedUserData,
                "--user-data", isolatedUserData], out _, out error));
        Assert.False(HeadlessEncodeBatchCommand.TryParse(
            ["--headless-encode", "--source", first, "--user-data", "relative-user-data"], out _, out error));

        Assert.False(HeadlessEncodeBatchCommand.TryParse(
            ["--headless-encode", "--source", "relative.mp4"], out _, out error));
        Assert.Contains("absolute", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BatchSettlesEachJobBeforeStartingTheNextAndContinuesAfterPerFileFailure()
    {
        string[] sources = ["source-1", "source-2", "source-3"];
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var processor = new DelegateProcessor(async (source, index, _) =>
        {
            if (index == 1)
            {
                firstStarted.SetResult();
                await releaseFirst.Task;
                return Result(index, source, HeadlessEncodeJobStatus.Failed);
            }
            if (index == 2)
            {
                secondStarted.SetResult();
                await releaseSecond.Task;
                return Result(index, source, HeadlessEncodeJobStatus.Success);
            }
            return Result(index, source, HeadlessEncodeJobStatus.RecoveredSuccess);
        });

        Task<HeadlessEncodeBatchResult> running = HeadlessEncodeBatchRunner.RunAsync(
            Command(sources), processor, CancellationToken.None);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { 1 }, processor.StartedIndexes);
        releaseFirst.SetResult();

        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { 1, 2 }, processor.StartedIndexes);
        releaseSecond.SetResult();
        HeadlessEncodeBatchResult result = await running;

        Assert.Equal(new[] { 1, 2, 3 }, processor.StartedIndexes);
        Assert.Equal(new[]
        {
            HeadlessEncodeJobStatus.Failed,
            HeadlessEncodeJobStatus.Success,
            HeadlessEncodeJobStatus.RecoveredSuccess
        }, result.Jobs.Select(job => job.Status));
        Assert.Equal("Failed", result.Status);
        Assert.Equal(4, result.ExitCode);
    }

    [Fact]
    public async Task RejectionsAreReportedAndFatalEnvironmentFailureStopsTheBatch()
    {
        var processor = new DelegateProcessor((source, index, _) =>
        {
            if (index == 1)
                return Task.FromResult(Result(index, source, HeadlessEncodeJobStatus.Rejected));
            throw new HeadlessEncodeFatalException("FFmpeg environment is unavailable.");
        });

        HeadlessEncodeBatchResult result = await HeadlessEncodeBatchRunner.RunAsync(
            Command(["first", "second", "third"]), processor, CancellationToken.None);

        Assert.Equal("FatalFailure", result.Status);
        Assert.Equal(8, result.ExitCode);
        Assert.Equal(2, processor.StartedIndexes.Count);
        Assert.Equal(HeadlessEncodeJobStatus.NotStarted, result.Jobs[2].Status);
    }

    [Fact]
    public void TransientSettingsUseNormalDefaultsAndRetainSourceByDefaultWithoutSavedJobMutation()
    {
        string root = Path.Combine(Path.GetTempPath(), "MediaFlux-transient-config-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string source = Path.Combine(root, "source.mp4");
            File.WriteAllBytes(source, [1, 2, 3, 4]);
            string jobsPath = Path.Combine(root, "data", "encode-jobs.json");
            Directory.CreateDirectory(Path.GetDirectoryName(jobsPath)!);
            File.WriteAllText(jobsPath, "saved-job-store-sentinel");

            Config config = new()
            {
                LastQualityMode = "Manual",
                LastQualityTarget = nameof(QualityTarget.Balanced),
                LastEncoderId = VideoEncoderIds.Nvenc,
                LastVideoCodec = nameof(VideoCodecFamily.Hevc)
            };
            EncodeJobSettings settings = HeadlessSavedJobItemPipeline.CreateTransientSettings(
                config, source, null, "Automatic", deleteSourceAfterCompression: false);

            Assert.Equal("Automatic", settings.QualityMode);
            Assert.Equal(nameof(QualityTarget.Balanced), settings.QualityTarget);
            Assert.False(settings.DeleteSourceAfterCompression);
            Assert.Equal(root, settings.OutputFolder);
            Assert.Equal("saved-job-store-sentinel", File.ReadAllText(jobsPath));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void TransientStorageContractOverrideDoesNotMutateConfigAndNeverEnablesPolicyCRetry()
    {
        StorageSavingsOptions configured = new()
        {
            Enabled = false,
            ExperimentalPolicyCRetryEnabled = true,
            TargetMode = StorageSavingsOptions.QualityTarget,
            QualityValue = 24
        };

        StorageSavingsOptions transient = HeadlessSavedJobItemPipeline.ResolveTransientStorageSavingsOptions(
            configured, requireStorageSavingsContract: true);

        Assert.True(transient.Enabled);
        Assert.False(transient.ExperimentalPolicyCRetryEnabled);
        Assert.Equal(StorageSavingsOptions.QualityTarget, transient.TargetMode);
        Assert.Equal(24, transient.QualityValue);
        Assert.False(configured.Enabled);
        Assert.True(configured.ExperimentalPolicyCRetryEnabled);

        StorageSavingsOptions defaultTransient = HeadlessSavedJobItemPipeline.ResolveTransientStorageSavingsOptions(
            configured, requireStorageSavingsContract: false);
        Assert.False(defaultTransient.Enabled);
        Assert.False(defaultTransient.ExperimentalPolicyCRetryEnabled);
    }

    private static HeadlessEncodeBatchCommand Command(IReadOnlyList<string> sources) =>
        new(sources, null, "Automatic", DeleteSourceAfterCompression: false);

    private static HeadlessEncodeJobResult Result(int index, string source, HeadlessEncodeJobStatus status) =>
        new(index, $"operation-{index}", source, status, DateTime.UtcNow, DateTime.UtcNow);

    private sealed class DelegateProcessor(
        Func<string, int, CancellationToken, Task<HeadlessEncodeJobResult>> execute) : IHeadlessEncodeBatchProcessor
    {
        public List<int> StartedIndexes { get; } = [];

        public Task<HeadlessEncodeJobResult> ExecuteOneAsync(
            string sourcePath,
            string? outputDirectory,
            string? qualityModeOverride,
            bool deleteSourceAfterCompression,
            bool requireStorageSavingsContract,
            int index,
            CancellationToken cancellationToken,
            Action<string>? progress = null)
        {
            StartedIndexes.Add(index);
            return execute(sourcePath, index, cancellationToken);
        }
    }
}
