namespace MediaFlux.Services;

/// <summary>
/// Keeps delayed progress callbacks from an earlier FFmpeg attempt from being
/// applied after a bounded recovery attempt has started.
/// </summary>
internal sealed class EncodeProgressAttemptTracker
{
    internal int CurrentAttempt { get; private set; }

    internal EncodeProgressAttemptDisposition Observe(int attempt)
    {
        if (attempt <= 0)
            return EncodeProgressAttemptDisposition.IgnoreStale;

        if (attempt < CurrentAttempt)
            return EncodeProgressAttemptDisposition.IgnoreStale;

        if (attempt > CurrentAttempt)
        {
            CurrentAttempt = attempt;
            return EncodeProgressAttemptDisposition.AcceptAndReset;
        }

        return EncodeProgressAttemptDisposition.Accept;
    }
}

internal enum EncodeProgressAttemptDisposition
{
    IgnoreStale,
    Accept,
    AcceptAndReset
}
