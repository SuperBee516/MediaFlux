using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using MediaFlux.Models;

namespace MediaFlux.Services;

/// <summary>Metadata for a model understood by an AI restoration backend. UI code consumes this, never file names.</summary>
public sealed record AiRestorationModel(
    string Id,
    string DisplayName,
    AiRestorationMode Category,
    IReadOnlyList<AiRestorationScale> SupportedScales,
    string ModelsDirectory,
    string ParamPath,
    string BinPath,
    string BackendId,
    string ResolvedModelName = "")
{
    public string BackendModelName => string.IsNullOrWhiteSpace(ResolvedModelName) ? Id : ResolvedModelName;
}

public sealed record AiRestorationCapabilities(
    bool IsAvailable,
    string BackendId,
    string ExecutablePath,
    string Identity,
    bool VulkanAvailable,
    IReadOnlyList<string> Devices,
    IReadOnlyList<AiRestorationModel> Models,
    string? Error)
{
    public static AiRestorationCapabilities Unavailable(string executablePath, string error) =>
        new(false, "ncnn-vulkan", executablePath, "", false, Array.Empty<string>(), Array.Empty<AiRestorationModel>(), error);
}

/// <summary>Validated immutable backend state reused for every frame in one AI operation.</summary>
public sealed record AiBackendRuntimeDescriptor(string RuntimeVersion, string Precision, string EngineStatus, string CacheState, string BuildSource);
public sealed record AiRestorationSession(AiRestorationCapabilities Capabilities, AiRestorationModel Model, AiBackendRuntimeDescriptor? Runtime = null);

public sealed class AiRestorationValidationException : InvalidOperationException
{
    public AiRestorationValidationException(string message) : base(message) { }
}

/// <summary>
/// Discovers an optional local NCNN/Vulkan compatible executable and complete models.
/// It intentionally has no downloader and does not turn an unavailable optional component
/// into a failure for ordinary FFmpeg restoration.
/// </summary>
public sealed class AiRestorationBackendService : IAiRestorationBackend
{
    private static readonly ConcurrentDictionary<string, AiRestorationCapabilities> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, byte> ResolutionLog = new(StringComparer.OrdinalIgnoreCase);
    private readonly IMediaToolProcessRunner _runner;
    private readonly string _applicationDirectory;
    private readonly Action<string>? _log;
    private readonly AiModelManager _models;

    public AiRestorationBackendService(string applicationDirectory, IMediaToolProcessRunner? runner = null, Action<string>? log = null)
    {
        _applicationDirectory = string.IsNullOrWhiteSpace(applicationDirectory) ? AppDomain.CurrentDomain.BaseDirectory : applicationDirectory;
        _runner = runner ?? new MediaToolProcessRunner();
        _log = log;
        _models = new AiModelManager(log: log);
    }

    public string Id => "ncnn-vulkan";
    public string DisplayName => "NCNN Vulkan";
    public async Task<AiBackendMetadata> GetMetadataAsync(VideoRestorationSettings settings, CancellationToken cancellationToken = default)
    {
        AiRestorationCapabilities capabilities = await GetCapabilitiesAsync(settings, cancellationToken).ConfigureAwait(false);
        string version;
        try { version = File.Exists(capabilities.ExecutablePath) ? FileVersionInfo.GetVersionInfo(capabilities.ExecutablePath).FileVersion ?? "Unknown" : "Unavailable"; } catch { version = "Unknown"; }
        bool ready = capabilities.IsAvailable && capabilities.VulkanAvailable && capabilities.Models.Count > 0;
        string? reason = capabilities.Error ?? (!capabilities.VulkanAvailable ? "Vulkan device unavailable." : capabilities.Models.Count == 0 ? "No complete supported AI models were found." : null);
        return new(Id, DisplayName, version, capabilities.IsAvailable, ready, reason, true, true, true, true, capabilities.VulkanAvailable, new[] { $"Vulkan: {(capabilities.VulkanAvailable ? "Detected" : "Unavailable")}", $"Models: {capabilities.Models.Count}" });
    }

    public async Task<AiRestorationCapabilities> GetCapabilitiesAsync(VideoRestorationSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        string executable = ResolveExecutable(settings.AiBackendPath);
        string models = ResolveModelsDirectory(executable, settings.AiModelsDirectory);
        if (!File.Exists(executable))
            return AiRestorationCapabilities.Unavailable(executable, "AI backend unavailable: install or configure a local NCNN/Vulkan restoration executable.");

        string identity = BuildIdentity(executable, models);
        if (Cache.TryGetValue(identity, out AiRestorationCapabilities? cached))
        {
            _log?.Invoke($"[AI Restoration] capability cache hit; backend={executable}; identity={identity}.");
            return cached;
        }

        _log?.Invoke($"[AI Restoration] capability cache miss; backend={executable}; models={models}.");
        try
        {
            // NCNN tools commonly write help and version information to stderr. The shared
            // runner concurrently drains both streams, so this is safe for verbose builds.
            MediaToolProcessResult result = await _runner.RunAsync(new MediaToolProcessRequest
            {
                FileName = executable,
                Arguments = new[] { "-h" },
                Timeout = TimeSpan.FromSeconds(10)
            }, cancellationToken).ConfigureAwait(false);
            string output = result.StandardOutput + "\n" + result.StandardError;
            if (result.TimedOut || (result.ExitCode != 0 && string.IsNullOrWhiteSpace(output)))
                return AiRestorationCapabilities.Unavailable(executable, "AI backend unavailable: MediaFlux could not query the configured executable.");

            AiModelDiscoverySummary modelSummary = await _models.DiscoverNcnnAsync(models, cancellationToken).ConfigureAwait(false);
            IReadOnlyList<AiRestorationModel> discovered = modelSummary.Available.Select(model => model.ToNcnnRestorationModel()).ToArray();
            bool vulkan = output.Contains("vulkan", StringComparison.OrdinalIgnoreCase) ||
                          Path.GetFileName(executable).Contains("vulkan", StringComparison.OrdinalIgnoreCase);
            // Most NCNN command-line builds only enumerate devices during processing. Auto is
            // always a valid backend selection; explicit IDs are exposed only when reported.
            string[] devices = ParseDevices(output).Prepend("Auto").Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var capabilities = new AiRestorationCapabilities(true, "ncnn-vulkan", executable, identity, vulkan, devices, discovered,
                discovered.Count == 0 ? "No complete supported AI models were found next to the backend." : null);
            Cache[identity] = capabilities;
            _log?.Invoke($"[AI Restoration] backend=ncnn-vulkan; identity={identity}; vulkan={(vulkan ? "reported" : "not reported")}; devices={string.Join(", ", devices)}; models={discovered.Count}; modelDirectory={models}.");
            return capabilities;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log?.Invoke($"[AI Restoration] capability discovery failed: {ex.Message}");
            return AiRestorationCapabilities.Unavailable(executable, "AI backend unavailable: MediaFlux could not inspect the configured executable.");
        }
    }

    public async Task<AiRestorationModel> ValidateSelectionAsync(VideoRestorationSettings settings, CancellationToken cancellationToken = default)
    {
        if (settings.AiMode == AiRestorationMode.Off)
            throw new AiRestorationValidationException("AI restoration is not enabled.");
        AiRestorationCapabilities capabilities = await GetCapabilitiesAsync(settings, cancellationToken).ConfigureAwait(false);
        return ValidateSelection(settings, capabilities);
    }

    private AiRestorationModel ValidateSelection(VideoRestorationSettings settings, AiRestorationCapabilities capabilities)
    {
        if (!capabilities.IsAvailable)
            throw new AiRestorationValidationException(capabilities.Error ?? "AI backend unavailable.");
        if (!capabilities.VulkanAvailable)
            throw new AiRestorationValidationException("Vulkan device unavailable: the configured AI backend did not report Vulkan support.");
        if (!capabilities.Devices.Contains(settings.AiDevice, StringComparer.OrdinalIgnoreCase))
            throw new AiRestorationValidationException($"Vulkan device unavailable: '{settings.AiDevice}' is not available for the configured AI backend.");
        string logicalId = NormalizeLogicalModelId(settings.AiModelId);
        AiRestorationModel? model = capabilities.Models.FirstOrDefault(model => MatchesSettings(model, settings));
        if (model is null)
        {
            bool knownLogicalModel = capabilities.Models.Any(candidate => candidate.Id.Equals(logicalId, StringComparison.OrdinalIgnoreCase));
            throw new AiRestorationValidationException(string.IsNullOrWhiteSpace(settings.AiModelId)
                ? "AI model missing/incomplete: choose a detected model before encoding."
                : knownLogicalModel
                    ? $"AI model missing/incomplete: '{settings.AiModelId}' has no complete {(int)settings.AiScale}x model pair in '{capabilities.Models.First(candidate => candidate.Id.Equals(logicalId, StringComparison.OrdinalIgnoreCase)).ModelsDirectory}'."
                    : $"AI model missing/incomplete: '{settings.AiModelId}' was not found or is incomplete.");
        }
        if (model.Category != settings.AiMode)
            throw new AiRestorationValidationException($"AI model '{model.DisplayName}' is not compatible with {settings.AiMode} restoration.");
        string resolutionKey = $"{capabilities.Identity}|{logicalId}|{model.BackendModelName}|{settings.AiScale}";
        if (ResolutionLog.TryAdd(resolutionKey, 0)) _log?.Invoke($"[AI Restoration] model resolved; logical={logicalId}; resolved={model.BackendModelName}; scale={(int)settings.AiScale}x; models={model.ModelsDirectory}.");
        return model;
    }

    public static void InvalidateCache() { Cache.Clear(); ResolutionLog.Clear(); }

    /// <summary>Builds safe argument tokens for one deterministic frame operation.</summary>
    public IReadOnlyList<string> BuildFrameArguments(AiRestorationCapabilities capabilities, AiRestorationModel model, VideoRestorationSettings settings, string input, string output, NcnnRuntimeConfiguration? runtimeConfiguration = null)
    {
        if (!Path.IsPathFullyQualified(input) || !Path.IsPathFullyQualified(output))
            throw new AiRestorationValidationException("AI restoration requires absolute staging paths.");
        if (!File.Exists(input)) throw new FileNotFoundException("AI restoration input frame is missing.", input);
        var arguments = new List<string> { "-i", input, "-o", output, "-m", model.ModelsDirectory, "-n", model.BackendModelName, "-s", ((int)settings.AiScale).ToString(), "-g", settings.AiDevice.Equals("Auto", StringComparison.OrdinalIgnoreCase) ? "auto" : settings.AiDevice };
        AppendRuntimeArguments(arguments, runtimeConfiguration);
        return arguments;
    }

    /// <summary>Builds one NCNN directory-mode operation for an owned chunk of PNG frames.</summary>
    public IReadOnlyList<string> BuildDirectoryArguments(AiRestorationSession session, VideoRestorationSettings settings, string inputDirectory, string outputDirectory, NcnnRuntimeConfiguration? runtimeConfiguration = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!Path.IsPathFullyQualified(inputDirectory) || !Path.IsPathFullyQualified(outputDirectory))
            throw new AiRestorationValidationException("AI restoration requires absolute staging directories.");
        if (!Directory.Exists(inputDirectory))
            throw new DirectoryNotFoundException("AI restoration input-frame directory is missing.");
        var arguments = new List<string> { "-i", inputDirectory, "-o", outputDirectory, "-m", session.Model.ModelsDirectory, "-n", session.Model.BackendModelName, "-s", ((int)settings.AiScale).ToString(), "-g", settings.AiDevice.Equals("Auto", StringComparison.OrdinalIgnoreCase) ? "auto" : settings.AiDevice, "-f", "png" };
        AppendRuntimeArguments(arguments, runtimeConfiguration);
        return arguments;
    }

    public async Task ProcessFrameAsync(VideoRestorationSettings settings, string input, string stagingOutput, CancellationToken cancellationToken = default)
    {
        AiRestorationSession session = await CreateSessionAsync(settings, cancellationToken).ConfigureAwait(false);
        await ProcessFrameAsync(session, settings, input, stagingOutput, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves the executable, model, and device once. A feature-length encode must not
    /// repeatedly rescan model files or rediscover capabilities for every source frame.
    /// </summary>
    public async Task<AiRestorationSession> CreateSessionAsync(VideoRestorationSettings settings, CancellationToken cancellationToken = default)
    {
        AiRestorationCapabilities capabilities = await GetCapabilitiesAsync(settings, cancellationToken).ConfigureAwait(false);
        AiRestorationModel model = ValidateSelection(settings, capabilities);
        return new AiRestorationSession(capabilities, model);
    }

    public async Task ProcessFrameAsync(AiRestorationSession session, VideoRestorationSettings settings, string input, string stagingOutput, CancellationToken cancellationToken = default, NcnnRuntimeConfiguration? runtimeConfiguration = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        try
        {
            if (File.Exists(stagingOutput)) File.Delete(stagingOutput);
            MediaToolProcessResult result = await _runner.RunAsync(new MediaToolProcessRequest
            {
                FileName = session.Capabilities.ExecutablePath,
                Arguments = BuildFrameArguments(session.Capabilities, session.Model, settings, input, stagingOutput, runtimeConfiguration),
                Timeout = TimeSpan.FromMinutes(2)
            }, cancellationToken).ConfigureAwait(false);
            if (result.TimedOut) throw new AiRestorationValidationException("AI restoration timed out while processing a frame.");
            if (result.ExitCode != 0) throw new AiRestorationValidationException("AI restoration failed to initialize the selected Vulkan device or process the frame.");
            if (!File.Exists(stagingOutput) || new FileInfo(stagingOutput).Length < 64)
                throw new AiRestorationValidationException("AI restoration produced an incomplete frame.");
        }
        catch { try { if (File.Exists(stagingOutput)) File.Delete(stagingOutput); } catch { } throw; }
    }

    /// <summary>
    /// Runs NCNN once for a bounded directory chunk. Progress is derived from complete
    /// expected outputs while the process runs; output identity is validated by the caller.
    /// </summary>
    public async Task<AiDirectoryProcessDiagnostic> ProcessDirectoryAsync(
        AiRestorationSession session,
        VideoRestorationSettings settings,
        string inputDirectory,
        string outputDirectory,
        IReadOnlyList<string> expectedOutputFrames,
        Action<int>? completedFrames,
        CancellationToken cancellationToken = default,
        NcnnRuntimeConfiguration? runtimeConfiguration = null,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (expectedOutputFrames.Count == 0)
            throw new ArgumentException("AI restoration needs at least one expected output frame.", nameof(expectedOutputFrames));
        if (expectedOutputFrames.Any(path => !Path.IsPathFullyQualified(path) || !Path.GetDirectoryName(path)!.Equals(outputDirectory, StringComparison.OrdinalIgnoreCase)))
            throw new AiRestorationValidationException("AI restoration output-frame paths must belong to the owned output directory.");

        Directory.CreateDirectory(outputDirectory);
        CleanupOwnedOutputs(outputDirectory);
        IReadOnlyList<string> arguments = BuildDirectoryArguments(session, settings, inputDirectory, outputDirectory, runtimeConfiguration);
        var stopwatch = Stopwatch.StartNew();
        _log?.Invoke($"[AI Restoration] chunk start; inputFrames={Directory.EnumerateFiles(inputDirectory, "*.png").Count()}; expectedOutputFrames={expectedOutputFrames.Count}; model={session.Model.BackendModelName}; scale={(int)settings.AiScale}x; device={settings.AiDevice}.");
        Task<MediaToolProcessResult>? operation = null;
        try
        {
            operation = _runner.RunAsync(new MediaToolProcessRequest
            {
                FileName = session.Capabilities.ExecutablePath,
                Arguments = arguments,
                Timeout = timeout ?? TimeSpan.FromMinutes(30),
                SendQuitOnCancellation = true
            }, cancellationToken);

            int lastReported = -1;
            TimeSpan lastReportAt = TimeSpan.Zero;
            while (!operation.IsCompleted)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int complete = CountCompleteOutputs(expectedOutputFrames);
                if (complete != lastReported && (complete == expectedOutputFrames.Count || stopwatch.Elapsed - lastReportAt >= TimeSpan.FromMilliseconds(500)))
                {
                    completedFrames?.Invoke(complete);
                    lastReported = complete;
                    lastReportAt = stopwatch.Elapsed;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
            }

            MediaToolProcessResult result = await operation.ConfigureAwait(false);
            string? runtimeFailure = FindFatalRuntimeFailure(result.StandardOutput, result.StandardError);
            if (result.TimedOut || result.ExitCode != 0 || runtimeFailure is not null)
            {
                AiDirectoryProcessDiagnostic failedDiagnostic = CreateDirectoryDiagnostic(session.Capabilities.ExecutablePath, arguments, result, stopwatch.Elapsed, expectedOutputFrames.Count, outputDirectory);
                string failure = BuildDirectoryFailureDiagnostic(session.Capabilities.ExecutablePath, arguments, result, stopwatch.Elapsed) +
                    (runtimeFailure is null ? "" : $"{Environment.NewLine}AI restoration backend reported a fatal Vulkan runtime failure despite exit code {result.ExitCode}: {runtimeFailure}.") +
                    Environment.NewLine + FormatDirectoryDiagnostic(failedDiagnostic);
                _log?.Invoke(failure);
                throw new AiRestorationValidationException(failure);
            }

            int finalComplete = CountCompleteOutputs(expectedOutputFrames);
            if (finalComplete != lastReported)
                completedFrames?.Invoke(finalComplete);
            double fps = stopwatch.Elapsed.TotalSeconds > 0 ? finalComplete / stopwatch.Elapsed.TotalSeconds : 0;
            AiDirectoryProcessDiagnostic diagnostic = CreateDirectoryDiagnostic(session.Capabilities.ExecutablePath, arguments, result, stopwatch.Elapsed, expectedOutputFrames.Count, outputDirectory);
            _log?.Invoke(FormatDirectoryDiagnostic(diagnostic));
            _log?.Invoke($"[AI Restoration] chunk complete; inputFrames={Directory.EnumerateFiles(inputDirectory, "*.png").Count()}; outputFrames={finalComplete}; elapsed={stopwatch.Elapsed:g}; fps={fps:0.###}; model={session.Model.BackendModelName}; scale={(int)settings.AiScale}x; device={settings.AiDevice}.");
            return diagnostic;
        }
        catch (OperationCanceledException)
        {
            if (operation != null)
            {
                try { await operation.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
            CleanupOwnedOutputs(outputDirectory);
            throw;
        }
        catch
        {
            CleanupOwnedOutputs(outputDirectory);
            throw;
        }
    }

    internal static int CountCompleteOutputs(IReadOnlyList<string> expectedOutputFrames) =>
        expectedOutputFrames.Count(IsReadablePng);

    private static bool IsReadablePng(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length < 64) return false;
            byte[] header = new byte[24];
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return stream.Read(header, 0, header.Length) == header.Length &&
                   header[0] == 137 && header[1] == 80 && header[2] == 78 && header[3] == 71 &&
                   header[12] == 73 && header[13] == 72 && header[14] == 68 && header[15] == 82 &&
                   header[16..24].Any(value => value != 0);
        }
        catch { return false; }
    }

    internal static string BuildDirectoryFailureDiagnostic(string executable, IReadOnlyList<string> arguments, MediaToolProcessResult result, TimeSpan elapsed) =>
        $"AI restoration batch failed. command={SanitizeDiagnostic(executable)} {string.Join(" ", arguments.Select(SanitizeDiagnostic))}; elapsed={elapsed:g}; exitCode={result.ExitCode}; timedOut={result.TimedOut}; stdout={SanitizeDiagnostic(result.StandardOutput)}; stderr={SanitizeDiagnostic(result.StandardError)}";

    private static AiDirectoryProcessDiagnostic CreateDirectoryDiagnostic(string executable, IReadOnlyList<string> arguments, MediaToolProcessResult result, TimeSpan elapsed, int expectedFrames, string outputDirectory)
    {
        FileInfo[] files = Directory.Exists(outputDirectory) ? Directory.EnumerateFiles(outputDirectory).Select(path => new FileInfo(path)).OrderBy(file => file.Name, StringComparer.Ordinal).ToArray() : Array.Empty<FileInfo>();
        return new($"{executable} {string.Join(" ", arguments)}", result.ExitCode, elapsed, result.StandardOutput, result.StandardError, expectedFrames, files.Length,
            files.FirstOrDefault()?.Name, files.LastOrDefault()?.Name, files.FirstOrDefault()?.LastWriteTimeUtc, files.LastOrDefault()?.LastWriteTimeUtc, result.TimedOut, executable);
    }

    internal static string FormatDirectoryDiagnostic(AiDirectoryProcessDiagnostic value) =>
        $"[AI NCNN Process] Command: {value.CommandLine}{Environment.NewLine}Exit Code: {value.ExitCode}; Execution Time: {value.Elapsed:g}; Expected Frames: {value.ExpectedFrames}; Restored Frames Produced: {value.RestoredFrames}{Environment.NewLine}First Output: {value.FirstOutputFileName ?? "<none>"} ({value.FirstOutputTimestamp?.ToString("O") ?? "<none>"}); Last Output: {value.LastOutputFileName ?? "<none>"} ({value.LastOutputTimestamp?.ToString("O") ?? "<none>"}){Environment.NewLine}Stdout: {value.StandardOutput}{Environment.NewLine}Stderr: {value.StandardError}";

    /// <summary>
    /// Real-ESRGAN's Vulkan backend can emit these fatal allocation/queue errors yet still
    /// return exit code zero and create structurally valid black PNGs. Treat that as a failed
    /// inference operation before those frames can become an apparently valid intermediate.
    /// </summary>
    internal static string? FindFatalRuntimeFailure(string? standardOutput, string? standardError)
    {
        string output = string.Concat(standardOutput, "\n", standardError);
        string[] signatures =
        {
            "vkAllocateMemory failed",
            "vkWaitForFences failed",
            "vkQueueSubmit failed",
            "Vulkan device lost"
        };
        return signatures.FirstOrDefault(signature =>
            output.Contains(signature, StringComparison.OrdinalIgnoreCase));
    }

    private static void AppendRuntimeArguments(List<string> arguments, NcnnRuntimeConfiguration? runtimeConfiguration)
    {
        NcnnRuntimeConfiguration configuration = runtimeConfiguration ?? NcnnRuntimeConfiguration.SafeDefault;
        configuration.Validate();
        configuration.Threads?.Validate();
        if (configuration.Threads is NcnnThreadConfiguration threads)
        {
            arguments.Add("-j");
            arguments.Add(threads.ToString());
        }
        if (configuration.TileSize is int tile)
        {
            arguments.Add("-t");
            arguments.Add(tile.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    private static void CleanupOwnedOutputs(string outputDirectory)
    {
        try
        {
            foreach (string path in Directory.EnumerateFiles(outputDirectory))
                File.Delete(path);
        }
        catch { }
    }

    private static string SanitizeDiagnostic(string? value)
    {
        string sanitized = string.IsNullOrWhiteSpace(value) ? "<none>" : value.Replace("\r", " ").Replace("\n", " ").Trim();
        return sanitized[..Math.Min(sanitized.Length, 4096)];
    }

    private string ResolveExecutable(string configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(Environment.ExpandEnvironmentVariables(configured.Trim())))
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(configured.Trim()));
        string[] candidates = { "realesrgan-ncnn-vulkan.exe", "realesrgan-ncnn-vulkan", "realesr-animevideov3-ncnn-vulkan.exe" };
        foreach (string directory in new[] { _applicationDirectory, Path.Combine(_applicationDirectory, "programs"), Path.Combine(_applicationDirectory, "Programs") })
            foreach (string candidate in candidates)
                if (File.Exists(Path.Combine(directory, candidate))) return Path.Combine(directory, candidate);
        return Path.Combine(_applicationDirectory, candidates[0]);
    }

    private static string ResolveModelsDirectory(string executable, string configured) =>
        !string.IsNullOrWhiteSpace(configured) ? Path.GetFullPath(Environment.ExpandEnvironmentVariables(configured.Trim())) :
        Path.Combine(Path.GetDirectoryName(executable) ?? AppDomain.CurrentDomain.BaseDirectory, "models");

    /// <summary>Accept older persisted AnimeVideo IDs while resolving exactly one scale suffix.</summary>
    internal static string NormalizeLogicalModelId(string? configuredId)
    {
        string value = configuredId?.Trim() ?? "";
        foreach (AiRestorationScale scale in new[] { AiRestorationScale.X2, AiRestorationScale.X3, AiRestorationScale.X4 })
        {
            string suffix = "-x" + (int)scale;
            if (value.Equals("realesr-animevideov3" + suffix, StringComparison.OrdinalIgnoreCase)) return "realesr-animevideov3";
        }
        return value;
    }

    internal static bool MatchesSettings(AiRestorationModel model, VideoRestorationSettings settings) =>
        model.Id.Equals(NormalizeLogicalModelId(settings.AiModelId), StringComparison.OrdinalIgnoreCase) &&
        model.Category == settings.AiMode && model.SupportedScales.Contains(settings.AiScale);

    private static IEnumerable<string> ParseDevices(string output)
    {
        foreach (string line in output.Split('\n'))
        {
            int index = line.IndexOf("GPU", StringComparison.OrdinalIgnoreCase);
            if (index < 0) continue;
            string normalized = line.Trim();
            if (normalized.Length is > 0 and < 160) yield return normalized;
        }
    }

    private static string BuildIdentity(string executable, string models)
    {
        static string Stamp(string path) => File.Exists(path) ? $"{Path.GetFullPath(path)}|{new FileInfo(path).Length}|{File.GetLastWriteTimeUtc(path).Ticks}" : Path.GetFullPath(path);
        string modelStamp = Directory.Exists(models) ? string.Join("|", Directory.EnumerateFiles(models, "*.param").Concat(Directory.EnumerateFiles(models, "*.bin")).OrderBy(path => path, StringComparer.OrdinalIgnoreCase).Select(Stamp)) : models;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Stamp(executable) + "|" + modelStamp))).Substring(0, 24);
    }
}
