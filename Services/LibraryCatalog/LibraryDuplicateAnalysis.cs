using System.Collections.Concurrent;
using System.Security.Cryptography;
using MediaFlux.Services;
using MediaFlux.Models;

namespace MediaFlux.Services.LibraryCatalog
{
    public sealed record LibraryDuplicateAnalysisOptions(
        int WorkerCount = 2,
        int CandidateBatchSize = 128,
        int QuickSampleBytes = 64 * 1024,
        TimeSpan? EncodingPollInterval = null)
    {
        public TimeSpan EffectiveEncodingPollInterval => EncodingPollInterval ?? TimeSpan.FromSeconds(2);
    }

    public sealed record LibraryDuplicateAnalysisProgress(
        string Stage,
        long SizeCandidates,
        long QuickHashed,
        long FullHashed,
        long ExactGroups,
        long ErrorCount,
        string CurrentPath,
        bool IsPaused);

    public sealed record LibraryDuplicateAnalysisResult(
        DuplicateAnalysisStatus Status,
        long SizeCandidates,
        long QuickHashed,
        long FullHashed,
        long ExactGroups,
        long ErrorCount,
        string ErrorText);

    internal static class ExactDuplicateHashService
    {
        public const string QuickAlgorithm = "sha256-3x64k";
        public const int QuickVersion = 1;
        public const string FullAlgorithm = "sha256";
        public const int FullVersion = 1;

        public static async Task<byte[]> ComputeQuickAsync(
            LibraryHashCandidate candidate,
            int sampleBytes,
            CancellationToken cancellationToken)
        {
            ValidateFile(candidate);
            await using var stream = new FileStream(
                candidate.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: sampleBytes, FileOptions.Asynchronous | FileOptions.RandomAccess);
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hash.AppendData(BitConverter.GetBytes(candidate.SizeBytes));
            long[] offsets = candidate.SizeBytes <= sampleBytes
                ? new[] { 0L }
                : new[]
                {
                    0L,
                    Math.Max(0, (candidate.SizeBytes - sampleBytes) / 2),
                    Math.Max(0, candidate.SizeBytes - sampleBytes)
                }.Distinct().ToArray();
            byte[] buffer = new byte[Math.Min(sampleBytes, (int)Math.Min(int.MaxValue, Math.Max(1, candidate.SizeBytes)))];
            foreach (long offset in offsets)
            {
                hash.AppendData(BitConverter.GetBytes(offset));
                stream.Position = offset;
                int remaining = (int)Math.Min(buffer.Length, candidate.SizeBytes - offset);
                while (remaining > 0)
                {
                    int read = await stream.ReadAsync(buffer.AsMemory(0, remaining), cancellationToken).ConfigureAwait(false);
                    if (read == 0) throw new EndOfStreamException("The file changed while its quick fingerprint was being read.");
                    hash.AppendData(buffer, 0, read);
                    remaining -= read;
                }
            }
            ValidateFile(candidate);
            return hash.GetHashAndReset();
        }

        public static async Task<byte[]> ComputeFullAsync(
            LibraryHashCandidate candidate,
            CancellationToken cancellationToken)
        {
            ValidateFile(candidate);
            await using FileStream stream = new(
                candidate.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            ValidateFile(candidate);
            return hash;
        }

        private static void ValidateFile(LibraryHashCandidate candidate)
        {
            var info = new FileInfo(candidate.FullPath);
            if (!info.Exists)
                throw new FileNotFoundException("The indexed file is no longer available.", candidate.FullPath);
            if (info.Length != candidate.SizeBytes || info.LastWriteTimeUtc.Ticks != candidate.LastWriteUtc.Ticks)
                throw new IOException("The file changed after it was indexed; rescan it before duplicate analysis.");
        }
    }

    public sealed class LibraryDuplicateAnalysisCoordinator : IDisposable
    {
        private readonly ILibraryAnalysisCatalog _catalog;
        private readonly LibraryDuplicateAnalysisOptions _options;
        private readonly Func<bool> _isEncodingActive;
        private readonly Func<string, bool> _isProtectedPath;
        private readonly AsyncPauseGate _pause = new();
        private readonly LibraryStorageScheduler _storageScheduler;
        private DuplicateKeeperPreferences _keeperPreferences;
        private readonly object _sync = new();
        private CancellationTokenSource? _activeCancellation;
        private TaskCompletionSource? _activeCompletion;
        private int _waitingForEncoding;
        private bool _disposed;

        public LibraryDuplicateAnalysisCoordinator(
            ILibraryAnalysisCatalog catalog,
            LibraryDuplicateAnalysisOptions? options = null,
            Func<bool>? isEncodingActive = null,
            Func<string, bool>? isProtectedPath = null,
            LibraryStorageScheduler? storageScheduler = null,
            DuplicateKeeperPreferences? keeperPreferences = null)
        {
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            _options = options ?? new LibraryDuplicateAnalysisOptions();
            if (_options.WorkerCount < 1 || _options.CandidateBatchSize < 1 || _options.QuickSampleBytes < 1024)
                throw new ArgumentOutOfRangeException(nameof(options));
            _isEncodingActive = isEncodingActive ?? (() => false);
            _isProtectedPath = isProtectedPath ?? (_ => false);
            _storageScheduler = storageScheduler ?? new LibraryStorageScheduler();
            _keeperPreferences = keeperPreferences?.Clone() ?? new DuplicateKeeperPreferences();
            _keeperPreferences.Normalize();
        }

        public void UpdateKeeperPreferences(DuplicateKeeperPreferences preferences)
        {
            ArgumentNullException.ThrowIfNull(preferences);
            DuplicateKeeperPreferences copy = preferences.Clone();
            copy.Normalize();
            lock (_sync) _keeperPreferences = copy;
        }

        public void RefreshKeeperRecommendations()
        {
            int offset = 0;
            while (true)
            {
                ExactDuplicateGroupPage page = _catalog.QueryDuplicateGroups(new DuplicateGroupQuery(Offset: offset, Limit: 500));
                if (page.Groups.Count == 0) return;
                foreach (ExactDuplicateGroupRecord group in page.Groups)
                {
                    IReadOnlyList<ExactDuplicateMemberRecord> members = _catalog.GetDuplicateGroupMembers(group.GroupId);
                    if (members.Count == 0 || members.Any(member => member.IsManualKeeper)) continue;
                    DuplicateKeeperPreferences preferences;
                    lock (_sync) preferences = _keeperPreferences.Clone();
                    _catalog.SetSuggestedKeeper(group.GroupId, ExactDuplicateKeeperPolicy.Select(members, preferences).Keeper.FileId);
                }
                offset += page.Groups.Count;
                if (offset >= page.TotalCount) return;
            }
        }

        public event EventHandler<LibraryDuplicateAnalysisProgress>? ProgressChanged;
        public bool IsRunning { get { lock (_sync) return _activeCancellation != null; } }
        public bool IsPaused => _pause.IsPaused;
        public bool IsWaitingForEncoding => Volatile.Read(ref _waitingForEncoding) != 0;

        public void Pause() => _pause.Pause();
        public void Resume() => _pause.Resume();
        public void Cancel() { lock (_sync) _activeCancellation?.Cancel(); }

        public async Task<LibraryDuplicateAnalysisResult> AnalyzeAsync(CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            CancellationTokenSource linked;
            lock (_sync)
            {
                if (_activeCancellation != null) throw new InvalidOperationException("Duplicate analysis is already running.");
                linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                _activeCancellation = linked;
                _activeCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            DuplicateAnalysisHandle? run = null;
            long sizeCandidates = 0, quick = 0, full = 0, groups = 0, errors = 0;
            string errorText = "";
            DuplicateAnalysisStatus status = DuplicateAnalysisStatus.Failed;
            try
            {
                run = _catalog.BeginDuplicateAnalysis(ExactDuplicateHashService.QuickAlgorithm, ExactDuplicateHashService.QuickVersion, ExactDuplicateHashService.FullAlgorithm, ExactDuplicateHashService.FullVersion);
                sizeCandidates = _catalog.CountSizeCandidates();
                Report("Size candidates", sizeCandidates, quick, full, groups, errors, "", false);
                (long quickCompleted, long quickErrors) = await ProcessStageAsync(fullStage: false, sizeCandidates, linked.Token).ConfigureAwait(false);
                quick = quickCompleted;
                errors += quickErrors;
                (long fullCompleted, long fullErrors) = await ProcessStageAsync(fullStage: true, sizeCandidates, linked.Token).ConfigureAwait(false);
                full = fullCompleted;
                errors += fullErrors;
                linked.Token.ThrowIfCancellationRequested();
                Report("Building exact groups", sizeCandidates, quick, full, groups, errors, "", false);
                groups = _catalog.RebuildExactDuplicateGroups(run, ExactDuplicateHashService.FullAlgorithm, ExactDuplicateHashService.FullVersion);
                // Group replacement is atomic. Once it commits, finish keeper scoring so a
                // late cancellation cannot expose a partially published result generation.
                await ScoreKeepersAsync(run, CancellationToken.None).ConfigureAwait(false);
                status = DuplicateAnalysisStatus.Completed;
            }
            catch (OperationCanceledException)
            {
                status = DuplicateAnalysisStatus.Canceled;
                errorText = "Canceled by the user. Completed fingerprints remain reusable.";
            }
            catch (Exception ex)
            {
                status = DuplicateAnalysisStatus.Failed;
                errorText = ex.Message;
                errors++;
            }
            finally
            {
                if (run != null)
                    _catalog.CompleteDuplicateAnalysis(run, new DuplicateAnalysisCompletion(status, sizeCandidates, quick, full, groups, errors, errorText));
                lock (_sync)
                {
                    _activeCancellation?.Dispose();
                    _activeCancellation = null;
                    _activeCompletion?.TrySetResult();
                    _activeCompletion = null;
                }
                Report(status.ToString(), sizeCandidates, quick, full, groups, errors, "", false);
            }
            return new LibraryDuplicateAnalysisResult(status, sizeCandidates, quick, full, groups, errors, errorText);
        }

        private async Task<(long Completed, long Errors)> ProcessStageAsync(bool fullStage, long sizeCandidates, CancellationToken cancellationToken)
        {
            long completed = 0, errors = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await WaitForPermissionAsync(cancellationToken).ConfigureAwait(false);
                IReadOnlyList<LibraryHashCandidate> batch = fullStage
                    ? _catalog.GetFullHashCandidates(ExactDuplicateHashService.QuickVersion, ExactDuplicateHashService.FullVersion, _options.CandidateBatchSize)
                    : _catalog.GetQuickHashCandidates(ExactDuplicateHashService.QuickVersion, _options.CandidateBatchSize);
                if (batch.Count == 0) break;
                var unique = batch.GroupBy(PhysicalKey, StringComparer.OrdinalIgnoreCase).Select(group => group.First()).ToList();
                using var workerGate = new SemaphoreSlim(_options.WorkerCount, _options.WorkerCount);
                LibraryHashWrite[] writes = await Task.WhenAll(unique.Select(async candidate =>
                {
                    await workerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        await using (await _storageScheduler.AcquireAsync(
                                         candidate.FullPath,
                                         candidate.VolumeId,
                                         cancellationToken).ConfigureAwait(false))
                        {
                            await WaitForPermissionAsync(cancellationToken).ConfigureAwait(false);
                            Report(fullStage ? "Full SHA-256" : "Quick fingerprints", sizeCandidates,
                                fullStage ? 0 : Interlocked.Read(ref completed), fullStage ? Interlocked.Read(ref completed) : 0, 0, 0, candidate.FullPath, _pause.IsPaused);
                            byte[] hash = fullStage
                                ? await ExactDuplicateHashService.ComputeFullAsync(candidate, cancellationToken).ConfigureAwait(false)
                                : await ExactDuplicateHashService.ComputeQuickAsync(candidate, _options.QuickSampleBytes, cancellationToken).ConfigureAwait(false);
                            return new LibraryHashWrite(candidate, hash);
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        return new LibraryHashWrite(candidate, null, ex.Message);
                    }
                    finally { workerGate.Release(); }
                })).ConfigureAwait(false);
                _catalog.SaveHashBatch(
                    writes,
                    fullStage ? LibraryHashKind.FullSha256 : LibraryHashKind.QuickFingerprint,
                    fullStage ? ExactDuplicateHashService.FullAlgorithm : ExactDuplicateHashService.QuickAlgorithm,
                    fullStage ? ExactDuplicateHashService.FullVersion : ExactDuplicateHashService.QuickVersion);
                completed += writes.LongCount(write => write.Hash != null);
                errors += writes.LongCount(write => write.Hash == null);
            }
            return (completed, errors);
        }

        private async Task ScoreKeepersAsync(DuplicateAnalysisHandle run, CancellationToken cancellationToken)
        {
            long after = 0;
            while (true)
            {
                IReadOnlyList<long> ids = _catalog.GetDuplicateGroupIds(run.RunId, after, 500);
                if (ids.Count == 0) return;
                foreach (long id in ids)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    IReadOnlyList<ExactDuplicateMemberRecord> members = _catalog.GetDuplicateGroupMembers(id);
                    foreach (ExactDuplicateMemberRecord member in members.Where(member => !member.IsProtected && _isProtectedPath(member.FullPath)))
                        _catalog.SetFileProtection(member.FileId, true, "Configured duplicate reference path");
                    members = _catalog.GetDuplicateGroupMembers(id);
                    DuplicateKeeperPreferences preferences;
                    lock (_sync) preferences = _keeperPreferences.Clone();
                    _catalog.SetSuggestedKeeper(id, ExactDuplicateKeeperPolicy.Select(members, preferences).Keeper.FileId);
                }
                after = ids[^1];
            }
        }

        internal static DuplicateItem ToLegacyItem(ExactDuplicateMemberRecord item) => new(
            item.FullPath, item.SizeBytes, item.VideoCodec, item.Width ?? 0, item.Height ?? 0, item.DurationSeconds ?? 0,
            item.TotalBitRate.HasValue ? (int)Math.Clamp(item.TotalBitRate.Value / 1000, 0, int.MaxValue) : 0,
            item.LastWriteUtc, item.LastWriteUtc, item.IsProtected, "", "");

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

        private void Report(string stage, long size, long quick, long full, long groups, long errors, string path, bool paused) =>
            ProgressChanged?.Invoke(this, new LibraryDuplicateAnalysisProgress(stage, size, quick, full, groups, errors, path, paused));
        private static string PhysicalKey(LibraryHashCandidate item) => string.IsNullOrWhiteSpace(item.VolumeId) || string.IsNullOrWhiteSpace(item.FileIdentity) ? item.PathKey : $"{item.VolumeId}:{item.FileIdentity}";

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
