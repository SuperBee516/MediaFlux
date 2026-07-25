using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class DvdTempCleanupServiceTests : IDisposable
{
    private readonly string _root;

    public DvdTempCleanupServiceTests()
    {
        _root = Path.Combine(
            Path.GetTempPath(),
            "MediaFlux-DvdTempCleanupTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void RemovesOnlyStaleMediaFluxDvdOperationDirectories()
    {
        DateTime now = new(2026, 7, 24, 12, 0, 0, DateTimeKind.Utc);
        string stale = CreateDirectory("dvd-stale", now.AddDays(-8));
        string recent = CreateDirectory("dvd-recent", now.AddDays(-1));
        string unrelated = CreateDirectory("other-stale", now.AddDays(-30));

        DvdTempCleanupResult result =
            DvdTempCleanupService.CleanupStaleOperations(
                _root,
                TimeSpan.FromDays(7),
                now);

        Assert.Equal(1, result.RemovedDirectoryCount);
        Assert.Empty(result.Errors);
        Assert.False(Directory.Exists(stale));
        Assert.True(Directory.Exists(recent));
        Assert.True(Directory.Exists(unrelated));
    }

    private string CreateDirectory(string name, DateTime lastWriteUtc)
    {
        string path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "title.ffconcat"), "test");
        Directory.SetLastWriteTimeUtc(path, lastWriteUtc);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
