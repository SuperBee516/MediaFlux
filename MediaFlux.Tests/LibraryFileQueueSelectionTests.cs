using MediaFlux.Services;
using MediaFlux.Services.LibraryCatalog;
using Xunit;

namespace MediaFlux.Tests;

public sealed class LibraryFileQueueSelectionTests
{
    [Fact]
    public async Task CatalogSelectionUsesPresentStableRowsAndDoesNotCheckFilesystem()
    {
        string root = Path.Combine(Path.GetTempPath(), "MediaFlux.QueueSelection", Guid.NewGuid().ToString("N"));
        string higherIdPath = Path.Combine(root, "higher-id.mkv");
        string lowerIdPath = Path.Combine(root, "lower-id.mkv");
        LibraryFileViewRecord lowerId = Record(9, lowerIdPath);
        LibraryFileViewRecord higherId = Record(42, higherIdPath);
        LibraryFileViewRecord unavailable = Record(4, Path.Combine(root, "missing.mkv"), IndexedFileAvailability.Missing);
        LibraryFileViewRecord invalidId = Record(0, Path.Combine(root, "invalid-id.mkv"));
        LibraryFileViewRecord invalidPath = Record(17, "");
        LibraryFileViewRecord duplicatePath = Record(50, lowerIdPath);

        LibraryFileQueueResult prepared = LibraryFileQueueSelection.PreparePresentCatalogSelection(
            new[] { higherId, unavailable, duplicatePath, lowerId, invalidId, invalidPath, higherId });

        Assert.False(File.Exists(higherIdPath));
        Assert.False(File.Exists(lowerIdPath));
        Assert.Equal(new[] { lowerIdPath, higherIdPath }, prepared.AvailablePaths);
        Assert.Equal(3, prepared.UnavailableCount);
        Assert.False(prepared.Dispatched);

        var submitted = new List<string[]>();
        LibraryFileQueueResult dispatched = await LibraryFileQueueSelection.DispatchPresentCatalogSelectionAsync(
            new[] { higherId, unavailable, duplicatePath, lowerId, invalidId, invalidPath, higherId },
            paths =>
            {
                submitted.Add(paths.ToArray());
                return Task.CompletedTask;
            });

        Assert.True(dispatched.Dispatched);
        Assert.Equal(new[] { lowerIdPath, higherIdPath }, Assert.Single(submitted));
        Assert.Equal(3, dispatched.UnavailableCount);
    }

    [Fact]
    public async Task CatalogDispatchAwaitsTheNormalAsyncHandoffAndSkipsEmptyEligibility()
    {
        LibraryFileViewRecord present = Record(1, Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "present.mkv"));
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<LibraryFileQueueResult> dispatch = LibraryFileQueueSelection.DispatchPresentCatalogSelectionAsync(
            new[] { present },
            async _ =>
            {
                started.SetResult(true);
                await release.Task;
            });

        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(dispatch.IsCompleted);
        release.SetResult(true);
        Assert.True((await dispatch).Dispatched);

        bool invoked = false;
        LibraryFileQueueResult empty = await LibraryFileQueueSelection.DispatchPresentCatalogSelectionAsync(
            new[] { Record(2, "", IndexedFileAvailability.Present) },
            _ =>
            {
                invoked = true;
                return Task.CompletedTask;
            });
        Assert.False(empty.Dispatched);
        Assert.Empty(empty.AvailablePaths);
        Assert.False(invoked);
    }

    [Fact]
    public async Task CatalogDispatchPropagatesHandoffFailuresForTheUiToReport()
    {
        LibraryFileViewRecord present = Record(3, Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "failure.mkv"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            LibraryFileQueueSelection.DispatchPresentCatalogSelectionAsync(
                new[] { present },
                _ => Task.FromException(new InvalidOperationException("import failed"))));
    }

    private static LibraryFileViewRecord Record(
        long id,
        string path,
        IndexedFileAvailability availability = IndexedFileAvailability.Present) =>
        new(
            id,
            Path.GetFileName(path),
            path,
            Path.GetDirectoryName(path) ?? "",
            0,
            DateTime.UtcNow,
            availability,
            "",
            "",
            null,
            null,
            null,
            null,
            LibraryProbeStatus.Pending,
            "",
            false);
}
