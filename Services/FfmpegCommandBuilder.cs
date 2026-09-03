using System.Text;
using MediaFlux.Models;
using MediaFlux.Services.Encoders;

namespace MediaFlux.Services
{
    internal sealed class FfmpegCommandBuilder
    {
        private readonly EncoderRegistry _encoderRegistry;
        private readonly Func<string, double> _getPrimaryAudioBitrateKbps;
        private readonly Action<string>? _log;

        public FfmpegCommandBuilder(
            EncoderRegistry encoderRegistry,
            Func<string, double> getPrimaryAudioBitrateKbps,
            Action<string>? log = null)
        {
            _encoderRegistry = encoderRegistry ??
                throw new ArgumentNullException(nameof(encoderRegistry));
            _getPrimaryAudioBitrateKbps = getPrimaryAudioBitrateKbps ??
                throw new ArgumentNullException(
                    nameof(getPrimaryAudioBitrateKbps));
            _log = log;
        }

        public string Build(FfmpegCommandRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(request.Input);

            ValidatedEncoderSettings validated =
                EncodingRequestValidator.ValidateAndNormalize(
                    _encoderRegistry,
                    request.Encoder,
                    request.UseGpu,
                    request.TargetMb,
                    request.EncoderPreset,
                    request.QualityValue,
                    request.TenBit,
                    request.AudioChannels,
                    request.ConcurrentEncoderSessions);
            ResolvedVideoEncoder resolved = validated.Resolved;
            IVideoEncoderProvider provider = resolved.Provider;
            VideoEncoderSelection selection = resolved.Selection;

            bool isAsfFamilyInput =
                request.Input.Kind == EncodingInputKind.File &&
                IsAsfFamilyInput(request.Input.SourcePath);

            bool wantsTenBit = validated.TenBit;

            string outputPixelFormat = wantsTenBit
                ? (provider.Capabilities.IsHardware ? "p010le" : "yuv420p10le")
                : (provider.Capabilities.IsHardware ? "nv12" : "yuv420p");
            string scaleExpression = request.ScaleMode switch
            {
                EncodingService.ScaleMode.To720p => "-2:720",
                EncodingService.ScaleMode.To1080p => "-2:1080",
                EncodingService.ScaleMode.To1440p => "-2:1440",
                EncodingService.ScaleMode.To4K => "-2:2160",
                _ => string.Empty
            };
            bool sourceIsTenBit = IsTenBitPixelFormat(request.SourcePixelFormat);
            VideoRestorationSettings effectiveRestoration = VideoRestorationModeResolver.Resolve(request.Restoration);
            string restorationFilterChain = request.RestorationFilterOverride ?? VideoRestorationPipeline.BuildFilterChain(effectiveRestoration, request.ScaleMode);
            bool requiresVideoFilter =
                !string.IsNullOrEmpty(scaleExpression) ||
                string.IsNullOrWhiteSpace(request.SourcePixelFormat) ||
                sourceIsTenBit != wantsTenBit || !string.IsNullOrEmpty(restorationFilterChain);

            var context = new EncoderArgumentContext
            {
                Selection = selection,
                UseGpu = validated.UseGpu,
                WantsTenBit = wantsTenBit,
                TenBitPixelFormat = wantsTenBit ? outputPixelFormat : null,
                OutputPixelFormat = outputPixelFormat,
                ScaleExpression = scaleExpression,
                RestorationFilterChain = restorationFilterChain,
                Preset = validated.Preset,
                QualityValue = validated.QualityValue,
                ConcurrentEncoderSessions =
                    validated.ConcurrentEncoderSessions,
                IsAsfFamilyInput = isAsfFamilyInput,
                UseGpuResidentHighBitDepthOutput =
                    validated.UseGpu &&
                    wantsTenBit &&
                    selection.EncoderId.Equals(
                        VideoEncoderIds.Nvenc,
                        StringComparison.OrdinalIgnoreCase) &&
                    request.NvencHighBitDepthOutputSupported,
                UseGpuResidentFrames =
                    validated.UseGpu &&
                    selection.EncoderId.Equals(
                        VideoEncoderIds.Nvenc,
                        StringComparison.OrdinalIgnoreCase) &&
                    request.PreferNvencGpuResidentFrames &&
                    // A software format/scale filter must receive software
                    // frames.  Do not make FFmpeg insert an implicit bridge
                    // between CUDA and system-memory filter domains.
                    !requiresVideoFilter,
                RequiresVideoFilter = requiresVideoFilter
            };

            var builder = new StringBuilder();
            builder.Append("-y ");

            provider.AppendInputAcceleration(builder, context);
            if (validated.UseGpu && isAsfFamilyInput)
            {
                _log?.Invoke(
                    "[EncodingService] WMV/ASF input detected; using software " +
                    "decode before selected hardware encode.");
            }

            if (request.SampleStart is { } sampleStart && sampleStart > TimeSpan.Zero)
                builder.Append($"-ss {Seconds(sampleStart.TotalSeconds)} ");
            if (request.SplitSource is { } split)
            {
                AppendInputPath(builder, split.VideoPath);
                AppendInput(builder, split.AncillarySource);
            }
            else AppendInput(builder, request.Input);
            bool copyDataStreams = request.CopyDataStreams &&
                OutputContainerPolicy.SupportsGenericDataStreams(
                    request.ContainerDecision.Resolved);
            bool usePlannedMp4Subtitles = request.ContainerDecision.Resolved == OutputContainer.Mp4 && request.ContainerDecision.StreamPlans.Count > 0;
            if (request.SplitSource is { } splitMapping)
            {
                builder.Append("-map 0:v:0 ");
                AppendStreamMapping(builder, splitMapping.AncillarySource, request.MapMode, request.CopySubtitles && !usePlannedMp4Subtitles, copyDataStreams, request.CopyAttachments, 1, includeVideo: false);
                if (usePlannedMp4Subtitles) AppendPlannedSubtitleMappings(builder, request.ContainerDecision, 1);
                builder.Append("-map_metadata 1 -map_chapters 1 ");
            }
            else { AppendStreamMapping(builder, request.Input, request.MapMode, request.CopySubtitles && !usePlannedMp4Subtitles, copyDataStreams, request.CopyAttachments); if (usePlannedMp4Subtitles) AppendPlannedSubtitleMappings(builder, request.ContainerDecision, 0); builder.Append("-map_metadata 0 -map_chapters 0 "); }
            AppendObsoleteVideoStatisticsCleanup(builder);
            if (request.SampleDuration is { } sampleDuration && sampleDuration > TimeSpan.Zero)
                builder.Append($"-t {Seconds(sampleDuration.TotalSeconds)} ");

            if (request.CopySubtitles)
            {
                builder.Append("-c:s copy ");
                int subtitleOutputIndex = 0;
                foreach (StreamCompatibilityPlan plan in request.ContainerDecision.StreamPlans.Where(plan => plan.StreamType.Equals("subtitle", StringComparison.OrdinalIgnoreCase) && plan.Action is StreamCompatibilityAction.Copy or StreamCompatibilityAction.Transcode))
                {
                    if (plan.Action == StreamCompatibilityAction.Transcode)
                        builder.Append($"-c:s:{subtitleOutputIndex} {plan.TargetCodec} ");
                    subtitleOutputIndex++;
                }
            }
            else
                builder.Append("-sn ");
            if (request.CopyAttachments)
                builder.Append("-c:t copy ");

            provider.AppendVideoFilters(builder, context);

            if (request.TargetMb is > 0)
            {
                AppendTargetSizeArguments(
                    builder,
                    request,
                    provider,
                    context);
            }
            else
            {
                provider.AppendQualityArguments(builder, context);
            }
            EncoderProviderUtilities.AppendOutputFormatFlags(builder, context);

            AppendAudioArguments(builder, request);
            if (request.ContainerDecision.Resolved == OutputContainer.Mp4)
                builder.Append("-movflags +faststart ");
            builder.Append($"-f {request.ContainerDecision.MuxerName} ");
            builder.Append($"\"{request.OutputPath}\"");
            return builder.ToString();
        }

        internal static string BuildInputAndMappingArguments(
            EncodingInputSource input,
            EncodingService.StreamMapMode mapMode,
            bool copySubtitles,
            bool copyDataStreams,
            bool copyAttachments = false)
        {
            var builder = new StringBuilder();
            AppendInput(builder, input);
            AppendStreamMapping(
                builder,
                input,
                mapMode,
                copySubtitles,
                copyDataStreams,
                copyAttachments);
            return builder.ToString().Trim();
        }

        private void AppendTargetSizeArguments(
            StringBuilder builder,
            FfmpegCommandRequest request,
            IVideoEncoderProvider provider,
            EncoderArgumentContext context)
        {
            if (request.KnownDuration <= TimeSpan.Zero)
            {
                _log?.Invoke(
                    "[EncodingService] Target-size bitrate budgeting skipped " +
                    "because input duration could not be determined; using " +
                    "quality-based encoding instead.");
                provider.AppendQualityArguments(builder, context);
                return;
            }

            double seconds = request.KnownDuration.TotalSeconds;
            double totalKbps = (request.TargetMb!.Value * 8192d) / seconds;

            double plannedAudioKbps;
            if (request.AudioChannels is > 0)
            {
                plannedAudioKbps =
                    (request.AudioChannels.Value >= 6 ? 384 : 192) *
                    Math.Max(1, request.Input.KnownAudioStreamCount);
            }
            else if (request.ForceMp4CompatibleAudio)
            {
                plannedAudioKbps =
                    192 * Math.Max(1, request.Input.KnownAudioStreamCount);
            }
            else
            {
                plannedAudioKbps =
                    request.Input.KnownAudioBitrateKbps is > 0
                        ? request.Input.KnownAudioBitrateKbps.Value
                        : _getPrimaryAudioBitrateKbps(
                            request.Input.InputPath);
            }

            double overheadKbps = Math.Max(16, totalKbps * 0.01);
            double mappedAncillaryKbps =
                request.Input.KnownMappedAncillaryBitrateKbps;
            double videoKbps =
                totalKbps -
                plannedAudioKbps -
                mappedAncillaryKbps -
                overheadKbps;
            if (videoKbps < 100)
                videoKbps = 100;

            double maxRateKbps = Math.Round(videoKbps * 1.08);
            double bufferSizeKbps = Math.Round(videoKbps * 1.4);

            _log?.Invoke(
                $"[EncodingService] Target bitrate plan: " +
                $"target={request.TargetMb.Value:0.##} MB, " +
                $"duration={seconds:0.##} sec, total={totalKbps:0} kbps, " +
                $"audio={plannedAudioKbps:0} kbps, " +
                $"subtitles/data={mappedAncillaryKbps:0} kbps, " +
                $"video={videoKbps:0} kbps.");

            provider.AppendTargetSizeArguments(
                builder,
                context,
                videoKbps,
                maxRateKbps,
                bufferSizeKbps);
        }

        private static void AppendAudioArguments(
            StringBuilder builder,
            FfmpegCommandRequest request)
        {
            if (request.AudioChannels is > 0)
            {
                builder.Append("-c:a aac -b:a 192k ");
                builder.Append($"-ac {request.AudioChannels.Value} ");
            }
            else if (request.ForceMp4CompatibleAudio)
            {
                builder.Append("-c:a aac -b:a 192k ");
            }
            else
            {
                builder.Append("-c:a copy ");
            }
        }

        private static void AppendObsoleteVideoStatisticsCleanup(StringBuilder builder)
        {
            // Matroska muxers often carry these values as per-stream tags. They
            // describe the old encoded video and are no longer authoritative once
            // the video stream is transcoded; preserve all other metadata.
            foreach (string key in new[]
                     {
                         "BPS", "BPS-eng", "NUMBER_OF_BYTES",
                         "NUMBER_OF_BYTES-eng", "NUMBER_OF_FRAMES",
                         "NUMBER_OF_FRAMES-eng", "DURATION", "ENCODER",
                         "_STATISTICS_TAGS", "_STATISTICS_WRITING_APP",
                         "_STATISTICS_WRITING_DATE_UTC"
                     })
            {
                builder.Append($"-metadata:s:v:0 {key}= ");
            }
        }

        private static void AppendInput(
            StringBuilder builder,
            EncodingInputSource input)
        {
            if (input.Kind == EncodingInputKind.DvdPhysicalConcat)
                builder.Append("-fflags +genpts ");

            builder.Append("-i \"");
            builder.Append(input.InputPath);
            builder.Append("\" ");
        }
        private static void AppendInputPath(StringBuilder builder, string path) => builder.Append("-i \"").Append(path).Append("\" ");

        private static string Seconds(double value) =>
            Math.Max(0, value).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

        private static void AppendStreamMapping(
            StringBuilder builder,
            EncodingInputSource input,
            EncodingService.StreamMapMode mapMode,
            bool copySubtitles,
            bool copyDataStreams,
            bool copyAttachments, int inputIndex = 0, bool includeVideo = true)
        {
            if (input.HasExplicitStreamSelection)
            {
                if (includeVideo) foreach (int streamIndex in input.VideoStreamIndexes)
                    builder.Append($"-map {inputIndex}:{streamIndex} ");
                foreach (int streamIndex in input.AudioStreamIndexes)
                    builder.Append($"-map {inputIndex}:{streamIndex} ");
                if (copySubtitles)
                {
                    foreach (int streamIndex in input.SubtitleStreamIndexes)
                        builder.Append($"-map {inputIndex}:{streamIndex} ");
                }

                if (!copyDataStreams)
                    builder.Append("-dn ");
                if (copyAttachments)
                    builder.Append($"-map {inputIndex}:t? ");
                return;
            }

            if (includeVideo) builder.Append($"-map {inputIndex}:v:0 ");
            if (mapMode == EncodingService.StreamMapMode.KeepAll)
            {
                builder.Append($"-map {inputIndex}:a? ");
                if (copySubtitles)
                    builder.Append($"-map {inputIndex}:s? ");
                if (copyDataStreams)
                    builder.Append($"-map {inputIndex}:d? ");
                else
                    builder.Append("-dn ");
                if (copyAttachments)
                    builder.Append($"-map {inputIndex}:t? ");
            }
            else
            {
                builder.Append($"-map {inputIndex}:a:0? ");
                if (copySubtitles)
                    builder.Append($"-map {inputIndex}:s? ");
                if (copyDataStreams)
                    builder.Append($"-map {inputIndex}:d? ");
                else
                    builder.Append("-dn ");
                if (copyAttachments)
                    builder.Append($"-map {inputIndex}:t? ");
            }
        }

        private static void AppendPlannedSubtitleMappings(StringBuilder builder, OutputContainerDecision decision, int inputIndex)
        {
            foreach (StreamCompatibilityPlan plan in decision.StreamPlans.Where(plan =>
                         plan.StreamType.Equals("subtitle", StringComparison.OrdinalIgnoreCase) &&
                         plan.Action is StreamCompatibilityAction.Copy or StreamCompatibilityAction.Transcode))
                builder.Append($"-map {inputIndex}:{plan.StreamIndex} ");
        }

        private static bool IsAsfFamilyInput(string path)
        {
            string extension = Path.GetExtension(path);
            return extension.Equals(
                       ".wmv",
                       StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(
                       ".asf",
                       StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsTenBitPixelFormat(string? value) =>
            !string.IsNullOrWhiteSpace(value) &&
            (value.Contains("10", StringComparison.OrdinalIgnoreCase) ||
             value.StartsWith("p010", StringComparison.OrdinalIgnoreCase));
    }
}
