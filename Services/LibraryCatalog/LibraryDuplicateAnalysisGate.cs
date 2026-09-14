namespace MediaFlux.Services.LibraryCatalog;

public enum LibraryDuplicateAnalyzer
{
    Exact,
    Visual
}

public sealed class LibraryDuplicateAnalysisGate : IDisposable
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly object _sync = new();
    private LibraryDuplicateAnalyzer? _activeAnalyzer;
    private bool _disposed;

    public LibraryDuplicateAnalyzer? ActiveAnalyzer
    {
        get { lock (_sync) return _activeAnalyzer; }
    }

    public async Task<Lease> AcquireAsync(LibraryDuplicateAnalyzer analyzer, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        lock (_sync)
        {
            if (_disposed)
            {
                _semaphore.Release();
                throw new ObjectDisposedException(nameof(LibraryDuplicateAnalysisGate));
            }
            _activeAnalyzer = analyzer;
        }
        return new Lease(this, analyzer);
    }

    private void Release(LibraryDuplicateAnalyzer analyzer)
    {
        lock (_sync)
        {
            if (_activeAnalyzer == analyzer)
                _activeAnalyzer = null;
        }
        _semaphore.Release();
    }

    public void Dispose()
    {
        lock (_sync) _disposed = true;
        _semaphore.Dispose();
    }

    public sealed class Lease : IDisposable
    {
        private readonly LibraryDuplicateAnalysisGate _owner;
        private readonly LibraryDuplicateAnalyzer _analyzer;
        private int _released;

        internal Lease(LibraryDuplicateAnalysisGate owner, LibraryDuplicateAnalyzer analyzer)
        {
            _owner = owner;
            _analyzer = analyzer;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                _owner.Release(_analyzer);
        }
    }
}
