using MediaFlux.Models;
using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class AiRuntimeTelemetryServiceTests
{
    [Fact]
    public void IdleSnapshotUsesExplicitUnavailableValues()
    {
        var telemetry = new AiRuntimeTelemetryService(new AiBenchmarkDatabase(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db")));

        AiRuntimeTelemetrySnapshot snapshot = telemetry.GetSnapshot();

        Assert.False(snapshot.IsActive);
        Assert.Equal("Idle", snapshot.Status);
        Assert.Equal("Unavailable", snapshot.Backend);
        Assert.False(snapshot.BenchmarkAvailable);
    }

    [Fact]
    public void ActiveSessionProjectsExistingProgressRuntimeAndHardwareObservations()
    {
        string database = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db");
        var telemetry = new AiRuntimeTelemetryService(new AiBenchmarkDatabase(database));
        var session = new AiRestorationSession(
            new(true, "ncnn-vulkan", "ai.exe", "ncnn-1.0", true, new[] { "auto" }, Array.Empty<AiRestorationModel>(), null),
            new("general", "General", AiRestorationMode.General, new[] { AiRestorationScale.X4 }, "models", "a.param", "a.bin", "ncnn-vulkan", "realesrgan-x4plus"));
        var settings = new VideoRestorationSettings { AiMode = AiRestorationMode.General, AiScale = AiRestorationScale.X4 };
        var hardware = new HardwareSnapshot("CPU", 8, "GPU", "555.1", 8L * 1024 * 1024 * 1024, null, "C:", "C:", "C:", "Windows", "FFmpeg");

        telemetry.Begin(session, settings, 180, 1920, 1080, hardware);
        telemetry.SetRuntime(new NcnnRuntimeSelection(new NcnnRuntimeConfiguration(new NcnnThreadConfiguration(2, 2, 2), 256), NcnnRuntimeConfigurationSource.Cached));
        telemetry.ReportProgress(new AiIntermediateProgress(AiIntermediateStage.AiProcessing, 90, 180, "processing", 1, 1, 30, 25, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(4), "ncnn-vulkan"));
        telemetry.RecordHardwareSample(new HardwareUsageSample(80, 2L * 1024 * 1024 * 1024, 35, null, null));

        AiRuntimeTelemetrySnapshot snapshot = telemetry.GetSnapshot();
        Assert.True(snapshot.IsActive);
        Assert.Equal("ncnn-vulkan", snapshot.Backend);
        Assert.Equal("NCNN", snapshot.Provider);
        Assert.Equal(90, snapshot.FramesProcessed);
        Assert.Equal(25, snapshot.AverageFramesPerSecond);
        Assert.Equal("2:2:2", snapshot.Threads);
        Assert.Equal("256", snapshot.TileSize);
        Assert.Equal(80, snapshot.GpuUtilizationPercent);
        Assert.Equal(2L * 1024 * 1024 * 1024, snapshot.PeakVramBytes);

        telemetry.Complete();
        Assert.False(telemetry.GetSnapshot().IsActive);
        Assert.Equal("Idle", telemetry.GetSnapshot().Status);
        try { File.Delete(database); } catch { }
    }

    [Fact]
    public void TensorRtSessionPublishesEnginePrecisionAndCacheState()
    {
        var telemetry = new AiRuntimeTelemetryService(new AiBenchmarkDatabase(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db")));
        var model = new AiRestorationModel("general", "General", AiRestorationMode.General, new[] { AiRestorationScale.X2 }, "models", "model.onnx", "model.engine", "nvidia-tensorrt", "general");
        var runtime = new AiBackendRuntimeDescriptor("10.2", "FP16", "Validated", "Reused", "Cached engine");
        var session = new AiRestorationSession(new(true, "nvidia-tensorrt", "mediaflux-tensorrt.exe", "identity", true, new[] { "GPU" }, new[] { model }, null), model, runtime);

        telemetry.Begin(session, new VideoRestorationSettings { AiScale = AiRestorationScale.X2 }, 10, 1280, 720, new HardwareSnapshot("CPU", 8, "GPU", "Driver", null, null, "C:", "C:", "C:", "Windows", "FFmpeg"));
        telemetry.SetRuntime(runtime);

        AiRuntimeTelemetrySnapshot snapshot = telemetry.GetSnapshot();
        Assert.Equal("nvidia-tensorrt", snapshot.Backend);
        Assert.Equal("TensorRT", snapshot.RuntimeApi);
        Assert.Equal("10.2", snapshot.RuntimeVersion);
        Assert.Equal("FP16", snapshot.Precision);
        Assert.Equal("Validated", snapshot.EngineStatus);
        Assert.Equal("Reused", snapshot.EngineCacheState);
    }
}
