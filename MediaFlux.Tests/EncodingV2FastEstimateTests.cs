using System.Diagnostics;
using System.Text.Json;
using MediaFlux.Models;
using MediaFlux.Services;
using MediaFlux.Services.EncodingV2;
using Xunit;

namespace MediaFlux.Tests;

public sealed class EncodingV2FastEstimateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mediaflux-v2-fast-estimate-" + Guid.NewGuid().ToString("N"));

    public EncodingV2FastEstimateTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void SameInputIsDeterministicAndQualityIsMonotonic()
    {
        V2FastEstimateRequest request = Request(Facts());
        V2FastEstimateResult first = V2FastEstimateService.Estimate(request);
        V2FastEstimateResult repeated = V2FastEstimateService.Estimate(request);
        Assert.Equal(first.EstimatedVideoBytes, repeated.EstimatedVideoBytes);
        Assert.Equal(first.EstimatedAudioBytes, repeated.EstimatedAudioBytes);
        Assert.Equal(first.EstimatedTotalBytes, repeated.EstimatedTotalBytes);
        Assert.Equal(first.EstimateBasis, repeated.EstimateBasis);

        long q20 = V2FastEstimateService.Estimate(request with { QualityValue = 20 }).EstimatedVideoBytes;
        long q24 = V2FastEstimateService.Estimate(request with { QualityValue = 24 }).EstimatedVideoBytes;
        long q28 = V2FastEstimateService.Estimate(request with { QualityValue = 28 }).EstimatedVideoBytes;
        Assert.True(q20 > q24 && q24 > q28, $"Expected strict CRF monotonicity, got {q20}, {q24}, {q28}.");
    }

    [Fact]
    public void ReducedGeometryReducesVideoEstimate()
    {
        V2FastEstimateRequest full = Request(Facts());
        V2FastEstimateResult original = V2FastEstimateService.Estimate(full);
        V2FastEstimateResult reduced = V2FastEstimateService.Estimate(full with
        {
            GeometryMode = "fitBox",
            MaxWidth = 640,
            MaxHeight = 360
        });
        Assert.Equal((640, 360), (reduced.Width, reduced.Height));
        Assert.True(reduced.EstimatedVideoBytes < original.EstimatedVideoBytes);
        Assert.Equal(original.EstimatedAudioBytes, reduced.EstimatedAudioBytes);
    }

    [Fact]
    public void SourceRateNormalizationBoundsHighAndLowBitrateInputs()
    {
        V2FastEstimateSourceFacts highRateH264 = new()
        {
            SourcePath = Path.Combine(_root, "high-rate-h264.mp4"),
            SourceSizeBytes = 11_695_921,
            DurationSeconds = 3,
            VideoCodec = "h264",
            Width = 1920,
            Height = 1080,
            FrameRate = 30,
            VideoBitrateKbps = 31_000,
            AudioStreams = [new V2FastEstimateAudioStreamFacts { Codec = "aac", BitrateKbps = 192, DurationSeconds = 3 }]
        };
        V2FastEstimateResult highRate = V2FastEstimateService.Estimate(Request(highRateH264) with
        {
            Encoder = "libx265",
            Codec = "hevc",
            QualityMode = "crf",
            QualityValue = 27,
            Preset = "ultrafast",
            AudioMode = "copy"
        });
        Assert.True(highRate.EstimatedTotalBytes < highRateH264.SourceSizeBytes / 2,
            "A very high source bitrate must not be copied linearly into a lower-quality HEVC target estimate.");
        Assert.True(highRate.Confidence == V2EstimateConfidence.Low,
            "An input outside the static source-rate envelope must advertise low confidence.");

        V2FastEstimateSourceFacts lowRateH264 = new()
        {
            SourcePath = Path.Combine(_root, "low-rate-h264.mp4"),
            SourceSizeBytes = 81_013,
            DurationSeconds = 3,
            VideoCodec = "h264",
            Width = 640,
            Height = 360,
            FrameRate = 24,
            VideoBitrateKbps = 216,
            AudioStreams = Array.Empty<V2FastEstimateAudioStreamFacts>()
        };
        V2FastEstimateResult lowRate = V2FastEstimateService.Estimate(Request(lowRateH264) with
        {
            Encoder = "libx265",
            Codec = "hevc",
            QualityMode = "crf",
            QualityValue = 28,
            Preset = "ultrafast",
            AudioMode = "none"
        });
        Assert.Equal(V2SavingsClassificationKind.NoSavings, lowRate.SavingsClassification.Kind);
    }

    [Fact]
    public void EstimatesAllPhaseOneEncoderCodecPairs()
    {
        (string Encoder, string Codec, string Mode)[] pairs =
        [
            ("libx264", "h264", "crf"),
            ("h264_nvenc", "h264", "cq"),
            ("libx265", "hevc", "crf"),
            ("hevc_nvenc", "hevc", "cq"),
            ("libsvtav1", "av1", "crf"),
            ("av1_nvenc", "av1", "cq")
        ];
        foreach ((string encoder, string codec, string mode) in pairs)
        {
            V2FastEstimateResult result = V2FastEstimateService.Estimate(Request(Facts()) with
            {
                Encoder = encoder,
                Codec = codec,
                QualityMode = mode,
                QualityValue = 24,
                Preset = encoder.EndsWith("_nvenc", StringComparison.Ordinal) ? "p5" :
                    encoder == "libsvtav1" ? "6" : "medium"
            });
            Assert.True(result.EstimatedTotalBytes > 0, encoder);
        }
    }

    [Fact]
    public void AudioCopyAacAndNoneUseSelectedStreamFacts()
    {
        V2FastEstimateSourceFacts facts = WithAudio(Facts(),
            new V2FastEstimateAudioStreamFacts { Codec = "vorbis", SizeBytes = 40_000, DurationSeconds = 10 },
            new V2FastEstimateAudioStreamFacts { Codec = "opus", BitrateKbps = 64, DurationSeconds = 10 });
        V2FastEstimateRequest request = Request(facts);
        V2FastEstimateResult copy = V2FastEstimateService.Estimate(request);
        V2FastEstimateResult aac = V2FastEstimateService.Estimate(request with { AudioMode = "aac", AudioBitrateKbps = 128 });
        V2FastEstimateResult none = V2FastEstimateService.Estimate(request with { AudioMode = "none" });

        Assert.Equal(120_000, copy.EstimatedAudioBytes);
        Assert.Equal(3_200_000, aac.EstimatedAudioBytes);
        Assert.Equal(0, none.EstimatedAudioBytes);
        Assert.True(copy.EstimatedAudioBytes > none.EstimatedAudioBytes);
        Assert.Equal(copy.EstimatedVideoBytes, none.EstimatedVideoBytes);
    }

    [Fact]
    public void OutputAudioPolicyDoesNotChangeDerivedSourceVideoRate()
    {
        V2FastEstimateSourceFacts facts = Facts() with { VideoBitrateKbps = null };
        V2FastEstimateRequest request = Request(facts);
        V2FastEstimateResult copy = V2FastEstimateService.Estimate(request);
        V2FastEstimateResult aac = V2FastEstimateService.Estimate(request with
        {
            AudioMode = "aac",
            AudioBitrateKbps = 256
        });
        V2FastEstimateResult none = V2FastEstimateService.Estimate(request with { AudioMode = "none" });
        Assert.Equal(copy.EstimatedVideoBytes, aac.EstimatedVideoBytes);
        Assert.Equal(copy.EstimatedVideoBytes, none.EstimatedVideoBytes);
        Assert.Equal(0, none.EstimatedAudioBytes);
    }

    [Fact]
    public void SavingsClassificationAndProcessAllSemanticsAreExplicit()
    {
        V2FastEstimateResult baseline = V2FastEstimateService.Estimate(Request(Facts()));
        V2FastEstimateResult noSavings = V2FastEstimateService.Estimate(Request(Facts(sourceBytes: baseline.EstimatedTotalBytes - 1)) with
        {
            ProcessAll = true
        });
        V2FastEstimateResult little = V2FastEstimateService.Estimate(Request(Facts(sourceBytes: (long)Math.Ceiling(baseline.EstimatedTotalBytes / 0.97d))));
        V2FastEstimateResult processAll = V2FastEstimateService.Estimate(Request(Facts(sourceBytes: (long)Math.Ceiling(baseline.EstimatedTotalBytes / 0.97d))) with
        {
            ProcessAll = true
        });
        V2FastEstimateResult meaningful = V2FastEstimateService.Estimate(Request(Facts(sourceBytes: (long)Math.Ceiling(baseline.EstimatedTotalBytes / 0.90d))));

        Assert.Equal(V2SavingsClassificationKind.NoSavings, noSavings.SavingsClassification.Kind);
        Assert.True(noSavings.SavingsClassification.RecommendedSkip);
        Assert.Equal(V2SavingsClassificationPolicy.ProvisionalDefaultThresholdPercent,
            noSavings.SavingsClassification.ThresholdPercent);
        Assert.Equal(V2SavingsClassificationKind.LittleSavings, little.SavingsClassification.Kind);
        Assert.True(little.SavingsClassification.RecommendedSkip);
        Assert.Equal(V2SavingsClassificationKind.LittleSavings, processAll.SavingsClassification.Kind);
        Assert.False(processAll.SavingsClassification.RecommendedSkip);
        Assert.Equal(V2SavingsClassificationKind.MeaningfulSavings, meaningful.SavingsClassification.Kind);
        Assert.False(meaningful.SavingsClassification.RecommendedSkip);
    }

    [Fact]
    public async Task QualitySetProbesOnceAndSuppliedFactsAvoidProbe()
    {
        var metadata = new CountingMetadata(Facts());
        V2FastEstimateCommandRequest request = Command(Facts()) with
        {
            SourcePath = Path.Combine(_root, "not-on-disk.mp4"),
            SourceFacts = null
        };
        (V2FastEstimateResolvedSource source, IReadOnlyList<V2FastEstimateResult> results) =
            await V2FastEstimateService.EstimateQualitySetAsync(request, [20, 24, 28], metadata);
        Assert.True(source.ProbeOccurred);
        Assert.Equal(1, metadata.Calls);
        Assert.Equal(3, results.Count);
        Assert.True(results[0].EstimatedVideoBytes > results[1].EstimatedVideoBytes);
        Assert.True(results[1].EstimatedVideoBytes > results[2].EstimatedVideoBytes);

        var suppliedMetadata = new CountingMetadata(Facts());
        (source, results) = await V2FastEstimateService.EstimateQualitySetAsync(
            Command(Facts()), [20, 24, 28], suppliedMetadata);
        Assert.False(source.ProbeOccurred);
        Assert.Equal(0, suppliedMetadata.Calls);
        Assert.Equal(3, results.Count);
    }

    [Fact]
    public void UnsupportedAndMalformedInputsAreRejectedClearly()
    {
        V2FastEstimateRequest request = Request(Facts());
        V2PlanningException unsupported = Assert.Throws<V2PlanningException>(() =>
            V2FastEstimateService.Estimate(request with { Encoder = "not-a-v2-encoder" }));
        Assert.Equal("UnsupportedEncoder", unsupported.Code);

        V2PlanningException badFacts = Assert.Throws<V2PlanningException>(() =>
            V2FastEstimateService.Estimate(request with { SourceFacts = Facts() with { DurationSeconds = double.NaN } }));
        Assert.Equal("InvalidSourceFacts", badFacts.Code);

        V2PlanningException badThreshold = Assert.Throws<V2PlanningException>(() =>
            V2FastEstimateService.Estimate(request with { LittleSavingsThresholdPercent = 101 }));
        Assert.Equal("InvalidSavingsThreshold", badThreshold.Code);
    }

    [Fact]
    public async Task HeadlessEstimateAndQualitySetAreJsonAndNeverInvokeProductionFfmpeg()
    {
        string requestPath = Path.Combine(_root, "estimate.json");
        V2FastEstimateCommandRequest command = Command(Facts() with { SourcePath = Path.Combine(_root, "source.mp4") });
        await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(command, JsonOptions));

        using var output = new StringWriter();
        using var error = new StringWriter();
        int exit = await EncodingV2HeadlessCommand.RunAsync(EncodingV2HeadlessOperation.Estimate,
            requestPath, output, error);
        Assert.Equal(0, exit);
        using JsonDocument document = JsonDocument.Parse(output.ToString());
        JsonElement root = document.RootElement;
        Assert.Equal("completed", root.GetProperty("status").GetString());
        Assert.Equal(0, root.GetProperty("ffmpegProductionInvocationCount").GetInt32());
        Assert.Equal(0, root.GetProperty("ffprobeInvocationCount").GetInt32());
        Assert.True(root.GetProperty("estimate").GetProperty("estimatedTotalBytes").GetInt64() > 0);

        command = Command(Facts()) with { QualityValues = [20, 24, 28] };
        await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(command, JsonOptions));
        output.GetStringBuilder().Clear();
        exit = await EncodingV2HeadlessCommand.RunAsync(EncodingV2HeadlessOperation.EstimateQualitySet,
            requestPath, output, error);
        Assert.Equal(0, exit);
        using JsonDocument qualitySet = JsonDocument.Parse(output.ToString());
        Assert.Equal(3, qualitySet.RootElement.GetProperty("estimates").GetArrayLength());
        Assert.Equal(0, qualitySet.RootElement.GetProperty("ffmpegProductionInvocationCount").GetInt32());
        Assert.Equal(0, qualitySet.RootElement.GetProperty("ffprobeInvocationCount").GetInt32());
    }

    [Theory]
    [InlineData("--v2-estimate", EncodingV2HeadlessOperation.Estimate)]
    [InlineData("--v2-estimate-quality-set", EncodingV2HeadlessOperation.EstimateQualitySet)]
    [InlineData("--v2-benchmark-estimates", EncodingV2HeadlessOperation.BenchmarkEstimates)]
    public void HeadlessCommandsAreRegistered(string command, EncodingV2HeadlessOperation expected)
    {
        Assert.True(EncodingV2HeadlessCommand.IsRequested([command, "request.json"]));
        Assert.True(EncodingV2HeadlessCommand.TryParse([command, "request.json"],
            out EncodingV2HeadlessOperation operation, out string path, out string error), error);
        Assert.Equal(expected, operation);
        Assert.Equal("request.json", path);
    }

    [Fact]
    public async Task HeadlessBenchmarkReportsCorrectCountsAndPercentiles()
    {
        string requestPath = Path.Combine(_root, "benchmark.json");
        V2FastEstimateCommandRequest command = Command(Facts()) with
        {
            QualityValues = [20, 24, 28],
            Repetitions = 30
        };
        await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(command, JsonOptions));
        using var output = new StringWriter();
        int exit = await EncodingV2HeadlessCommand.RunAsync(EncodingV2HeadlessOperation.BenchmarkEstimates,
            requestPath, output, new StringWriter());
        Assert.Equal(0, exit);
        using JsonDocument document = JsonDocument.Parse(output.ToString());
        JsonElement root = document.RootElement;
        JsonElement benchmark = root.GetProperty("benchmark");
        Assert.Equal(30, benchmark.GetProperty("totalCalculations").GetInt32());
        Assert.Equal(0, benchmark.GetProperty("ffmpegProductionInvocationCount").GetInt32());
        Assert.Equal(29, benchmark.GetProperty("warmEstimateMilliseconds").GetArrayLength());
        double p50 = benchmark.GetProperty("warmP50Milliseconds").GetDouble();
        double p95 = benchmark.GetProperty("warmP95Milliseconds").GetDouble();
        double maximum = benchmark.GetProperty("warmMaximumMilliseconds").GetDouble();
        Assert.InRange(p50, 0, p95);
        Assert.InRange(p95, p50, maximum);
        Assert.Equal(0, benchmark.GetProperty("coldProbeMilliseconds").GetDouble());
        Assert.True(benchmark.GetProperty("sourceFactsWereSupplied").GetBoolean());
    }

    [Fact]
    public void EstimateAndOneHundredRowRefreshMeetResponsivenessSanity()
    {
        V2FastEstimateRequest request = Request(Facts());
        var elapsed = Stopwatch.StartNew();
        V2FastEstimateResult[] results = Enumerable.Range(0, 100)
            .Select(index => V2FastEstimateService.Estimate(request with { QualityValue = 20 + index % 10 }))
            .ToArray();
        elapsed.Stop();
        Assert.Equal(100, results.Length);
        Assert.All(results, result => Assert.InRange(result.CalculationMilliseconds, 0, 100));
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(1), $"100 calculations took {elapsed.Elapsed.TotalMilliseconds:0.##} ms.");
    }

    [Fact]
    public void EstimatorSourceHasNoV1OrResearchOrchestrationReferences()
    {
        string sourcePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "Services", "EncodingV2", "V2FastEstimate.cs"));
        string source = File.ReadAllText(sourcePath);
        string[] forbidden =
        [
            "EncodingService", "EstimateBackgroundService", "MainForm", "AdaptiveStorageSavings",
            "StorageSavingsSampleSelector", "SmartEncode", "PredictionShadow", "HistoricalCalibration",
            "EmpiricalPreview", "QueueEmpiricalPreviewProvider", "StorageSavingsSampleCampaign"
        ];
        foreach (string name in forbidden)
            Assert.DoesNotContain(name, source, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private V2FastEstimateSourceFacts Facts(long sourceBytes = 100_000_000) => new()
    {
        SourcePath = Path.Combine(_root, "source.mp4"),
        SourceSizeBytes = sourceBytes,
        DurationSeconds = 100,
        VideoCodec = "h264",
        Width = 1920,
        Height = 1080,
        FrameRate = 24,
        VideoBitrateKbps = 7000,
        AudioStreams = [new V2FastEstimateAudioStreamFacts { Codec = "aac", BitrateKbps = 128, DurationSeconds = 100 }]
    };

    private static V2FastEstimateRequest Request(V2FastEstimateSourceFacts facts) => new()
    {
        SourceFacts = facts,
        Encoder = "libx264",
        Codec = "h264",
        Container = "mp4",
        QualityMode = "crf",
        QualityValue = 24,
        Preset = "medium",
        GeometryMode = "source",
        AudioMode = "copy"
    };

    private V2FastEstimateCommandRequest Command(V2FastEstimateSourceFacts facts) => new()
    {
        SourcePath = facts.SourcePath,
        SourceFacts = facts,
        Encoder = "libx264",
        Codec = "h264",
        Container = "mp4",
        QualityMode = "crf",
        QualityValue = 24,
        Preset = "medium",
        GeometryMode = "source",
        AudioMode = "copy"
    };

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static V2FastEstimateSourceFacts WithAudio(V2FastEstimateSourceFacts facts,
        params V2FastEstimateAudioStreamFacts[] streams) => new()
    {
        SourcePath = facts.SourcePath,
        SourceSizeBytes = facts.SourceSizeBytes,
        DurationSeconds = facts.DurationSeconds,
        VideoCodec = facts.VideoCodec,
        Width = facts.Width,
        Height = facts.Height,
        FrameRate = facts.FrameRate,
        VideoBitrateKbps = facts.VideoBitrateKbps,
        AudioStreams = streams
    };

    private sealed class CountingMetadata(V2FastEstimateSourceFacts facts) : IV2SourceMetadataProvider
    {
        public int Calls { get; private set; }
        public Task<V2MediaFacts> ProbeAsync(string sourcePath, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            var video = new MediaProbeStreamInfo
            {
                Index = 0,
                CodecType = "video",
                CodecName = facts.VideoCodec,
                Width = facts.Width,
                Height = facts.Height,
                BitRate = facts.VideoBitrateKbps.HasValue ? (long)(facts.VideoBitrateKbps.Value * 1000) : null,
                AverageFrameRate = facts.FrameRate,
                DurationSeconds = facts.DurationSeconds
            };
            MediaProbeStreamInfo[] streams = [video, .. facts.AudioStreams.Select((audio, index) => new MediaProbeStreamInfo
            {
                Index = index + 1,
                CodecType = "audio",
                CodecName = audio.Codec,
                BitRate = audio.BitrateKbps.HasValue ? (long)(audio.BitrateKbps.Value * 1000) : null,
                DurationSeconds = audio.DurationSeconds
            })];
            var probe = new MediaProbeResult
            {
                Success = true,
                FormatName = "matroska",
                DurationSeconds = facts.DurationSeconds,
                SizeBytes = facts.SourceSizeBytes,
                Streams = streams
            };
            return Task.FromResult(new V2MediaFacts
            {
                SourcePath = sourcePath,
                Probe = probe,
                SourceLength = facts.SourceSizeBytes,
                SourceLastWriteUtcTicks = 1,
                PrimaryVideoCodec = facts.VideoCodec,
                SourceWidth = facts.Width,
                SourceHeight = facts.Height,
                AudioCodecs = facts.AudioStreams.Select(audio => audio.Codec).ToArray()
            });
        }
    }
}
