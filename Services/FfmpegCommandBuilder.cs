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

            string? tenBitPixelFormat = wantsTenBit
                ? (provider.Capabilities.IsHardware
                    ? "p010le"
                    : "yuv420p10le")
                : null;

            string scaleExpression = request.ScaleMode switch
            {
                EncodingService.ScaleMode.To720p => "-2:720",
                EncodingService.ScaleMode.To1080p => "-2:1080",
                EncodingService.ScaleMode.To1440p => "-2:1440",
                EncodingService.ScaleMode.To4K => "-2:2160",
                _ => string.Empty
            };

            var context = new EncoderArgumentContext
            {
                Selection = selection,
                UseGpu = validated.UseGpu,
                WantsTenBit = wantsTenBit,
                TenBitPixelFormat = tenBitPixelFormat,
                ScaleExpression = scaleExpression,
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
                    request.NvencHighBitDepthOutputSupported
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
            AppendInput(builder, request.Input);
            AppendStreamMapping(
                builder,
                request.Input,
                request.MapMode,
                request.CopySubtitles,
                request.CopyDataStreams,
                request.CopyAttachments);
            builder.Append("-map_metadata 0 -map_chapters 0 ");
            if (request.SampleDuration is { } sampleDuration && sampleDuration > TimeSpan.Zero)
                builder.Append($"-t {Seconds(sampleDuration.TotalSeconds)} ");

            if (request.CopySubtitles)
                builder.Append("-c:s copy ");
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

        private static string Seconds(double value) =>
            Math.Max(0, value).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

        private static void AppendStreamMapping(
            StringBuilder builder,
            EncodingInputSource input,
            EncodingService.StreamMapMode mapMode,
            bool copySubtitles,
            bool copyDataStreams,
            bool copyAttachments)
        {
            if (input.HasExplicitStreamSelection)
            {
                foreach (int streamIndex in input.VideoStreamIndexes)
                    builder.Append($"-map 0:{streamIndex} ");
                foreach (int streamIndex in input.AudioStreamIndexes)
                    builder.Append($"-map 0:{streamIndex} ");
                if (copySubtitles)
                {
                    foreach (int streamIndex in input.SubtitleStreamIndexes)
                        builder.Append($"-map 0:{streamIndex} ");
                }

                if (!copyDataStreams)
                    builder.Append("-dn ");
                if (copyAttachments)
                    builder.Append("-map 0:t? ");
                return;
            }

            builder.Append("-map 0:v:0 ");
            if (mapMode == EncodingService.StreamMapMode.KeepAll)
            {
                builder.Append("-map 0:a? ");
                if (copySubtitles)
                    builder.Append("-map 0:s? ");
                if (copyDataStreams)
                    builder.Append("-map 0:d? ");
                else
                    builder.Append("-dn ");
                if (copyAttachments)
                    builder.Append("-map 0:t? ");
            }
            else
            {
                builder.Append("-map 0:a:0? ");
                if (copySubtitles)
                    builder.Append("-map 0:s? ");
                if (copyDataStreams)
                    builder.Append("-map 0:d? ");
                else
                    builder.Append("-dn ");
                if (copyAttachments)
                    builder.Append("-map 0:t? ");
            }
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
    }
}
