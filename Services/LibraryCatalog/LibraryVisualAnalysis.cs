using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using MediaFlux.Services;

namespace MediaFlux.Services.LibraryCatalog
{
    public interface ILibraryVisualFingerprintExtractor
    {
        string ToolVersion { get; }
        Task<IReadOnlyList<ulong>> ExtractAsync(VisualFingerprintCandidate candidate, CancellationToken cancellationToken);
    }

    public sealed class FfmpegVisualFingerprintExtractor : ILibraryVisualFingerprintExtractor
    {
        public const int SampleCount = 6;
        private const int FrameBytes = 9 * 8;
        private readonly string _ffmpegPath;
        private readonly TimeSpan _timeout;

        public FfmpegVisualFingerprintExtractor(
            string applicationDirectory,
            string? configuredFfmpegPath = null,
            TimeSpan? timeout = null)
        {
            _ffmpegPath = FfmpegToolResolver.Resolve(applicationDirectory, configuredFfmpegPath).FfmpegPath;
            _timeout = timeout ?? TimeSpan.FromMinutes(2);
            ToolVersion = ReadToolVersion(_ffmpegPath);
        }

        public string ToolVersion { get; }

        public async Task<IReadOnlyList<ulong>> ExtractAsync(
            VisualFingerprintCandidate candidate,
            CancellationToken cancellationToken)
        {
            ValidateFile(candidate);
            if (!File.Exists(_ffmpegPath)) throw new FileNotFoundException("FFmpeg is required for visual similarity analysis.", _ffmpegPath);
            double[] fractions = { 0.10, 0.26, 0.42, 0.58, 0.74, 0.90 };
            var startInfo = new ProcessStartInfo
            {
                FileName = _ffmpegPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                ErrorDialog = false,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            startInfo.ArgumentList.Add("-hide_banner");
            startInfo.ArgumentList.Add("-loglevel");
            startInfo.ArgumentList.Add("error");
            startInfo.ArgumentList.Add("-nostdin");
            foreach (double fraction in fractions)
            {
                double seconds = Math.Clamp(candidate.DurationSeconds * fraction, 0, Math.Max(0, candidate.DurationSeconds - 0.05));
                startInfo.ArgumentList.Add("-ss");
                startInfo.ArgumentList.Add(seconds.ToString("0.###", CultureInfo.InvariantCulture));
                startInfo.ArgumentList.Add("-i");
                startInfo.ArgumentList.Add(candidate.FullPath);
            }
            string inputs = string.Join("", Enumerable.Range(0, SampleCount).Select(index =>
                $"[{index}:v:0]trim=end_frame=1,scale=9:8:flags=area,format=gray,setpts=PTS-STARTPTS[v{index}];"));
            string concat = string.Concat(Enumerable.Range(0, SampleCount).Select(index => $"[v{index}]")) + $"hstack=inputs={SampleCount}[out]";
            startInfo.ArgumentList.Add("-filter_complex");
            startInfo.ArgumentList.Add(inputs + concat);
            startInfo.ArgumentList.Add("-map");
            startInfo.ArgumentList.Add("[out]");
            startInfo.ArgumentList.Add("-frames:v");
            startInfo.ArgumentList.Add("1");
            startInfo.ArgumentList.Add("-f");
            startInfo.ArgumentList.Add("rawvideo");
            startInfo.ArgumentList.Add("pipe:1");

            using var process = new Process { StartInfo = startInfo };
            process.Start();
            Task<string> stderrTask = ReadBoundedErrorAsync(process.StandardError);
            using var output = new MemoryStream(FrameBytes * SampleCount);
            Task copyTask = process.StandardOutput.BaseStream.CopyToAsync(output, cancellationToken);
            using var timeout = new CancellationTokenSource(_timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            try
            {
                await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
                await copyTask.ConfigureAwait(false);
            }
            catch
            {
                TryKill(process);
                if (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                    throw new TimeoutException("FFmpeg visual fingerprint extraction timed out.");
                throw;
            }
            string error = await stderrTask.ConfigureAwait(false);
            if (process.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? $"FFmpeg exited with code {process.ExitCode}." : error);
            ValidateFile(candidate);
            byte[] bytes = output.ToArray();
            if (bytes.Length < FrameBytes * SampleCount) throw new InvalidDataException($"FFmpeg returned only {bytes.Length} of {FrameBytes * SampleCount} required visual-sample bytes.");
            var hashes = new ulong[SampleCount];
            byte[] frame = new byte[FrameBytes];
            int tiledRowBytes = 9 * SampleCount;
            for (int sample = 0; sample < SampleCount; sample++)
            {
                for (int row = 0; row < 8; row++)
                    bytes.AsSpan(row * tiledRowBytes + sample * 9, 9).CopyTo(frame.AsSpan(row * 9, 9));
                hashes[sample] = DifferenceHash(frame);
            }
            return hashes;
        }

        internal static ulong DifferenceHash(ReadOnlySpan<byte> frame)
        {
            if (frame.Length < FrameBytes) throw new ArgumentException("A 9x8 grayscale frame is required.", nameof(frame));
            ulong hash = 0;
            int bit = 0;
            for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++, bit++)
                if (frame[y * 9 + x] > frame[y * 9 + x + 1]) hash |= 1UL << bit;
            return hash;
        }

        private static void ValidateFile(VisualFingerprintCandidate candidate)
        {
            var info = new FileInfo(candidate.FullPath);
            if (!info.Exists) throw new FileNotFoundException("The indexed file is no longer available.", candidate.FullPath);
            if (info.Length != candidate.SizeBytes || info.LastWriteTimeUtc.Ticks != candidate.LastWriteUtc.Ticks)
                throw new IOException("The file changed after it was indexed; rescan it before visual analysis.");
        }

        private static async Task<string> ReadBoundedErrorAsync(StreamReader reader)
        {
            char[] buffer = new char[4096];
            var text = new System.Text.StringBuilder();
            int read;
            while ((read = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
                if (text.Length < 64 * 1024) text.Append(buffer, 0, Math.Min(read, 64 * 1024 - text.Length));
            return text.ToString().Trim();
        }

        private static string ReadToolVersion(string path)
        {
            try
            {
                FileVersionInfo info = FileVersionInfo.GetVersionInfo(path);
                return $"ffmpeg-{info.FileVersion ?? info.ProductVersion ?? File.GetLastWriteTimeUtc(path).Ticks.ToString(CultureInfo.InvariantCulture)}";
            }
            catch { return "ffmpeg-unknown"; }
        }

        private static void TryKill(Process process)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
        }
    }

    public sealed record LibraryVisualAnalysisOptions(
        int WorkerCount = 2,
        int FingerprintBatchSize = 32,
        int CandidateBatchSize = 256,
        int MaximumBandBucket = 128,
        int MinimumBandMatches = 3,
        double MinimumConfidence = 76,
        TimeSpan? EncodingPollInterval = null)
    {
        public TimeSpan EffectiveEncodingPollInterval => EncodingPollInterval ?? TimeSpan.FromSeconds(2);
    }

    public sealed record LibraryVisualAnalysisProgress(
        string Stage,
        long EligibleFiles,
        long FingerprintedFiles,
        long CandidatePairs,
        long MatchPairs,
        long ErrorCount,
        string CurrentPath,
        bool IsPaused);

    public sealed record LibraryVisualAnalysisResult(
        DuplicateAnalysisStatus Status,
        long EligibleFiles,
        long FingerprintedFiles,
        long CandidatePairs,
        long MatchPairs,
        long ErrorCount,
        string ErrorText);

    public sealed class LibraryVisualAnalysisCoordinator : IDisposable
    {
        public const string Algorithm = "dhash-6x9x8-banded";
        public const int AlgorithmVersion = 1;
        private readonly ILibraryVisualCatalog _catalog;
        private readonly ILibraryVisualFingerprintExtractor _extractor;
        private readonly LibraryVisualAnalysisOptions _options;
        private readonly Func<bool> _isEncodingActive;
        private readonly LibraryStorageScheduler _storageScheduler;
        private readonly MediaFlux.Models.DuplicateKeeperPreferences _keeperPreferences;
        private readonly AsyncPauseGate _pause = new();
        private readonly object _sync = new();
        private CancellationTokenSource? _activeCancellation;
        private TaskCompletionSource? _activeCompletion;
        private int _waitingForEncoding;
        private bool _disposed;

        public LibraryVisualAnalysisCoordinator(
            ILibraryVisualCatalog catalog,
            ILibraryVisualFingerprintExtractor extractor,
            LibraryVisualAnalysisOptions? options = null,
            Func<bool>? isEncodingActive = null,
            LibraryStorageScheduler? storageScheduler = null,
            MediaFlux.Models.DuplicateKeeperPreferences? keeperPreferences = null)
        {
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            _extractor = extractor ?? throw new ArgumentNullException(nameof(extractor));
            _options = options ?? new LibraryVisualAnalysisOptions();
            if (_options.WorkerCount < 1 || _options.WorkerCount > 8 || _options.FingerprintBatchSize < 1 || _options.CandidateBatchSize < 1)
                throw new ArgumentOutOfRangeException(nameof(options));
            _isEncodingActive = isEncodingActive ?? (() => false);
            _storageScheduler = storageScheduler ?? new LibraryStorageScheduler();
            _keeperPreferences = (keeperPreferences ?? new MediaFlux.Models.DuplicateKeeperPreferences()).Clone();
            _keeperPreferences.Normalize();
        }

        public event EventHandler<LibraryVisualAnalysisProgress>? ProgressChanged;
        public bool IsRunning { get { lock (_sync) return _activeCancellation != null; } }
        public bool IsPaused => _pause.IsPaused;
        public bool IsWaitingForEncoding => Volatile.Read(ref _waitingForEncoding) != 0;
        public void Pause() => _pause.Pause();
        public void Resume() => _pause.Resume();
        public void Cancel() { lock (_sync) _activeCancellation?.Cancel(); }

        public async Task<LibraryVisualAnalysisResult> AnalyzeAsync(CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            CancellationTokenSource linked;
            lock (_sync)
            {
                if (_activeCancellation != null) throw new InvalidOperationException("Visual analysis is already running.");
                linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                _activeCancellation = linked;
                _activeCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            VisualAnalysisHandle? run = null;
            long eligible = 0, fingerprinted = 0, candidates = 0, matches = 0, errors = 0;
            string errorText = "";
            DuplicateAnalysisStatus status = DuplicateAnalysisStatus.Failed;
            try
            {
                run = _catalog.BeginVisualAnalysis(Algorithm, AlgorithmVersion);
                eligible = _catalog.CountVisualFingerprintCandidates(AlgorithmVersion, _extractor.ToolVersion);
                while (true)
                {
                    await WaitForPermissionAsync(linked.Token).ConfigureAwait(false);
                    IReadOnlyList<VisualFingerprintCandidate> batch = _catalog.GetVisualFingerprintCandidates(AlgorithmVersion, _extractor.ToolVersion, _options.FingerprintBatchSize);
                    if (batch.Count == 0) break;
                    using var workerGate = new SemaphoreSlim(_options.WorkerCount, _options.WorkerCount);
                    VisualFingerprintWrite[] writes = await Task.WhenAll(batch.Select(async candidate =>
                    {
                        await workerGate.WaitAsync(linked.Token).ConfigureAwait(false);
                        try
                        {
                            await WaitForPermissionAsync(linked.Token).ConfigureAwait(false);
                            await using (await _storageScheduler.AcquireAsync(candidate.FullPath, candidate.VolumeId, linked.Token).ConfigureAwait(false))
                            {
                                Report("Extracting visual fingerprints", eligible, fingerprinted, candidates, matches, errors, candidate.FullPath);
                                IReadOnlyList<ulong> hashes = await _extractor.ExtractAsync(candidate, linked.Token).ConfigureAwait(false);
                                return new VisualFingerprintWrite(candidate, hashes, _extractor.ToolVersion);
                            }
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex) { return new VisualFingerprintWrite(candidate, Array.Empty<ulong>(), _extractor.ToolVersion, ex.Message); }
                        finally { workerGate.Release(); }
                    })).ConfigureAwait(false);
                    _catalog.SaveVisualFingerprintBatch(writes, Algorithm, AlgorithmVersion);
                    fingerprinted += writes.LongCount(write => write.FrameHashes.Count > 0);
                    errors += writes.LongCount(write => write.FrameHashes.Count == 0);
                }

                linked.Token.ThrowIfCancellationRequested();
                Report("Generating indexed visual candidates", eligible, fingerprinted, candidates, matches, errors, "");
                candidates = _catalog.BuildVisualCandidatePairs(run, AlgorithmVersion, _options.MaximumBandBucket, _options.MinimumBandMatches);
                _catalog.PrepareVisualSimilarityGroups(run);
                long afterLeft = 0, afterRight = 0;
                while (true)
                {
                    await WaitForPermissionAsync(linked.Token).ConfigureAwait(false);
                    IReadOnlyList<VisualCandidatePair> batch = _catalog.GetVisualCandidatePairs(run.RunId, afterLeft, afterRight, _options.CandidateBatchSize);
                    if (batch.Count == 0) break;
                    var accepted = new List<VisualMatchWrite>(batch.Count);
                    foreach (VisualCandidatePair pair in batch)
                    {
                        linked.Token.ThrowIfCancellationRequested();
                        VisualMatchWrite? match = Compare(pair, _options.MinimumConfidence);
                        if (match != null) accepted.Add(match);
                    }
                    if (accepted.Count > 0) _catalog.AppendVisualSimilarityGroups(run, accepted);
                    matches += accepted.Count;
                    afterLeft = batch[^1].LeftFileId;
                    afterRight = batch[^1].RightFileId;
                    Report("Scoring visual candidates", eligible, fingerprinted, candidates, matches, errors, "");
                }
                _catalog.PublishVisualSimilarityGroups(run);
                ScoreKeepers(linked.Token);
                status = DuplicateAnalysisStatus.Completed;
            }
            catch (OperationCanceledException)
            {
                status = DuplicateAnalysisStatus.Canceled;
                errorText = "Canceled by the user. Completed visual fingerprints remain reusable.";
            }
            catch (Exception ex)
            {
                status = DuplicateAnalysisStatus.Failed;
                errorText = ex.Message;
                errors++;
            }
            finally
            {
                if (run != null) _catalog.CompleteVisualAnalysis(run, new VisualAnalysisCompletion(status, eligible, fingerprinted, candidates, matches, errors, errorText));
                lock (_sync)
                {
                    _activeCancellation?.Dispose();
                    _activeCancellation = null;
                    _activeCompletion?.TrySetResult();
                    _activeCompletion = null;
                }
                Report(status.ToString(), eligible, fingerprinted, candidates, matches, errors, "");
            }
            return new LibraryVisualAnalysisResult(status, eligible, fingerprinted, candidates, matches, errors, errorText);
        }

        internal static VisualMatchWrite? Compare(VisualCandidatePair pair, double minimumConfidence)
        {
            int comparisons = Math.Min(pair.LeftFingerprint.FrameHashes.Count, pair.RightFingerprint.FrameHashes.Count);
            if (comparisons < 4) return null;
            double durationDelta = Math.Abs(pair.LeftDurationSeconds - pair.RightDurationSeconds);
            double tolerance = Math.Max(3, Math.Min(pair.LeftDurationSeconds, pair.RightDurationSeconds) * 0.03);
            if (durationDelta > tolerance) return null;
            int matches = 0, totalDistance = 0;
            for (int index = 0; index < comparisons; index++)
            {
                int distance = BitOperations.PopCount(pair.LeftFingerprint.FrameHashes[index] ^ pair.RightFingerprint.FrameHashes[index]);
                totalDistance += distance;
                if (distance <= 12) matches++;
            }
            double average = totalDistance / (double)comparisons;
            double matchRatio = matches / (double)comparisons;
            double durationPenalty = tolerance <= 0 ? 0 : durationDelta / tolerance * 4;
            double confidence = Math.Clamp(100 - average * 2.1 - durationPenalty, 0, 99.5);
            if (matchRatio < 0.67 || confidence < minimumConfidence) return null;
            return new VisualMatchWrite(pair.LeftFileId, pair.RightFileId, confidence, matches, comparisons, average, durationDelta,
                $"{matches}/{comparisons} aligned samples within dHash distance 12; average distance {average:0.0}; {pair.BandMatches} indexed band matches; duration delta {durationDelta:0.###}s.");
        }

        private void ScoreKeepers(CancellationToken token)
        {
            int offset = 0;
            while (true)
            {
                VisualSimilarityGroupPage page = _catalog.QueryVisualGroups(new VisualGroupQuery(Offset: offset, Limit: 500));
                if (page.Groups.Count == 0) return;
                foreach (VisualSimilarityGroupRecord group in page.Groups)
                {
                    token.ThrowIfCancellationRequested();
                    IReadOnlyList<VisualSimilarityMemberRecord> members = _catalog.GetVisualGroupMembers(group.GroupId);
                    DuplicateKeeperEvaluation score = DuplicateKeeperScoringService.Evaluate(
                        members.Select(LibraryVisualDuplicateCleanupService.ToLegacyItem).ToArray(), _keeperPreferences);
                    long? keeper = score.RequiresReview || score.Keeper == null ? null :
                        members.First(x => string.Equals(x.FullPath, score.Keeper.Path, StringComparison.OrdinalIgnoreCase)).FileId;
                    _catalog.SetVisualSuggestedKeeper(group.GroupId, keeper);
                }
                offset += page.Groups.Count;
                if (offset >= page.TotalCount) return;
            }
        }

        private async Task WaitForPermissionAsync(CancellationToken token)
        {
            await _pause.WaitAsync(token).ConfigureAwait(false);
            try
            {
                while (_isEncodingActive())
                {
                    Volatile.Write(ref _waitingForEncoding, 1);
                    await Task.Delay(_options.EffectiveEncodingPollInterval, token).ConfigureAwait(false);
                    await _pause.WaitAsync(token).ConfigureAwait(false);
                }
            }
            finally { Volatile.Write(ref _waitingForEncoding, 0); }
        }

        private void Report(string stage, long eligible, long fingerprinted, long candidates, long matches, long errors, string path) =>
            ProgressChanged?.Invoke(this, new LibraryVisualAnalysisProgress(stage, eligible, fingerprinted, candidates, matches, errors, path, _pause.IsPaused));

        public bool CancelAndWait(TimeSpan timeout)
        {
            Task? completion;
            lock (_sync)
            {
                _activeCancellation?.Cancel();
                completion = _activeCompletion?.Task;
            }
            if (completion == null) return true;
            try { return completion.Wait(timeout); }
            catch { return completion.IsCompleted; }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            CancelAndWait(TimeSpan.FromSeconds(10));
        }
    }
}
