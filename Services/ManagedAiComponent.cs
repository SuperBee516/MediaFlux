using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;

namespace MediaFlux.Services;

public sealed record ManagedAiComponentDescriptor(
    string ComponentId,
    string DisplayName,
    string ProviderId,
    string Version,
    string PackageIdentity,
    Uri DownloadUri,
    string? ExpectedSha256,
    string ExecutableRelativePath,
    string ModelsRelativePath,
    string Platform,
    string Architecture);

public static class ManagedAiComponents
{
    // MediaFlux pins this exact immutable upstream release asset. The digest below was
    // calculated locally from the official Real-ESRGAN GitHub v0.2.5.0 Windows asset;
    // it is maintained by MediaFlux and is not claimed to be an upstream-published checksum.
    public static ManagedAiComponentDescriptor RealEsrgan { get; } = new(
        "realesrgan-ncnn-vulkan",
        "Real-ESRGAN NCNN/Vulkan",
        "ncnn-vulkan",
        "0.2.5.0",
        "realesrgan-ncnn-vulkan-20220424-windows.zip",
        new Uri("https://github.com/xinntao/Real-ESRGAN/releases/download/v0.2.5.0/realesrgan-ncnn-vulkan-20220424-windows.zip"),
        "ABC02804E17982A3BE33675E4D471E91EA374E65B70167ABC09E31ACB412802D",
        "realesrgan-ncnn-vulkan.exe",
        "models",
        "windows",
        "x64");
}

public enum ManagedAiInstallationState { NotInstalled, Installed, Incomplete, Invalid }

public sealed record ManagedAiInstallationInspection(
    ManagedAiInstallationState State,
    string ManagedDirectory,
    string? ExecutablePath,
    string? ModelsDirectory,
    string Version,
    string PackageIdentity,
    bool ExecutableExists,
    bool ModelsDirectoryExists,
    string Detail);

public sealed class ManagedAiComponentStateService
{
    private readonly AiBackendPathResolver _resolver;
    public ManagedAiComponentStateService(string applicationDirectory, ManagedAiComponentDescriptor? descriptor = null) => _resolver = new(applicationDirectory, descriptor);
    public ManagedAiInstallationInspection InspectRealEsrgan() => _resolver.InspectManagedInstallation();
}

public sealed record AiBackendResolution(string ExecutablePath, string ModelsDirectory, bool IsManaged);

/// <summary>Single authoritative path policy for the optional NCNN/Vulkan backend.</summary>
public sealed class AiBackendPathResolver
{
    private readonly string _applicationDirectory;
    private readonly ManagedAiComponentDescriptor _descriptor;

    public AiBackendPathResolver(string applicationDirectory, ManagedAiComponentDescriptor? descriptor = null)
    {
        _applicationDirectory = Path.GetFullPath(string.IsNullOrWhiteSpace(applicationDirectory) ? AppDomain.CurrentDomain.BaseDirectory : applicationDirectory);
        _descriptor = descriptor ?? ManagedAiComponents.RealEsrgan;
    }

    public AiBackendResolution Resolve(string? configuredExecutable, string? configuredModelsDirectory)
    {
        string? explicitExecutable = ExistingFile(configuredExecutable);
        if (explicitExecutable is not null)
            return new(explicitExecutable, ExistingDirectory(configuredModelsDirectory) ?? Path.Combine(Path.GetDirectoryName(explicitExecutable)!, "models"), false);
        bool configuredExecutableWasSpecified = !string.IsNullOrWhiteSpace(configuredExecutable);
        string? unpairedExplicitModels = configuredExecutableWasSpecified ? null : ExistingDirectory(configuredModelsDirectory);

        string managedRoot = Path.Combine(_applicationDirectory, "Programs", "RealESRGAN", _descriptor.Version);
        string managedExecutable = Path.Combine(managedRoot, _descriptor.ExecutableRelativePath);
        if (File.Exists(managedExecutable))
            return new(Path.GetFullPath(managedExecutable), unpairedExplicitModels ?? Path.Combine(managedRoot, _descriptor.ModelsRelativePath), true);

        string[] candidates = { "realesrgan-ncnn-vulkan.exe", "realesrgan-ncnn-vulkan", "realesr-animevideov3-ncnn-vulkan.exe" };
        foreach (string directory in new[] { _applicationDirectory, Path.Combine(_applicationDirectory, "programs"), Path.Combine(_applicationDirectory, "Programs") })
            foreach (string candidate in candidates)
            {
                string path = Path.Combine(directory, candidate);
                if (File.Exists(path))
                    return new(Path.GetFullPath(path), unpairedExplicitModels ?? Path.Combine(directory, "models"), false);
            }

        string fallback = Path.Combine(_applicationDirectory, candidates[0]);
        return new(fallback, unpairedExplicitModels ?? Path.Combine(_applicationDirectory, "models"), false);
    }

    public ManagedAiInstallationInspection InspectManagedInstallation()
    {
        string root = Path.Combine(_applicationDirectory, "Programs", "RealESRGAN", _descriptor.Version);
        string executable = Path.Combine(root, _descriptor.ExecutableRelativePath);
        string models = Path.Combine(root, _descriptor.ModelsRelativePath);
        try
        {
            bool rootExists = Directory.Exists(root), executableExists = File.Exists(executable), modelsExists = Directory.Exists(models);
            ManagedAiInstallationState state = !rootExists ? ManagedAiInstallationState.NotInstalled : executableExists && modelsExists ? ManagedAiInstallationState.Installed : ManagedAiInstallationState.Incomplete;
            string detail = state switch { ManagedAiInstallationState.NotInstalled => "Managed package directory does not exist.", ManagedAiInstallationState.Installed => "Managed executable and models directory are present.", _ => "Managed package is missing its executable or models directory." };
            return new(state, root, executable, models, _descriptor.Version, _descriptor.PackageIdentity, executableExists, modelsExists, detail);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new(ManagedAiInstallationState.Invalid, root, executable, models, _descriptor.Version, _descriptor.PackageIdentity, false, false, ex.Message);
        }
    }

    private static string? ExistingFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        string expanded = Environment.ExpandEnvironmentVariables(path.Trim());
        return File.Exists(expanded) ? Path.GetFullPath(expanded) : null;
    }
    private static string? ExistingDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        string expanded = Environment.ExpandEnvironmentVariables(path.Trim());
        return Directory.Exists(expanded) ? Path.GetFullPath(expanded) : null;
    }
}

public enum AiProvisioningStage { Downloading, Verifying, Extracting, Validating, Installing, Completed }
public sealed record AiProvisioningProgress(AiProvisioningStage Stage, long BytesTransferred, long? TotalBytes, double? Percentage);
public sealed record AiProvisioningResult(bool Succeeded, string ManagedDirectory, string Version, string PackageIdentity, string Detail);

public interface IManagedAiPackageDownloader
{
    Task DownloadAsync(Uri source, string destination, IProgress<AiProvisioningProgress>? progress, CancellationToken cancellationToken);
}

public sealed class HttpManagedAiPackageDownloader : IManagedAiPackageDownloader
{
    private readonly HttpClient _client;
    public HttpManagedAiPackageDownloader(HttpClient? client = null) => _client = client ?? new HttpClient();
    public async Task DownloadAsync(Uri source, string destination, IProgress<AiProvisioningProgress>? progress, CancellationToken cancellationToken)
    {
        if (source.Scheme != Uri.UriSchemeHttps) throw new InvalidOperationException("Managed AI packages must be downloaded over HTTPS.");
        using HttpResponseMessage response = await _client.GetAsync(source, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        long? total = response.Content.Headers.ContentLength;
        await using Stream input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using FileStream output = new(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        byte[] buffer = new byte[81920]; long count = 0; int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0) { await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false); count += read; progress?.Report(new(AiProvisioningStage.Downloading, count, total, total is > 0 ? count * 100d / total.Value : null)); }
    }
}

public sealed class ManagedAiComponentInstaller
{
    private readonly ManagedAiComponentDescriptor _descriptor;
    private readonly string _managedRoot;
    private readonly IManagedAiPackageDownloader _downloader;
    private readonly Action<string>? _log;

    public ManagedAiComponentInstaller(ManagedAiComponentDescriptor? descriptor = null, string? managedRoot = null, IManagedAiPackageDownloader? downloader = null, Action<string>? log = null)
    {
        _descriptor = descriptor ?? ManagedAiComponents.RealEsrgan;
        _managedRoot = Path.GetFullPath(managedRoot ?? AppPaths.ManagedRealEsrganDirectory);
        _downloader = downloader ?? new HttpManagedAiPackageDownloader(); _log = log;
    }

    public async Task<AiProvisioningResult> InstallAsync(IProgress<AiProvisioningProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        string unresolvedDestination = Path.Combine(_managedRoot, _descriptor.Version);
        if (string.IsNullOrWhiteSpace(_descriptor.ExpectedSha256)) return new(false, unresolvedDestination, _descriptor.Version, _descriptor.PackageIdentity, $"No authoritative SHA-256 is configured for {_descriptor.PackageIdentity}; refusing unverified installation.");
        if (!Uri.UriSchemeHttps.Equals(_descriptor.DownloadUri.Scheme, StringComparison.OrdinalIgnoreCase)) return new(false, unresolvedDestination, _descriptor.Version, _descriptor.PackageIdentity, "Managed AI package source must use HTTPS.");
        string staging = Path.Combine(Path.GetDirectoryName(_managedRoot)!, $".realesrgan-staging-{Guid.NewGuid():N}");
        string archive = Path.Combine(staging, _descriptor.PackageIdentity); string extracted = Path.Combine(staging, "package"); string destination = Path.Combine(_managedRoot, _descriptor.Version); string? backup = null;
        try
        {
            Directory.CreateDirectory(staging); _log?.Invoke($"[AI Provisioning] selected {_descriptor.PackageIdentity} version {_descriptor.Version}; download started.");
            await _downloader.DownloadAsync(_descriptor.DownloadUri, archive, progress, cancellationToken).ConfigureAwait(false);
            progress?.Report(new(AiProvisioningStage.Verifying, 0, null, null));
            await using FileStream archiveStream = File.OpenRead(archive);
            string actual = Convert.ToHexString(await SHA256.HashDataAsync(archiveStream, cancellationToken).ConfigureAwait(false));
            if (!actual.Equals(_descriptor.ExpectedSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Managed AI package SHA-256 does not match the pinned hash.");
            _log?.Invoke("[AI Provisioning] package verification succeeded.");
            progress?.Report(new(AiProvisioningStage.Extracting, 0, null, null)); Directory.CreateDirectory(extracted); ExtractSafely(archive, extracted, cancellationToken);
            progress?.Report(new(AiProvisioningStage.Validating, 0, null, null)); ValidateStructure(extracted);
            progress?.Report(new(AiProvisioningStage.Installing, 0, null, null)); Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (Directory.Exists(destination)) { backup = destination + $".backup-{Guid.NewGuid():N}"; Directory.Move(destination, backup); }
            Directory.Move(extracted, destination);
            if (backup is not null) TryDeleteDirectory(backup);
            AiRestorationBackendService.InvalidateCache();
            progress?.Report(new(AiProvisioningStage.Completed, 0, null, 100)); _log?.Invoke($"[AI Provisioning] installed at {destination}.");
            return new(true, destination, _descriptor.Version, _descriptor.PackageIdentity, "Managed package installed and validated.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            if (!Directory.Exists(destination) && backup is not null && Directory.Exists(backup)) { try { Directory.Move(backup, destination); } catch { } }
            _log?.Invoke($"[AI Provisioning] installation failed: {ex.Message}");
            return new(false, destination, _descriptor.Version, _descriptor.PackageIdentity, ex.Message);
        }
        finally { TryDeleteDirectory(staging); }
    }

    private void ValidateStructure(string root)
    {
        string exe = SafeCombine(root, _descriptor.ExecutableRelativePath), models = SafeCombine(root, _descriptor.ModelsRelativePath);
        if (!File.Exists(exe) || !Directory.Exists(models)) throw new InvalidDataException("Managed AI package is structurally incomplete.");
    }
    private void ExtractSafely(string archive, string destination, CancellationToken token)
    {
        using ZipArchive zip = ZipFile.OpenRead(archive);
        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            token.ThrowIfCancellationRequested(); string target = SafeCombine(destination, entry.FullName);
            if (string.IsNullOrEmpty(entry.Name)) { Directory.CreateDirectory(target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!); using Stream input = entry.Open(); using FileStream output = new(target, FileMode.CreateNew, FileAccess.Write, FileShare.None); input.CopyTo(output);
        }
    }
    private static string SafeCombine(string root, string relative)
    {
        if (Path.IsPathRooted(relative)) throw new InvalidDataException("Managed AI archive contains an absolute path.");
        string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar; string full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Managed AI archive contains a path traversal entry.");
        return full;
    }
    private static void TryDeleteDirectory(string path) { try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { } }
}
