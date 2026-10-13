namespace MediaFlux;

// Owned by the Files page UI thread. Each request owns its cancellation source
// until its worker has finished, including requests superseded by a newer edit.
internal sealed class LibraryFilesRequestCoordinator : IDisposable
{
    private CancellationTokenSource? _active;
    private long _generation;
    private bool _disposed;

    internal readonly record struct Request(long Generation, CancellationTokenSource Source)
    {
        internal CancellationToken Token => Source.Token;
    }

    internal Request Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Invalidate();
        var source = new CancellationTokenSource();
        _active = source;
        return new Request(_generation, source);
    }

    internal void Invalidate()
    {
        if (_disposed) return;
        _generation++;
        _active?.Cancel();
        _active = null;
    }

    internal bool IsCurrent(Request request) =>
        !_disposed && _generation == request.Generation &&
        ReferenceEquals(_active, request.Source) && !request.Source.IsCancellationRequested;

    internal void Complete(Request request)
    {
        if (ReferenceEquals(_active, request.Source))
            _active = null;
        request.Source.Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        Invalidate();
        _disposed = true;
    }
}
