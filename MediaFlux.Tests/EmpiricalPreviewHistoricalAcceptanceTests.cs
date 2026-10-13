using System.Text.Json;
using MediaFlux.Models;
using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

/// <summary>Opt-in, two fixed historical sources; measures only each deterministic prepared plan.</summary>
public sealed class EmpiricalPreviewHistoricalAcceptanceTests
{
    private sealed class HistoricalPreviewFactAttribute : FactAttribute
    {
        public HistoricalPreviewFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("MEDIAFLUX_PREVIEW_ACCEPTANCE") != "1")
                Skip = "Bounded historical preview acceptance requires explicit invocation.";
        }
    }

    [HistoricalPreviewFact]
    public async Task OnlyPreviewTwoHistoricalSourcesWithoutProductionExecution()
    {
        string tool = Environment.GetEnvironmentVariable("MEDIAFLUX_PREVIEW_FFMPEG")!;
        string sourceA = Environment.GetEnvironmentVariable("MEDIAFLUX_PREVIEW_SOURCE_A")!;
        string sourceB = Environment.GetEnvironmentVariable("MEDIAFLUX_PREVIEW_SOURCE_B")!;
        string report = Environment.GetEnvironmentVariable("MEDIAFLUX_PREVIEW_REPORT")!;
        Assert.True(File.Exists(tool));
        Assert.True(File.Exists(sourceA));
        Assert.True(File.Exists(sourceB));
        string probePath = Path.Combine(Path.GetDirectoryName(tool)!, "ffprobe.exe");
        var probe = new FfprobeService(probePath, new MediaToolProcessRunner());
        // Only bounded sample-command methods are used. EncodeAsync and finalization are never invoked.
        var encoding = new EncodingService(Path.GetDirectoryName(tool)!, _ => { }, null, tool, probePath);
        var provider = new QueueEmpiricalPreviewProvider(encoding, probe, new EncodeExecutionSnapshotBuilder(), tool);
        var common = new EncodeExecutionSnapshotSettings
        {
            OutputFolder = "", CompressionProfile = "Medium Quality (Default)", EncoderId = VideoEncoderIds.Nvenc,
            CodecFamily = VideoCodecFamily.Hevc, VideoFormatText = "H.265 / HEVC (x265)", EncoderPreset = "p5",
            NumericQualityValue = 19, AutomaticQuality = false, QualityTarget = QualityTarget.HighQuality,
            TenBit = true, AudioChannelSelection = "Keep", LimitGpuEncodingQueueToOneJob = false,
            IncludeConcurrentEncoderSessions = true, AutoTargetSize = true, TargetSizeText = "",
            ScaleMode = EncodingService.ScaleMode.None, EnableOutputSuffix = false, EnableCodecSuffix = false,
            OutputSuffix = "", Restoration = new(), StorageSavings = new(), OutputContainer = OutputContainerSelection.Auto,
            CompatibilityPolicy = ContainerCompatibilityPolicy.Intelligent, DeleteSourceAfterCompression = false
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        QueueProductionPlanEstimate a = await provider.PrepareAndEstimateAsync(
            common, new() { SourceFilePath = sourceA, LogicalSourcePath = sourceA }, 0, timeout.Token);
        QueueProductionPlanEstimate b = await provider.PrepareAndEstimateAsync(
            common with { AutomaticQuality = true, StorageSavings = new() { Enabled = true } },
            new() { SourceFilePath = sourceB, LogicalSourcePath = sourceB }, 0, timeout.Token);
        var evidence = new
        {
            SourceA = a.PreparedPlan.Estimate,
            PlanA = a.PreparedPlan.Identity,
            SourceB = b.PreparedPlan.Estimate,
            PlanB = b.PreparedPlan.Identity,
            DiagnosticA = a.Diagnostic,
            DiagnosticB = b.Diagnostic
        };
        Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        await File.WriteAllTextAsync(report, JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
        Assert.Equal(a.PreparedPlan.Identity.PlanFingerprint, a.PreparedPlan.Estimate?.Plan.PlanFingerprint);
        Assert.Equal(b.PreparedPlan.Identity.PlanFingerprint, b.PreparedPlan.Estimate?.Plan.PlanFingerprint);
        Assert.Equal(1, a.PreparedPlan.Estimate?.SampleEvidence?.Select(sample => sample.Quality).Distinct().Count());
        Assert.Equal(1, b.PreparedPlan.Estimate?.SampleEvidence?.Select(sample => sample.Quality).Distinct().Count());
    }
}
