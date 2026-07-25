using MediaFlux.Models;

namespace MediaFlux.Services
{
    public sealed class DvdEncodingInputSession : IDisposable
    {
        private readonly DvdConcatManifest _manifest;

        internal DvdEncodingInputSession(
            DvdConcatManifest manifest,
            EncodingInputSource input)
        {
            _manifest = manifest;
            Input = input;
        }

        public EncodingInputSource Input { get; }
        public string TemporaryDirectory => _manifest.OperationDirectory;
        public bool CleanupSucceeded => _manifest.CleanupSucceeded;
        public string CleanupError => _manifest.CleanupError;

        public void Dispose()
        {
            _manifest.Dispose();
        }
    }

    public sealed class DvdEncodingInputFactory
    {
        private readonly DvdConcatManifestBuilder _manifestBuilder;

        public DvdEncodingInputFactory(DvdConcatManifestBuilder manifestBuilder)
        {
            _manifestBuilder = manifestBuilder ??
                throw new ArgumentNullException(nameof(manifestBuilder));
        }

        public DvdEncodingInputSession Create(DvdImportOptions options)
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

            DvdConcatManifest manifest = _manifestBuilder.Create(options.Candidate);
            try
            {
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

                var input = new EncodingInputSource
                {
                    Kind = EncodingInputKind.ConcatManifest,
                    InputPath = manifest.ManifestPath,
                    SourcePath = sourceFolder,
                    OutputBaseName = OutputPathService.SanitizeFileName(outputBaseName),
                    KnownDurationSeconds = options.Candidate.CombinedDurationSeconds,
                    VideoStreamIndexes = new[] { manifest.GetConcatStreamIndex(video.Index) },
                    AudioStreamIndexes = representative.Streams
                        .Where(stream =>
                            stream.CodecType.Equals("audio", StringComparison.OrdinalIgnoreCase) &&
                            selectedAudio.Contains(stream.Index))
                        .Select(stream => manifest.GetConcatStreamIndex(stream.Index))
                        .ToArray(),
                    SubtitleStreamIndexes = representative.Streams
                        .Where(stream =>
                            stream.CodecType.Equals("subtitle", StringComparison.OrdinalIgnoreCase) &&
                            selectedSubtitles.Contains(stream.Index))
                        .Select(stream => manifest.GetConcatStreamIndex(stream.Index))
                        .ToArray(),
                    AllowSourceDeletion = false
                };
                return new DvdEncodingInputSession(manifest, input);
            }
            catch
            {
                manifest.Dispose();
                throw;
            }
        }
    }
}
