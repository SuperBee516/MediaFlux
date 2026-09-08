namespace MediaFlux.Services.LibraryCatalog;

/// <summary>Async, cancellation-aware facade for the persisted Library Overview projection.</summary>
public sealed class LibraryOverviewQueryService : IDisposable
{
    private readonly ILibraryOverviewCatalog _catalog;
    private readonly CancellationTokenSource _disposeCancellation = new();
    private readonly object _lifecycleGate = new();
    private TaskCompletionSource? _drained;
    private int _activeQueries;
    private bool _disposed;

    public LibraryOverviewQueryService(ILibraryOverviewCatalog catalog) => _catalog = catalog;

    public Task<LibraryOverviewSnapshot> LoadAsync(int metadataVersion, CancellationToken cancellationToken = default)
        => QueryAsync(() => _catalog.GetOverviewSnapshot(metadataVersion), cancellationToken);

    public Task<IReadOnlyList<LibraryOverviewScanHistoryEntry>> LoadHistoryAsync(
        int limit = 365, CancellationToken cancellationToken = default)
        => QueryAsync(() => _catalog.GetOverviewScanHistory(limit), cancellationToken);

    private async Task<T> QueryAsync<T>(Func<T> query, CancellationToken cancellationToken)
    {
        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _activeQueries++;
        }
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCancellation.Token);
        try
        {
            linked.Token.ThrowIfCancellationRequested();
            T result = await Task.Run(query, linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            return result;
        }
        finally
        {
            lock (_lifecycleGate)
            {
                if (--_activeQueries == 0)
                    _drained?.TrySetResult();
            }
        }
    }

    public void Dispose()
    {
        Task? drainTask;
        lock (_lifecycleGate)
        {
            if (_disposed) return;
            _disposed = true;
            drainTask = _activeQueries == 0 ? null : (_drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }
        _disposeCancellation.Cancel();
        drainTask?.GetAwaiter().GetResult();
        _disposeCancellation.Dispose();
    }
}
