using MediaFlux.Models;
using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class AiHealthServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MediaFluxAiHealthTests", Guid.NewGuid().ToString("N"));
    public AiHealthServiceTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    [Fact]
    public void HealthyRuntimeReportsNoActionNeeded()
    {
        AiRuntimeTelemetryService telemetry = CreateTelemetry(withBenchmark: true);
        AiHealthEvaluation health = new AiHealthService(telemetry, () => Selection(), () => null, () => true).Evaluate();

        Assert.Equal(AiHealthStatus.Healthy, health.Overall);
        Assert.Equal("Ready", health.BackendAvailability);
        Assert.Equal("Available", health.BenchmarkStatus);
        Assert.Equal(new[] { "No action needed." }, health.Recommendations);
    }

    [Fact]
    public void MissingBenchmarkProducesDeterministicRecommendation()
    {
        AiRuntimeTelemetryService telemetry = CreateTelemetry(withBenchmark: false);
        AiHealthEvaluation health = new AiHealthService(telemetry, () => Selection(), () => null, () => true).Evaluate();

        Assert.Equal(AiHealthStatus.Warning, health.Overall);
        Assert.Contains(health.Recommendations, value => value.StartsWith("Benchmark recommended", StringComparison.Ordinal));
    }

    [Fact]
    public void FallbackBackendIsReported()
    {
        AiRuntimeTelemetryService telemetry = CreateTelemetry(withBenchmark: true);
        AiBackendSelectionDecisionSnapshot fallback = Selection() with { FallbackReason = "TensorRT inference failed." };
        AiHealthEvaluation health = new AiHealthService(telemetry, () => fallback, () => null, () => true).Evaluate();

        Assert.Equal(AiHealthStatus.Warning, health.Overall);
        Assert.True(health.UsingFallbackBackend);
        Assert.Contains(health.Recommendations, value => value.StartsWith("Using fallback backend", StringComparison.Ordinal));
    }

    [Fact]
    public void DriverChangeIsDistinguishedFromAnOrdinaryMissingBenchmark()
    {
        AiRuntimeTelemetryService telemetry = CreateTelemetry(withBenchmark: true, benchmarkDriver: "Previous Driver");
        AiHealthEvaluation health = new AiHealthService(telemetry, () => Selection(), () => null, () => true).Evaluate();

        Assert.Equal("Changed", health.DriverRuntimeCompatibility);
        Assert.Contains(health.Recommendations, value => value.StartsWith("Driver changed", StringComparison.Ordinal));
    }

    [Fact]
    public void TensorRtEngineStateIsExposedWithoutDuplicateChecks()
    {
        AiRuntimeTelemetryService telemetry = CreateTelemetry(withBenchmark: true, tensorRt: true);
        TensorRtRuntimeDiagnosticSnapshot tensorRt = new("10.2", "13.0", "GPU", "FP16", "engine.plan", "Reused", "Shared frame validation passed.", null, DateTimeOffset.UtcNow);
        AiHealthEvaluation health = new AiHealthService(telemetry, () => Selection(AiBackendSelection.NvidiaTensorRt), () => tensorRt, () => true).Evaluate();

        Assert.Equal("Shared frame validation passed.", health.EngineStatus);
        Assert.Equal("Reused", health.EngineCacheStatus);
        Assert.Equal(AiHealthStatus.Healthy, health.Overall);
    }

    private AiRuntimeTelemetryService CreateTelemetry(bool withBenchmark, bool tensorRt = false, string benchmarkDriver = "Driver")
    {
        AiBenchmarkDatabase database = new(Path.Combine(_root, Guid.NewGuid() + ".db"));
        var telemetry = new AiRuntimeTelemetryService(database);
        string backend = tensorRt ? "nvidia-tensorrt" : "ncnn-vulkan";
        string identity = tensorRt ? "tensorrt-identity" : "ncnn-identity";
        string precision = tensorRt ? "FP16" : "FP32";
        var model = new AiRestorationModel("general", "General", AiRestorationMode.General, new[] { AiRestorationScale.X2 }, "models", "model.param", "model.bin", backend, "general-x2");
        AiBackendRuntimeDescriptor? descriptor = tensorRt ? new("10.2", precision, "Validated", "Reused", "Cached engine") : null;
        var session = new AiRestorationSession(new(true, backend, "backend.exe", identity, true, new[] { "GPU" }, new[] { model }, null), model, descriptor);
        var settings = new VideoRestorationSettings { AiMode = AiRestorationMode.General, AiScale = AiRestorationScale.X2 };
        HardwareSnapshot hardware = new("CPU", 8, "GPU", "Driver", null, null, "C:", "C:", "C:", "Windows", "FFmpeg");
        if (withBenchmark) database.Store(new(new(backend, identity, "general-x2", "GPU", benchmarkDriver, precision, 2, "1080p"), NcnnRuntimeConfiguration.SafeDefault, 30, null, true, DateTimeOffset.UtcNow, "validated"));
        telemetry.Begin(session, settings, 120, 1920, 1080, hardware);
        if (descriptor is not null) telemetry.SetRuntime(descriptor); else telemetry.SetRuntime(new NcnnRuntimeSelection(NcnnRuntimeConfiguration.SafeDefault, NcnnRuntimeConfigurationSource.Cached));
        return telemetry;
    }

    private static AiBackendSelectionDecisionSnapshot Selection(AiBackendSelection selected = AiBackendSelection.NcnnVulkan) => new(AiBackendSelection.Auto, selected, selected == AiBackendSelection.NvidiaTensorRt ? "nvidia-tensorrt" : "ncnn-vulkan", "Validated selection.", null, 30, DateTimeOffset.UtcNow);
}
