using MediaFlux.Models;
using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class DuplicateDetectionServiceTests : IDisposable
{
    private readonly string _root;
    private readonly string _dataDirectory;
    private readonly MediaInfoService _mediaInfoService;

    public DuplicateDetectionServiceTests()
    {
        _root = Path.Combine(
            Path.GetTempPath(),
            "MediaFlux-DuplicateDetectionTests",
            Guid.NewGuid().ToString("N"));
        _dataDirectory = Path.Combine(_root, "data");
        Directory.CreateDirectory(_dataDirectory);
        _mediaInfoService = new MediaInfoService(
            _root,
            persistentCacheEnabled: false);
    }

    [Fact]
    public async Task ExactScanGroupsOnlyByteIdenticalSameSizeFiles()
    {
        string first = WriteFile("first.mkv", 1, 2, 3, 4);
        string second = WriteFile("second.mkv", 1, 2, 3, 4);
        string different = WriteFile("different.mkv", 4, 3, 2, 1);
        var service = CreateService();

        DuplicateScanResult result = await service.AnalyzeAsync(
            new[] { first, second, different },
            ExactOptions(),
            progress: null,
            CancellationToken.None);

        DuplicateGroup group = Assert.Single(result.Groups);
        Assert.Equal("Exact", group.ConfidenceLabel);
        Assert.Equal(100, group.ConfidenceScore);
        Assert.Equal("Exact hash", group.MatchMethod);
        Assert.Equal(2, group.Items.Count);
        Assert.Contains(group.Items, item => item.Path == first);
        Assert.Contains(group.Items, item => item.Path == second);
        Assert.DoesNotContain(group.Items, item => item.Path == different);
        Assert.Equal(1, result.DuplicateFiles);
        Assert.Equal(4, result.PotentialRecoverableBytes);
        Assert.Single(group.Items, item => item.Recommendation.Contains("keeper", StringComparison.OrdinalIgnoreCase));
        Assert.Single(group.Items, item => item.Recommendation == "Trash candidate");
    }

    [Fact]
    public async Task ExactScanMakesReferenceFileTheProtectedKeeper()
    {
        string referenceFolder = Path.Combine(_root, "reference");
        string otherFolder = Path.Combine(_root, "other");
        Directory.CreateDirectory(referenceFolder);
        Directory.CreateDirectory(otherFolder);
        string reference = WriteFile(Path.Combine("reference", "keeper.mp4"), 9, 8, 7);
        string duplicate = WriteFile(Path.Combine("other", "copy.mp4"), 9, 8, 7);
        var service = CreateService();

        DuplicateScanResult result = await service.AnalyzeAsync(
            new[] { duplicate, reference },
            ExactOptions(referenceFolder),
            progress: null,
            CancellationToken.None);

        DuplicateGroup group = Assert.Single(result.Groups);
        DuplicateItem keeper = Assert.Single(group.Items, item => item.Recommendation == "Protected keeper");
        Assert.Equal(reference, keeper.Path);
        Assert.True(keeper.IsReferenceProtected);
        Assert.Equal("Trash candidate", Assert.Single(group.Items, item => item.Path == duplicate).Recommendation);
        Assert.Equal(3, result.PotentialRecoverableBytes);
    }

    [Fact]
    public async Task ExactHashCacheIsInvalidatedWhenFileSignatureChanges()
    {
        string first = WriteFile("first.avi", 5, 5, 5, 5);
        string second = WriteFile("second.avi", 5, 5, 5, 5);
        var service = CreateService();

        DuplicateScanResult initial = await service.AnalyzeAsync(
            new[] { first, second },
            ExactOptions(),
            progress: null,
            CancellationToken.None);
        Assert.Single(initial.Groups);

        // Recreate the service so this assertion characterizes validation of
        // entries loaded from the persistent signature cache, not only memory.
        service = CreateService();

        DateTime changedTimestamp = File.GetLastWriteTimeUtc(second).AddMinutes(2);
        File.WriteAllBytes(second, new byte[] { 6, 6, 6, 6 });
        File.SetLastWriteTimeUtc(second, changedTimestamp);

        DuplicateScanResult rescanned = await service.AnalyzeAsync(
            new[] { first, second },
            ExactOptions(),
            progress: null,
            CancellationToken.None);

        Assert.Empty(rescanned.Groups);
        Assert.Equal(0, rescanned.DuplicateFiles);
        Assert.Equal(0, rescanned.PotentialRecoverableBytes);
    }

    [Fact]
    public async Task ExactScanHonorsPreCanceledToken()
    {
        string first = WriteFile("first.mov", 1, 1, 1);
        string second = WriteFile("second.mov", 1, 1, 1);
        var service = CreateService();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.AnalyzeAsync(
                new[] { first, second },
                ExactOptions(),
                progress: null,
                cancellation.Token));
    }

    public void Dispose()
    {
        _mediaInfoService.FlushCache();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private DuplicateDetectionService CreateService()
    {
        return new DuplicateDetectionService(
            _mediaInfoService,
            _root,
            persistentCacheEnabled: true,
            dataDirectory: _dataDirectory);
    }

    private static DuplicateScanOptions ExactOptions(params string[] referenceFolders)
    {
        return new DuplicateScanOptions(
            DuplicateScanModes.Exact,
            referenceFolders,
            new DuplicateKeeperPreferences());
    }

    private string WriteFile(string relativePath, params byte[] contents)
    {
        string path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, contents);
        return path;
    }
}
