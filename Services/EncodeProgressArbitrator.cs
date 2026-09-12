namespace MediaFlux.Services;

public enum EncodeProgressBasis
{
    Timestamp,
    MeasuredFrames,
    DerivedCfrFrames,
    Indeterminate
}

/// <summary>Chooses one stable progress basis for a single FFmpeg encode.</summary>
internal sealed class EncodeProgressArbitrator
{
    internal const int TimestampStallUpdateThreshold = 3;
    private readonly TimeSpan _duration;
    private readonly long? _totalFrames;
    private readonly double? _frameRate;
    private readonly bool _cfrFallbackEligible;
    private double _lastTimestamp = -1;
    private long _lastFrame;
    private int _stalledUpdates;
    private double _highestPercent;
    private EncodeProgressBasis _basis = EncodeProgressBasis.Timestamp;

    internal EncodeProgressArbitrator(TimeSpan duration, long? totalFrames, double? frameRate, bool cfrFallbackEligible)
    {
        _duration = duration;
        _totalFrames = totalFrames is > 0 ? totalFrames : null;
        _frameRate = frameRate is > 0 && double.IsFinite(frameRate.Value) ? frameRate : null;
        _cfrFallbackEligible = cfrFallbackEligible && _duration > TimeSpan.Zero && _frameRate is > 0;
    }

    internal bool CfrFallbackEligible => _cfrFallbackEligible;
    internal EncodeProgressBasis Basis => _basis;

    internal EncodeProgressArbitrationResult Update(double? timestampSeconds, long? frame, double fps, double speed, double bitrateKbps, long? outputSizeBytes)
    {
        bool timestampValid = timestampSeconds is double timestamp && timestamp >= 0 && double.IsFinite(timestamp);
        bool frameAdvancing = frame is long currentFrame && currentFrame > 0 && currentFrame > _lastFrame;
        if (frame is long frameValue && frameValue > 0)
            _lastFrame = Math.Max(_lastFrame, frameValue);

        if (timestampValid)
        {
            double timestampValue = timestampSeconds ?? 0;
            if (_lastTimestamp >= 0 && timestampValue <= _lastTimestamp + 0.000001 && frameAdvancing)
                _stalledUpdates++;
            else if (timestampValue > _lastTimestamp + 0.000001)
                _stalledUpdates = 0;
            _lastTimestamp = Math.Max(_lastTimestamp, timestampValue);
        }

        bool stalled = _stalledUpdates >= TimestampStallUpdateThreshold && frameAdvancing;
        if ((!timestampValid && frameAdvancing || stalled) && (_totalFrames is > 0 || _cfrFallbackEligible))
            _basis = _totalFrames is > 0 ? EncodeProgressBasis.MeasuredFrames : EncodeProgressBasis.DerivedCfrFrames;
        else if (_basis is EncodeProgressBasis.Timestamp && !timestampValid && !(_totalFrames is > 0 || _cfrFallbackEligible))
            _basis = EncodeProgressBasis.Indeterminate;

        double? percent = null;
        double mediaSeconds = timestampValid ? timestampSeconds ?? 0 : 0;
        if (_basis == EncodeProgressBasis.Timestamp && timestampValid && _duration > TimeSpan.Zero)
            percent = (timestampSeconds ?? 0) / _duration.TotalSeconds * 100;
        else if (_basis == EncodeProgressBasis.MeasuredFrames && frame is > 0 && _totalFrames is > 0)
        {
            percent = frame.Value / (double)_totalFrames.Value * 100;
            mediaSeconds = _frameRate is > 0 ? frame.Value / _frameRate.Value : mediaSeconds;
        }
        else if (_basis == EncodeProgressBasis.DerivedCfrFrames && frame is > 0 && _frameRate is > 0 && _duration > TimeSpan.Zero)
        {
            mediaSeconds = frame.Value / _frameRate.Value;
            percent = mediaSeconds / _duration.TotalSeconds * 100;
        }

        if (percent is double candidate && double.IsFinite(candidate))
            _highestPercent = Math.Clamp(Math.Max(_highestPercent, candidate), 0, 100);

        return new(
            mediaSeconds,
            _highestPercent,
            fps,
            speed,
            bitrateKbps,
            frame,
            outputSizeBytes,
            _basis,
            stalled,
            timestampValid ? timestampSeconds : null,
            _totalFrames ?? (_cfrFallbackEligible ? (long?)Math.Round(_duration.TotalSeconds * _frameRate!.Value, MidpointRounding.AwayFromZero) : null));
    }
}

internal readonly record struct EncodeProgressArbitrationResult(
    double MediaSeconds, double Percent, double Fps, double Speed, double BitrateKbps,
    long? EncodedFrames, long? OutputSizeBytes, EncodeProgressBasis Basis,
    bool TimestampStalled, double? TimestampSeconds, long? TotalFrames);
