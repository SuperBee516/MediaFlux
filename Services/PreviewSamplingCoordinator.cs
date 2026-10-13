using System.Diagnostics;

namespace MediaFlux.Services;

/// <summary>One advisory encoder slot. Production cancels and drains it before probing or encoding.</summary>
internal sealed class PreviewSamplingCoordinator
{
    internal static PreviewSamplingCoordinator Shared { get; } = new();
    private readonly object _sync = new();
    private readonly SemaphoreSlim _slot = new(1, 1);
    private int _production;
    private CancellationTokenSource? _preview;
    private TaskCompletionSource? _drained;

    internal async Task<T> PreviewAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken token)
    {
        await _slot.WaitAsync(token).ConfigureAwait(false);
        CancellationTokenSource? cancellation = null;
        try
        {
            lock (_sync)
            {
                if (_production > 0) throw new PreviewUnavailableException("Production encoding owns the media tools.");
                cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
                _preview = cancellation;
                _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            try { return await work(cancellation.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            { throw new PreviewUnavailableException("Preview yielded to production encoding."); }
        }
        finally
        {
            lock (_sync)
            {
                if (cancellation is not null)
                {
                    _preview = null;
                    _drained!.TrySetResult();
                    cancellation.Dispose();
                }
            }
            _slot.Release();
        }
    }

    internal async Task<IDisposable> EnterProductionAsync(CancellationToken token)
    {
        Task drain;
        AutomaticEncodePerformanceInstrumentation? instrumentation = AutomaticEncodePerformanceInstrumentation.Current;
        long drainStartedAt = instrumentation is null ? 0 : Stopwatch.GetTimestamp();
        bool previewActive = false;
        bool cancellationRequested = false;
        lock (_sync)
        {
            _production++;
            if (_preview is not null)
            {
                previewActive = true;
                _preview.Cancel();
                cancellationRequested = true;
            }
            drain = _drained?.Task ?? Task.CompletedTask;
        }
        var lease = new ProductionLease(this);
        bool drained = false;
        try
        {
            await drain.WaitAsync(token).ConfigureAwait(false);
            drained = true;
            return lease;
        }
        catch
        {
            lease.Dispose();
            throw;
        }
        finally
        {
            if (instrumentation is not null)
                instrumentation.RecordPreviewDrain(previewActive, cancellationRequested, drainStartedAt, drained);
        }
    }

    private sealed class ProductionLease(PreviewSamplingCoordinator owner) : IDisposable
    {
        private PreviewSamplingCoordinator? _owner = owner;
        public void Dispose()
        {
            var value = Interlocked.Exchange(ref _owner, null);
            if (value is not null) lock (value._sync) value._production--;
        }
    }
}

internal sealed class PreviewUnavailableException(string message) : InvalidOperationException(message);
