using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class HardwarePerformanceTests
{
    [Fact]
    public void HardwareDiscoveryReturnsACompactPerJobSnapshot()
    {
        HardwareSnapshot snapshot = HardwarePerformanceService.Capture(
            Path.GetTempPath(), Path.GetTempPath(), Path.GetTempPath(), "missing-ffmpeg.exe");

        Assert.True(snapshot.LogicalCores > 0);
        Assert.False(string.IsNullOrWhiteSpace(snapshot.Cpu));
        Assert.False(string.IsNullOrWhiteSpace(snapshot.WindowsVersion));
        Assert.Equal("Unavailable", snapshot.FfmpegVersion);
    }

    [Fact]
    public void HardwareSamplesProduceAverageAndPeakSummaryMetrics()
    {
        var timing = new PerformanceTimingService();
        timing.SetHardwareSnapshot(Snapshot());
        timing.RecordHardwareSample(new(20, 1024L * 1024 * 1024, 40, 10 * 1024 * 1024, 4 * 1024 * 1024));
        timing.RecordHardwareSample(new(60, 3L * 1024 * 1024 * 1024, 80, 30 * 1024 * 1024, 8 * 1024 * 1024));
        string summary = timing.BuildSummary();

        Assert.Contains("Hardware Summary", summary);
        Assert.Contains("GPU Utilization Avg/Peak: 40% / 60%", summary);
        Assert.Contains("GPU VRAM Avg/Peak: 2 GiB / 3 GiB", summary);
        Assert.Contains("CPU Utilization Avg/Peak: 60% / 80%", summary);
        Assert.Contains("Disk Read/Write Avg: 20 MiB/s / 6 MiB/s", summary);
    }

    [Fact]
    public void AiDisabledSummaryHasHardwareButNoSamplingSection()
    {
        var timing = new PerformanceTimingService();
        timing.SetHardwareSnapshot(Snapshot());
        string summary = timing.BuildSummary();

        Assert.Contains("Hardware Summary", summary);
        Assert.DoesNotContain("Samples:", summary);
        Assert.DoesNotContain("AI Backend:", summary);
    }

    [Fact]
    public void AiBackendAndCancellationRemainVisibleInSummary()
    {
        var timing = new PerformanceTimingService();
        timing.SetHardwareSnapshot(Snapshot());
        timing.SetAiBackend("ncnn-vulkan", "missing-ai.exe");
        timing.RecordHardwareSample(new(30, null, 50, null, null));
        using (timing.Measure(PerformanceTimingStage.AiProcessing)) { }
        string summary = timing.BuildSummary();

        Assert.Contains("AI Backend: ncnn-vulkan Unavailable", summary);
        Assert.Contains("AI Processing: Not Completed", summary);
        Assert.Contains("GPU Utilization Avg/Peak: 30% / 30%", summary);
    }

    private static HardwareSnapshot Snapshot() => new(
        "Test CPU", 8, "Test GPU", "1.0", 8L * 1024 * 1024 * 1024,
        32L * 1024 * 1024 * 1024, "C:\\", "D:\\", "E:\\", "Windows Test", "FFmpeg Test");
}
