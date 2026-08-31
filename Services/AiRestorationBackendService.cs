using System.Collections.Concurrent;
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
    string BackendId);

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

public sealed class AiRestorationValidationException : InvalidOperationException
{
    public AiRestorationValidationException(string message) : base(message) { }
}

/// <summary>
/// Discovers an optional local NCNN/Vulkan compatible executable and complete models.
/// It intentionally has no downloader and does not turn an unavailable optional component
/// into a failure for ordinary FFmpeg restoration.
/// </summary>
public sealed class AiRestorationBackendService
{
    private static readonly ConcurrentDictionary<string, AiRestorationCapabilities> Cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly IMediaToolProcessRunner _runner;
    private readonly string _applicationDirectory;
    private readonly Action<string>? _log;

    private sealed record ModelDefinition(string Id, string DisplayName, AiRestorationMode Category, AiRestorationScale[] Scales);
    private static readonly ModelDefinition[] KnownModels =
    {
        new("realesr-animevideov3", "Real-ESRGAN AnimeVideo v3", AiRestorationMode.Animation, new[] { AiRestorationScale.X2, AiRestorationScale.X4 }),
        new("realesrgan-x4plus-anime", "Real-ESRGAN x4plus Anime", AiRestorationMode.Animation, new[] { AiRestorationScale.X4 }),
        new("realesrgan-x4plus", "Real-ESRGAN x4plus", AiRestorationMode.General, new[] { AiRestorationScale.X4 })
    };

    public AiRestorationBackendService(string applicationDirectory, IMediaToolProcessRunner? runner = null, Action<string>? log = null)
    {
        _applicationDirectory = string.IsNullOrWhiteSpace(applicationDirectory) ? AppDomain.CurrentDomain.BaseDirectory : applicationDirectory;
        _runner = runner ?? new MediaToolProcessRunner();
        _log = log;
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

            IReadOnlyList<AiRestorationModel> discovered = DiscoverModels(models);
            bool vulkan = output.Contains("vulkan", StringComparison.OrdinalIgnoreCase) ||
                          Path.GetFileName(executable).Contains("vulkan", StringComparison.OrdinalIgnoreCase);
            // Most NCNN command-line builds only enumerate devices during processing. Auto is
            // always a valid backend selection; explicit IDs are exposed only when reported.
            string[] devices = ParseDevices(output).Prepend("Auto").Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var capabilities = new AiRestorationCapabilities(true, "ncnn-vulkan", executable, identity, vulkan, devices, discovered,
                discovered.Count == 0 ? "No complete supported AI models were found next to the backend." : null);
            Cache[identity] = capabilities;
            _log?.Invoke($"[AI Restoration] backend=ncnn-vulkan; identity={identity}; vulkan={(vulkan ? "reported" : "not reported")}; devices={string.Join(", ", devices)}; models={discovered.Count}.");
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
        if (!capabilities.IsAvailable)
            throw new AiRestorationValidationException(capabilities.Error ?? "AI backend unavailable.");
        if (!capabilities.VulkanAvailable)
            throw new AiRestorationValidationException("Vulkan device unavailable: the configured AI backend did not report Vulkan support.");
        if (!capabilities.Devices.Contains(settings.AiDevice, StringComparer.OrdinalIgnoreCase))
            throw new AiRestorationValidationException($"Vulkan device unavailable: '{settings.AiDevice}' is not available for the configured AI backend.");
        AiRestorationModel? model = capabilities.Models.FirstOrDefault(model =>
            model.Id.Equals(settings.AiModelId, StringComparison.OrdinalIgnoreCase));
        if (model is null)
            throw new AiRestorationValidationException(string.IsNullOrWhiteSpace(settings.AiModelId)
                ? "AI model missing/incomplete: choose a detected model before encoding."
                : $"AI model missing/incomplete: '{settings.AiModelId}' was not found or is incomplete.");
        if (model.Category != settings.AiMode)
            throw new AiRestorationValidationException($"AI model '{model.DisplayName}' is not compatible with {settings.AiMode} restoration.");
        if (!model.SupportedScales.Contains(settings.AiScale))
            throw new AiRestorationValidationException($"Unsupported model scale: {model.DisplayName} does not support {(int)settings.AiScale}x.");
        return model;
    }

    public static void InvalidateCache() => Cache.Clear();

    /// <summary>Builds safe argument tokens for one deterministic frame operation.</summary>
    public IReadOnlyList<string> BuildFrameArguments(AiRestorationCapabilities capabilities, AiRestorationModel model, VideoRestorationSettings settings, string input, string output)
    {
        if (!Path.IsPathFullyQualified(input) || !Path.IsPathFullyQualified(output))
            throw new AiRestorationValidationException("AI restoration requires absolute staging paths.");
        if (!File.Exists(input)) throw new FileNotFoundException("AI restoration input frame is missing.", input);
        return new[] { "-i", input, "-o", output, "-m", model.ModelsDirectory, "-n", model.Id, "-s", ((int)settings.AiScale).ToString(), "-g", settings.AiDevice.Equals("Auto", StringComparison.OrdinalIgnoreCase) ? "auto" : settings.AiDevice };
    }

    public async Task ProcessFrameAsync(VideoRestorationSettings settings, string input, string stagingOutput, CancellationToken cancellationToken = default)
    {
        AiRestorationCapabilities capabilities = await GetCapabilitiesAsync(settings, cancellationToken).ConfigureAwait(false);
        AiRestorationModel model = await ValidateSelectionAsync(settings, cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(stagingOutput)) File.Delete(stagingOutput);
            MediaToolProcessResult result = await _runner.RunAsync(new MediaToolProcessRequest
            {
                FileName = capabilities.ExecutablePath,
                Arguments = BuildFrameArguments(capabilities, model, settings, input, stagingOutput),
                Timeout = TimeSpan.FromMinutes(2)
            }, cancellationToken).ConfigureAwait(false);
            if (result.TimedOut) throw new AiRestorationValidationException("AI restoration timed out while processing a frame.");
            if (result.ExitCode != 0) throw new AiRestorationValidationException("AI restoration failed to initialize the selected Vulkan device or process the frame.");
            if (!File.Exists(stagingOutput) || new FileInfo(stagingOutput).Length < 64)
                throw new AiRestorationValidationException("AI restoration produced an incomplete frame.");
        }
        catch { try { if (File.Exists(stagingOutput)) File.Delete(stagingOutput); } catch { } throw; }
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

    private static IReadOnlyList<AiRestorationModel> DiscoverModels(string modelsDirectory)
    {
        if (!Directory.Exists(modelsDirectory)) return Array.Empty<AiRestorationModel>();
        return KnownModels.Select(definition =>
        {
            string param = Path.Combine(modelsDirectory, definition.Id + ".param");
            string bin = Path.Combine(modelsDirectory, definition.Id + ".bin");
            return File.Exists(param) && new FileInfo(param).Length > 0 && File.Exists(bin) && new FileInfo(bin).Length > 0
                ? new AiRestorationModel(definition.Id, definition.DisplayName, definition.Category, definition.Scales, modelsDirectory, param, bin, "ncnn-vulkan")
                : null;
        }).Where(model => model is not null).Cast<AiRestorationModel>().ToArray();
    }

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
