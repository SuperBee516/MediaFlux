using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using MediaFlux.Models;
using MediaFlux.Services;
using MediaFlux.Services.LibraryCatalog;
using Xunit;

namespace MediaFlux.Tests;

public sealed class FfmpegManagedProvisioningTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MediaFluxFfmpegTests", Guid.NewGuid().ToString("N"));
    public FfmpegManagedProvisioningTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    [Fact]
    public void MissingManagedInstallationIsNotInstalledAndIncompleteIsReported()
    {
        var service = new FfmpegManagedComponentStateService(_root);
        Assert.Equal(ManagedComponentInstallationState.NotInstalled, service.Inspect().State);
        string root = Path.Combine(_root, "Programs", "FFmpeg", FfmpegManagedComponents.Release.Version, "bin");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "ffmpeg.exe"), "x");
        Assert.Equal(ManagedComponentInstallationState.Incomplete, service.Inspect().State);
    }

    [Fact]
    public async Task SuccessfulInstallIsImmediatelyDiscoverableAsAnAssociatedPair()
    {
        string archive = CreateArchive(includeBoth: true, unsafeEntry: false);
        var descriptor = FfmpegManagedComponents.Release with { PackageIdentity = "fixture.zip", ExpectedSha256 = Hash(archive) };
        var result = await new ManagedComponentInstaller(descriptor, Path.Combine(_root, "Programs", "FFmpeg"), new FfmpegManagedStructureValidator(), new FixtureDownloader(archive)).InstallAsync();
        Assert.True(result.Succeeded, result.Detail);
        FfmpegToolPaths tools = FfmpegToolResolver.Resolve(_root);
        Assert.Equal(FfmpegToolSource.Managed, tools.Source);
        Assert.True(File.Exists(Path.Combine(_root, "Programs", "FFmpeg", FfmpegManagedComponents.Release.Version, "bin", "ffmpeg.exe")));
        Assert.True(File.Exists(Path.Combine(_root, "Programs", "FFmpeg", FfmpegManagedComponents.Release.Version, "bin", "ffprobe.exe")));
    }

    [Fact]
    public async Task ChecksumMismatchAndUnsafeArchiveDoNotPromote()
    {
        string archive = CreateArchive(includeBoth: true, unsafeEntry: false);
        var badHash = FfmpegManagedComponents.Release with { Version = "bad", PackageIdentity = "fixture.zip", ExpectedSha256 = new string('0', 64) };
        var bad = await new ManagedComponentInstaller(badHash, Path.Combine(_root, "Programs", "FFmpeg"), new FfmpegManagedStructureValidator(), new FixtureDownloader(archive)).InstallAsync();
        Assert.False(bad.Succeeded);
        Assert.False(Directory.Exists(Path.Combine(_root, "Programs", "FFmpeg", "bad")));

        string unsafeArchive = CreateArchive(includeBoth: true, unsafeEntry: true);
        var unsafeDescriptor = FfmpegManagedComponents.Release with { Version = "unsafe", PackageIdentity = "fixture.zip", ExpectedSha256 = Hash(unsafeArchive) };
        var rejected = await new ManagedComponentInstaller(unsafeDescriptor, Path.Combine(_root, "Programs", "FFmpeg"), new FfmpegManagedStructureValidator(), new FixtureDownloader(unsafeArchive)).InstallAsync();
        Assert.False(rejected.Succeeded);
        Assert.False(Directory.Exists(Path.Combine(_root, "Programs", "FFmpeg", "unsafe")));
    }

    [Fact]
    public void ValidExplicitPairRetainsPrecedenceOverManagedInstallation()
    {
        string managed = Path.Combine(_root, "Programs", "FFmpeg", FfmpegManagedComponents.Release.Version, "bin");
        Directory.CreateDirectory(managed);
        File.WriteAllText(Path.Combine(managed, "ffmpeg.exe"), "managed");
        File.WriteAllText(Path.Combine(managed, "ffprobe.exe"), "managed");
        string manual = Path.Combine(_root, "manual"); Directory.CreateDirectory(manual);
        string ffmpeg = Path.Combine(manual, "ffmpeg.exe"), ffprobe = Path.Combine(manual, "ffprobe.exe");
        File.WriteAllText(ffmpeg, "manual"); File.WriteAllText(ffprobe, "manual");
        FfmpegToolPaths tools = FfmpegToolResolver.Resolve(_root, ffmpeg, ffprobe);
        Assert.Equal(FfmpegToolSource.Configured, tools.Source);
        Assert.Equal(Path.GetFullPath(ffmpeg), tools.FfmpegPath);
        Assert.Equal(Path.GetFullPath(ffprobe), tools.FfprobePath);
    }

    [Fact]
    public void ManagedToolsSurviveVelopackCurrentDirectoryReplacement()
    {
        string appDirectory = Path.Combine(_root, "current");
        string toolDirectory = Path.Combine(
            appDirectory,
            "Programs",
            "FFmpeg",
            FfmpegManagedComponents.Release.Version,
            "bin");
        Directory.CreateDirectory(toolDirectory);
        string ffmpeg = Path.Combine(toolDirectory, "ffmpeg.exe");
        string ffprobe = Path.Combine(toolDirectory, "ffprobe.exe");
        File.WriteAllText(ffmpeg, "v1.6.4 ffmpeg fixture");
        File.WriteAllText(ffprobe, "v1.6.4 ffprobe fixture");

        string configPath = Path.Combine(_root, "UserData", "config.json");
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        File.WriteAllText(configPath, JsonSerializer.Serialize(new
        {
            FfmpegPath = ffmpeg,
            FfprobePath = ffprobe
        }));

        Config beforeUpdate = Config.Load(configPath);
        FfmpegToolPaths installedTools = FfmpegToolResolver.Resolve(appDirectory, beforeUpdate.FfmpegPath, beforeUpdate.FfprobePath);
        Assert.Equal(FfmpegToolSource.Configured, installedTools.Source);

        FfmpegManagedPaths.PreserveLegacyInstallation(appDirectory);
        string persistentBin = Path.Combine(_root, "Programs", "FFmpeg", FfmpegManagedComponents.Release.Version, "bin");
        Assert.True(File.Exists(Path.Combine(persistentBin, "ffmpeg.exe")));
        Assert.True(File.Exists(Path.Combine(persistentBin, "ffprobe.exe")));

        // Velopack replaces app content while the user-data configuration survives.
        Directory.Delete(appDirectory, recursive: true);
        Directory.CreateDirectory(appDirectory);

        Config afterUpdate = Config.Load(configPath);
        Assert.Equal(ffmpeg, afterUpdate.FfmpegPath);
        Assert.Equal(ffprobe, afterUpdate.FfprobePath);
        FfmpegToolPaths restoredConfiguredTools = FfmpegToolResolver.Resolve(appDirectory, afterUpdate.FfmpegPath, afterUpdate.FfprobePath);
        Assert.Equal(FfmpegToolSource.Configured, restoredConfiguredTools.Source);
        Assert.Equal(Path.Combine(persistentBin, "ffmpeg.exe"), restoredConfiguredTools.FfmpegPath);
        Assert.Equal(Path.Combine(persistentBin, "ffprobe.exe"), restoredConfiguredTools.FfprobePath);

        FfmpegToolPaths restoredManagedTools = FfmpegToolResolver.Resolve(appDirectory);
        Assert.Equal(FfmpegToolSource.Managed, restoredManagedTools.Source);
        Assert.Equal(restoredConfiguredTools.FfmpegPath, restoredManagedTools.FfmpegPath);
    }

    [Fact]
    public void ManagedProvisionerUsesPersistentSiblingOfVelopackCurrentDirectory()
    {
        string current = Path.Combine(_root, "current");
        string expected = Path.Combine(_root, "Programs", "FFmpeg");
        Assert.Equal(expected, FfmpegManagedPaths.PersistentRoot(current));
        Assert.Equal(Path.Combine(current, "Programs", "FFmpeg"), FfmpegManagedPaths.LegacyRoot(current));
    }

    [Fact]
    public void ManagedReleaseUsesGyanGitHubMirrorWithPinnedArchiveHash()
    {
        ManagedComponentDescriptor descriptor = FfmpegManagedComponents.Release;
        Assert.Equal("https://github.com/GyanD/codexffmpeg/releases/download/8.1.2/ffmpeg-8.1.2-essentials_build.zip", descriptor.DownloadUri.AbsoluteUri);
        Assert.Equal("db580001caa24ac104c8cb856cd113a87b0a443f7bdf47d8c12b1d740584a2ec", descriptor.ExpectedSha256);
    }

    [Fact]
    public void ExplicitFfmpegDoesNotPairWithAnotherInstallation()
    {
        string manual = Path.Combine(_root, "manual"), other = Path.Combine(_root, "other");
        Directory.CreateDirectory(manual); Directory.CreateDirectory(other);
        string ffmpeg = Path.Combine(manual, "ffmpeg.exe");
        File.WriteAllText(ffmpeg, "manual");
        File.WriteAllText(Path.Combine(other, "ffprobe.exe"), "other");

        FfmpegToolPaths tools = FfmpegToolResolver.Resolve(_root, ffmpeg);

        Assert.False(tools.AreAllAvailable);
        Assert.Equal(FfmpegToolSource.Unavailable, tools.Source);
    }

    private string CreateArchive(bool includeBoth, bool unsafeEntry)
    {
        string archive = Path.Combine(_root, Guid.NewGuid() + ".zip");
        using (ZipArchive zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            Add(zip, "package/bin/ffmpeg.exe", "ffmpeg");
            if (includeBoth) Add(zip, "package/bin/ffprobe.exe", "ffprobe");
            if (unsafeEntry) Add(zip, "../outside.exe", "bad");
        }
        return archive;
    }

    private static void Add(ZipArchive zip, string path, string content)
    {
        using StreamWriter writer = new(zip.CreateEntry(path).Open()); writer.Write(content);
    }

    private static string Hash(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private sealed class FixtureDownloader(string source) : IManagedComponentPackageDownloader
    {
        public Task DownloadAsync(Uri _, string destination, IProgress<ManagedComponentProvisioningProgress>? __, CancellationToken ___)
        {
            File.Copy(source, destination); return Task.CompletedTask;
        }
    }
}
