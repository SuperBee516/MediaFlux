using System.Globalization;
using MediaFlux.Models;

namespace MediaFlux.Services;

internal sealed record RecoverableSourceBaselineResult(
    bool Success,
    RecoverableSourceBaseline? Baseline,
    string Reason)
{
    public static RecoverableSourceBaselineResult Failed(string reason) => new(false, null, reason);
}

/// <summary>
/// Measures the decodable video coverage of a source already proven to contain
/// bitstream corruption. It has no output artifact and cannot authorize a retry.
/// </summary>
internal sealed class RecoverableSourceBaselineService
{
    private readonly string _ffmpegPath;
    private readonly IMediaToolProcessRunner _runner;

    public RecoverableSourceBaselineService(string ffmpegPath, IMediaToolProcessRunner? runner = null)
    {
        _ffmpegPath = ffmpegPath;
        _runner = runner ?? new MediaToolProcessRunner();
    }

    public async Task<RecoverableSourceBaselineResult> MeasureAsync(
        string sourcePath,
        double advertisedDurationSeconds,
        CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || advertisedDurationSeconds <= 0 ||
            !double.IsFinite(advertisedDurationSeconds))
            return RecoverableSourceBaselineResult.Failed("Recoverable coverage requires a source path and authoritative advertised duration.");

        var progress = new DecodeProgress();
        try
        {
            MediaToolProcessResult process = await _runner.RunAsync(new MediaToolProcessRequest
            {
                FileName = _ffmpegPath,
                Arguments = new[]
                {
                    "-hide_banner", "-nostats", "-loglevel", "error", "-progress", "pipe:1",
                    "-fflags", "+genpts", "-i", sourcePath,
                    "-map", "0:v:0", "-an", "-sn", "-dn", "-fps_mode", "passthrough",
                    "-f", "null", "-"
                },
                Timeout = TimeSpan.FromMinutes(30),
                SendQuitOnCancellation = true,
                StandardOutputLineCallback = progress.Consume
            }, token).ConfigureAwait(false);

            if (process.ExitCode != 0 || process.TimedOut || !progress.ReachedEnd ||
                progress.DecodedFrames <= 0 || progress.TailPresentationSeconds <= 0)
                return RecoverableSourceBaselineResult.Failed(
                    $"Recoverable decode coverage was not proven: exit={process.ExitCode}; timed-out={process.TimedOut}; " +
                    $"end={progress.ReachedEnd}; frames={progress.DecodedFrames}; tail={progress.TailPresentationSeconds:0.###}s.");

            if (Math.Abs(progress.TailPresentationSeconds - advertisedDurationSeconds) > 0.75)
                return RecoverableSourceBaselineResult.Failed(
                    $"Recoverable decode coverage ended at {progress.TailPresentationSeconds:0.###}s, " +
                    $"which does not reach the advertised {advertisedDurationSeconds:0.###}s presentation tail.");

            return new RecoverableSourceBaselineResult(true,
                new RecoverableSourceBaseline(progress.DecodedFrames, progress.TailPresentationSeconds,
                    $"full tolerant decode reached EOF; frames={progress.DecodedFrames}; tail={progress.TailPresentationSeconds:0.###}s; advertised={advertisedDurationSeconds:0.###}s"),
                "Recoverable video coverage was measured through the advertised presentation tail.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return RecoverableSourceBaselineResult.Failed($"Recoverable decode coverage measurement failed: {ex.Message}");
        }
    }

    private sealed class DecodeProgress
    {
        public long DecodedFrames { get; private set; }
        public double TailPresentationSeconds { get; private set; }
        public bool ReachedEnd { get; private set; }

        public void Consume(string line)
        {
            int separator = line.IndexOf('=');
            if (separator <= 0) return;
            string key = line[..separator];
            string value = line[(separator + 1)..].Trim();
            if (key.Equals("frame", StringComparison.OrdinalIgnoreCase) &&
                long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long frames))
                DecodedFrames = Math.Max(DecodedFrames, frames);
            else if (key.Equals("out_time", StringComparison.OrdinalIgnoreCase) &&
                TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out TimeSpan tail))
                TailPresentationSeconds = Math.Max(TailPresentationSeconds, tail.TotalSeconds);
            else if (key.Equals("progress", StringComparison.OrdinalIgnoreCase) &&
                value.Equals("end", StringComparison.OrdinalIgnoreCase))
                ReachedEnd = true;
        }
    }
}
