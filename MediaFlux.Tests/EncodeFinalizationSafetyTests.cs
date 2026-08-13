using MediaFlux.Models;
using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class EncodeFinalizationSafetyTests : IDisposable
{
    private readonly string _root;

    public EncodeFinalizationSafetyTests()
    {
        _root = Path.Combine(
            Path.GetTempPath(),
            "MediaFlux-EncodeFinalizationTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public async Task SuccessfulValidationPromotesWithoutOverwriteAndReverifies()
    {
        string source = CreateFile("source [safe].mkv", 4096);
        string final = Path.Combine(_root, "final unusual ' name.mp4");
        string stage = OutputPathService.CreateEncodeStagingPath(final);
        File.WriteAllBytes(stage, new byte[8192]);
        var validator = new FakeValidationService();
        var service = new EncodeOutputFinalizationService(validator);

        EncodeFinalizationResult result = await service.FinalizeAsync(
            Request(source, stage, final));

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(File.Exists(final));
        Assert.False(File.Exists(stage));
        Assert.Equal(1, validator.StagedCalls);
        Assert.Equal(1, validator.PromotedCalls);
        Assert.Equal(8192, result.FinalOutputSizeBytes);
    }

    [Fact]
    public async Task PromotionCollisionPreservesValidatedStage()
    {
        string source = CreateFile("source.mkv", 4096);
        string final = CreateFile("existing.mp4", 1024);
        string stage = OutputPathService.CreateEncodeStagingPath(final);
        File.WriteAllBytes(stage, new byte[8192]);
        var service = new EncodeOutputFinalizationService(
            new FakeValidationService());

        EncodeFinalizationResult result = await service.FinalizeAsync(
            Request(source, stage, final));

        Assert.False(result.Success);
        Assert.Equal(EncodeFinalizationFailureKind.Promotion, result.FailureKind);
        Assert.True(File.Exists(stage));
        Assert.Equal(1024, new FileInfo(final).Length);
        Assert.Equal(stage, result.RecoverableOutputPath);
    }

    [Fact]
    public async Task PromotionFailurePreservesStageAndNeverReportsSuccess()
    {
        string source = CreateFile("source.mkv", 4096);
        string final = Path.Combine(_root, "final.mp4");
        string stage = OutputPathService.CreateEncodeStagingPath(final);
        File.WriteAllBytes(stage, new byte[8192]);
        var service = new EncodeOutputFinalizationService(
            new FakeValidationService(),
            new ThrowingPromoter());

        EncodeFinalizationResult result = await service.FinalizeAsync(
            Request(source, stage, final));

        Assert.False(result.Success);
        Assert.Equal(EncodeFinalizationFailureKind.Promotion, result.FailureKind);
        Assert.True(File.Exists(stage));
        Assert.False(File.Exists(final));
    }

    [Fact]
    public async Task ValidationFailureLeavesFinalNameUnexposed()
    {
        string source = CreateFile("source.mkv", 4096);
        string final = Path.Combine(_root, "final.mp4");
        string stage = OutputPathService.CreateEncodeStagingPath(final);
        File.WriteAllBytes(stage, new byte[8192]);
        var service = new EncodeOutputFinalizationService(
            new FakeValidationService(stagedSuccess: false));

        EncodeFinalizationResult result = await service.FinalizeAsync(
            Request(source, stage, final));

        Assert.False(result.Success);
        Assert.Equal(EncodeFinalizationFailureKind.Validation, result.FailureKind);
        Assert.True(File.Exists(stage));
        Assert.False(File.Exists(final));
    }

    [Fact]
    public async Task FailedFinalVerificationMovesOutputBackToPartialName()
    {
        string source = CreateFile("source.mkv", 4096);
        string final = Path.Combine(_root, "final.mp4");
        string stage = OutputPathService.CreateEncodeStagingPath(final);
        File.WriteAllBytes(stage, new byte[8192]);
        var service = new EncodeOutputFinalizationService(
            new FakeValidationService(promotedSuccess: false));

        EncodeFinalizationResult result = await service.FinalizeAsync(
            Request(source, stage, final));

        Assert.False(result.Success);
        Assert.Equal(
            EncodeFinalizationFailureKind.FinalVerification,
            result.FailureKind);
        Assert.True(File.Exists(stage));
        Assert.False(File.Exists(final));
        Assert.Equal(stage, result.RecoverableOutputPath);
    }

    [Fact]
    public async Task CancellationDuringFinalVerificationRestoresPartialOutput()
    {
        string source = CreateFile("source.mkv", 4096);
        string final = Path.Combine(_root, "final.mp4");
        string stage = OutputPathService.CreateEncodeStagingPath(final);
        File.WriteAllBytes(stage, new byte[8192]);
        var service = new EncodeOutputFinalizationService(
            new FakeValidationService(cancelPromoted: true));

        EncodeFinalizationCanceledException exception =
            await Assert.ThrowsAsync<EncodeFinalizationCanceledException>(
                () => service.FinalizeAsync(Request(source, stage, final)));

        Assert.Equal(stage, exception.Result.RecoverableOutputPath);
        Assert.True(File.Exists(stage));
        Assert.False(File.Exists(final));
    }

    [Fact]
    public void ConcurrentStageAllocationUsesUniqueSameDirectoryPartialNames()
    {
        string final = Path.Combine(_root, "shared final.mp4");
        string[] paths = Enumerable.Range(0, 64)
            .AsParallel()
            .Select(_ => OutputPathService.CreateEncodeStagingPath(final))
            .ToArray();

        Assert.Equal(paths.Length, paths.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(paths, path =>
        {
            Assert.Equal(_root, Path.GetDirectoryName(path));
            Assert.EndsWith(".mp4.partial", path, StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith(".", Path.GetFileName(path), StringComparison.Ordinal);
        });
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("canceled")]
    public async Task EnabledIncompleteOutputCleanupDeletesOnlyPartialOutput(
        string outcome)
    {
        string source = CreateFile($"cleanup-source-{outcome}.mkv", 4096);
        string final = Path.Combine(_root, $"cleanup-final-{outcome}.mp4");
        string stage = OutputPathService.CreateEncodeStagingPath(final);
        File.WriteAllBytes(stage, new byte[8192]);

        string message =
            await IncompleteEncodeOutputCleanupService.CleanupAsync(
                source,
                stage,
                cleanupEnabled: true,
                outcome);

        Assert.True(File.Exists(source));
        Assert.False(File.Exists(stage));
        Assert.False(File.Exists(final));
        Assert.Contains("deleted", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DisabledCleanupConservativelyRetainsRecoverablePartial()
    {
        string source = CreateFile("cleanup-disabled-source.mkv", 4096);
        string final = Path.Combine(_root, "cleanup-disabled-final.mp4");
        string stage = OutputPathService.CreateEncodeStagingPath(final);
        File.WriteAllBytes(stage, new byte[8192]);

        string message =
            await IncompleteEncodeOutputCleanupService.CleanupAsync(
                source,
                stage,
                cleanupEnabled: false,
                outcome: "failed");

        Assert.True(File.Exists(source));
        Assert.True(File.Exists(stage));
        Assert.Contains("disabled", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CleanupNeverDeletesPathThatMatchesSource()
    {
        string source = CreateFile("cleanup-collision.mkv", 4096);

        string message =
            await IncompleteEncodeOutputCleanupService.CleanupAsync(
                source,
                source,
                cleanupEnabled: true,
                outcome: "failed");

        Assert.True(File.Exists(source));
        Assert.Contains("matched", message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(".mp4")]
    [InlineData(".mkv")]
    public void SourceDeletionOccursOnlyAfterVerifiedFinalization(string extension)
    {
        string source = CreateFile("delete me.mkv", 4096);
        string final = CreateFile("verified final" + extension, 8192);
        EncodingInputSource input = EncodingInputSource.FromFile(source);
        var result = new EncodingService.EncodeResult(
            success: true,
            outputPath: final,
            finalizationSucceeded: true,
            finalOutputSizeBytes: 8192);

        SourceDeletionResult deletion =
            SourceDeletionService.DeleteAfterFinalization(
                source,
                input,
                deleteRequested: true,
                result);

        Assert.True(deletion.Deleted, deletion.Message);
        Assert.False(File.Exists(source));
        Assert.True(File.Exists(final));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void SourceIsRetainedWithoutCompleteValidatedSuccess(
        bool encodeSuccess,
        bool finalizationSucceeded)
    {
        string source = CreateFile(
            $"retained-{encodeSuccess}-{finalizationSucceeded}.mkv",
            4096);
        string final = CreateFile(
            $"untrusted-{encodeSuccess}-{finalizationSucceeded}.mp4",
            8192);
        var result = new EncodingService.EncodeResult(
            encodeSuccess,
            final,
            finalizationSucceeded: finalizationSucceeded,
            finalOutputSizeBytes: 8192);

        SourceDeletionResult deletion =
            SourceDeletionService.DeleteAfterFinalization(
                source,
                EncodingInputSource.FromFile(source),
                deleteRequested: true,
                result);

        Assert.False(deletion.Deleted);
        Assert.True(File.Exists(source));
        Assert.Contains("retained", deletion.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SourceIsRetainedWhenVerifiedFinalFileChanges()
    {
        string source = CreateFile("source changed output.mkv", 4096);
        string final = CreateFile("changed output.mp4", 4096);
        var result = new EncodingService.EncodeResult(
            true,
            final,
            finalizationSucceeded: true,
            finalOutputSizeBytes: 8192);

        SourceDeletionResult deletion =
            SourceDeletionService.DeleteAfterFinalization(
                source,
                EncodingInputSource.FromFile(source),
                deleteRequested: true,
                result);

        Assert.False(deletion.Deleted);
        Assert.True(File.Exists(source));
        Assert.Contains("changed", deletion.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DvdSourceDeletionSafetyRemainsDisabled()
    {
        string source = CreateFile("VTS_01_1.VOB", 4096);
        string final = CreateFile("dvd final.mp4", 8192);
        var dvdInput = new EncodingInputSource
        {
            Kind = EncodingInputKind.DvdPhysicalConcat,
            InputPath = "concat:test",
            SourcePath = _root,
            SourceFiles = new[] { source },
            AllowSourceDeletion = false
        };
        var result = new EncodingService.EncodeResult(
            true,
            final,
            finalizationSucceeded: true,
            finalOutputSizeBytes: 8192);

        SourceDeletionResult deletion =
            SourceDeletionService.DeleteAfterFinalization(
                source,
                dvdInput,
                deleteRequested: true,
                result);

        Assert.False(deletion.Deleted);
        Assert.True(File.Exists(source));
        Assert.Contains("disables", deletion.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HistoryAndStatisticsPersistExplicitFinalizationOutcome()
    {
        string historyPath = Path.Combine(_root, "history.json");
        var history = new HistoryService(historyPath);
        history.Append(new JobHistoryRecord
        {
            Type = JobType.Encode,
            Status = JobStatus.Failed,
            StartUtc = DateTime.UtcNow.AddSeconds(-1),
            EndUtc = DateTime.UtcNow,
            SourcePath = "source.mkv",
            OutputPath = "final.mp4",
            StagingPath = ".final.mediaflux-id.mp4.partial",
            FinalizationOutcome = "Validation",
            SourceDeletionResult = "Original source retained."
        });

        JobHistoryRecord loaded = Assert.Single(history.LoadAll());
        Assert.Equal("Validation", loaded.FinalizationOutcome);
        Assert.EndsWith(".partial", loaded.StagingPath);
        Assert.Contains("retained", loaded.SourceDeletionResult!);

        EncodingStatisticsSnapshot statistics =
            EncodingStatisticsCalculator.Aggregate(
                new[]
                {
                    new EncodingStatisticsRecord
                    {
                        Outcome = EncodingStatisticsOutcome.ValidationFailed,
                        EndUtc = DateTime.UtcNow
                    },
                    new EncodingStatisticsRecord
                    {
                        Outcome = EncodingStatisticsOutcome.PromotionFailed,
                        EndUtc = DateTime.UtcNow
                    },
                    new EncodingStatisticsRecord
                    {
                        Outcome = EncodingStatisticsOutcome.Failed,
                        EndUtc = DateTime.UtcNow
                    }
                });
        Assert.Equal(2, statistics.FinalizationFailed);
        Assert.Equal(1, statistics.Failed);
        Assert.Equal(0, statistics.Successful);
    }

    private EncodeOutputValidationRequest Request(
        string source,
        string stage,
        string final) => new()
    {
        Input = EncodingInputSource.FromFile(source),
        OutputPath = stage,
        FinalOutputPath = final,
        Encoder = new VideoEncoderSelection(
            VideoEncoderIds.Libx265,
            VideoCodecFamily.Hevc,
            "libx265")
    };

    private string CreateFile(string name, int length)
    {
        string path = Path.Combine(_root, name);
        File.WriteAllBytes(path, new byte[length]);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private sealed class FakeValidationService :
        IEncodeOutputValidationService
    {
        private readonly bool _stagedSuccess;
        private readonly bool _promotedSuccess;
        private readonly bool _cancelPromoted;

        public FakeValidationService(
            bool stagedSuccess = true,
            bool promotedSuccess = true,
            bool cancelPromoted = false)
        {
            _stagedSuccess = stagedSuccess;
            _promotedSuccess = promotedSuccess;
            _cancelPromoted = cancelPromoted;
        }

        public int StagedCalls { get; private set; }
        public int PromotedCalls { get; private set; }

        public Task<EncodeOutputValidationResult> ValidateStagedAsync(
            EncodeOutputValidationRequest request,
            CancellationToken cancellationToken = default)
        {
            StagedCalls++;
            return Task.FromResult(Result(
                _stagedSuccess,
                request.OutputPath,
                _stagedSuccess ? "" : "simulated invalid output"));
        }

        public Task<EncodeOutputValidationResult> ValidatePromotedAsync(
            EncodeOutputValidationRequest request,
            EncodeOutputValidationEvidence stagedEvidence,
            CancellationToken cancellationToken = default)
        {
            PromotedCalls++;
            if (_cancelPromoted)
                throw new OperationCanceledException(cancellationToken);
            return Task.FromResult(Result(
                _promotedSuccess,
                request.FinalOutputPath,
                _promotedSuccess ? "" : "simulated final probe failure"));
        }

        private static EncodeOutputValidationResult Result(
            bool success,
            string path,
            string error)
        {
            long size = File.Exists(path) ? new FileInfo(path).Length : 0;
            MediaProbeResult probe = new()
            {
                Success = success,
                DurationSeconds = 10,
                Streams = new[]
                {
                    new MediaProbeStreamInfo
                    {
                        CodecType = "video",
                        CodecName = "hevc"
                    }
                }
            };
            return new EncodeOutputValidationResult
            {
                Success = success,
                ErrorMessage = error,
                Summary = success ? "simulated validation passed" : "",
                Evidence = success
                    ? new EncodeOutputValidationEvidence
                    {
                        SourceProbe = probe,
                        OutputProbe = probe,
                        OutputSizeBytes = size
                    }
                    : null
            };
        }
    }

    private sealed class ThrowingPromoter : IEncodeOutputPromoter
    {
        public void Promote(string stagingPath, string finalOutputPath) =>
            throw new IOException("simulated promotion failure");

        public string TryRestoreToStaging(
            string finalOutputPath,
            string stagingPath) => stagingPath;
    }
}
