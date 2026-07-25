using System.Globalization;
using System.Text;
using MediaFlux.Models;

namespace MediaFlux.Services
{
    public sealed class DvdConcatManifest : IDisposable
    {
        private readonly string _tempRoot;
        private bool _disposed;

        internal DvdConcatManifest(
            string tempRoot,
            string operationDirectory,
            string manifestPath,
            IReadOnlyDictionary<int, int> sourceToConcatStreamIndex)
        {
            _tempRoot = Path.GetFullPath(tempRoot);
            OperationDirectory = Path.GetFullPath(operationDirectory);
            ManifestPath = Path.GetFullPath(manifestPath);
            SourceToConcatStreamIndex = sourceToConcatStreamIndex;
        }

        public string OperationDirectory { get; }
        public string ManifestPath { get; }
        public IReadOnlyDictionary<int, int> SourceToConcatStreamIndex { get; }
        public bool CleanupSucceeded { get; private set; }
        public string CleanupError { get; private set; } = "";

        public int GetConcatStreamIndex(int sourceStreamIndex)
        {
            return SourceToConcatStreamIndex.TryGetValue(sourceStreamIndex, out int concatIndex)
                ? concatIndex
                : sourceStreamIndex;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            try
            {
                string rootWithSeparator = Path.TrimEndingDirectorySeparator(_tempRoot) +
                                           Path.DirectorySeparatorChar;
                if (!OperationDirectory.StartsWith(
                        rootWithSeparator,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                const int attempts = 3;
                for (int attempt = 1; attempt <= attempts; attempt++)
                {
                    if (!Directory.Exists(OperationDirectory))
                    {
                        CleanupSucceeded = true;
                        return;
                    }

                    try
                    {
                        Directory.Delete(OperationDirectory, recursive: true);
                    }
                    catch when (attempt < attempts)
                    {
                        Thread.Sleep(50 * attempt);
                    }
                }

                CleanupSucceeded = !Directory.Exists(OperationDirectory);
                if (!CleanupSucceeded)
                    CleanupError = "The temporary DVD operation directory still exists.";
            }
            catch (Exception ex)
            {
                CleanupSucceeded = false;
                CleanupError = ex.Message;
            }
        }
    }

    public sealed class DvdConcatManifestBuilder
    {
        private readonly string _tempRoot;

        public DvdConcatManifestBuilder(string? tempRoot = null)
        {
            _tempRoot = Path.GetFullPath(
                string.IsNullOrWhiteSpace(tempRoot)
                    ? AppPaths.TempDirectory
                    : tempRoot);
        }

        public DvdConcatManifest Create(DvdTitleCandidate candidate)
        {
            ArgumentNullException.ThrowIfNull(candidate);
            if (candidate.Segments.Count == 0)
                throw new InvalidOperationException("The DVD title has no program segments.");

            Directory.CreateDirectory(_tempRoot);
            string operationDirectory = Path.Combine(
                _tempRoot,
                "dvd-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(operationDirectory);
            string manifestPath = Path.Combine(operationDirectory, "title.ffconcat");

            try
            {
                var builder = new StringBuilder();
                builder.AppendLine("ffconcat version 1.0");

                MediaProbeResult? representative = candidate.Segments
                    .Select(segment => segment.ProbeResult)
                    .FirstOrDefault(probe => probe?.Success == true);
                var relevantStreams = representative?.Streams
                    .Where(IsRemuxableStreamType)
                    .OrderBy(stream => stream.Index)
                    .ToList() ?? new List<MediaProbeStreamInfo>();

                bool canDeclareExactIds =
                    relevantStreams.Count > 0 &&
                    relevantStreams.All(stream => !string.IsNullOrWhiteSpace(stream.Id)) &&
                    relevantStreams.Select(stream => stream.Id)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Count() == relevantStreams.Count;
                var sourceToConcat = new Dictionary<int, int>();
                if (canDeclareExactIds)
                {
                    for (int concatIndex = 0; concatIndex < relevantStreams.Count; concatIndex++)
                    {
                        MediaProbeStreamInfo stream = relevantStreams[concatIndex];
                        builder.AppendLine("stream");
                        builder.Append("exact_stream_id ");
                        builder.AppendLine(stream.Id);
                        sourceToConcat[stream.Index] = concatIndex;
                    }
                }
                else
                {
                    foreach (MediaProbeStreamInfo stream in relevantStreams)
                        sourceToConcat[stream.Index] = stream.Index;
                }

                foreach (DvdSegmentInfo segment in candidate.Segments.OrderBy(x => x.SegmentNumber))
                {
                    builder.Append("file '");
                    builder.Append(EscapeManifestPath(segment.Path));
                    builder.AppendLine("'");
                    if (segment.ProbeResult?.DurationSeconds is > 0)
                    {
                        builder.Append("duration ");
                        builder.AppendLine(segment.ProbeResult.DurationSeconds.Value.ToString(
                            "0.######",
                            CultureInfo.InvariantCulture));
                    }
                }

                File.WriteAllText(
                    manifestPath,
                    builder.ToString(),
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                return new DvdConcatManifest(
                    _tempRoot,
                    operationDirectory,
                    manifestPath,
                    sourceToConcat);
            }
            catch
            {
                try
                {
                    if (Directory.Exists(operationDirectory))
                        Directory.Delete(operationDirectory, recursive: true);
                }
                catch
                {
                    // Preserve the original manifest creation failure.
                }

                throw;
            }
        }

        public static string EscapeManifestPath(string path)
        {
            string normalized = Path.GetFullPath(path).Replace('\\', '/');
            return normalized.Replace("'", "'\\''", StringComparison.Ordinal);
        }

        private static bool IsRemuxableStreamType(MediaProbeStreamInfo stream)
        {
            return stream.CodecType.Equals("video", StringComparison.OrdinalIgnoreCase) ||
                   stream.CodecType.Equals("audio", StringComparison.OrdinalIgnoreCase) ||
                   stream.CodecType.Equals("subtitle", StringComparison.OrdinalIgnoreCase);
        }
    }
}
