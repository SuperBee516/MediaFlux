using MediaFlux.Models;

namespace MediaFlux.Services
{
    public static class OutputContainerPolicy
    {
        private static readonly HashSet<string> Mp4AudioCodecs = new(
            new[] { "aac", "alac", "mp3", "ac3", "eac3" },
            StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> Mp4SubtitleCodecs = new(
            new[] { "mov_text", "tx3g", "webvtt" },
            StringComparer.OrdinalIgnoreCase);

        // FFmpeg's Matroska muxer accepts attachments, but not arbitrary AVMEDIA_TYPE_DATA
        // streams. Keep this capability explicit so mapping and container selection agree.
        public static bool SupportsGenericDataStreams(OutputContainer container) => false;

        public static OutputContainerSelection ParseSelection(string? value) =>
            Enum.TryParse(value, true, out OutputContainerSelection parsed)
                ? parsed
                : OutputContainerSelection.Mp4;

        public static OutputContainerDecision Decide(
            OutputContainerSelection requested,
            MediaProbeResult source,
            EncodingInputSource input,
            EncodingService.StreamMapMode mapMode,
            bool copySubtitles = true,
            bool copyDataStreams = true,
            bool copyAttachments = true,
            bool audioWillBeTranscoded = false)
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(input);

            IReadOnlyList<MediaProbeStreamInfo> selectedAudio = SelectStreams(source, input.AudioStreamIndexes, input.HasExplicitStreamSelection, "audio",
                mapMode == EncodingService.StreamMapMode.FirstAudioOnly ? 1 : int.MaxValue);
            IReadOnlyList<MediaProbeStreamInfo> selectedSubtitles = copySubtitles
                ? SelectStreams(source, input.SubtitleStreamIndexes, input.HasExplicitStreamSelection, "subtitle", int.MaxValue)
                : Array.Empty<MediaProbeStreamInfo>();
            int attachmentCount = copyAttachments && !input.HasExplicitStreamSelection ? Count(source, "attachment") : 0;

            var warnings = new List<string>();
            if (!audioWillBeTranscoded)
            {
                string[] incompatibleAudio = selectedAudio
                    .Where(stream => !Mp4AudioCodecs.Contains(stream.CodecName))
                    .Select(stream => string.IsNullOrWhiteSpace(stream.CodecName) ? "unknown" : stream.CodecName)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (incompatibleAudio.Length > 0)
                    warnings.Add($"copied audio codec(s) not conservatively supported by MP4: {string.Join(", ", incompatibleAudio)}");
            }

            string[] incompatibleSubtitles = selectedSubtitles
                .Where(stream => !Mp4SubtitleCodecs.Contains(stream.CodecName))
                .Select(stream => string.IsNullOrWhiteSpace(stream.CodecName) ? "unknown" : stream.CodecName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (incompatibleSubtitles.Length > 0)
                warnings.Add($"subtitle codec(s) that MP4 cannot preserve by stream copy: {string.Join(", ", incompatibleSubtitles)}");
            if (attachmentCount > 0)
                warnings.Add($"{attachmentCount} attachment stream(s) that MP4 will not preserve");

            OutputContainer resolved = requested switch
            {
                OutputContainerSelection.Matroska => OutputContainer.Matroska,
                OutputContainerSelection.Auto when warnings.Count > 0 => OutputContainer.Matroska,
                _ => OutputContainer.Mp4
            };
            bool matroska = resolved == OutputContainer.Matroska;
            string reason = requested == OutputContainerSelection.Auto
                ? matroska
                    ? $"Auto selected Matroska to preserve {warnings[0]}."
                    : "Auto selected MP4 because the requested streams are MP4-compatible."
                : requested == OutputContainerSelection.Matroska
                    ? "Matroska was selected explicitly for broad stream preservation."
                    : warnings.Count == 0
                        ? "MP4 was selected explicitly and the requested streams are compatible."
                        : "MP4 was selected explicitly; incompatible streams will be omitted after confirmation.";

            return new OutputContainerDecision
            {
                Requested = requested,
                Resolved = resolved,
                Reason = reason,
                CompatibilityWarnings = warnings,
                CopySubtitles = copySubtitles && (matroska || incompatibleSubtitles.Length == 0),
                // Neither supported output container can safely mux arbitrary FFmpeg data
                // streams. They are intentionally omitted rather than driving Auto to MKV.
                CopyDataStreams = copyDataStreams &&
                    SupportsGenericDataStreams(resolved) &&
                    !input.HasExplicitStreamSelection,
                CopyAttachments = copyAttachments && matroska && !input.HasExplicitStreamSelection
            };
        }

        private static IReadOnlyList<MediaProbeStreamInfo> SelectStreams(
            MediaProbeResult source, IReadOnlyList<int> explicitIndexes, bool hasExplicitSelection, string type, int maximum)
        {
            IEnumerable<MediaProbeStreamInfo> streams = source.Streams.Where(s => IsType(s, type));
            if (hasExplicitSelection)
                streams = streams.Where(s => explicitIndexes.Contains(s.Index));
            return streams.Take(maximum).ToArray();
        }

        private static int Count(MediaProbeResult source, string type) =>
            source.Streams.Count(stream => IsType(stream, type));

        private static bool IsType(MediaProbeStreamInfo stream, string type) =>
            stream.CodecType.Equals(type, StringComparison.OrdinalIgnoreCase);
    }
}
