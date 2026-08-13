using MediaFlux.Models;
using MediaFlux.Services;
using MediaFlux.Services.LibraryCatalog;
using Xunit;

namespace MediaFlux.Tests;

public sealed class LibraryAnalyzerGeneralFileRemovalTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MediaFlux-GeneralRemoval", Guid.NewGuid().ToString("N"));

    public LibraryAnalyzerGeneralFileRemovalTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void PreviewExcludesProtectedAndUnavailableFilesAndDeduplicatesPhysicalBytes()
    {
        string first = Create("first.mkv", 10);
        string second = Create("second.mkv", 10);
        string protectedPath = Create("protected.mkv", 20);
        var snapshots = new Dictionary<long, LibraryGeneralFileSnapshot>
        {
            [1] = Snapshot(1, first, 10, "same"),
            [2] = Snapshot(2, second, 10, "same"),
            [3] = Snapshot(3, protectedPath, 20, "protected", isProtected: true)
        };
        var service = Service((id, _) => snapshots.GetValueOrDefault(id));

        LibraryGeneralFileRemovalPreview preview = service.Preview(new[]
        {
            (1L, first), (2L, second), (3L, protectedPath), (4L, Path.Combine(_root, "missing.mkv"))
        }, DuplicateCleanupAction.RecycleBin);

        Assert.Equal(2, preview.Eligible.Count);
        Assert.Equal(10, preview.ExpectedReclaimableBytes);
        Assert.Equal(1, preview.ProtectedExcluded);
        Assert.Equal(1, preview.UnavailableExcluded);
        Assert.Single(preview.AffectedLocations);
    }

    [Fact]
    public async Task ExecutionRevalidatesProtectionAndIdentityAndAuditsEveryAttempt()
    {
        string validPath = Create("valid.mkv", 11);
        string protectedPath = Create("became-protected.mkv", 12);
        string changedPath = Create("changed.mkv", 13);
        var snapshots = new Dictionary<long, LibraryGeneralFileSnapshot>
        {
            [1] = Snapshot(1, validPath, 11, "a"),
            [2] = Snapshot(2, protectedPath, 12, "b"),
            [3] = Snapshot(3, changedPath, 13, "c")
        };
        var actions = new FakeActions(); var audit = new FakeAudit();
        var service = new LibraryGeneralFileRemovalService((id, _) => snapshots.GetValueOrDefault(id), audit, actions);
        LibraryGeneralFileRemovalPreview preview = service.Preview(snapshots.Values.Select(item => (item.FileId, item.FullPath)), DuplicateCleanupAction.RecycleBin);
        snapshots[2] = snapshots[2] with { IsProtected = true };
        snapshots[3] = snapshots[3] with { FileIdentity = "changed" };

        LibraryGeneralFileRemovalResult result = await service.ExecuteAsync(preview);

        Assert.Equal(1, result.Succeeded);
        Assert.Equal(2, result.Excluded);
        Assert.Equal(new[] { validPath }, actions.Recycled);
        Assert.Equal(3, audit.Entries.Count);
        Assert.Contains(audit.Entries, item => item.Message == LibraryGeneralFileRemovalService.ProtectedReason);
        Assert.Contains(audit.Entries, item => item.Message.Contains("identity", StringComparison.OrdinalIgnoreCase));
        Assert.Single(result.LocationsRequiringRescan);
    }

    [Fact]
    public async Task BatchStopsSafelyWhenCancellationIsRequested()
    {
        string first = Create("one.mkv", 1); string second = Create("two.mkv", 1);
        var snapshots = new Dictionary<long, LibraryGeneralFileSnapshot> { [1] = Snapshot(1, first, 1, "1"), [2] = Snapshot(2, second, 1, "2") };
        using var cancellation = new CancellationTokenSource();
        var actions = new FakeActions(() => cancellation.Cancel());
        var service = new LibraryGeneralFileRemovalService((id, _) => snapshots[id], new FakeAudit(), actions);
        LibraryGeneralFileRemovalPreview preview = service.Preview(snapshots.Values.Select(item => (item.FileId, item.FullPath)), DuplicateCleanupAction.RecycleBin);

        LibraryGeneralFileRemovalResult result = await service.ExecuteAsync(preview, cancellationToken: cancellation.Token);

        Assert.True(result.Cancelled);
        Assert.Equal(1, result.Succeeded);
        Assert.Single(actions.Recycled);
    }

    [Fact]
    public void CrossNavigationOnlyEnablesCatalogDestinationsPresentOnTheItem()
    {
        var item = new StorageReclamationPlanItem { FileId = 7, SourcePath = "movie.mkv", ExactGroupId = 2, VisualFamilyId = 9 };
        LibraryCrossNavigationState state = LibraryCrossNavigationState.For(item);
        Assert.True(state.Files);
        Assert.True(state.ExactDuplicates);
        Assert.False(state.VisualDuplicates);
        Assert.True(state.VisualFamily);
    }

    private LibraryGeneralFileRemovalService Service(Func<long, string, LibraryGeneralFileSnapshot?> resolver) =>
        new(resolver, new FakeAudit(), new FakeActions());

    private LibraryGeneralFileSnapshot Snapshot(long id, string path, long size, string identity, bool isProtected = false) =>
        new(id, path, _root, size, File.GetLastWriteTimeUtc(path), "volume", identity, IndexedFileAvailability.Present, isProtected);

    private string Create(string name, int bytes)
    {
        string path = Path.Combine(_root, name); File.WriteAllBytes(path, new byte[bytes]); return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private sealed class FakeAudit : ILibraryGeneralFileRemovalAudit
    {
        public List<LibraryGeneralFileRemovalAuditEntry> Entries { get; } = new();
        public void Append(LibraryGeneralFileRemovalAuditEntry entry) => Entries.Add(entry);
    }

    private sealed class FakeActions(Action? after = null) : ILibraryGeneralFileActions
    {
        public List<string> Recycled { get; } = new();
        public void Recycle(string path) { Recycled.Add(path); after?.Invoke(); }
        public void DeletePermanent(string path) => throw new NotSupportedException();
        public string Quarantine(string path, string quarantineRoot, long fileId) => throw new NotSupportedException();
    }
}
