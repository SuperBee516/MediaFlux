using System.IO.Compression;
using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class AiDiagnosticsPackageServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MediaFluxAiDiagnosticsTests", Guid.NewGuid().ToString("N"));
    public AiDiagnosticsPackageServiceTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task CreatesReadableZipAndRecordsMissingOptionalComponents()
    {
        TensorRtRuntimeDiagnostics.Shared.Record(new("10.2", "13.0", "GPU", "FP16", "engine.plan", "Reused", "Shared frame validation passed.", null, DateTimeOffset.UtcNow));
        var telemetry = new AiRuntimeTelemetryService(new AiBenchmarkDatabase(Path.Combine(_root, "benchmarks.db")));
        var service = new AiDiagnosticsPackageService(telemetry, new AiBenchmarkManagementService(new AiBenchmarkDatabase(Path.Combine(_root, "benchmarks.db"))), () => new HardwareSnapshot("CPU", 8, "GPU", "Driver", null, null, "Unavailable", "Unavailable", "Unavailable", "Windows", "Unavailable"));
        var progress = new List<string>();

        AiDiagnosticsPackageResult result = await service.CreateAsync(_root, new CapturingProgress(progress));

        Assert.True(File.Exists(result.PackagePath));
        using var zip = ZipFile.OpenRead(result.PackagePath);
        Assert.NotNull(zip.GetEntry("Summary.md"));
        Assert.NotNull(zip.GetEntry("Runtime/runtime-telemetry.json"));
        Assert.NotNull(zip.GetEntry("Runtime/backend-selection.json"));
        Assert.NotNull(zip.GetEntry("Runtime/tensorrt-runtime.json"));
        Assert.NotNull(zip.GetEntry("Health/ai-health.json"));
        Assert.NotNull(zip.GetEntry("Hardware/hardware-snapshot.json"));
        Assert.NotNull(zip.GetEntry("Benchmarks/benchmarks.mfai-benchmarks.json"));
        using var reader = new StreamReader(zip.GetEntry("Summary.md")!.Open());
        string summary = await reader.ReadToEndAsync();
        Assert.Contains("FFprobe metadata: Unavailable", summary);
        Assert.Contains("## AI Health", summary);
        Assert.Contains(result.MissingComponents, value => value.Contains("FFprobe", StringComparison.Ordinal));
        Assert.NotEmpty(progress);
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }
    private sealed class CapturingProgress(List<string> values) : IProgress<string> { public void Report(string value) => values.Add(value); }
}
