using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;

namespace MediaFlux.Services;

public sealed record ManagedComponentDescriptor(
    string ComponentId,
    string DisplayName,
    string ProviderId,
    string Version,
    string PackageIdentity,
    Uri DownloadUri,
    string? ExpectedSha256,
    string Platform,
    string Architecture);

public enum ManagedComponentInstallationState { NotInstalled, Incomplete, Invalid, Installed }

public sealed record ManagedComponentInstallationInspection(
    ManagedComponentInstallationState State,
    string ManagedDirectory,
    string Version,
    string PackageIdentity,
    string Detail);

public enum ManagedComponentProvisioningStage { Downloading, Verifying, Extracting, Validating, Installing, Completed }
public sealed record ManagedComponentProvisioningProgress(ManagedComponentProvisioningStage Stage, long BytesTransferred, long? TotalBytes, double? Percentage);
public sealed record ManagedComponentProvisioningResult(bool Succeeded, string ManagedDirectory, string Version, string PackageIdentity, string Detail);

public interface IManagedComponentPackageDownloader
{
    Task DownloadAsync(Uri source, string destination, IProgress<ManagedComponentProvisioningProgress>? progress, CancellationToken cancellationToken);
}

public sealed class HttpManagedComponentPackageDownloader : IManagedComponentPackageDownloader
{
    private readonly HttpClient _client;
    public HttpManagedComponentPackageDownloader(HttpClient? client = null) => _client = client ?? new HttpClient();

    public async Task DownloadAsync(Uri source, string destination, IProgress<ManagedComponentProvisioningProgress>? progress, CancellationToken cancellationToken)
    {
        if (!string.Equals(source.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Managed component packages must be downloaded over HTTPS.");
        using HttpResponseMessage response = await _client.GetAsync(source, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        long? total = response.Content.Headers.ContentLength;
        await using Stream input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using FileStream output = new(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        byte[] buffer = new byte[81920]; long count = 0; int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            count += read;
            progress?.Report(new(ManagedComponentProvisioningStage.Downloading, count, total, total is > 0 ? count * 100d / total.Value : null));
        }
    }
}

public interface IManagedComponentValidator
{
    void Validate(string extractedRoot);
}

/// <summary>Shared verified-download, safe-extraction, validation, promotion, and rollback boundary.</summary>
public sealed class ManagedComponentInstaller
{
    private readonly ManagedComponentDescriptor _descriptor;
    private readonly string _managedRoot;
    private readonly IManagedComponentPackageDownloader _downloader;
    private readonly IManagedComponentValidator _validator;
    private readonly Action? _invalidateCache;
    private readonly Action<string>? _log;

    public ManagedComponentInstaller(ManagedComponentDescriptor descriptor, string managedRoot, IManagedComponentValidator validator,
        IManagedComponentPackageDownloader? downloader = null, Action? invalidateCache = null, Action<string>? log = null)
    {
        _descriptor = descriptor;
        _managedRoot = Path.GetFullPath(managedRoot);
        _validator = validator;
        _downloader = downloader ?? new HttpManagedComponentPackageDownloader();
        _invalidateCache = invalidateCache ?? FfmpegSetupService.InvalidateCaches;
        _log = log;
    }

    public async Task<ManagedComponentProvisioningResult> InstallAsync(IProgress<ManagedComponentProvisioningProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        string destination = Path.Combine(_managedRoot, _descriptor.Version);
        if (string.IsNullOrWhiteSpace(_descriptor.ExpectedSha256))
            return Failure(destination, "No authoritative SHA-256 is configured; refusing unverified installation.");
        if (!string.Equals(_descriptor.DownloadUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return Failure(destination, "Managed component package source must use HTTPS.");

        string staging = Path.Combine(Path.GetDirectoryName(_managedRoot)!, $".{_descriptor.ComponentId}-staging-{Guid.NewGuid():N}");
        string archive = Path.Combine(staging, _descriptor.PackageIdentity);
        string extracted = Path.Combine(staging, "package");
        string? backup = null;
        try
        {
            Directory.CreateDirectory(staging);
            await _downloader.DownloadAsync(_descriptor.DownloadUri, archive, progress, cancellationToken).ConfigureAwait(false);
            progress?.Report(new(ManagedComponentProvisioningStage.Verifying, 0, null, null));
            await using FileStream stream = File.OpenRead(archive);
            string actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
            if (!actual.Equals(_descriptor.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Managed component package SHA-256 does not match the pinned hash.");
            progress?.Report(new(ManagedComponentProvisioningStage.Extracting, 0, null, null));
            Directory.CreateDirectory(extracted);
            ExtractSafely(archive, extracted, cancellationToken);
            NormalizeSingleArchiveRoot(extracted);
            progress?.Report(new(ManagedComponentProvisioningStage.Validating, 0, null, null));
            _validator.Validate(extracted);
            progress?.Report(new(ManagedComponentProvisioningStage.Installing, 0, null, null));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (Directory.Exists(destination))
            {
                backup = destination + $".backup-{Guid.NewGuid():N}";
                Directory.Move(destination, backup);
            }
            Directory.Move(extracted, destination);
            if (backup is not null) TryDeleteDirectory(backup);
            _invalidateCache?.Invoke();
            progress?.Report(new(ManagedComponentProvisioningStage.Completed, 0, null, 100));
            return new(true, destination, _descriptor.Version, _descriptor.PackageIdentity, "Managed component installed and validated.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            if (!Directory.Exists(destination) && backup is not null && Directory.Exists(backup))
            {
                try { Directory.Move(backup, destination); } catch { }
            }
            _log?.Invoke($"[Managed Provisioning] {_descriptor.ComponentId} installation failed: {ex.Message}");
            return Failure(destination, ex.Message);
        }
        finally { TryDeleteDirectory(staging); }
    }

    private ManagedComponentProvisioningResult Failure(string destination, string detail) =>
        new(false, destination, _descriptor.Version, _descriptor.PackageIdentity, detail);

    private static void ExtractSafely(string archive, string destination, CancellationToken token)
    {
        using ZipArchive zip = ZipFile.OpenRead(archive);
        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            token.ThrowIfCancellationRequested();
            string target = SafeCombine(destination, entry.FullName);
            if (string.IsNullOrEmpty(entry.Name)) { Directory.CreateDirectory(target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using Stream input = entry.Open();
            using FileStream output = new(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            input.CopyTo(output);
        }
    }

    private static void NormalizeSingleArchiveRoot(string extracted)
    {
        string[] directories = Directory.GetDirectories(extracted);
        if (directories.Length != 1 || Directory.GetFiles(extracted).Length != 0)
            return;
        string archiveRoot = directories[0];
        foreach (string entry in Directory.GetFileSystemEntries(archiveRoot))
        {
            string destination = Path.Combine(extracted, Path.GetFileName(entry));
            if (Directory.Exists(entry)) Directory.Move(entry, destination);
            else File.Move(entry, destination);
        }
        Directory.Delete(archiveRoot);
    }

    internal static string SafeCombine(string root, string relative)
    {
        if (Path.IsPathRooted(relative)) throw new InvalidDataException("Managed component archive contains an absolute path.");
        string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Managed component archive contains a path traversal entry.");
        return full;
    }

    private static void TryDeleteDirectory(string path) { try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { } }
}

public sealed class FfmpegManagedStructureValidator : IManagedComponentValidator
{
    public void Validate(string extractedRoot)
    {
        string? bin = Directory.EnumerateDirectories(extractedRoot, "*", SearchOption.TopDirectoryOnly)
            .Select(path => Path.Combine(path, "bin"))
            .FirstOrDefault(path => File.Exists(Path.Combine(path, "ffmpeg.exe")) && File.Exists(Path.Combine(path, "ffprobe.exe")));
        bin ??= Directory.Exists(Path.Combine(extractedRoot, "bin")) ? Path.Combine(extractedRoot, "bin") : null;
        if (bin is null) throw new InvalidDataException("Managed FFmpeg package is structurally incomplete.");
        foreach (string name in new[] { "ffmpeg.exe", "ffprobe.exe" })
            if (new FileInfo(Path.Combine(bin, name)).Length == 0) throw new InvalidDataException("Managed FFmpeg executable is empty.");
    }
}

public static class FfmpegManagedComponents
{
    // Gyan.dev is linked by FFmpeg's official download page. The checksum is published
    // in the adjacent .sha256 file and independently verified by MediaFlux before pinning.
    public static ManagedComponentDescriptor Release { get; } = new(
        "ffmpeg", "FFmpeg", "gyan.dev", "8.1.2", "ffmpeg-8.1.2-essentials_build.zip",
        new Uri("https://www.gyan.dev/ffmpeg/builds/packages/ffmpeg-8.1.2-essentials_build.zip"),
        "db580001caa24ac104c8cb856cd113a87b0a443f7bdf47d8c12b1d740584a2ec", "windows", "x64");
}

public sealed record FfmpegManagedInstallationInspection(ManagedComponentInstallationState State, string ManagedDirectory, string FfmpegPath, string FfprobePath, string Version, string Detail);

public sealed class FfmpegManagedComponentStateService
{
    private readonly string _applicationDirectory;
    public FfmpegManagedComponentStateService(string applicationDirectory) => _applicationDirectory = Path.GetFullPath(applicationDirectory);
    public FfmpegManagedInstallationInspection Inspect()
    {
        string root = Path.Combine(_applicationDirectory, "Programs", "FFmpeg", FfmpegManagedComponents.Release.Version);
        string ffmpeg = Path.Combine(root, "bin", "ffmpeg.exe"), ffprobe = Path.Combine(root, "bin", "ffprobe.exe");
        try
        {
            bool rootExists = Directory.Exists(root), ffmpegExists = File.Exists(ffmpeg), ffprobeExists = File.Exists(ffprobe);
            ManagedComponentInstallationState state = !rootExists ? ManagedComponentInstallationState.NotInstalled : ffmpegExists && ffprobeExists && new FileInfo(ffmpeg).Length > 0 && new FileInfo(ffprobe).Length > 0 ? ManagedComponentInstallationState.Installed : ManagedComponentInstallationState.Incomplete;
            return new(state, root, ffmpeg, ffprobe, FfmpegManagedComponents.Release.Version, state switch { ManagedComponentInstallationState.NotInstalled => "Managed FFmpeg directory does not exist.", ManagedComponentInstallationState.Installed => "Managed FFmpeg and FFprobe are present.", _ => "Managed FFmpeg package is incomplete or contains an empty executable." });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { return new(ManagedComponentInstallationState.Invalid, root, ffmpeg, ffprobe, FfmpegManagedComponents.Release.Version, ex.Message); }
    }
}

public sealed class FfmpegManagedComponentInstaller
{
    private readonly ManagedComponentInstaller _installer;
    public FfmpegManagedComponentInstaller(string applicationDirectory, IManagedComponentPackageDownloader? downloader = null, Action? invalidateCache = null, Action<string>? log = null)
    {
        _installer = new(FfmpegManagedComponents.Release, Path.Combine(applicationDirectory, "Programs", "FFmpeg"), new FfmpegManagedStructureValidator(), downloader, invalidateCache, log);
    }
    public Task<ManagedComponentProvisioningResult> InstallAsync(IProgress<ManagedComponentProvisioningProgress>? progress = null, CancellationToken cancellationToken = default) => _installer.InstallAsync(progress, cancellationToken);
}

public sealed record FfmpegManagedRuntimeValidation(bool FfmpegStarted, bool FfprobeStarted, bool FfmpegSucceeded, bool FfprobeSucceeded, string FfmpegVersion, string FfprobeVersion, string Detail);

public static class FfmpegManagedRuntimeValidator
{
    public static async Task<FfmpegManagedRuntimeValidation> ValidateAsync(string ffmpegPath, string ffprobePath, CancellationToken cancellationToken = default)
    {
        (bool started, bool succeeded, string output) ffmpeg = await RunVersionAsync(ffmpegPath, cancellationToken).ConfigureAwait(false);
        (bool started, bool succeeded, string output) ffprobe = await RunVersionAsync(ffprobePath, cancellationToken).ConfigureAwait(false);
        return new(ffmpeg.started, ffprobe.started, ffmpeg.succeeded, ffprobe.succeeded, FirstVersionLine(ffmpeg.output), FirstVersionLine(ffprobe.output),
            ffmpeg.succeeded && ffprobe.succeeded ? "FFmpeg and FFprobe launched successfully." : "One or both managed FFmpeg tools failed lightweight runtime validation.");
    }

    private static async Task<(bool started, bool succeeded, string output)> RunVersionAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using Process process = new()
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = path,
                    Arguments = "-version",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            if (!process.Start()) return (false, false, string.Empty);
            Task<string> stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            Task<string> stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return (true, process.ExitCode == 0, (await stdout.ConfigureAwait(false)) + (await stderr.ConfigureAwait(false)));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { return (false, false, ex.Message); }
    }

    private static string FirstVersionLine(string output) => output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
}
