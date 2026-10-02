using System.Diagnostics;
using MediaFlux.Models;
using MediaFlux.Services;
using MediaFlux.Services.Encoders;
using Xunit;
using Xunit.Abstractions;

namespace MediaFlux.Tests;

public sealed class AdaptiveStorageSavingsRetryAcceptanceTests(ITestOutputHelper output)
{
    [LiveAcceptanceTheory]
    [InlineData(true)]
    [InlineData(false)]
    [Trait("Category", "LiveAcceptance")]
    public async Task PolicyCRetryUsesTwoFreshCandidatesAndTheRealApplicationFinalizer(bool retryAccepted)
    {
        ToolPaths? tools = GetLiveToolPaths();
        Assert.True(tools is not null,
            "Real-validator acceptance was not executed: set MEDIAFLUX_LIVE_FFMPEG_PATH and MEDIAFLUX_LIVE_FFPROBE_PATH to an existing real FFmpeg/FFprobe pair.");
        output.WriteLine($"FFmpeg: {tools!.FfmpegPath}");
        output.WriteLine($"FFprobe: {tools.FfprobePath}");

        string root = Path.Combine(Path.GetTempPath(), "MediaFlux-PolicyCRetryAcceptance", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string source = await CreateHighQualitySourceAsync(tools.FfmpegPath, root);
            long sourceBytes = new FileInfo(source).Length;
            VideoEncoderSelection encoder = EncoderRegistry.Default.Resolve(
                VideoEncoderIds.Libx265, VideoCodecFamily.Hevc).Selection;
            StorageSavingsContract permissiveContract = StorageSavingsContractService.ResolveRequirements(sourceBytes, 0);
            AdaptiveQualitySelectionEvidence? baselineSelection = null;
            var realPreflight = CreateRealFinalizer(tools);
            var baselineService = new EncodingService(
                Path.GetDirectoryName(tools.FfmpegPath)!, _ => { }, logCallback: null,
                tools.FfmpegPath, tools.FfprobePath, realPreflight,
                new FixedAdaptiveSamples((_, _, sample) => new(sample, 1, sample.Duration.TotalSeconds)));
            EncodingService.EncodeResult baseline = await baselineService.EncodeWithResultAsync(
                CreateRequest(source, root, encoder, permissiveContract,
                    adaptive: true, retryGate: false,
                    selectionCallback: evidence => baselineSelection = evidence));

            Assert.True(baseline.Success, baseline.ValidationSummary);
            Assert.True(baseline.FinalizationSucceeded);
            Assert.True(File.Exists(source));
            Assert.NotNull(baselineSelection?.SelectedQuality);
            Assert.True(baseline.FinalOutputSizeBytes is long baselineBytes && baselineBytes < sourceBytes);

            int initialQuality = baselineSelection!.SelectedQuality!.Value;
            Assert.True(initialQuality < baselineSelection.WorstAcceptableQuality,
                "The selected quality must have a lower-quality candidate inside the frozen envelope.");
            int retryQuality = initialQuality + 1;
            var retryMeasurementService = new EncodingService(
                Path.GetDirectoryName(tools.FfmpegPath)!, _ => { }, logCallback: null,
                tools.FfmpegPath, tools.FfprobePath, CreateRealFinalizer(tools));
            EncodingService.EncodeResult retryMeasurement = await retryMeasurementService.EncodeWithResultAsync(
                CreateRequest(source, root, encoder, StorageSavingsContract.Disabled,
                    adaptive: false, retryGate: false, quality: retryQuality, suffix: "_retry-measurement"));

            Assert.True(retryMeasurement.Success, retryMeasurement.ValidationSummary);
            Assert.True(retryMeasurement.FinalizationSucceeded);
            long initialBytes = baseline.FinalOutputSizeBytes ?? throw new InvalidOperationException("The initial measurement did not report output bytes.");
            long retryBytes = retryMeasurement.FinalOutputSizeBytes ?? throw new InvalidOperationException("The retry measurement did not report output bytes.");
            Assert.True(initialBytes > retryBytes,
                "The real-media fixture must give the one-step-lower-quality candidate a smaller output.");
            Assert.True(initialBytes - retryBytes >= 2,
                "The real-media fixture must leave a byte threshold strictly between the two candidate sizes.");
            long maximumAcceptedBytes = retryAccepted
                ? initialBytes - ((initialBytes - retryBytes) / 2)
                : retryBytes - 1;
            Assert.True(maximumAcceptedBytes > 0 && maximumAcceptedBytes < sourceBytes);
            StorageSavingsContract capturedContract = StorageSavingsContractService.ResolveRequirements(
                sourceBytes, minimumSavingsPercent: 0,
                minimumSavingsBytes: sourceBytes - maximumAcceptedBytes);

            var validatorProcesses = new RecordingMediaToolRunner();
            var recordingValidator = new RecordingValidationService(CreateRealValidator(tools, validatorProcesses), validatorProcesses);
            var recordingFinalizer = new RecordingFinalizationService(new EncodeOutputFinalizationService(recordingValidator));
            var processAttempts = new List<int>();
            var stagingPaths = new List<string>();
            var outcomes = new List<EncodingExecutionOutcome>();
            AdaptiveQualitySelectionEvidence? retrySelection = null;
            var retryService = new EncodingService(
                Path.GetDirectoryName(tools.FfmpegPath)!, _ => { }, logCallback: null,
                tools.FfmpegPath, tools.FfprobePath, recordingFinalizer,
                new FixedAdaptiveSamples((request, _, sample) =>
                {
                    double uncertainStreamBytes = request.Ancillary.StreamBytes - request.Ancillary.MinimumStreamBytes;
                    if (uncertainStreamBytes <= 2 || request.Contract.MaximumAcceptedOutputBytes is not long maximum ||
                        maximum <= request.Ancillary.MinimumStreamBytes + uncertainStreamBytes / 2)
                        throw new InvalidOperationException("The live fixture cannot produce borderline sample evidence for its captured contract.");
                    long videoBytes = (long)Math.Floor(maximum - request.Ancillary.MinimumStreamBytes - uncertainStreamBytes / 2);
                    return new(sample, Math.Max(1, videoBytes), sample.Duration.TotalSeconds);
                }),
                async (arguments, stagingPath, attempt, cancellationToken) =>
                {
                    processAttempts.Add(attempt);
                    stagingPaths.Add(stagingPath);
                    if (attempt == 2)
                    {
                        Assert.Single(stagingPaths.Take(1));
                        Assert.False(File.Exists(stagingPaths[0]), "Attempt 1 staging must be removed before attempt 2 launches.");
                    }
                    var result = await RunProcessWithArgumentStringAsync(
                        tools.FfmpegPath, arguments, cancellationToken);
                    Assert.Equal(0, result.ExitCode);
                    Assert.True(File.Exists(stagingPath));
                    return (result.ExitCode, result.StandardError);
                });

            EncodingRequest retryRequest = CreateRequest(source, root, encoder, capturedContract,
                    adaptive: true, retryGate: true,
                    selectionCallback: evidence => retrySelection = evidence,
                    outcomeCallback: outcomes.Add);
            if (retryAccepted)
            {
                EncodingService.EncodeResult retried = await retryService.EncodeWithResultAsync(retryRequest);
                Assert.True(retried.Success, retried.ValidationSummary);
                Assert.True(retried.FinalizationSucceeded);
                Assert.Contains("decode-integrity", retried.ValidationSummary, StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                EncodeFinalizationException rejected = await Assert.ThrowsAsync<EncodeFinalizationException>(
                    () => retryService.EncodeWithResultAsync(retryRequest));
                Assert.Equal(EncodeFinalizationFailureKind.StoragePolicyRejected, rejected.Result.FailureKind);
            }
            Assert.True(File.Exists(source), "The source remains because this request did not authorize source deletion.");
            Assert.Equal(new[] { 1, 2 }, processAttempts);
            Assert.Equal(2, recordingFinalizer.Requests.Count);
            Assert.Same(recordingFinalizer.Requests[0].StorageSavingsContract,
                recordingFinalizer.Requests[1].StorageSavingsContract);
            Assert.Same(capturedContract, recordingFinalizer.Requests[0].StorageSavingsContract);
            Assert.Equal(recordingFinalizer.Requests[0].FinalOutputPath, recordingFinalizer.Requests[1].FinalOutputPath);
            Assert.NotEqual(recordingFinalizer.Requests[0].OutputPath, recordingFinalizer.Requests[1].OutputPath);
            Assert.False(File.Exists(recordingFinalizer.Requests[0].OutputPath));
            Assert.Equal(!retryAccepted, File.Exists(recordingFinalizer.Requests[1].OutputPath));
            Assert.Equal(retryAccepted, File.Exists(recordingFinalizer.Requests[1].FinalOutputPath));
            Assert.NotNull(retrySelection);
            Assert.Equal(initialQuality, retrySelection!.SelectedQuality);
            Assert.Equal(AdaptiveSampleClassification.Borderline,
                retrySelection.Candidates.Single(candidate => candidate.Quality == initialQuality).Classification);

            EncodingExecutionOutcome outcome = outcomes.Last();
            AdaptiveStorageSavingsRetryTrace trace = Assert.IsType<AdaptiveStorageSavingsRetryTrace>(
                outcome.AdaptiveStorageSavingsRetry);
            Assert.Equal(retryAccepted ? EncodingTerminalResult.Completed : EncodingTerminalResult.StoragePolicyRejected, outcome.TerminalResult);
            Assert.Equal(2, outcome.ProductionEncodeCount);
            Assert.Equal(2, trace.Attempts.Count);
            Assert.Equal(AdaptiveStorageSavingsAttemptOutcome.StoragePolicyRejected, trace.Attempts[0].Outcome);
            Assert.Equal(retryAccepted ? AdaptiveStorageSavingsAttemptOutcome.Accepted : AdaptiveStorageSavingsAttemptOutcome.StoragePolicyRejected, trace.Attempts[1].Outcome);
            Assert.Equal(EncodingLifecycleStatus.Passed, trace.Attempts[1].TechnicalValidationStatus);
            Assert.Equal(AdaptiveStorageSavingsStageDisposition.Deleted, trace.Attempts[0].StageDisposition);
            Assert.Equal(retryAccepted ? AdaptiveStorageSavingsStageDisposition.Promoted : AdaptiveStorageSavingsStageDisposition.Retained, trace.Attempts[1].StageDisposition);
            Assert.Equal(retryQuality, trace.Attempts[1].EffectiveQuality);
            Assert.NotEqual(trace.Attempts[0].StageId, trace.Attempts[1].StageId);
            StorageSavingsEvaluation initialPhaseOne = Assert.IsType<StorageSavingsEvaluation>(trace.Attempts[0].PhaseOneResult);
            StorageSavingsEvaluation retryPhaseOne = Assert.IsType<StorageSavingsEvaluation>(trace.Attempts[1].PhaseOneResult);
            Assert.Equal(StorageSavingsAcceptance.Rejected, initialPhaseOne.Acceptance);
            Assert.Equal(retryAccepted ? StorageSavingsAcceptance.Accepted : StorageSavingsAcceptance.Rejected, retryPhaseOne.Acceptance);
            Assert.Same(initialPhaseOne.Contract, retryPhaseOne.Contract);
            Assert.Equal(2, recordingValidator.StagedResults.Count);
            Assert.All(recordingValidator.StagedResults, result => Assert.True(result.Success, result.ErrorMessage));
            Assert.Equal(retryAccepted ? 1 : 0, recordingValidator.PromotedResults.Count);
            Assert.All(recordingValidator.PromotedResults, result => Assert.True(result.Success, result.ErrorMessage));
            Assert.Empty(outcome.Recovery);
            output.WriteLine($"Production FFmpeg launches: {processAttempts.Count}; attempts: {string.Join(", ", processAttempts)}; recovery encodes: 0.");
            output.WriteLine($"Attempt 1: {trace.Attempts[0].Outcome}; Phase 1: {initialPhaseOne.Acceptance}; bytes: {initialPhaseOne.CandidateOutputBytes}; stage: {trace.Attempts[0].StageId}; disposition: {trace.Attempts[0].StageDisposition}.");
            output.WriteLine($"Attempt 2: {trace.Attempts[1].Outcome}; Phase 1: {retryPhaseOne.Acceptance}; bytes: {retryPhaseOne.CandidateOutputBytes}; stage: {trace.Attempts[1].StageId}; disposition: {trace.Attempts[1].StageDisposition}.");
            output.WriteLine($"Actual EncodeOutputValidationService: {recordingValidator.StagedResults.Count} staged and {recordingValidator.PromotedResults.Count} promoted validations; FFprobe launches: {validatorProcesses.Requests.Count(request => Path.GetFileName(request.FileName).Equals("ffprobe.exe", StringComparison.OrdinalIgnoreCase))}; FFmpeg validation launches: {validatorProcesses.Requests.Count(request => Path.GetFileName(request.FileName).Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase))}.");
            output.WriteLine($"Original captured contract reused by reference: true; fresh stage paths and identities: true; final logical result: {outcome.TerminalResult}; source retained: {File.Exists(source)}.");
        }
        finally
        {
            try
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
            catch
            {
                // Preserve artifacts if a live media process still owns a handle.
            }
        }
    }

    private static EncodingRequest CreateRequest(
        string source,
        string root,
        VideoEncoderSelection encoder,
        StorageSavingsContract contract,
        bool adaptive,
        bool retryGate,
        int? quality = null,
        string suffix = "",
        Action<AdaptiveQualitySelectionEvidence>? selectionCallback = null,
        Action<EncodingExecutionOutcome>? outcomeCallback = null) => new()
    {
        Input = EncodingInputSource.FromFile(source),
        OutputFolder = root,
        Suffix = suffix,
        Encoder = encoder,
        UseGpu = false,
        // Matroska AAC does not expose a stream bitrate. An explicit stereo
        // transcode gives the unchanged adaptive policy a resolved audio budget.
        AudioChannels = 2,
        StorageSavingsContract = contract,
        AdaptiveStorageSavingsEnabled = adaptive,
        ExperimentalPolicyCRetryEnabled = retryGate,
        AdaptiveSelectionCallback = selectionCallback,
        QualityIntent = adaptive ? EncodingQualityIntent.Automatic(QualityTarget.Balanced) : null,
        QualityValue = quality,
        EncoderPreset = "ultrafast",
        MapMode = EncodingService.StreamMapMode.KeepAll,
        CopySubtitles = false,
        CopyDataStreams = false,
        CopyAttachments = false,
        OutputContainer = OutputContainerSelection.Matroska,
        CompatibilityPolicy = ContainerCompatibilityPolicy.Intelligent,
        EncodingExecutionOutcomeCallback = outcomeCallback
    };

    private static IEncodeOutputFinalizationService CreateRealFinalizer(ToolPaths tools)
        => new EncodeOutputFinalizationService(CreateRealValidator(tools));

    private static EncodeOutputValidationService CreateRealValidator(ToolPaths tools, IMediaToolProcessRunner? runner = null)
    {
        runner ??= new MediaToolProcessRunner();
        var probe = new FfprobeService(tools.FfprobePath, runner);
        return new EncodeOutputValidationService(
            probe,
            new FfmpegDecodeIntegritySpotCheckService(tools.FfmpegPath, runner),
            outputTimingAnalyzer: (path, token) =>
                new SourceTimingAnalysisService(tools.FfprobePath, runner).AnalyzeAsync(path, token),
            fullVideoDecodeCoverageService: new FfmpegFullVideoDecodeCoverageService(tools.FfmpegPath, runner));
    }

    private static async Task<string> CreateHighQualitySourceAsync(string ffmpegPath, string root)
    {
        string source = Path.Combine(root, "policy-c-source.mkv");
        var start = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        foreach (string argument in new[]
        {
            "-hide_banner", "-loglevel", "error", "-y",
            "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=24",
            "-f", "lavfi", "-i", "sine=frequency=1000:sample_rate=48000",
            "-t", "5", "-map", "0:v:0", "-map", "1:a:0",
            "-c:v", "libx264", "-preset", "ultrafast", "-crf", "0",
            "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "96k", source
        }) start.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = start };
        process.Start();
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        string standardError = await stderr;
        _ = await stdout;
        Assert.True(process.ExitCode == 0, standardError);
        Assert.True(File.Exists(source));
        return source;
    }

    private static async Task<(int ExitCode, string StandardError)> RunProcessWithArgumentStringAsync(
        string executable, string arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo
        {
            FileName = executable,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        using var process = new Process { StartInfo = start };
        process.Start();
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(stdout, stderr);
            throw;
        }
        await stdout;
        return (process.ExitCode, await stderr);
    }

    private static ToolPaths? GetLiveToolPaths()
    {
        string? ffmpeg = Environment.GetEnvironmentVariable("MEDIAFLUX_LIVE_FFMPEG_PATH");
        if (string.IsNullOrWhiteSpace(ffmpeg) || !File.Exists(ffmpeg)) return null;
        string? ffprobe = Environment.GetEnvironmentVariable("MEDIAFLUX_LIVE_FFPROBE_PATH");
        if (string.IsNullOrWhiteSpace(ffprobe))
            ffprobe = Path.Combine(Path.GetDirectoryName(ffmpeg)!, "ffprobe.exe");
        return File.Exists(ffprobe) ? new ToolPaths(ffmpeg, ffprobe) : null;
    }

    public sealed class LiveAcceptanceTheoryAttribute : TheoryAttribute
    {
        public LiveAcceptanceTheoryAttribute()
        {
            // Discovery-time skipping is visible to ordinary CI. Required local
            // acceptance must run the body and fail if its real tools are absent.
            if (Environment.GetEnvironmentVariable("MEDIAFLUX_REQUIRE_POLICY_C_ACCEPTANCE") != "1" && GetLiveToolPaths() is null)
                Skip = "Environment-dependent real-validator acceptance requires an existing FFmpeg/FFprobe pair.";
        }
    }

    private sealed class FixedAdaptiveSamples(
        Func<AdaptiveQualitySelectionRequest, int, RepresentativeSample, RepresentativeSampleEvidence> measure)
        : IAdaptiveVideoSampleRunner
    {
        public Task<RepresentativeSampleEvidence> MeasureAsync(
            AdaptiveQualitySelectionRequest request,
            int quality,
            RepresentativeSample sample,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(measure(request, quality, sample));
        }
    }

    private sealed class RecordingFinalizationService(IEncodeOutputFinalizationService inner)
        : IEncodeOutputFinalizationService
    {
        public List<EncodeOutputValidationRequest> Requests { get; } = [];

        public async Task<EncodeFinalizationResult> FinalizeAsync(
            EncodeOutputValidationRequest request,
            Action<string>? statusCallback = null,
            CancellationToken cancellationToken = default)
        {
            if (Requests.Count == 1)
            {
                Assert.Same(Requests[0].StorageSavingsContract, request.StorageSavingsContract);
                Assert.False(File.Exists(request.FinalOutputPath), "Attempt 1 must not promote after its Phase 1 rejection.");
                Assert.False(File.Exists(Requests[0].OutputPath), "Attempt 1 staging must be removed before attempt 2 validation.");
            }
            Requests.Add(request);
            EncodeFinalizationResult result = await inner.FinalizeAsync(request, statusCallback, cancellationToken);
            if (Requests.Count == 1)
            {
                Assert.False(result.Success);
                Assert.Equal(EncodeFinalizationFailureKind.StoragePolicyRejected, result.FailureKind);
                Assert.True(result.StagedValidationResult?.Success == true);
                Assert.False(File.Exists(request.FinalOutputPath));
            }
            return result;
        }
    }

    private sealed record ToolPaths(string FfmpegPath, string FfprobePath);

    private sealed class RecordingMediaToolRunner : IMediaToolProcessRunner
    {
        private readonly MediaToolProcessRunner _inner = new();
        public List<MediaToolProcessRequest> Requests { get; } = [];

        public async Task<MediaToolProcessResult> RunAsync(MediaToolProcessRequest request, CancellationToken cancellationToken = default)
        {
            MediaToolProcessResult result = await _inner.RunAsync(request, cancellationToken);
            Requests.Add(request);
            return result;
        }
    }

    private sealed class RecordingValidationService(EncodeOutputValidationService inner, RecordingMediaToolRunner processes)
        : IEncodeOutputValidationService
    {
        public List<EncodeOutputValidationResult> StagedResults { get; } = [];
        public List<EncodeOutputValidationResult> PromotedResults { get; } = [];

        public async Task<EncodeOutputValidationResult> ValidateStagedAsync(EncodeOutputValidationRequest request, CancellationToken cancellationToken = default)
        {
            int firstProcess = processes.Requests.Count;
            EncodeOutputValidationResult result = await inner.ValidateStagedAsync(request, cancellationToken);
            MediaToolProcessRequest[] launches = processes.Requests.Skip(firstProcess).ToArray();
            Assert.Contains(launches, launch => Path.GetFileName(launch.FileName).Equals("ffprobe.exe", StringComparison.OrdinalIgnoreCase) && launch.Arguments.Contains(request.OutputPath));
            Assert.Contains(launches, launch => Path.GetFileName(launch.FileName).Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase) && launch.Arguments.Contains(request.OutputPath));
            StagedResults.Add(result);
            return result;
        }

        public async Task<EncodeOutputValidationResult> ValidatePromotedAsync(EncodeOutputValidationRequest request, EncodeOutputValidationEvidence stagedEvidence, CancellationToken cancellationToken = default)
        {
            EncodeOutputValidationResult result = await inner.ValidatePromotedAsync(request, stagedEvidence, cancellationToken);
            PromotedResults.Add(result);
            return result;
        }
    }
}
