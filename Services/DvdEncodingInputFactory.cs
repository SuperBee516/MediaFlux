using MediaFlux.Models;

namespace MediaFlux.Services
{
    public sealed class DvdEncodingInputFactory
    {
        public EncodingInputSource Create(DvdImportOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(options.Candidate);
            if (options.OutputMode != DvdOutputMode.EncodeUsingCurrentSettings)
            {
                throw new InvalidOperationException(
                    "The selected DVD operation is not an encode operation.");
            }
            if (!options.Candidate.IsValidForConversion)
            {
                throw new InvalidOperationException(
                    "The selected DVD title has analysis errors and cannot be encoded safely.");
            }
            if (options.Candidate.Segments.Count == 0)
                throw new InvalidOperationException("The DVD title has no program segments.");

            DvdPhysicalInput physicalInput =
                DvdPhysicalInputBuilder.Create(options.Candidate);
            MediaProbeResult? representative = options.Candidate.Segments
                .Select(segment => segment.ProbeResult)
                .FirstOrDefault(probe => probe?.Success == true);
            if (representative == null)
                throw new InvalidOperationException("The DVD title has no usable stream analysis.");

            MediaProbeStreamInfo? video = representative.Streams.FirstOrDefault(stream =>
                stream.CodecType.Equals("video", StringComparison.OrdinalIgnoreCase));
            if (video == null)
                throw new InvalidOperationException("The DVD title has no video stream.");

            var selectedAudio = options.SelectedAudioStreamIndexes.ToHashSet();
            var selectedSubtitles = options.SelectedSubtitleStreamIndexes.ToHashSet();
            string sourceFolder = Path.GetDirectoryName(
                options.Candidate.Segments[0].Path) ?? "";
            string outputBaseName = Path.GetFileNameWithoutExtension(options.OutputPath);
            if (string.IsNullOrWhiteSpace(outputBaseName))
                outputBaseName = $"{options.Candidate.TitleSetId} DVD Title";

            return new EncodingInputSource
            {
                Kind = EncodingInputKind.DvdPhysicalConcat,
                InputPath = physicalInput.InputUrl,
                SourcePath = sourceFolder,
                SourceFiles = physicalInput.SourceFiles,
                OutputBaseName = OutputPathService.SanitizeFileName(outputBaseName),
                KnownDurationSeconds = options.Candidate.CombinedDurationSeconds,
                VideoStreamIndexes = new[] { video.Index },
                AudioStreamIndexes = representative.Streams
                    .Where(stream =>
                        stream.CodecType.Equals("audio", StringComparison.OrdinalIgnoreCase) &&
                        selectedAudio.Contains(stream.Index))
                    .Select(stream => stream.Index)
                    .ToArray(),
                SubtitleStreamIndexes = representative.Streams
                    .Where(stream =>
                        stream.CodecType.Equals("subtitle", StringComparison.OrdinalIgnoreCase) &&
                        selectedSubtitles.Contains(stream.Index))
                    .Select(stream => stream.Index)
                    .ToArray(),
                AllowSourceDeletion = false
            };
        }
    }
}
