namespace MediaFlux.Services.LibraryCatalog
{
    internal sealed class AsyncPauseGate
    {
        private volatile TaskCompletionSource<bool> _resume = CompletedSource();

        public bool IsPaused => !_resume.Task.IsCompleted;

        public void Pause()
        {
            while (true)
            {
                TaskCompletionSource<bool> current = _resume;
                if (!current.Task.IsCompleted)
                    return;
                var paused = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                if (Interlocked.CompareExchange(ref _resume, paused, current) == current)
                    return;
            }
        }

        public void Resume() => _resume.TrySetResult(true);

        public Task WaitAsync(CancellationToken cancellationToken) =>
            _resume.Task.WaitAsync(cancellationToken);

        private static TaskCompletionSource<bool> CompletedSource()
        {
            var source = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            source.SetResult(true);
            return source;
        }
    }
}
