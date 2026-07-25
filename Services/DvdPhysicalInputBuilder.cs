using MediaFlux.Models;

namespace MediaFlux.Services
{
    /// <summary>
    /// Describes DVD VOB segments as one physical MPEG program stream. DVD title
    /// VOBs are byte-split pieces of the same stream; opening each segment through
    /// FFmpeg's virtual concat demuxer independently retimestamps the pieces and can
    /// create invalid packets at the split boundaries.
    /// </summary>
    public sealed class DvdPhysicalInput
    {
        internal DvdPhysicalInput(
            string inputUrl,
            IReadOnlyList<string> sourceFiles)
        {
            InputUrl = inputUrl;
            SourceFiles = sourceFiles;
        }

        public string InputUrl { get; }
        public IReadOnlyList<string> SourceFiles { get; }
    }

    public static class DvdPhysicalInputBuilder
    {
        public static DvdPhysicalInput Create(DvdTitleCandidate candidate)
        {
            ArgumentNullException.ThrowIfNull(candidate);
            if (candidate.Segments.Count == 0)
                throw new InvalidOperationException("The DVD title has no program segments.");

            string[] sourceFiles = candidate.Segments
                .OrderBy(segment => segment.SegmentNumber)
                .Select(segment => Path.GetFullPath(segment.Path))
                .ToArray();
            foreach (string path in sourceFiles)
            {
                if (!File.Exists(path))
                    throw new FileNotFoundException("A DVD program segment is missing.", path);
                if (path.Contains('|'))
                    throw new InvalidOperationException(
                        "A DVD segment path contains the FFmpeg concat delimiter '|'.");
            }

            string inputUrl = "concat:" + string.Join(
                "|",
                sourceFiles.Select(ToFileUrl));
            return new DvdPhysicalInput(inputUrl, sourceFiles);
        }

        internal static string ToFileUrl(string path) =>
            "file:" + Path.GetFullPath(path).Replace('\\', '/');
    }
}
