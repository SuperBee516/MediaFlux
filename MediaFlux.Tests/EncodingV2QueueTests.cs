using MediaFlux.Models;
using MediaFlux.Services.EncodingV2;
using Xunit;

namespace MediaFlux.Tests;

public sealed class EncodingV2QueueTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MediaFlux-V2-queue-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _ffmpeg;

    public EncodingV2QueueTests()
    {
        Directory.CreateDirectory(_root);
        _ffmpeg = Path.Combine(_root, "ffmpeg.exe");
        File.WriteAllText(_ffmpeg, "test executable placeholder");
    }

    [Fact]
    public async Task SuccessQueuePreservesOrderIdsOrdinalsEventsAndTimings()
    {
        var progress = new V2FfmpegProgress(12, 24, 28, "750kB", .5, 1200, 2, 50);
        var executor = new DelegateQueueItemExecutor((itemId, request, observer, _, _) =>
            Task.FromResult(Result(request, observer, V2LifecycleState.Completed,
                ffmpegProgress: itemId == "first" ? progress : null)));
        V2EncodingQueueRequest request = Queue("first", "second", "third");
        var observed = new List<V2EncodingQueueEvent>();

        V2EncodingQueueResult result = await new V2EncodingQueueRunner(executor)
            .RunAsync(request, observed.Add);

        Assert.Equal(V2EncodingQueueState.Completed, result.State);
        Assert.Equal(3, result.AttemptedItems);
        Assert.Equal(3, result.CompletedItems);
        Assert.Equal(0, result.FailedItems);
        Assert.Equal(new[] { "first", "second", "third" }, result.Items.Select(item => item.ItemId));
        Assert.Equal(new[] { 1, 2, 3 }, result.Items.Select(item => item.ItemOrdinal));
        Assert.Equal(1, executor.MaximumActive);
        Assert.All(result.Items, item => Assert.True(double.IsFinite(item.ActiveItemMilliseconds)));
        Assert.All(result.Items.Skip(1), item => Assert.True(double.IsFinite(item.InterItemDispatchLatencyMilliseconds)));
        Assert.Equal(result.Events.Select(item => item.Sequence), result.Events.Select(item => item.Sequence).Order());
        Assert.Equal(result.Events.Select(item => item.Sequence), observed.Select(item => item.Sequence));
        Assert.Equal(3, result.Events.Count(item => item.Type == V2EncodingQueueEventType.ItemStarted));
        Assert.Equal(3, result.Events.Count(item => item.Type == V2EncodingQueueEventType.ItemCompleted));
        Assert.Equal(7, result.Events.Count(item => item.Type == V2EncodingQueueEventType.ItemLifecycle));
        V2EncodingQueueEvent progressEvent = Assert.Single(result.Events, item =>
            item.Type == V2EncodingQueueEventType.ItemLifecycle && item.ItemEvent?.FfmpegProgress is not null);
        Assert.Equal(progress, progressEvent.ItemEvent!.FfmpegProgress);
        Assert.Equal(progress, progressEvent.Progress!.ActiveItemProgress);
        Assert.Equal(0, result.PendingItems);
        Assert.False(result.ContinuedAfterFailures);
        Assert.Equal("queue-test", result.QueueId);
    }

    [Fact]
    public async Task MiddleFailureDoesNotPreventLaterItemsAndCountsContinuation()
    {
        var executor = new DelegateQueueItemExecutor((itemId, request, observer, _, _) =>
        {
            V2LifecycleState terminal = itemId == "three" ? V2LifecycleState.Failed : V2LifecycleState.Completed;
            return Task.FromResult(Result(request, observer, terminal,
                terminal == V2LifecycleState.Failed ? new V2Failure("InjectedForTest", "Expected failure.") : null));
        });

        V2EncodingQueueResult result = await new V2EncodingQueueRunner(executor).RunAsync(
            Queue("one", "two", "three", "four", "five"));

        Assert.Equal(V2EncodingQueueState.CompletedWithFailures, result.State);
        Assert.Equal(5, result.AttemptedItems);
        Assert.Equal(4, result.CompletedItems);
        Assert.Equal(1, result.FailedItems);
        Assert.Equal("InjectedForTest", result.Items[2].Failure?.Code);
        Assert.Equal(new[] { "one", "two", "three", "four", "five" }, executor.StartedIds);
        Assert.True(result.ContinuedAfterFailures);
        Assert.Equal("CompletedWithFailures", result.Events[^1].TerminalClassification);
    }

    [Fact]
    public async Task ArbitraryItemExceptionIsIsolatedAtQueueBoundary()
    {
        var executor = new DelegateQueueItemExecutor((itemId, request, observer, _, _) =>
        {
            if (itemId == "two") throw new InvalidOperationException("one job exploded");
            return Task.FromResult(SuccessResult(request, observer));
        });

        V2EncodingQueueResult result = await new V2EncodingQueueRunner(executor).RunAsync(Queue("one", "two", "three"));

        Assert.Equal(V2EncodingQueueState.CompletedWithFailures, result.State);
        Assert.Equal("QueueItemException", result.Items[1].Failure?.Code);
        Assert.Equal(new[] { "one", "two", "three" }, executor.StartedIds);
        Assert.Equal(1, result.FailedItems);
        Assert.Equal(2, result.CompletedItems);
        Assert.True(result.ContinuedAfterFailures);
    }

    [Fact]
    public async Task CancellationStopsActiveItemAndLeavesPendingItemsUnstarted()
    {
        using var cancellation = new CancellationTokenSource();
        var executor = new DelegateQueueItemExecutor((itemId, request, observer, _, _) =>
        {
            if (itemId == "active")
            {
                Emit(observer, request, V2LifecycleState.Queued, 1);
                cancellation.Cancel();
                return Task.FromResult(Result(request, observer, V2LifecycleState.Canceled,
                    new V2Failure("Canceled", "canceled while active"), includeQueued: false));
            }
            return Task.FromResult(SuccessResult(request, observer));
        });

        V2EncodingQueueResult result = await new V2EncodingQueueRunner(executor)
            .RunAsync(Queue("active", "pending-2", "pending-3"), cancellationToken: cancellation.Token);

        Assert.Equal(V2EncodingQueueState.Canceled, result.State);
        Assert.Equal(1, result.AttemptedItems);
        Assert.Equal(1, result.CanceledItems);
        Assert.Equal(2, result.PendingItems);
        Assert.All(result.Items.Skip(1), item => Assert.Equal(V2EncodingQueueItemState.PendingAtCancellation, item.State));
        Assert.All(result.Items.Skip(1), item => Assert.Null(item.Execution));
        Assert.Equal(new[] { "active" }, executor.StartedIds);
        Assert.Contains(result.Events, item => item.Type == V2EncodingQueueEventType.QueueCancellationRequested);
        Assert.Equal(2, result.Events.Count(item => item.Type == V2EncodingQueueEventType.ItemPendingAtCancellation));
    }

    [Fact]
    public async Task InvalidQueueConfigurationFailsBeforeStartingAnyItem()
    {
        var executor = new DelegateQueueItemExecutor((_, request, observer, _, _) => Task.FromResult(SuccessResult(request, observer)));
        V2EncodingQueueResult result = await new V2EncodingQueueRunner(executor).RunAsync(Queue("duplicate", "duplicate"));

        Assert.Equal(V2EncodingQueueState.Failed, result.State);
        Assert.Equal("InvalidQueueRequest", result.Failure?.Code);
        Assert.Equal(0, result.AttemptedItems);
        Assert.Empty(executor.StartedIds);
    }

    [Fact]
    public async Task CertificationInjectionIsDisabledByDefaultQueueRunner()
    {
        string source = CreateSource("disabled-injection-source");
        V2EncodingQueueRequest request = WithCertification(
            CoreQueue(CoreItem("injected", source, Path.Combine(_root, "disabled-injection.mp4"))),
            "disabled-cert", "injected", V2QueueFailurePoint.ProductionExecution);
        var executor = new DelegateQueueItemExecutor((_, itemRequest, observer, _, _) =>
            Task.FromResult(SuccessResult(itemRequest, observer)));

        V2EncodingQueueResult result = await new V2EncodingQueueRunner(executor).RunAsync(request);

        Assert.Equal(V2EncodingQueueState.Failed, result.State);
        Assert.Equal("InvalidQueueRequest", result.Failure?.Code);
        Assert.Equal(0, result.AttemptedItems);
        Assert.True(File.Exists(source));
        Assert.False(File.Exists(request.Items[0].Request!.OutputPath));
    }

    [Fact]
    public async Task QueuePathConflictIsRejectedBeforeOneOutputCanBecomeAnotherSource()
    {
        string source1 = CreateSource("overlap-first-source");
        string source2 = Path.Combine(_root, "overlap-shared-path.mp4");
        V2EncodingQueueRequest request = CoreQueue(
            CoreItem("first", source1, source2),
            CoreItem("second", source2, Path.Combine(_root, "overlap-second-output.mp4")));
        var executor = new DelegateQueueItemExecutor((_, itemRequest, observer, _, _) =>
            Task.FromResult(SuccessResult(itemRequest, observer)));

        V2EncodingQueueResult result = await new V2EncodingQueueRunner(executor).RunAsync(request);

        Assert.Equal(V2EncodingQueueState.Failed, result.State);
        Assert.Equal("InvalidQueueRequest", result.Failure?.Code);
        Assert.Equal(0, result.AttemptedItems);
        Assert.Empty(executor.StartedIds);
    }

    [Theory]
    [InlineData(V2QueueFailurePoint.BeforeProduction)]
    [InlineData(V2QueueFailurePoint.ProductionExecution)]
    [InlineData(V2QueueFailurePoint.Validation)]
    [InlineData(V2QueueFailurePoint.Promotion)]
    public async Task InjectedItemFailureCleansOnlyOwnedStagingAndContinues(V2QueueFailurePoint point)
    {
        string source1 = CreateSource("failure-source-" + point);
        string source2 = CreateSource("later-source-" + point);
        V2EncodingQueueRequest request = CoreQueue(
            CoreItem("failed", source1, Path.Combine(_root, "failed-" + point + ".mp4")),
            CoreItem("later", source2, Path.Combine(_root, "later-" + point + ".mp4")));
        request = WithCertification(request, "phase2-" + point, "failed", point);
        var runner = new V2EncodingQueueRunner(CoreExecutor());

        V2EncodingQueueResult result = await runner.RunAsync(request, allowCertificationInjection: true);

        Assert.Equal(V2EncodingQueueState.CompletedWithFailures, result.State);
        Assert.Equal(V2EncodingQueueItemState.Failed, result.Items[0].State);
        Assert.Equal(V2EncodingQueueItemState.Completed, result.Items[1].State);
        Assert.True(File.Exists(source1));
        Assert.True(File.Exists(source2));
        Assert.False(File.Exists(result.Items[0].Execution!.OutputPath));
        Assert.False(File.Exists(result.Items[0].Execution!.StagingPath));
        Assert.True(File.Exists(result.Items[1].Execution!.OutputPath));
        Assert.True(result.ContinuedAfterFailures);
        Assert.True(Assert.Single(result.Certification!.FailureInjections).Triggered);
    }

    [Fact]
    public async Task InjectedSourceDeletionFailureLeavesValidOutputAndContinuesWithWarning()
    {
        string source1 = CreateSource("delete-source");
        string source2 = CreateSource("delete-later-source");
        V2EncodingQueueRequest request = CoreQueue(
            CoreItem("delete-warning", source1, Path.Combine(_root, "delete-warning.mp4"), deleteSource: true),
            CoreItem("later", source2, Path.Combine(_root, "delete-later.mp4")));
        request = WithCertification(request, "phase2-deletion", "delete-warning", V2QueueFailurePoint.SourceDeletion);

        V2EncodingQueueResult result = await new V2EncodingQueueRunner(CoreExecutor())
            .RunAsync(request, allowCertificationInjection: true);

        Assert.Equal(V2EncodingQueueState.Completed, result.State);
        Assert.Equal(2, result.CompletedItems);
        Assert.Equal(1, result.WarningCount);
        Assert.True(File.Exists(source1));
        Assert.True(File.Exists(result.Items[0].Execution!.OutputPath));
        Assert.Contains(result.Items[0].Execution!.Warnings, warning => warning.Contains("certification injected", StringComparison.OrdinalIgnoreCase));
        Assert.True(Assert.Single(result.Certification!.FailureInjections).Triggered);
    }

    [Fact]
    public async Task ActiveCoreCancellationPreservesSourceCleansStageAndLeavesLaterJobsPending()
    {
        string source1 = CreateSource("cancel-source");
        string source2 = CreateSource("cancel-pending-source");
        using var cancellation = new CancellationTokenSource();
        V2EncodingQueueRequest request = CoreQueue(
            CoreItem("active", source1, Path.Combine(_root, "cancel-active.mp4")),
            CoreItem("pending", source2, Path.Combine(_root, "cancel-pending.mp4")));
        V2CoreQueueItemExecutor executor = new((itemRequest, injection, triggered) => new V2EncodingCore(
            new FakeMetadata(), new V2EncodePlanner(), new CancelingProductionExecutor(cancellation),
            new FakeValidator(), new V2FileOutputPromoter(), _ffmpeg));

        V2EncodingQueueResult result = await new V2EncodingQueueRunner(executor)
            .RunAsync(request, cancellationToken: cancellation.Token);

        Assert.Equal(V2EncodingQueueState.Canceled, result.State);
        Assert.Equal(V2EncodingQueueItemState.Canceled, result.Items[0].State);
        Assert.Equal(V2EncodingQueueItemState.PendingAtCancellation, result.Items[1].State);
        Assert.True(File.Exists(source1));
        Assert.True(File.Exists(source2));
        Assert.False(File.Exists(result.Items[0].Execution!.StagingPath));
        Assert.False(File.Exists(result.Items[0].Execution!.OutputPath));
    }

    [Fact]
    public async Task ProductionExecutorProjectsStructuredFfmpegStatisticsIntoV2Progress()
    {
        string source = CreateSource("progress-source");
        V2MediaFacts facts = await new FakeMetadata().ProbeAsync(source);
        FrozenEncodePlan plan = new V2EncodePlanner().CreatePlan(
            Request(Guid.NewGuid().ToString("N"), source, Path.Combine(_root, "progress.mp4")), facts, _ffmpeg);
        var processRunner = new ProgressReportingProcessRunner();
        var executor = new V2ProductionExecutor(processRunner);
        V2FfmpegProgress? observed = null;

        V2ProcessOutcome outcome = await executor.ExecuteWithProgressAsync(plan, null, value => observed = value);

        Assert.Equal(0, outcome.ExitCode);
        Assert.NotNull(observed);
        Assert.Equal(48, observed!.Frame);
        Assert.Equal(24, observed.FramesPerSecond);
        Assert.Equal(28, observed.Quantizer);
        Assert.Equal("750000 bytes", observed.OutputSize);
        Assert.Equal(.5, observed.OutputTimeSeconds);
        Assert.Equal(12288, observed.BitrateKbps);
        Assert.Equal(2, observed.SpeedMultiplier);
        Assert.Equal(50, observed.PercentComplete);
        Assert.Contains("-progress", processRunner.Arguments);
        Assert.Contains("pipe:2", processRunner.Arguments);
        Assert.Contains("-stats_period", processRunner.Arguments);
        Assert.Contains("1", processRunner.Arguments);
    }

    [Fact]
    public void HeadlessQueueCommandsAreExplicitAndSeparateFromNormalCertificationMode()
    {
        Assert.True(EncodingV2HeadlessCommand.TryParse(["--v2-run-queue", "queue.json"], out var run, out _, out _));
        Assert.Equal(EncodingV2HeadlessOperation.RunQueue, run);
        Assert.True(EncodingV2HeadlessCommand.TryParse(["--v2-run-queue-certification", "cert.json"], out var certify, out _, out _));
        Assert.Equal(EncodingV2HeadlessOperation.CertifyQueue, certify);
        Assert.False(EncodingV2HeadlessCommand.TryParse(["--v2-run-queue", "queue.json", "--inject"], out _, out _, out _));
    }

    [Fact]
    public async Task NormalHeadlessQueueCommandRejectsCertificationRequestData()
    {
        V2EncodingQueueRequest request = WithCertification(
            CoreQueue(CoreItem("item", CreateSource("headless-cert-source"), Path.Combine(_root, "headless-cert.mp4"))),
            "headless-cert", "item", V2QueueFailurePoint.Validation);
        string path = Path.Combine(_root, "queue.json");
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(request));
        using var output = new StringWriter();
        using var error = new StringWriter();

        int exitCode = await EncodingV2HeadlessCommand.RunAsync(
            EncodingV2HeadlessOperation.RunQueue, path, output, error);

        Assert.Equal(2, exitCode);
        Assert.Contains("CertificationRequiresExplicitCommand", output.ToString());
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private V2CoreQueueItemExecutor CoreExecutor() => new((request, injection, triggered) =>
        V2QueueCertificationCoreFactory.Create(new FakeMetadata(), new V2EncodePlanner(),
            new WritingProductionExecutor(), new FakeValidator(), new V2FileOutputPromoter(), _ffmpeg,
            injection, triggered));

    private V2EncodingQueueRequest CoreQueue(params V2EncodingQueueItemRequest[] items) => new()
    {
        QueueId = "core-test-" + Guid.NewGuid().ToString("N"),
        Items = items.ToList()
    };

    private V2EncodingQueueRequest WithCertification(V2EncodingQueueRequest request,
        string certificationId, string itemId, V2QueueFailurePoint point) => new()
    {
        QueueId = request.QueueId,
        Items = request.Items,
        Certification = new V2QueueCertificationRequest
        {
            CertificationId = certificationId,
            FailureInjections = [new V2QueueFailureInjection { ItemId = itemId, Point = point }]
        }
    };

    private V2EncodingQueueItemRequest CoreItem(string itemId, string source, string output, bool deleteSource = false) => new()
    {
        ItemId = itemId,
        Request = Request(Guid.NewGuid().ToString("N"), source, output, deleteSource)
    };

    private string CreateSource(string name)
    {
        string path = Path.Combine(_root, name + ".mp4");
        File.WriteAllBytes(path, Enumerable.Range(0, 1024).Select(value => (byte)value).ToArray());
        return path;
    }

    private static EncodingV2Request Request(string jobId, string source, string output, bool deleteSource = false) => new()
    {
        SchemaVersion = 1,
        JobId = jobId,
        SourcePath = source,
        OutputPath = output,
        Encoder = "libx265",
        Codec = "hevc",
        Container = "mp4",
        QualityMode = "crf",
        QualityValue = 27,
        Preset = "ultrafast",
        PixelFormat = "yuv420p",
        GeometryMode = "source",
        AudioMode = "none",
        SubtitleMode = "none",
        DeleteSourceOnSuccess = deleteSource
    };

    private V2EncodingQueueRequest Queue(params string[] ids)
    {
        V2EncodingQueueItemRequest[] items = ids.Select(id => new V2EncodingQueueItemRequest
        {
            ItemId = id,
            Request = new EncodingV2Request { JobId = Guid.NewGuid().ToString("N") }
        }).ToArray();
        return new V2EncodingQueueRequest { QueueId = "queue-test", Items = items.ToList() };
    }

    private static V2ExecutionResult SuccessResult(EncodingV2Request request, Action<V2LifecycleEvent>? observer) =>
        Result(request, observer, V2LifecycleState.Completed);

    private static V2ExecutionResult Result(EncodingV2Request request, Action<V2LifecycleEvent>? observer,
        V2LifecycleState state, V2Failure? failure = null, bool includeQueued = true,
        V2FfmpegProgress? ffmpegProgress = null)
    {
        List<V2LifecycleEvent> events = [];
        int sequence = 0;
        if (includeQueued) Emit(observer, request, V2LifecycleState.Queued, ++sequence, events);
        if (ffmpegProgress is not null)
            Emit(observer, request, V2LifecycleState.Encoding, ++sequence, events, ffmpegProgress);
        Emit(observer, request, state, ++sequence, events);
        return new V2ExecutionResult
        {
            JobId = request.JobId ?? "generated-job",
            State = state,
            SourcePath = request.SourcePath,
            OutputPath = request.OutputPath,
            Failure = failure,
            Warnings = Array.Empty<string>(),
            Events = events.AsReadOnly(),
            Timings = new V2StageDurations(ProbeMilliseconds: 0.1, EncodeMilliseconds: 0.2, TotalMilliseconds: 0.3)
        };
    }

    private static void Emit(Action<V2LifecycleEvent>? observer, EncodingV2Request request,
        V2LifecycleState state, int sequence, List<V2LifecycleEvent>? events = null,
        V2FfmpegProgress? ffmpegProgress = null)
    {
        var itemEvent = new V2LifecycleEvent(1, request.JobId ?? "generated-job", sequence, DateTime.UtcNow,
            state, state == V2LifecycleState.Queued ? "job.queued" : "job.terminal", state.ToString(),
            FfmpegProgress: ffmpegProgress);
        events?.Add(itemEvent);
        observer?.Invoke(itemEvent);
    }

    private sealed class DelegateQueueItemExecutor(
        Func<string, EncodingV2Request, Action<V2LifecycleEvent>?, Action, CancellationToken, Task<V2ExecutionResult>> handler)
        : IV2QueueItemExecutor
    {
        private int _active;
        public int MaximumActive { get; private set; }
        public List<string> StartedIds { get; } = [];

        public async Task<V2ExecutionResult> RunAsync(string itemId, EncodingV2Request request,
            V2QueueFailureInjection? failureInjection, Action<V2LifecycleEvent>? eventObserver,
            Action failureInjectionTriggered, CancellationToken cancellationToken)
        {
            StartedIds.Add(itemId);
            int active = Interlocked.Increment(ref _active);
            MaximumActive = Math.Max(MaximumActive, active);
            try { return await handler(itemId, request, eventObserver, failureInjectionTriggered, cancellationToken); }
            finally { Interlocked.Decrement(ref _active); }
        }
    }

    private sealed class FakeMetadata : IV2SourceMetadataProvider
    {
        public Task<V2MediaFacts> ProbeAsync(string sourcePath, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(sourcePath)) throw new FileNotFoundException("fake source missing", sourcePath);
            FileInfo info = new(sourcePath);
            var video = new MediaProbeStreamInfo
            {
                Index = 0, CodecType = "video", CodecName = "h264", Width = 64, Height = 32,
                PixelFormat = "yuv420p", DurationSeconds = 1
            };
            return Task.FromResult(new V2MediaFacts
            {
                SourcePath = Path.GetFullPath(sourcePath),
                Probe = new MediaProbeResult
                {
                    Success = true, FormatName = "mov,mp4,m4a,3gp,3g2,mj2", DurationSeconds = 1,
                    SizeBytes = info.Length, Streams = [video]
                },
                SourceLength = info.Length,
                SourceLastWriteUtcTicks = info.LastWriteTimeUtc.Ticks,
                PrimaryVideoCodec = video.CodecName,
                SourceWidth = 64,
                SourceHeight = 32
            });
        }
    }

    private sealed class WritingProductionExecutor : IV2ProductionExecutor
    {
        public Task<V2ProcessOutcome> ExecuteAsync(FrozenEncodePlan plan, Action<TimeSpan>? processStarted = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            processStarted?.Invoke(TimeSpan.FromMilliseconds(1));
            File.WriteAllBytes(plan.StagingPath, Enumerable.Repeat((byte)0x42, 1024).ToArray());
            return Task.FromResult(new V2ProcessOutcome(0, false, TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1), ""));
        }
    }

    private sealed class CancelingProductionExecutor(CancellationTokenSource cancellation) : IV2ProductionExecutor
    {
        public Task<V2ProcessOutcome> ExecuteAsync(FrozenEncodePlan plan, Action<TimeSpan>? processStarted = null,
            CancellationToken cancellationToken = default)
        {
            cancellation.Cancel();
            return Task.FromCanceled<V2ProcessOutcome>(cancellationToken);
        }
    }

    private sealed class ProgressReportingProcessRunner : MediaFlux.Services.IMediaToolProcessRunner
    {
        public IReadOnlyList<string> Arguments { get; private set; } = Array.Empty<string>();

        public Task<MediaFlux.Services.MediaToolProcessResult> RunAsync(
            MediaFlux.Services.MediaToolProcessRequest request, CancellationToken cancellationToken = default)
        {
            Arguments = request.Arguments;
            request.StandardErrorLineCallback?.Invoke("frame=48");
            request.StandardErrorLineCallback?.Invoke("fps=24.0");
            request.StandardErrorLineCallback?.Invoke("q=28.0");
            request.StandardErrorLineCallback?.Invoke("total_size=750000");
            request.StandardErrorLineCallback?.Invoke("out_time_us=500000");
            request.StandardErrorLineCallback?.Invoke("bitrate=12288.0kbits/s");
            request.StandardErrorLineCallback?.Invoke("speed=2.0x");
            request.StandardErrorLineCallback?.Invoke("progress=continue");
            return Task.FromResult(new MediaFlux.Services.MediaToolProcessResult { ExitCode = 0 });
        }
    }

    private sealed class FakeValidator : IV2CandidateValidator
    {
        public Task<V2ValidationOutcome> ValidateAsync(FrozenEncodePlan plan, V2MediaFacts source,
            string candidatePath, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileInfo output = new(candidatePath);
            return Task.FromResult(new V2ValidationOutcome(true, "fake output validated", output.Length, output.LastWriteTimeUtc.Ticks));
        }
    }
}
