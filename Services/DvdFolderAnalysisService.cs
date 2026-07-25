using System.Globalization;
using System.Text.RegularExpressions;
using MediaFlux.Models;

namespace MediaFlux.Services
{
    public sealed class DvdFolderAnalysisService
    {
        private static readonly Regex ProgramVobPattern = new(
            @"^VTS_(?<set>\d{2})_(?<segment>[1-9]\d*)\.VOB$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        private static readonly Regex MenuVobPattern = new(
            @"^(?:VIDEO_TS|VTS_\d{2}_0)\.VOB$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        private static readonly Regex TitleSetControlFilePattern = new(
            @"^VTS_\d{2}_0\.(?:IFO|BUP)$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        private readonly IMediaProbeService _probeService;
        private readonly int _maxConcurrentProbes;

        public DvdFolderAnalysisService(
            IMediaProbeService probeService,
            int maxConcurrentProbes = 2)
        {
            _probeService = probeService ??
                throw new ArgumentNullException(nameof(probeService));
            if (maxConcurrentProbes < 1)
                throw new ArgumentOutOfRangeException(nameof(maxConcurrentProbes));

            _maxConcurrentProbes = maxConcurrentProbes;
        }

        public async Task<DvdFolderAnalysisResult> AnalyzeAsync(
            string selectedFolder,
            IProgress<DvdAnalysisProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            string selectedPath = NormalizePath(selectedFolder);
            if (string.IsNullOrWhiteSpace(selectedPath) || !Directory.Exists(selectedPath))
            {
                return new DvdFolderAnalysisResult
                {
                    SelectedFolderPath = selectedPath,
                    ErrorMessage = "The selected DVD folder does not exist or cannot be accessed."
                };
            }

            string videoTsFolder;
            try
            {
                videoTsFolder = ResolveVideoTsFolder(selectedPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new DvdFolderAnalysisResult
                {
                    SelectedFolderPath = selectedPath,
                    ErrorMessage = $"MediaFlux could not inspect the selected folder: {ex.Message}"
                };
            }

            if (string.IsNullOrWhiteSpace(videoTsFolder))
            {
                return new DvdFolderAnalysisResult
                {
                    SelectedFolderPath = selectedPath,
                    ErrorMessage =
                        "No VIDEO_TS folder was found. Select VIDEO_TS directly or its parent folder."
                };
            }

            var result = new DvdFolderAnalysisResult
            {
                SelectedFolderPath = selectedPath,
                VideoTsFolderPath = videoTsFolder
            };

            progress?.Report(new DvdAnalysisProgress
            {
                Status = "Inspecting DVD folder"
            });

            List<string> files;
            try
            {
                files = Directory.EnumerateFiles(
                        videoTsFolder,
                        "*",
                        SearchOption.TopDirectoryOnly)
                    .Select(NormalizePath)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                result.ErrorMessage = $"The VIDEO_TS folder could not be read: {ex.Message}";
                return result;
            }

            bool hasControlFile = files.Any(path =>
                Path.GetFileName(path).Equals("VIDEO_TS.IFO", StringComparison.OrdinalIgnoreCase) ||
                TitleSetControlFilePattern.IsMatch(Path.GetFileName(path)));
            result.ResemblesDvdVideo = hasControlFile;
            if (!hasControlFile)
            {
                result.Warnings.Add(
                    "No DVD IFO control files were found. The VOB groups can still be reviewed, " +
                    "but this folder may not be a complete DVD-Video structure.");
            }

            var programFiles = new List<(string Path, string TitleSetId, int SegmentNumber)>();
            int menuVobCount = 0;
            foreach (string file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string name = Path.GetFileName(file);
                Match match = ProgramVobPattern.Match(name);
                if (match.Success)
                {
                    int segmentNumber = int.Parse(
                        match.Groups["segment"].Value,
                        NumberStyles.None,
                        CultureInfo.InvariantCulture);
                    string titleSetId = $"VTS_{match.Groups["set"].Value}";
                    programFiles.Add((file, titleSetId, segmentNumber));
                }
                else if (MenuVobPattern.IsMatch(name))
                {
                    menuVobCount++;
                }
            }

            if (programFiles.Count == 0)
            {
                result.ErrorMessage = menuVobCount > 0
                    ? "Only menu VOB files were detected. No sequential DVD program segments were found."
                    : "No valid DVD title sets were found.";
                return result;
            }

            var candidates = programFiles
                .GroupBy(file => file.TitleSetId, StringComparer.OrdinalIgnoreCase)
                .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                .Select(BuildCandidate)
                .ToList();
            result.Candidates = candidates;

            var analyzableSegments = candidates
                .SelectMany(candidate => candidate.Segments)
                .Where(segment => segment.IsReadable && segment.SizeBytes > 0)
                .ToList();
            int totalSegments = analyzableSegments.Count;
            int completedSegments = 0;

            using var semaphore = new SemaphoreSlim(_maxConcurrentProbes);
            var probeTasks = analyzableSegments.Select(async segment =>
            {
                await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    string titleSetId = GetTitleSetId(segment.Path);
                    progress?.Report(new DvdAnalysisProgress
                    {
                        Status = $"Analyzing title set {titleSetId}",
                        TitleSetId = titleSetId,
                        CompletedSegments = Volatile.Read(ref completedSegments),
                        TotalSegments = totalSegments
                    });

                    segment.ProbeResult = await _probeService.ProbeAsync(
                        segment.Path,
                        cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    int completed = Interlocked.Increment(ref completedSegments);
                    semaphore.Release();
                    progress?.Report(new DvdAnalysisProgress
                    {
                        Status = "Analyzing DVD structure",
                        TitleSetId = GetTitleSetId(segment.Path),
                        CompletedSegments = completed,
                        TotalSegments = totalSegments
                    });
                }
            });

            await Task.WhenAll(probeTasks).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            foreach (DvdTitleCandidate candidate in candidates)
                FinalizeCandidateAnalysis(candidate);

            RecommendMainFeature(result);
            if (result.RecommendedCandidate == null)
            {
                result.Warnings.Add(
                    "No fully valid DVD title set could be recommended. Review the candidate warnings. " +
                    "MediaFlux requires already accessible or decrypted DVD contents.");
            }

            progress?.Report(new DvdAnalysisProgress
            {
                Status = "DVD analysis complete",
                CompletedSegments = totalSegments,
                TotalSegments = totalSegments
            });

            return result;
        }

        public static string ResolveVideoTsFolder(string selectedFolder)
        {
            string selectedPath = NormalizePath(selectedFolder);
            if (string.IsNullOrWhiteSpace(selectedPath) || !Directory.Exists(selectedPath))
                return "";

            if (Path.GetFileName(selectedPath.TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar))
                .Equals("VIDEO_TS", StringComparison.OrdinalIgnoreCase))
            {
                return selectedPath;
            }

            return Directory.EnumerateDirectories(
                    selectedPath,
                    "*",
                    SearchOption.TopDirectoryOnly)
                .FirstOrDefault(directory =>
                    Path.GetFileName(directory).Equals(
                        "VIDEO_TS",
                        StringComparison.OrdinalIgnoreCase)) is { } match
                ? NormalizePath(match)
                : "";
        }

        private static DvdTitleCandidate BuildCandidate(
            IGrouping<string, (string Path, string TitleSetId, int SegmentNumber)> group)
        {
            var ordered = group
                .OrderBy(file => file.SegmentNumber)
                .ThenBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var segments = new List<DvdSegmentInfo>(ordered.Count);
            foreach (var file in ordered)
            {
                var segment = new DvdSegmentInfo
                {
                    Path = file.Path,
                    SegmentNumber = file.SegmentNumber
                };
                InspectSegmentFile(segment);
                segments.Add(segment);
            }

            var candidate = new DvdTitleCandidate
            {
                TitleSetId = group.Key.ToUpperInvariant(),
                Segments = segments,
                StartsAtSegmentOne = ordered.Count > 0 && ordered[0].SegmentNumber == 1
            };

            if (!candidate.StartsAtSegmentOne)
            {
                candidate.Warnings.Add(
                    $"{candidate.TitleSetId} does not begin with segment " +
                    $"{candidate.TitleSetId}_1.VOB.");
            }

            if (ordered.Count > 0)
            {
                var present = ordered
                    .Select(file => file.SegmentNumber)
                    .ToHashSet();
                candidate.MissingSegmentNumbers = Enumerable
                    .Range(1, ordered[^1].SegmentNumber)
                    .Where(number => !present.Contains(number))
                    .ToArray();
                foreach (int missing in candidate.MissingSegmentNumbers)
                {
                    candidate.Warnings.Add(
                        $"{candidate.TitleSetId} is missing segment " +
                        $"{candidate.TitleSetId}_{missing}.VOB.");
                }
            }

            foreach (DvdSegmentInfo segment in segments.Where(segment => !segment.IsReadable))
            {
                candidate.Warnings.Add(
                    $"{Path.GetFileName(segment.Path)} could not be read: {segment.ReadError}");
            }

            candidate.CombinedSizeBytes = segments.Sum(segment => segment.SizeBytes);
            return candidate;
        }

        private static void InspectSegmentFile(DvdSegmentInfo segment)
        {
            try
            {
                var file = new FileInfo(segment.Path);
                if (!file.Exists)
                {
                    segment.ReadError = "The file does not exist.";
                    return;
                }

                segment.SizeBytes = file.Length;
                if (file.Length == 0)
                {
                    segment.ReadError = "The file is empty.";
                    return;
                }

                using var stream = new FileStream(
                    segment.Path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                if (stream.ReadByte() < 0)
                {
                    segment.ReadError = "The file contains no readable data.";
                    return;
                }

                segment.IsReadable = true;
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                segment.ReadError = ex.Message;
            }
        }

        private static void FinalizeCandidateAnalysis(DvdTitleCandidate candidate)
        {
            foreach (DvdSegmentInfo segment in candidate.Segments)
            {
                if (!segment.IsReadable || segment.SizeBytes <= 0)
                    continue;

                MediaProbeResult? probe = segment.ProbeResult;
                if (probe?.Success == true)
                    continue;

                string error = probe?.ErrorMessage ?? "FFprobe did not return a result.";
                string warning =
                    $"FFprobe could not analyze {Path.GetFileName(segment.Path)}: {error}";
                if (LooksEncryptedOrCorrupt(error))
                {
                    warning +=
                        " The segment may be encrypted, corrupt, or otherwise unreadable. " +
                        "MediaFlux requires an already accessible VIDEO_TS folder.";
                }
                candidate.Warnings.Add(warning);
            }

            var validProbes = candidate.Segments
                .Select(segment => segment.ProbeResult)
                .Where(probe => probe?.Success == true)
                .Cast<MediaProbeResult>()
                .ToList();
            double segmentProbeDuration = validProbes
                .Where(probe => probe.DurationSeconds is > 0)
                .Sum(probe => probe.DurationSeconds!.Value);
            candidate.CombinedDurationSeconds = segmentProbeDuration;
            string videoTsFolder = Path.GetDirectoryName(
                candidate.Segments.FirstOrDefault()?.Path ?? "") ?? "";
            if (DvdIfoDurationReader.TryReadTitleSetDuration(
                    videoTsFolder,
                    candidate.TitleSetId,
                    out double ifoDuration,
                    out _))
            {
                candidate.CombinedDurationSeconds = ifoDuration;
                if (segmentProbeDuration > 0 &&
                    Math.Abs(segmentProbeDuration - ifoDuration) >
                    Math.Max(10, ifoDuration * 0.05))
                {
                    candidate.Warnings.Add(
                        $"{candidate.TitleSetId} contains discontinuous or wrapped VOB " +
                        "timestamps. MediaFlux recovered the title duration from its DVD " +
                        "navigation data.");
                }
            }
            candidate.ChapterCount = validProbes.Sum(probe => probe.Chapters.Count);

            MediaProbeResult? representative = validProbes.FirstOrDefault();
            MediaProbeStreamInfo? video = representative?.Streams.FirstOrDefault(stream =>
                stream.CodecType.Equals("video", StringComparison.OrdinalIgnoreCase));
            if (video != null)
            {
                candidate.VideoCodec = video.CodecName;
                candidate.VideoWidth = video.Width;
                candidate.VideoHeight = video.Height;
                candidate.DisplayAspectRatio = video.DisplayAspectRatio;
                candidate.FrameRate = video.FrameRate;
                candidate.FieldOrder = video.FieldOrder;
            }

            candidate.AudioStreamCount = representative?.Streams.Count(stream =>
                stream.CodecType.Equals("audio", StringComparison.OrdinalIgnoreCase)) ?? 0;
            candidate.SubtitleStreamCount = representative?.Streams.Count(stream =>
                stream.CodecType.Equals("subtitle", StringComparison.OrdinalIgnoreCase)) ?? 0;
            candidate.Languages = validProbes
                .SelectMany(probe => probe.Streams)
                .Select(stream => stream.Language)
                .Where(language => !string.IsNullOrWhiteSpace(language))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(language => language, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            candidate.HasConsistentStreams =
                validProbes.Count == candidate.Segments.Count &&
                validProbes.Count > 0 &&
                validProbes
                    .Select(BuildStreamSignature)
                    .Distinct(StringComparer.Ordinal)
                    .Count() == 1;
            if (validProbes.Count > 0 && !candidate.HasConsistentStreams)
            {
                candidate.Warnings.Add(
                    $"{candidate.TitleSetId} does not expose a consistent video, audio, and " +
                    "subtitle stream layout across every segment.");
            }

            if (candidate.CombinedDurationSeconds <= 0)
            {
                candidate.Warnings.Add(
                    $"FFprobe could not determine a usable combined duration for {candidate.TitleSetId}.");
            }
            if (video == null)
            {
                candidate.Warnings.Add(
                    $"No readable video stream was found for {candidate.TitleSetId}.");
            }

            bool everySegmentReadable = candidate.Segments.All(segment =>
                segment.IsReadable && segment.SizeBytes > 0);
            bool everyProbeSucceeded = candidate.Segments.All(segment =>
                segment.ProbeResult?.Success == true);
            candidate.IsValidForConversion =
                candidate.StartsAtSegmentOne &&
                candidate.MissingSegmentNumbers.Count == 0 &&
                everySegmentReadable &&
                everyProbeSucceeded &&
                candidate.HasConsistentStreams &&
                candidate.CombinedDurationSeconds > 0 &&
                video != null;
        }

        private static string BuildStreamSignature(MediaProbeResult probe)
        {
            return string.Join(
                "\n",
                probe.Streams
                    .Where(stream =>
                        stream.CodecType.Equals("video", StringComparison.OrdinalIgnoreCase) ||
                        stream.CodecType.Equals("audio", StringComparison.OrdinalIgnoreCase) ||
                        stream.CodecType.Equals("subtitle", StringComparison.OrdinalIgnoreCase))
                    .Select(stream =>
                        $"{stream.CodecType.ToLowerInvariant()}|" +
                        $"{(string.IsNullOrWhiteSpace(stream.Id) ? $"index:{stream.Index}" : stream.Id)}|" +
                        $"{stream.CodecName.ToLowerInvariant()}|" +
                        $"{stream.TimeBase}|" +
                        $"{stream.Width}x{stream.Height}")
                    .OrderBy(value => value, StringComparer.Ordinal));
        }

        private static void RecommendMainFeature(DvdFolderAnalysisResult result)
        {
            var valid = result.Candidates
                .Where(candidate => candidate.IsValidForConversion)
                .OrderByDescending(candidate => candidate.CombinedDurationSeconds)
                .ThenByDescending(candidate => candidate.CombinedSizeBytes)
                .ThenByDescending(candidate => candidate.AudioStreamCount)
                .ThenBy(candidate => candidate.TitleSetId, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (valid.Count == 0)
                return;

            DvdTitleCandidate recommended = valid[0];
            recommended.IsLikelyMainFeature = true;
            recommended.RecommendationReason =
                $"Likely Main Feature because it is the longest valid detected title set at " +
                $"{FormatDuration(recommended.CombinedDurationSeconds)}.";
            result.RecommendedCandidate = recommended;

            if (valid.Count < 2)
                return;

            DvdTitleCandidate runnerUp = valid[1];
            double difference = recommended.CombinedDurationSeconds -
                                runnerUp.CombinedDurationSeconds;
            double ambiguityThreshold = Math.Min(
                TimeSpan.FromMinutes(10).TotalSeconds,
                recommended.CombinedDurationSeconds * 0.10);
            if (difference > ambiguityThreshold)
                return;

            result.HasAmbiguousMainFeature = true;
            result.AmbiguityWarning =
                $"{recommended.TitleSetId} and {runnerUp.TitleSetId} have similar durations " +
                $"({FormatDuration(recommended.CombinedDurationSeconds)} and " +
                $"{FormatDuration(runnerUp.CombinedDurationSeconds)}). Review the selection; " +
                "the longest title is only a recommendation.";
            result.Warnings.Add(result.AmbiguityWarning);
        }

        private static string FormatDuration(double seconds)
        {
            TimeSpan duration = TimeSpan.FromSeconds(Math.Max(0, seconds));
            if (duration.TotalHours >= 1)
            {
                int hours = (int)duration.TotalHours;
                return $"{hours} hour{(hours == 1 ? "" : "s")} {duration.Minutes} minute" +
                       $"{(duration.Minutes == 1 ? "" : "s")}";
            }

            int minutes = Math.Max(1, (int)Math.Round(duration.TotalMinutes));
            return $"{minutes} minute{(minutes == 1 ? "" : "s")}";
        }

        private static bool LooksEncryptedOrCorrupt(string error)
        {
            return error.Contains("invalid data", StringComparison.OrdinalIgnoreCase) ||
                   error.Contains("encrypted", StringComparison.OrdinalIgnoreCase) ||
                   error.Contains("permission denied", StringComparison.OrdinalIgnoreCase) ||
                   error.Contains("could not find codec", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetTitleSetId(string path)
        {
            Match match = ProgramVobPattern.Match(Path.GetFileName(path));
            return match.Success
                ? $"VTS_{match.Groups["set"].Value}"
                : "";
        }

        private static string NormalizePath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return "";

            try
            {
                return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));
            }
            catch
            {
                return path.Trim();
            }
        }
    }
}
