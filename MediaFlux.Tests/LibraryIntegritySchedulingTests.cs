using MediaFlux.Services;
using MediaFlux.Services.LibraryCatalog;
using Xunit;

namespace MediaFlux.Tests;

public sealed class LibraryIntegritySchedulingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MediaFlux-IntegritySchedulingTests", Guid.NewGuid().ToString("N"));
    public LibraryIntegritySchedulingTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task SameVolumeIsSerializedWhileDifferentVolumesCanRunTogether()
    {
        string ffmpeg = FilePath("ffmpeg.exe"); var runner = new TrackingRunner();
        var catalog = new FakeCatalog(new[] { Item(1, "A"), Item(2, "A"), Item(3, "B") });
        using var coordinator = new LibraryIntegrityCoordinator(catalog, new LibraryIntegrityScrubService(ffmpeg, runner),
            new LibraryStorageScheduler(new VolumeResolver()));
        coordinator.QueueFiles(new long[] { 1, 2 }, LibraryIntegrityScrubType.Full); await catalog.WaitForCompletionsAsync(2);
        Assert.Equal(1, runner.MaximumConcurrent);

        runner.Reset(); catalog.ResetCompletions(); coordinator.QueueFiles(new long[] { 1, 3 }, LibraryIntegrityScrubType.Full);
        await catalog.WaitForCompletionsAsync(2); Assert.Equal(2, runner.MaximumConcurrent);
    }

    [Fact]
    public async Task ActiveEncodingDefersClaimsAndWorkersRemainBounded()
    {
        string ffmpeg = FilePath("ffmpeg.exe"); var runner = new TrackingRunner(); bool encoding = true;
        var catalog = new FakeCatalog(Enumerable.Range(1, 8).Select(id => Item(id, id.ToString())).ToArray());
        using var coordinator = new LibraryIntegrityCoordinator(catalog, new LibraryIntegrityScrubService(ffmpeg, runner),
            new LibraryStorageScheduler(new VolumeResolver()), () => encoding);
        coordinator.QueueFiles(Enumerable.Range(1, 8).Select(id => (long)id), LibraryIntegrityScrubType.Quick);
        await Task.Delay(300); Assert.Equal(0, runner.CallCount); encoding = false;
        await catalog.WaitForCompletionsAsync(8, TimeSpan.FromSeconds(10)); Assert.InRange(runner.MaximumConcurrent, 1, 2);
    }

    private LibraryIntegrityQueueItem Item(long id, string volume)
    {
        string path = FilePath($"{id}.mkv", 1024); var info = new FileInfo(path);
        return new(id, id, path, volume, info.Length, info.LastWriteTimeUtc, $"id-{id}", "h264", 60, 0,
            LibraryIntegrityScrubType.Quick, LibraryIntegrityQueueStatus.Pending, 0, 3, "", "", DateTime.UtcNow, DateTime.UtcNow);
    }
    private string FilePath(string name, int bytes = 1) { string path = Path.Combine(_root, name); if (!File.Exists(path)) File.WriteAllBytes(path, new byte[bytes]); return path; }
    private sealed class VolumeResolver : ILibraryStorageKeyResolver { public string ResolveStorageKey(string path, string reportedVolumeId = "") => reportedVolumeId; }
    private sealed class TrackingRunner : IMediaToolProcessRunner
    {
        private int _active, _maximum, _calls; private readonly Dictionary<string, int> _activeVolume = new(); public Dictionary<string, int> MaximumByVolume { get; } = new();
        public int MaximumConcurrent => _maximum; public int CallCount => _calls;
        public async Task<MediaToolProcessResult> RunAsync(MediaToolProcessRequest request, CancellationToken token = default)
        {
            Interlocked.Increment(ref _calls); string volume = Path.GetFileNameWithoutExtension(request.Arguments.First(value => value.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase)));
            int active = Interlocked.Increment(ref _active); InterlockedExtensions.Max(ref _maximum, active);
            lock (_activeVolume) { _activeVolume[volume] = _activeVolume.GetValueOrDefault(volume) + 1; MaximumByVolume[volume] = Math.Max(MaximumByVolume.GetValueOrDefault(volume), _activeVolume[volume]); }
            try { await Task.Delay(80, token); return new MediaToolProcessResult { ExitCode = 0 }; }
            finally { lock (_activeVolume) _activeVolume[volume]--; Interlocked.Decrement(ref _active); }
        }
        public void Reset() { _active = 0; _maximum = 0; _calls = 0; lock (_activeVolume) { _activeVolume.Clear(); MaximumByVolume.Clear(); } }
    }
    private sealed class FakeCatalog : ILibraryIntegrityCatalog
    {
        private readonly Dictionary<long, LibraryIntegrityQueueItem> _files; private readonly List<LibraryIntegrityQueueItem> _pending = new(); private int _completed; private TaskCompletionSource _changed = NewSignal();
        public FakeCatalog(IEnumerable<LibraryIntegrityQueueItem> files) => _files = files.ToDictionary(item => item.FileId);
        public long EnqueueIntegrity(long fileId, LibraryIntegrityScrubType type, string batchId = "", int maximumAttempts = 3) { lock (_pending) { LibraryIntegrityQueueItem item = _files[fileId] with { Id = DateTime.UtcNow.Ticks + fileId, ScrubType = type, Status = LibraryIntegrityQueueStatus.Pending }; _pending.Add(item); return item.Id; } }
        public IReadOnlyList<LibraryIntegrityQueueItem> ClaimIntegrityBatch(int limit, DateTime now) { lock (_pending) { LibraryIntegrityQueueItem[] items = _pending.Take(limit).ToArray(); _pending.RemoveRange(0, items.Length); return items.Select(item => item with { Status = LibraryIntegrityQueueStatus.Running }).ToArray(); } }
        public void CompleteIntegrityItem(long id, LibraryIntegrityResultWrite result, string error = "") => Complete(); public void CancelIntegrityItem(long id, LibraryIntegrityResultWrite result) => Complete();
        private void Complete() { Interlocked.Increment(ref _completed); _changed.TrySetResult(); }
        public async Task WaitForCompletionsAsync(int count, TimeSpan? timeout = null) { DateTime end = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5)); while (Volatile.Read(ref _completed) < count) { Task signal = _changed.Task; TimeSpan remain = end - DateTime.UtcNow; if (remain <= TimeSpan.Zero || await Task.WhenAny(signal, Task.Delay(remain)) != signal) throw new TimeoutException(); _changed = NewSignal(); } }
        public void ResetCompletions() { _completed = 0; _changed = NewSignal(); }
        private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int RecoverInterruptedIntegrity() => 0; public LibraryIntegrityPage QueryIntegrity(LibraryIntegrityQuery q) => new(0, Array.Empty<LibraryIntegrityResult>()); public LibraryIntegritySummary GetIntegritySummary() => new(0,0,0,0,0,0,0,0,0); public IReadOnlyList<long> GetIntegrityFileIds(long? l, LibraryIntegrityResultState? s, int limit = 50000) => Array.Empty<long>(); public LibraryIntegrityResult? GetIntegrityResult(long id) => null;
    }
    private static class InterlockedExtensions { public static void Max(ref int target, int value) { int current; while (value > (current = Volatile.Read(ref target)) && Interlocked.CompareExchange(ref target, value, current) != current) { } } }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
