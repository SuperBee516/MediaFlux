using System.IO.Compression;
using System.Security.Cryptography;
using MediaFlux.Services;
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
