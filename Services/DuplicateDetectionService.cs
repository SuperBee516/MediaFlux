using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaFlux.Models;

namespace MediaFlux.Services
{
    public sealed class DuplicateDetectionService
    {
        private const double DurationToleranceSeconds = 2.0;
        private const double DurationTolerancePercent = 0.01;
        private const int RequiredFrameHashes = 8;
        private const int StrongFrameDistanceThreshold = 8;
        private const int ReviewFrameDistanceThreshold = 14;
        private const double StrongFrameMatchRatio = 0.82;
        private const double ReviewFrameMatchRatio = 0.65;
        private const string SignatureVersion = "dhash-9x8-samples10-v1";

        private readonly MediaInfoService _mediaInfoService;
        private readonly string _ffmpegPath;
        private readonly string? _cachePath;
        private readonly bool _persistentCacheEnabled;
        private readonly object _cacheLock = new();
        private readonly Dictionary<string, SignatureCacheEntry> _signatureCache = new(StringComparer.OrdinalIgnoreCase);
        private bool _cacheDirty;

        public DuplicateDetectionService(
            MediaInfoService mediaInfoService,
            string? baseDirectory = null,
            string? ffmpegPath = null,
            bool persistentCacheEnabled = true,
            string? dataDirectory = null)
        {
            _mediaInfoService = mediaInfoService;
            var root = string.IsNullOrWhiteSpace(baseDirectory)
                ? AppDomain.CurrentDomain.BaseDirectory
                : baseDirectory;
            _ffmpegPath = FfmpegToolResolver.Resolve(root, configuredFfmpegPath: ffmpegPath).FfmpegPath;
            _persistentCacheEnabled = persistentCacheEnabled;
            if (_persistentCacheEnabled)
            {
                string cacheRoot = string.IsNullOrWhiteSpace(dataDirectory)
                    ? Path.Combine(root, "data")
                    : dataDirectory;
                _cachePath = Path.Combine(cacheRoot, "duplicate-signature-cache.json");
                LoadPersistentCache();
            }
        }

        public Task<DuplicateScanResult> AnalyzeAsync(
            IReadOnlyCollection<string> paths,
            IProgress<DuplicateScanProgress>? progress,
            CancellationToken cancellationToken)
        {
            return AnalyzeAsync(paths, DuplicateScanOptions.Default, progress, cancellationToken);
        }

        public Task<DuplicateScanResult> AnalyzeAsync(
            IReadOnlyCollection<string> paths,
            DuplicateScanOptions options,
            IProgress<DuplicateScanProgress>? progress,
            CancellationToken cancellationToken)
        {
            return Task.Run(() => Analyze(paths, options, progress, cancellationToken), cancellationToken);
        }

        private DuplicateScanResult Analyze(
            IReadOnlyCollection<string> paths,
            DuplicateScanOptions options,
            IProgress<DuplicateScanProgress>? progress,
            CancellationToken cancellationToken)
        {
            options = options.Normalize();
            var referenceRoots = NormalizeReferenceRoots(options.ReferenceFolders);
            var uniquePaths = paths
                .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var probes = new List<DuplicateFileProbe>(uniquePaths.Count);
            int totalWork = Math.Max(uniquePaths.Count * 2, 1);
            int completed = 0;
            bool readMediaMetadata = options.ScanMode != DuplicateScanModes.Exact;

            foreach (var path in uniquePaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var probe = CreateProbe(path, referenceRoots, readMediaMetadata);
                if (probe != null)
                    probes.Add(probe);

                completed++;
                progress?.Report(new DuplicateScanProgress(completed, totalWork, "Reading video metadata"));
            }

            var groups = new List<DuplicateGroup>();
            var assigned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var sameSizeGroup in probes.GroupBy(p => p.LengthBytes).Where(g => g.Count() > 1))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var hashedProbes = new List<DuplicateFileProbe>();
                foreach (var probe in sameSizeGroup)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var hashedProbe = probe with { ExactHash = GetOrComputeExactHash(probe, cancellationToken) };
                    if (!string.IsNullOrWhiteSpace(hashedProbe.ExactHash))
                        hashedProbes.Add(hashedProbe);

                    completed++;
                    progress?.Report(new DuplicateScanProgress(
                        Math.Min(completed, totalWork),
                        totalWork,
                        "Hashing same-size duplicate candidates"));
                }

                var hashed = hashedProbes
                    .GroupBy(p => p.ExactHash, StringComparer.OrdinalIgnoreCase)
                    .Where(g => g.Count() > 1);

                foreach (var hashGroup in hashed)
                {
                    var group = CreateGroup(
                        groups.Count + 1,
                        "Exact",
                        100,
                        "Identical file hash",
                        DuplicateGroupEvidence.Exact,
                        hashGroup,
                        options.KeeperPreferences);
                    groups.Add(group);
                    foreach (var item in group.Items)
                        assigned.Add(item.Path);
                }
            }

            if (options.ScanMode == DuplicateScanModes.Exact)
            {
                progress?.Report(new DuplicateScanProgress(totalWork, totalWork, "Finalizing exact duplicate results"));
                FlushCache();
                return BuildResult(groups);
            }

            var candidateBuckets = probes
                .Where(p => !assigned.Contains(p.Path) && p.DurationSeconds > 0)
                .GroupBy(p => new MetadataBucket((int)Math.Round(p.DurationSeconds / DurationToleranceSeconds)))
                .Where(g => g.Count() > 1);

            foreach (var bucket in candidateBuckets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var candidates = bucket.ToList();
                for (int i = 0; i < candidates.Count; i++)
                {
                    var probe = candidates[i];
                    if (probe.FrameHashes.Count == 0)
                        candidates[i] = probe with { FrameHashes = GetOrComputeFrameHashes(probe, cancellationToken) };

                    completed++;
                    progress?.Report(new DuplicateScanProgress(
                        Math.Min(completed, totalWork),
                        totalWork,
                        "Comparing sampled video frames"));
                }

                var remaining = candidates.Where(p => !assigned.Contains(p.Path)).ToList();
                while (remaining.Count > 1)
                {
                    var seed = remaining[0];
                    var matches = new List<DuplicateFileProbe> { seed };
                    var comparisons = new List<VideoComparisonResult>();
                    var consumedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { seed.Path };

                    foreach (var other in remaining.Skip(1))
                    {
                        var comparison = CompareVideos(seed, other);
                        if (comparison.IsMatch)
                        {
                            matches.Add(other);
                            comparisons.Add(comparison);
                            consumedPaths.Add(other.Path);
                        }
                    }

                    if (matches.Count > 1)
                    {
                        bool allStrong = comparisons.All(c => c.Label == "Strong visual match");
                        if (!allStrong && options.ScanMode != DuplicateScanModes.Review)
                        {
                            remaining = remaining
                                .Where(p => !assigned.Contains(p.Path) && !consumedPaths.Contains(p.Path))
                                .ToList();
                            continue;
                        }

                        int confidence = comparisons.Count > 0 ? comparisons.Min(c => c.ConfidenceScore) : 0;
                        string label = allStrong ? "Strong visual match" : "Review only";
                        string reason = BuildComparisonReason(comparisons);
                        var evidence = BuildGroupEvidence(label, comparisons);
                        var group = CreateGroup(groups.Count + 1, label, confidence, reason, evidence, matches, options.KeeperPreferences);
                        groups.Add(group);
                        if (allStrong)
                        {
                            foreach (var item in group.Items)
                                assigned.Add(item.Path);
                        }
                    }

                    remaining = remaining
                        .Where(p => !assigned.Contains(p.Path) && !consumedPaths.Contains(p.Path))
                        .ToList();
                }
            }
            progress?.Report(new DuplicateScanProgress(totalWork, totalWork, "Finalizing duplicate results"));
            FlushCache();
            return BuildResult(groups);
        }

        private DuplicateFileProbe? CreateProbe(
            string path,
            IReadOnlyCollection<string> referenceRoots,
            bool readMediaMetadata)
        {
            try
            {
                var file = new FileInfo(path);
                if (!file.Exists || file.Length <= 0)
                    return null;

                var info = readMediaMetadata
                    ? _mediaInfoService.GetInfo(path)
                    : new MediaInfoService.MediaInfo();
                var cacheEntry = GetValidCacheEntry(path, file.Length, file.LastWriteTimeUtc);
                return new DuplicateFileProbe(
                    path,
                    file.Length,
                    file.LastWriteTimeUtc,
                    file.CreationTime,
                    file.LastWriteTime,
                    info.VideoCodec ?? string.Empty,
                    info.Width ?? 0,
                    info.Height ?? 0,
                    info.DurationSeconds ?? 0,
                    info.BitrateKbps ?? 0,
                    info.Fps ?? 0,
                    IsReferenceProtected(path, referenceRoots),
                    cacheEntry?.ExactHash ?? string.Empty,
                    cacheEntry?.FrameHashes?.ToList() ?? new List<ulong>());
            }
            catch
            {
                return null;
            }
        }

        private string GetOrComputeExactHash(DuplicateFileProbe probe, CancellationToken cancellationToken)
        {
            if (!string.IsNullOrWhiteSpace(probe.ExactHash))
                return probe.ExactHash;

            string hash = ComputeFileHash(probe.Path, cancellationToken);
            if (!string.IsNullOrWhiteSpace(hash))
                UpdateCache(probe, hash, probe.FrameHashes);

            return hash;
        }

        private static string ComputeFileHash(string path, CancellationToken cancellationToken)
        {
            try
            {
                using var stream = File.OpenRead(path);
                using var sha = SHA256.Create();
                var buffer = new byte[1024 * 1024];
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    sha.TransformBlock(buffer, 0, read, null, 0);
                }
                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                return Convert.ToHexString(sha.Hash ?? Array.Empty<byte>());
            }
            catch
            {
                return string.Empty;
            }
        }

        private List<ulong> GetOrComputeFrameHashes(DuplicateFileProbe probe, CancellationToken cancellationToken)
        {
            if (probe.FrameHashes.Count > 0)
                return probe.FrameHashes;

            var hashes = ComputeFrameHashes(probe, cancellationToken);
            if (hashes.Count > 0)
                UpdateCache(probe, probe.ExactHash, hashes);

            return hashes;
        }

        private List<ulong> ComputeFrameHashes(DuplicateFileProbe probe, CancellationToken cancellationToken)
        {
            var hashes = new List<ulong>();
            if (!File.Exists(_ffmpegPath) || probe.DurationSeconds <= 0)
                return hashes;

            var fractions = new[] { 0.08, 0.16, 0.24, 0.32, 0.40, 0.50, 0.60, 0.70, 0.80, 0.90 };
            foreach (double fraction in fractions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                double seconds = Math.Max(0, Math.Min(probe.DurationSeconds - 0.1, probe.DurationSeconds * fraction));
                if (TryReadScaledFrame(probe.Path, seconds, cancellationToken, out var frameBytes) &&
                    TryDifferenceHash(frameBytes, out var hash))
                {
                    hashes.Add(hash);
                }
            }

            return hashes;
        }

        private bool TryReadScaledFrame(string path, double seconds, CancellationToken cancellationToken, out byte[] frameBytes)
        {
            frameBytes = Array.Empty<byte>();
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = _ffmpegPath,
                    Arguments = $"-v error -ss {seconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)} -i \"{path}\" -frames:v 1 -vf scale=9:8,format=gray -f rawvideo pipe:1",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    ErrorDialog = false,
                    WindowStyle = ProcessWindowStyle.Hidden
                };

                using var proc = Process.Start(psi);
                if (proc == null)
                    return false;

                using var ms = new MemoryStream();
                var copyTask = proc.StandardOutput.BaseStream.CopyToAsync(ms, cancellationToken);
                if (!proc.WaitForExit(12000))
                {
                    TryKill(proc);
                    return false;
                }

                copyTask.GetAwaiter().GetResult();
                _ = proc.StandardError.ReadToEnd();
                frameBytes = ms.ToArray();
                return frameBytes.Length >= 72;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryDifferenceHash(byte[] frameBytes, out ulong hash)
        {
            hash = 0;
            if (frameBytes.Length < 72)
                return false;

            int bit = 0;
            for (int y = 0; y < 8; y++)
            {
                int row = y * 9;
                for (int x = 0; x < 8; x++)
                {
                    if (frameBytes[row + x] > frameBytes[row + x + 1])
                        hash |= 1UL << bit;
                    bit++;
                }
            }

            return true;
        }

        private static VideoComparisonResult CompareVideos(
            DuplicateFileProbe left,
            DuplicateFileProbe right)
        {
            double durationDelta = Math.Abs(left.DurationSeconds - right.DurationSeconds);
            double durationTolerance = Math.Max(
                DurationToleranceSeconds,
                Math.Min(left.DurationSeconds, right.DurationSeconds) * DurationTolerancePercent);
            if (durationDelta > durationTolerance)
                return VideoComparisonResult.NoMatch($"Duration differs by {durationDelta:0.#} seconds");

            int comparisons = Math.Min(left.FrameHashes.Count, right.FrameHashes.Count);
            if (comparisons < RequiredFrameHashes)
                return VideoComparisonResult.NoMatch($"Not enough sampled frames ({comparisons}/{RequiredFrameHashes})");

            int strongMatches = 0;
            int reviewMatches = 0;
            int totalDistance = 0;
            for (int i = 0; i < comparisons; i++)
            {
                int distance = HammingDistance(left.FrameHashes[i], right.FrameHashes[i]);
                totalDistance += distance;
                if (distance <= StrongFrameDistanceThreshold)
                    strongMatches++;
                if (distance <= ReviewFrameDistanceThreshold)
                    reviewMatches++;
            }

            double strongRatio = strongMatches / (double)comparisons;
            double reviewRatio = reviewMatches / (double)comparisons;
            double avgDistance = totalDistance / (double)comparisons;

            if (strongRatio >= StrongFrameMatchRatio)
            {
                int score = Math.Clamp((int)Math.Round(100 - (avgDistance * 2.5)), 80, 98);
                return new VideoComparisonResult(
                    true,
                    "Strong visual match",
                    score,
                    $"{strongMatches}/{comparisons} sampled frames matched strongly; average hash distance {avgDistance:0.#}",
                    strongMatches,
                    reviewMatches,
                    comparisons,
                    avgDistance,
                    durationDelta);
            }

            if (reviewRatio >= ReviewFrameMatchRatio)
            {
                int score = Math.Clamp((int)Math.Round(70 - avgDistance), 45, 74);
                return new VideoComparisonResult(
                    true,
                    "Review only",
                    score,
                    $"{reviewMatches}/{comparisons} sampled frames are similar but below action threshold; average hash distance {avgDistance:0.#}",
                    strongMatches,
                    reviewMatches,
                    comparisons,
                    avgDistance,
                    durationDelta);
            }

            return VideoComparisonResult.NoMatch(
                $"{reviewMatches}/{comparisons} sampled frames matched review threshold; average hash distance {avgDistance:0.#}");
        }

        private static int HammingDistance(ulong left, ulong right)
        {
            ulong value = left ^ right;
            int count = 0;
            while (value != 0)
            {
                value &= value - 1;
                count++;
            }
            return count;
        }

        private static DuplicateGroup CreateGroup(
            int id,
            string confidenceLabel,
            int confidenceScore,
            string reason,
            DuplicateGroupEvidence evidence,
            IEnumerable<DuplicateFileProbe> probes,
            DuplicateKeeperPreferences keeperPreferences)
        {
            var items = probes
                .OrderByDescending(QualityScore)
                .ThenByDescending(p => p.LengthBytes)
                .Select(p => new DuplicateItem(
                    p.Path,
                    p.LengthBytes,
                    p.VideoCodec,
                    p.Width,
                    p.Height,
                    p.DurationSeconds,
                    p.BitrateKbps,
                    p.Created,
                    p.Modified,
                    p.IsReferenceProtected,
                    "",
                    "Review duplicate") { FrameRate = p.FrameRate })
                .ToList();

            var group = new DuplicateGroup(
                id,
                confidenceLabel,
                confidenceScore,
                reason,
                evidence.MatchMethod,
                evidence.FrameMatches,
                evidence.FrameComparisons,
                evidence.AverageHashDistance,
                evidence.DurationDeltaSeconds,
                items);
            return DuplicateKeeperScoringService.Apply(group, keeperPreferences, preserveManualSelection: false);
        }

        private static long QualityScore(DuplicateFileProbe probe)
        {
            long referenceBonus = probe.IsReferenceProtected ? 10_000_000_000_000L : 0;
            return referenceBonus + ((long)probe.Width * probe.Height * 1_000_000L) + (probe.BitrateKbps * 1000L) + probe.LengthBytes;
        }

        private static void TryKill(Process proc)
        {
            try { proc.Kill(entireProcessTree: true); }
            catch { }
        }

        private static string BuildComparisonReason(IReadOnlyCollection<VideoComparisonResult> comparisons)
        {
            if (comparisons.Count == 0)
                return "No visual comparison details available";

            return string.Join("; ", comparisons.Select(c => c.Reason).Distinct().Take(3));
        }

        private static DuplicateGroupEvidence BuildGroupEvidence(
            string label,
            IReadOnlyCollection<VideoComparisonResult> comparisons)
        {
            if (comparisons.Count == 0)
                return new DuplicateGroupEvidence(label, 0, 0, 0, 0);

            int frameComparisons = comparisons.Min(c => c.FrameComparisons);
            int frameMatches = string.Equals(label, "Strong visual match", StringComparison.OrdinalIgnoreCase)
                ? comparisons.Min(c => c.StrongMatches)
                : comparisons.Min(c => c.ReviewMatches);

            return new DuplicateGroupEvidence(
                label,
                frameMatches,
                frameComparisons,
                comparisons.Max(c => c.AverageHashDistance),
                comparisons.Max(c => c.DurationDeltaSeconds));
        }

        private static DuplicateScanResult BuildResult(IReadOnlyList<DuplicateGroup> groups)
        {
            var duplicateFiles = groups.Sum(g => Math.Max(0, g.Items.Count - 1));
            var recoverableBytes = groups.Sum(g =>
            {
                if (g.Items.Count <= 1)
                    return 0;

                var keeper = g.Items.FirstOrDefault(item =>
                    string.Equals(item.Recommendation, "Suggested keeper", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(item.Recommendation, "Protected keeper", StringComparison.OrdinalIgnoreCase));
                return keeper == null ? 0 : Math.Max(0, g.Items.Sum(i => i.LengthBytes) - keeper.LengthBytes);
            });

            return new DuplicateScanResult(groups, duplicateFiles, recoverableBytes);
        }

        private static IReadOnlyList<string> NormalizeReferenceRoots(IEnumerable<string> roots)
        {
            return roots
                .Where(root => !string.IsNullOrWhiteSpace(root) && Directory.Exists(root))
                .Select(root => Path.GetFullPath(root.Trim()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static bool IsReferenceProtected(string path, IReadOnlyCollection<string> referenceRoots)
        {
            if (referenceRoots.Count == 0)
                return false;

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(path);
            }
            catch
            {
                return false;
            }

            foreach (var root in referenceRoots)
            {
                if (string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase))
                    return true;

                string prefix = root + Path.DirectorySeparatorChar;
                if (fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        private SignatureCacheEntry? GetValidCacheEntry(string path, long length, DateTime lastWriteUtc)
        {
            if (!_persistentCacheEnabled)
                return null;

            lock (_cacheLock)
            {
                if (!_signatureCache.TryGetValue(path, out var entry))
                    return null;

                if (entry.Length == length &&
                    entry.LastWriteUtc == lastWriteUtc &&
                    string.Equals(entry.SignatureVersion, SignatureVersion, StringComparison.Ordinal))
                {
                    return entry;
                }

                _signatureCache.Remove(path);
                _cacheDirty = true;
                return null;
            }
        }

        private void UpdateCache(DuplicateFileProbe probe, string? exactHash, IReadOnlyList<ulong> frameHashes)
        {
            if (!_persistentCacheEnabled)
                return;

            lock (_cacheLock)
            {
                _signatureCache.TryGetValue(probe.Path, out var existing);
                _signatureCache[probe.Path] = new SignatureCacheEntry
                {
                    Length = probe.LengthBytes,
                    LastWriteUtc = probe.LastWriteUtc,
                    SignatureVersion = SignatureVersion,
                    ExactHash = !string.IsNullOrWhiteSpace(exactHash)
                        ? exactHash
                        : existing?.ExactHash ?? string.Empty,
                    FrameHashes = frameHashes.Count > 0
                        ? frameHashes.ToList()
                        : existing?.FrameHashes?.ToList() ?? new List<ulong>()
                };
                _cacheDirty = true;
            }
        }

        private void LoadPersistentCache()
        {
            if (string.IsNullOrWhiteSpace(_cachePath) || !File.Exists(_cachePath))
                return;

            try
            {
                var json = File.ReadAllText(_cachePath);
                var entries = JsonSerializer.Deserialize<Dictionary<string, SignatureCacheEntry>>(json);
                if (entries == null)
                    return;

                foreach (var item in entries)
                {
                    if (string.IsNullOrWhiteSpace(item.Key) || item.Value == null)
                        continue;

                    if (string.Equals(item.Value.SignatureVersion, SignatureVersion, StringComparison.Ordinal))
                        _signatureCache[item.Key] = item.Value;
                }
            }
            catch
            {
                _signatureCache.Clear();
            }
        }

        private void FlushCache()
        {
            if (!_persistentCacheEnabled || !_cacheDirty || string.IsNullOrWhiteSpace(_cachePath))
                return;

            try
            {
                var dir = Path.GetDirectoryName(_cachePath);
                if (!string.IsNullOrWhiteSpace(dir))
                    Directory.CreateDirectory(dir);

                Dictionary<string, SignatureCacheEntry> snapshot;
                lock (_cacheLock)
                {
                    snapshot = _signatureCache
                        .Where(item => File.Exists(item.Key))
                        .ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase);
                    _cacheDirty = false;
                }

                var options = new JsonSerializerOptions { WriteIndented = true };
                File.WriteAllText(_cachePath, JsonSerializer.Serialize(snapshot, options));
            }
            catch
            {
                // Cache persistence is an optimization. Scan results must not depend on it.
            }
        }

        public static void ClearPersistentCache(string? baseDirectory = null)
        {
            var root = string.IsNullOrWhiteSpace(baseDirectory)
                ? AppDomain.CurrentDomain.BaseDirectory
                : baseDirectory;
            var path = Path.Combine(root, "data", "duplicate-signature-cache.json");
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
                // Best effort only.
            }
        }

        private sealed record MetadataBucket(int DurationBucket);

        private sealed record VideoComparisonResult(
            bool IsMatch,
            string Label,
            int ConfidenceScore,
            string Reason,
            int StrongMatches,
            int ReviewMatches,
            int FrameComparisons,
            double AverageHashDistance,
            double DurationDeltaSeconds)
        {
            public static VideoComparisonResult NoMatch(string reason) =>
                new(false, "No match", 0, reason, 0, 0, 0, 0, 0);
        }

        private sealed record DuplicateGroupEvidence(
            string MatchMethod,
            int FrameMatches,
            int FrameComparisons,
            double AverageHashDistance,
            double DurationDeltaSeconds)
        {
            public static DuplicateGroupEvidence Exact { get; } =
                new("Exact hash", 0, 0, 0, 0);
        }

        private sealed record DuplicateFileProbe(
            string Path,
            long LengthBytes,
            DateTime LastWriteUtc,
            DateTime Created,
            DateTime Modified,
            string VideoCodec,
            int Width,
            int Height,
            double DurationSeconds,
            int BitrateKbps,
            double FrameRate,
            bool IsReferenceProtected,
            string ExactHash,
            List<ulong> FrameHashes);

        private sealed class SignatureCacheEntry
        {
            public long Length { get; set; }
            public DateTime LastWriteUtc { get; set; }
            public string SignatureVersion { get; set; } = "";
            public string ExactHash { get; set; } = "";
            public List<ulong> FrameHashes { get; set; } = new();
        }
    }

    public static class DuplicateScanModes
    {
        public const string Exact = "Exact duplicates";
        public const string Strict = "Strict visual duplicates";
        public const string Review = "Review similar videos";

        public static string Normalize(string? value)
        {
            return value switch
            {
                Exact => Exact,
                Review => Review,
                _ => Strict
            };
        }
    }

    public sealed record DuplicateScanOptions(
        string ScanMode,
        IReadOnlyCollection<string> ReferenceFolders,
        DuplicateKeeperPreferences KeeperPreferences)
    {
        public static DuplicateScanOptions Default { get; } =
            new(DuplicateScanModes.Strict, Array.Empty<string>(), new DuplicateKeeperPreferences());

        public DuplicateScanOptions Normalize()
        {
            return this with
            {
                ScanMode = DuplicateScanModes.Normalize(ScanMode),
                ReferenceFolders = ReferenceFolders ?? Array.Empty<string>(),
                KeeperPreferences = KeeperPreferences ?? new DuplicateKeeperPreferences()
            };
        }
    }

    public sealed record DuplicateScanProgress(int Current, int Total, string Stage);

    public sealed record DuplicateScanResult(
        IReadOnlyList<DuplicateGroup> Groups,
        int DuplicateFiles,
        long PotentialRecoverableBytes);

    public sealed record DuplicateGroup(
        int Id,
        string ConfidenceLabel,
        int ConfidenceScore,
        string Reason,
        string MatchMethod,
        int FrameMatches,
        int FrameComparisons,
        double AverageHashDistance,
        double DurationDeltaSeconds,
        IReadOnlyList<DuplicateItem> Items);

    public sealed record DuplicateItem(
        string Path,
        long LengthBytes,
        string VideoCodec,
        int Width,
        int Height,
        double DurationSeconds,
        int BitrateKbps,
        DateTime Created,
        DateTime Modified,
        bool IsReferenceProtected,
        string KeeperReason,
        string Recommendation)
    {
        public double FrameRate { get; init; }
    }
}
