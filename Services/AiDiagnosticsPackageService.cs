using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace MediaFlux.Services;

/// <summary>Read-only diagnostics packaging over existing runtime, benchmark, log, and hardware data.</summary>
public sealed class AiDiagnosticsPackageService
{
    private readonly AiRuntimeTelemetryService _telemetry;
    private readonly AiBenchmarkManagementService _benchmarks;
    private readonly Func<HardwareSnapshot> _hardware;

    public AiDiagnosticsPackageService(AiRuntimeTelemetryService? telemetry = null, AiBenchmarkManagementService? benchmarks = null, Func<HardwareSnapshot>? hardware = null)
    { _telemetry = telemetry ?? AiRuntimeTelemetryService.Shared; _benchmarks = benchmarks ?? new AiBenchmarkManagementService(); _hardware = hardware ?? (() => HardwarePerformanceService.Capture("", "", "", "")); }

    public async Task<AiDiagnosticsPackageResult> CreateAsync(string destinationDirectory, IProgress<string>? progress = null, CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        Directory.CreateDirectory(destinationDirectory);
        DateTimeOffset now = DateTimeOffset.Now;
        string path = OutputPathService.GetCollisionSafePath(Path.Combine(destinationDirectory, $"MediaFlux-AI-Diagnostics-{now:yyyyMMdd-HHmmss}.zip"));
        var missing = new List<string>();
        progress?.Report("Capturing AI runtime telemetry…");
        AiRuntimeTelemetrySnapshot runtime = _telemetry.GetSnapshot();
        AiHealthEvaluation health = new AiHealthService(_telemetry).Evaluate();
        HardwareSnapshot hardware = _hardware();
        IReadOnlyList<AiBenchmarkRecord> benchmarks = _benchmarks.List();
        try
        {
            await using FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false);
            await WriteJsonAsync(zip, "Runtime/runtime-telemetry.json", runtime, token);
            await WriteJsonAsync(zip, "Runtime/backend-selection.json", AiBackendSelectionDiagnostics.Shared.GetLatest(), token);
            await WriteJsonAsync(zip, "Runtime/tensorrt-runtime.json", TensorRtRuntimeDiagnostics.Shared.GetLatest(), token);
            await WriteJsonAsync(zip, "Health/ai-health.json", health, token);
            await WriteJsonAsync(zip, "Hardware/hardware-snapshot.json", hardware, token);
            await WriteJsonAsync(zip, "System/system-information.json", new { MediaFluxVersion = Version(), OperatingSystem = Environment.OSVersion.VersionString, Is64BitOperatingSystem = Environment.Is64BitOperatingSystem, ProcessorCount = Environment.ProcessorCount, CreatedAt = now }, token);
            await WriteTextAsync(zip, "Benchmarks/benchmarks.mfai-benchmarks.json", _benchmarks.CreateExportJson(benchmarks), token);
            progress?.Report("Collecting configuration and diagnostics…");
            await CopyIfPresentAsync(zip, AppPaths.NcnnPerformanceTuningCacheFile, "Runtime/ncnn-performance-tuning.json", missing, token);
            await CopyIfPresentAsync(zip, AppPaths.ConfigFile, "Runtime/config.json", missing, token);
            await CopyDirectoryIfPresentAsync(zip, AppPaths.RestorationProfilesDirectory, "Runtime/restoration-profiles", missing, token);
            string logPath = ErrorLogService.GetDefaultLogPath(AppPaths.UserDataDirectory);
            if (File.Exists(logPath)) await WriteTextAsync(zip, "Logs/mediaflux-errors-tail.log", ErrorLogService.ReadTail(logPath, 512 * 1024, out _), token); else missing.Add("AI forensic diagnostics/error log");
            // The package has no current media source contract; FFprobe metadata is intentionally
            // omitted rather than guessing a source file. The summary makes this explicit.
            missing.Add("FFprobe media metadata (no source was selected)");
            await WriteTextAsync(zip, "Summary.md", Summary(runtime, health, hardware, benchmarks.Count, missing, now), token);
            progress?.Report("Finalizing diagnostics package…");
        }
        catch
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
            throw;
        }
        return new(path, missing);
    }

    private static async Task WriteTextAsync(ZipArchive zip, string name, string text, CancellationToken token)
    { ZipArchiveEntry entry = zip.CreateEntry(name, CompressionLevel.Optimal); await using StreamWriter writer = new(entry.Open(), new UTF8Encoding(false)); token.ThrowIfCancellationRequested(); await writer.WriteAsync(text); }
    private static async Task WriteJsonAsync<T>(ZipArchive zip, string name, T value, CancellationToken token) => await WriteTextAsync(zip, name, JsonSerializer.Serialize(value, JsonOptions), token);
    private static async Task CopyIfPresentAsync(ZipArchive zip, string source, string target, ICollection<string> missing, CancellationToken token)
    { if (!File.Exists(source)) { missing.Add(Path.GetFileName(target)); return; } ZipArchiveEntry entry = zip.CreateEntry(target, CompressionLevel.Optimal); await using Stream input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); await using Stream output = entry.Open(); await input.CopyToAsync(output, token); }
    private static async Task CopyDirectoryIfPresentAsync(ZipArchive zip, string directory, string target, ICollection<string> missing, CancellationToken token)
    { if (!Directory.Exists(directory)) { missing.Add(Path.GetFileName(target)); return; } foreach (string source in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)) { string relative = Path.GetRelativePath(directory, source); await CopyIfPresentAsync(zip, source, target + "/" + relative.Replace('\\', '/'), missing, token); } }
    private static string Summary(AiRuntimeTelemetrySnapshot runtime, AiHealthEvaluation health, HardwareSnapshot hardware, int benchmarkCount, IReadOnlyList<string> missing, DateTimeOffset created)
    {
        AiBackendSelectionDecisionSnapshot? selection = AiBackendSelectionDiagnostics.Shared.GetLatest();
        TensorRtRuntimeDiagnosticSnapshot? tensorRt = TensorRtRuntimeDiagnostics.Shared.GetLatest();
        string tensorRtSummary = tensorRt is null ? "Unavailable" : $"version {tensorRt.TensorRtVersion}; precision {tensorRt.Precision}; engine {tensorRt.CacheState}; validation {tensorRt.ValidationStatus}" + (tensorRt.FailureReason is null ? "" : $"; failure {tensorRt.FailureReason}");
        return $"# MediaFlux AI Diagnostics Package{Environment.NewLine}{Environment.NewLine}Created: {created:O}{Environment.NewLine}MediaFlux version: {Version()}{Environment.NewLine}{Environment.NewLine}## Runtime{Environment.NewLine}- Active backend: {runtime.Backend}{Environment.NewLine}- Model: {runtime.Model}{Environment.NewLine}- GPU: {runtime.GpuName}{Environment.NewLine}- Driver: {runtime.DriverVersion}{Environment.NewLine}- Benchmark source: {runtime.BenchmarkSource}{Environment.NewLine}- Runtime profile: {runtime.RuntimeProfile}{Environment.NewLine}- Recent AI session status: {runtime.Status}{Environment.NewLine}- Validation enabled: {runtime.ValidationEnabled}{Environment.NewLine}- Backend selection: {(selection is null ? "Unavailable" : $"requested {selection.Requested}; selected {selection.Selected}; {selection.Reason}")}{Environment.NewLine}- TensorRT: {tensorRtSummary}{Environment.NewLine}{Environment.NewLine}## AI Health{Environment.NewLine}- Overall: {health.Overall}{Environment.NewLine}- Backend availability: {health.BackendAvailability}{Environment.NewLine}- Validation: {health.ValidationStatus}{Environment.NewLine}- Benchmark: {health.BenchmarkStatus}{Environment.NewLine}- Driver/runtime: {health.DriverRuntimeCompatibility}{Environment.NewLine}- Diagnostics: {health.DiagnosticsAvailability}{Environment.NewLine}- Recommendations:{Environment.NewLine}{string.Join(Environment.NewLine, health.Recommendations.Select(item => "  - " + item))}{Environment.NewLine}{Environment.NewLine}## Hardware{Environment.NewLine}- CPU: {hardware.Cpu}{Environment.NewLine}- GPU: {hardware.Gpu}{Environment.NewLine}- Driver: {hardware.GpuDriver}{Environment.NewLine}{Environment.NewLine}## Included data{Environment.NewLine}- Benchmark results: {benchmarkCount}{Environment.NewLine}- FFprobe metadata: Unavailable (no source selected){Environment.NewLine}{Environment.NewLine}## Missing at package creation{Environment.NewLine}" + (missing.Count == 0 ? "- None detected before packaging." : string.Join(Environment.NewLine, missing.Select(item => "- " + item))) + Environment.NewLine;
    }
    private static string Version() => Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "Unavailable";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
}

public sealed record AiDiagnosticsPackageResult(string PackagePath, IReadOnlyList<string> MissingComponents);
