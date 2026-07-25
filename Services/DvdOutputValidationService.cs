using MediaFlux.Models;

namespace MediaFlux.Services
{
    public interface IDvdOutputValidationService
    {
        Task<DvdOutputValidationResult> ValidateAsync(
            string outputPath,
            DvdTitleCandidate sourceCandidate,
            int expectedAudioStreams,
            int expectedSubtitleStreams,
            CancellationToken cancellationToken = default);
    }

    public sealed class DvdOutputValidationService : IDvdOutputValidationService
    {
        private readonly IMediaProbeService _probeService;

        public DvdOutputValidationService(IMediaProbeService probeService)
        {
            _probeService = probeService ??
                throw new ArgumentNullException(nameof(probeService));
        }

        public async Task<DvdOutputValidationResult> ValidateAsync(
            string outputPath,
            DvdTitleCandidate sourceCandidate,
            int expectedAudioStreams,
            int expectedSubtitleStreams,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(sourceCandidate);
            if (string.IsNullOrWhiteSpace(outputPath) || !File.Exists(outputPath))
                return Failed("The output file was not created.");

            long length;
            try
            {
                length = new FileInfo(outputPath).Length;
            }
            catch (Exception ex)
            {
                return Failed($"The output file could not be inspected: {ex.Message}");
            }

            if (length <= 0)
                return Failed("The output file is empty.");

            MediaProbeResult probe = await _probeService.ProbeAsync(
                outputPath,
                cancellationToken).ConfigureAwait(false);
            if (!probe.Success)
                return Failed($"FFprobe could not validate the completed output: {probe.ErrorMessage}", probe);

            int videoStreams = probe.Streams.Count(stream =>
                stream.CodecType.Equals("video", StringComparison.OrdinalIgnoreCase));
            if (videoStreams == 0)
                return Failed("The completed output does not contain a video stream.", probe);

            int audioStreams = probe.Streams.Count(stream =>
                stream.CodecType.Equals("audio", StringComparison.OrdinalIgnoreCase));
            if (audioStreams < expectedAudioStreams)
            {
                return Failed(
                    $"The completed output contains {audioStreams} audio stream(s), but " +
                    $"{expectedAudioStreams} were selected.",
                    probe);
            }

            int subtitleStreams = probe.Streams.Count(stream =>
                stream.CodecType.Equals("subtitle", StringComparison.OrdinalIgnoreCase));
            if (subtitleStreams < expectedSubtitleStreams)
            {
                return Failed(
                    $"The completed output contains {subtitleStreams} subtitle stream(s), but " +
                    $"{expectedSubtitleStreams} were selected.",
                    probe);
            }

            if (sourceCandidate.CombinedDurationSeconds > 0)
            {
                if (probe.DurationSeconds is not > 0)
                    return Failed("FFprobe could not determine the completed output duration.", probe);

                double minimumPlausibleDuration = sourceCandidate.CombinedDurationSeconds * 0.90;
                if (probe.DurationSeconds.Value < minimumPlausibleDuration)
                {
                    return Failed(
                        $"The output duration ({FormatDuration(probe.DurationSeconds.Value)}) is " +
                        $"suspiciously shorter than the selected DVD title " +
                        $"({FormatDuration(sourceCandidate.CombinedDurationSeconds)}).",
                        probe);
                }
            }

            return new DvdOutputValidationResult
            {
                Success = true,
                ProbeResult = probe
            };
        }

        private static DvdOutputValidationResult Failed(
            string message,
            MediaProbeResult? probe = null)
        {
            return new DvdOutputValidationResult
            {
                Success = false,
                ErrorMessage = message,
                ProbeResult = probe
            };
        }

        private static string FormatDuration(double seconds)
        {
            return TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(@"hh\:mm\:ss");
        }
    }
}
