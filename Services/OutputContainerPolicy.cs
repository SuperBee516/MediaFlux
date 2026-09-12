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
        // These codecs have a well-established FFmpeg decode-to-AAC path in
        // MediaFlux. Unknown codecs remain fail-closed rather than assuming
        // the configured FFmpeg can decode them.
        private static readonly HashSet<string> AacTranscodeSourceCodecs = new(
            new[] { "dts", "dca", "truehd", "mlp", "flac", "opus", "vorbis", "wma", "wmav1", "wmav2", "wmav3", "mp2", "mpa", "pcm_s16le", "pcm_s24le", "pcm_s32le", "pcm_f32le" },
            StringComparer.OrdinalIgnoreCase);

        // FFmpeg's Matroska muxer accepts attachments, but not arbitrary AVMEDIA_TYPE_DATA
        // streams. Keep this capability explicit so mapping and container selection agree.
        public static bool SupportsGenericDataStreams(OutputContainer container) => false;

        public static OutputContainerSelection ParseSelection(string? value) =>
            Enum.TryParse(value, true, out OutputContainerSelection parsed)
                ? parsed
                : OutputContainerSelection.Mp4;

        public static bool CanProceedAutomatically(OutputContainerDecision decision, ContainerCompatibilityPolicy policy) => policy switch
        {
            ContainerCompatibilityPolicy.Intelligent => !decision.HasUnsupportedMeaningfulStreams,
            ContainerCompatibilityPolicy.AlwaysAsk => !decision.RequiresConfirmation,
            ContainerCompatibilityPolicy.Strict => decision.StreamPlans.All(plan => plan.Action == StreamCompatibilityAction.Copy),
            _ => false
        };

        public static string DescribeBlockingStreams(OutputContainerDecision decision)
        {
            ArgumentNullException.ThrowIfNull(decision);
            StreamCompatibilityPlan[] blocking = decision.StreamPlans
                .Where(plan => plan.Action == StreamCompatibilityAction.Unsupported &&
                    (plan.StreamType.Equals("audio", StringComparison.OrdinalIgnoreCase) ||
                     plan.StreamType.Equals("video", StringComparison.OrdinalIgnoreCase) ||
                     plan.StreamType.Equals("subtitle", StringComparison.OrdinalIgnoreCase)))
                .ToArray();
            return blocking.Length == 0
                ? "No requested meaningful streams are blocked."
                : string.Join("; ", blocking.Select(plan =>
                    $"stream={plan.StreamIndex}; type={plan.StreamType}; codec={DisplayCodec(plan.Codec)}; requested={plan.RequestedAction}; target={decision.Resolved}; reason={plan.Reason}; decision={plan.Action}"));
        }

        /// <summary>Returns the existing plan with one failed text subtitle omitted.</summary>
        public static OutputContainerDecision ExcludeFailedTextSubtitle(
            OutputContainerDecision decision, int streamIndex, string diagnostic)
        {
            ArgumentNullException.ThrowIfNull(decision);
            StreamCompatibilityPlan[] plans = decision.StreamPlans.Select(plan =>
                plan.StreamIndex == streamIndex &&
                plan.StreamType.Equals("subtitle", StringComparison.OrdinalIgnoreCase) &&
                plan.Action is StreamCompatibilityAction.Copy or StreamCompatibilityAction.Transcode &&
                (IsTextSubtitleCodec(plan.Codec) || string.Equals(plan.TargetCodec, "mov_text", StringComparison.OrdinalIgnoreCase))
                    ? plan with { Action = StreamCompatibilityAction.Omit, Reason = diagnostic }
                    : plan).ToArray();
            if (!plans.Any(plan => plan.StreamIndex == streamIndex && plan.Action == StreamCompatibilityAction.Omit))
                throw new InvalidOperationException("Only a planned text subtitle may be excluded.");
            return new OutputContainerDecision
            {
                Requested = decision.Requested,
                Resolved = decision.Resolved,
                Reason = decision.Reason,
                CompatibilityWarnings = decision.CompatibilityWarnings
                    .Append($"subtitle stream {streamIndex} was excluded because its text conversion failed validation")
                    .ToArray(),
                StreamPlans = plans,
                CopySubtitles = plans.Any(plan => plan.StreamType.Equals("subtitle", StringComparison.OrdinalIgnoreCase) &&
                    plan.Action is StreamCompatibilityAction.Copy or StreamCompatibilityAction.Transcode),
                CopyDataStreams = decision.CopyDataStreams,
                CopyAttachments = decision.CopyAttachments
            };
        }

        /// <summary>Changes exactly one copied audio stream to the normal safe recovery codec.</summary>
        public static OutputContainerDecision RecoverCopiedAudio(OutputContainerDecision decision, int streamIndex)
        {
            ArgumentNullException.ThrowIfNull(decision);
            StreamCompatibilityPlan[] plans = decision.StreamPlans.Select(plan =>
                plan.StreamIndex == streamIndex &&
                plan.StreamType.Equals("audio", StringComparison.OrdinalIgnoreCase) &&
                plan.Action == StreamCompatibilityAction.Copy
                    ? plan with
                    {
                        Action = StreamCompatibilityAction.Transcode,
                        TargetCodec = SafeAudioRecoveryCodec(decision.Resolved),
                        Reason = "Source audio decode corruption was detected; the affected stream will be transcoded once for recovery."
                    }
                    : plan).ToArray();
            if (!plans.Any(plan => plan.StreamIndex == streamIndex && plan.StreamType.Equals("audio", StringComparison.OrdinalIgnoreCase) && plan.Action == StreamCompatibilityAction.Transcode))
                throw new InvalidOperationException("Only a copied audio stream may use audio corruption recovery.");
            return new OutputContainerDecision
            {
                Requested = decision.Requested,
                Resolved = decision.Resolved,
                Reason = decision.Reason,
                CompatibilityWarnings = decision.CompatibilityWarnings.Append($"audio stream {streamIndex} will be transcoded once because source decode corruption was detected").ToArray(),
                CopySubtitles = decision.CopySubtitles,
                CopyDataStreams = decision.CopyDataStreams,
                CopyAttachments = decision.CopyAttachments,
                StreamPlans = plans
            };
        }

        public static string SafeAudioRecoveryCodec(OutputContainer container) => "aac";

        /// <summary>
        /// Describes the effective audio plan produced by the command builder when
        /// a global AAC selection is required. This is intentionally explicit so
        /// validation can describe the artifact that was actually generated.
        /// </summary>
        public static OutputContainerDecision ResolveEffectiveGlobalAacAudioPlan(
            OutputContainerDecision decision,
            bool globalAacRequired)
        {
            ArgumentNullException.ThrowIfNull(decision);
            if (!globalAacRequired)
                return decision;

            StreamCompatibilityPlan[] plans = decision.StreamPlans.Select(plan =>
                plan.StreamType.Equals("audio", StringComparison.OrdinalIgnoreCase) &&
                plan.Action is StreamCompatibilityAction.Copy or StreamCompatibilityAction.Transcode
                    ? plan with
                    {
                        Action = StreamCompatibilityAction.Transcode,
                        TargetCodec = "aac",
                        Reason = "The effective sample command applies AAC to every selected audio stream."
                    }
                    : plan).ToArray();

            return new OutputContainerDecision
            {
                Requested = decision.Requested,
                Resolved = decision.Resolved,
                Reason = decision.Reason,
                CompatibilityWarnings = decision.CompatibilityWarnings,
                CopySubtitles = decision.CopySubtitles,
                CopyDataStreams = decision.CopyDataStreams,
                CopyAttachments = decision.CopyAttachments,
                StreamPlans = plans
            };
        }

        private static bool IsTextSubtitleCodec(string codec) =>
            codec.Equals("ass", StringComparison.OrdinalIgnoreCase) ||
            codec.Equals("ssa", StringComparison.OrdinalIgnoreCase) ||
            codec.Equals("mov_text", StringComparison.OrdinalIgnoreCase) ||
            codec.Equals("tx3g", StringComparison.OrdinalIgnoreCase) ||
            codec.Equals("webvtt", StringComparison.OrdinalIgnoreCase) ||
            codec.Equals("subrip", StringComparison.OrdinalIgnoreCase) ||
            codec.Equals("srt", StringComparison.OrdinalIgnoreCase);

        public static OutputContainerDecision Decide(
            OutputContainerSelection requested,
            MediaProbeResult source,
            EncodingInputSource input,
            EncodingService.StreamMapMode mapMode,
            bool copySubtitles = true,
            bool copyDataStreams = true,
            bool copyAttachments = true,
            bool audioWillBeTranscoded = false,
            ContainerCompatibilityPolicy compatibilityPolicy = ContainerCompatibilityPolicy.Intelligent)
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(input);

            IReadOnlyList<MediaProbeStreamInfo> selectedAudio = SelectStreams(source, input.AudioStreamIndexes, input.HasExplicitStreamSelection, "audio",
                mapMode == EncodingService.StreamMapMode.FirstAudioOnly ? 1 : int.MaxValue);
            IReadOnlyList<MediaProbeStreamInfo> selectedSubtitles = copySubtitles
                ? SelectStreams(source, input.SubtitleStreamIndexes, input.HasExplicitStreamSelection, "subtitle", int.MaxValue)
                : Array.Empty<MediaProbeStreamInfo>();
            int attachmentCount = copyAttachments &&
                requested != OutputContainerSelection.Matroska &&
                !input.HasExplicitStreamSelection
                ? Count(source, "attachment")
                : 0;

            bool explicitMp4 = requested == OutputContainerSelection.Mp4;
            var warnings = new List<string>();
            var plans = new List<StreamCompatibilityPlan>();
            int audioOutputOrdinal = 0;
            for (int selectedIndex = 0; selectedIndex < selectedAudio.Count; selectedIndex++)
            {
                MediaProbeStreamInfo stream = selectedAudio[selectedIndex];
                int sourceOrdinal = source.Streams.TakeWhile(candidate => candidate.Index != stream.Index).Count(candidate => IsType(candidate, "audio"));
                if (audioWillBeTranscoded)
                {
                    plans.Add(AudioPlan(stream, sourceOrdinal, audioOutputOrdinal++, StreamCompatibilityAction.Transcode,
                        "Selected audio will be converted to AAC by the requested audio layout.", "aac", "transcode"));
                }
                else if (!explicitMp4 || Mp4AudioCodecs.Contains(stream.CodecName))
                {
                    plans.Add(AudioPlan(stream, sourceOrdinal, audioOutputOrdinal++, StreamCompatibilityAction.Copy, "Selected audio is retained."));
                }
                else if (AacTranscodeSourceCodecs.Contains(stream.CodecName))
                {
                    plans.Add(AudioPlan(stream, sourceOrdinal, audioOutputOrdinal++, StreamCompatibilityAction.Transcode,
                        "MP4 cannot stream-copy this audio codec; MediaFlux will convert it to AAC.", "aac"));
                }
                else
                {
                    plans.Add(AudioPlan(stream, sourceOrdinal, null, StreamCompatibilityAction.Unsupported,
                        "MP4 audio codec cannot be safely copied and MediaFlux has no conservative AAC conversion path."));
                }
            }
            if (!audioWillBeTranscoded)
            {
                string[] incompatibleAudio = selectedAudio
                    .Where(stream => !Mp4AudioCodecs.Contains(stream.CodecName))
                    .Select(stream => string.IsNullOrWhiteSpace(stream.CodecName) ? "unknown" : stream.CodecName)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (incompatibleAudio.Length > 0 && requested != OutputContainerSelection.Matroska)
                    warnings.Add($"audio codec(s) requiring compatible conversion or a decision: {string.Join(", ", incompatibleAudio)}");
            }

            string[] incompatibleSubtitles = selectedSubtitles
                .Where(stream => !Mp4SubtitleCodecs.Contains(stream.CodecName))
                .Select(stream => string.IsNullOrWhiteSpace(stream.CodecName) ? "unknown" : stream.CodecName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            int subtitleOutputOrdinal = 0;
            for (int selectedIndex = 0; selectedIndex < selectedSubtitles.Count; selectedIndex++)
            {
                MediaProbeStreamInfo stream = selectedSubtitles[selectedIndex];
                int sourceOrdinal = source.Streams.TakeWhile(candidate => candidate.Index != stream.Index).Count(candidate => IsType(candidate, "subtitle"));
                bool supported = !explicitMp4 || Mp4SubtitleCodecs.Contains(stream.CodecName);
                bool ass = stream.CodecName.Equals("ass", StringComparison.OrdinalIgnoreCase) || stream.CodecName.Equals("ssa", StringComparison.OrdinalIgnoreCase);
                StreamCompatibilityAction action = supported ? StreamCompatibilityAction.Copy : ass ? StreamCompatibilityAction.Transcode : StreamCompatibilityAction.Unsupported;
                string streamReason = supported && explicitMp4 ? "Selected subtitle is MP4-compatible." : supported
                    ? "Selected subtitle will be copied into Matroska."
                    : ass
                    ? "ASS/SSA will be converted to mov_text; styling may be lost."
                    : "Subtitle cannot be safely represented in MP4.";
                plans.Add(new(stream.Index, "subtitle", stream.CodecName, action, streamReason, ass ? "mov_text" : null,
                    Language: stream.Language,
                    Title: stream.Tags.TryGetValue("title", out string? title) ? title : null,
                    Dispositions: stream.Dispositions,
                    SourceTypeOrdinal: sourceOrdinal,
                    OutputTypeOrdinal: action is StreamCompatibilityAction.Copy or StreamCompatibilityAction.Transcode ? subtitleOutputOrdinal++ : null));
            }
            if (incompatibleSubtitles.Length > 0 && requested != OutputContainerSelection.Matroska)
                warnings.Add($"subtitle codec(s) requiring conversion or a decision: {string.Join(", ", incompatibleSubtitles)}");
            if (attachmentCount > 0)
            {
                if (requested != OutputContainerSelection.Matroska)
                    warnings.Add($"{attachmentCount} attachment stream(s) that MP4 will not preserve");
                foreach (MediaProbeStreamInfo stream in source.Streams.Where(s => IsType(s, "attachment")))
                    plans.Add(new(stream.Index, "attachment", stream.CodecName, StreamCompatibilityAction.Omit, "MP4 does not preserve attachments."));
            }
            if (copyDataStreams)
                foreach (MediaProbeStreamInfo stream in source.Streams.Where(s => IsType(s, "data")))
                    plans.Add(new(stream.Index, "data", stream.CodecName, StreamCompatibilityAction.Omit, "Neither supported output container safely muxes this data stream."));

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
                    : "MP4 was selected explicitly; compatible conversions will be applied and unsupported requested streams will stop before encoding.";

            return new OutputContainerDecision
            {
                Requested = requested,
                Resolved = resolved,
                Reason = reason,
                CompatibilityWarnings = warnings,
                StreamPlans = plans,
                CopySubtitles = copySubtitles && (matroska || selectedSubtitles.Any(s =>
                    Mp4SubtitleCodecs.Contains(s.CodecName) || s.CodecName.Equals("ass", StringComparison.OrdinalIgnoreCase) || s.CodecName.Equals("ssa", StringComparison.OrdinalIgnoreCase))),
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
            {
                // FFmpeg maps explicit indexes in the supplied order. Keep the
                // plan in that exact order so per-output audio metadata and
                // dispositions target the stream FFmpeg actually produces.
                return explicitIndexes
                    .Select(index => source.Streams.FirstOrDefault(stream =>
                        stream.Index == index && IsType(stream, type)))
                    .Where(stream => stream is not null)
                    .Cast<MediaProbeStreamInfo>()
                    .Take(maximum)
                    .ToArray();
            }
            return streams.Take(maximum).ToArray();
        }

        private static int Count(MediaProbeResult source, string type) =>
            source.Streams.Count(stream => IsType(stream, type));

        private static bool IsType(MediaProbeStreamInfo stream, string type) =>
            stream.CodecType.Equals(type, StringComparison.OrdinalIgnoreCase);

        private static string DisplayCodec(string? codec) =>
            string.IsNullOrWhiteSpace(codec) ? "unknown" : codec;

        private static StreamCompatibilityPlan AudioPlan(
            MediaProbeStreamInfo stream, int sourceTypeOrdinal, int? outputTypeOrdinal,
            StreamCompatibilityAction action,
            string reason,
            string? targetCodec = null,
            string requestedAction = "copy") =>
            new(stream.Index, "audio", stream.CodecName, action, reason, targetCodec,
                requestedAction, stream.Language,
                stream.Tags.TryGetValue("title", out string? title) ? title : null,
                stream.Dispositions, sourceTypeOrdinal, outputTypeOrdinal);
    }
}
