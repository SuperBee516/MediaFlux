using System.Diagnostics;

namespace MediaFlux.Services.EncodingV2;

/// <summary>Versioned sequential queue envelope for existing fixed-plan V2 requests.</summary>
public sealed class V2EncodingQueueRequest
{
    public int SchemaVersion { get; init; } = 1;
    public string? QueueId { get; init; }
    public string? EventLogPath { get; init; }
    public List<V2EncodingQueueItemRequest> Items { get; init; } = [];
    public V2QueueCertificationRequest? Certification { get; init; }
}

public sealed class V2EncodingQueueItemRequest
{
    public string ItemId { get; init; } = "";
    public EncodingV2Request? Request { get; init; }
}

public enum V2EncodingQueueState { Queued, Running, Completed, CompletedWithFailures, Canceled, Failed }
public enum V2EncodingQueueItemState { Completed, Failed, Canceled, Skipped, PendingAtCancellation }
public enum V2EncodingQueueEventType
{
    QueueStarted,
    ItemStarted,
    ItemLifecycle,
    ItemCompleted,
    ItemFailed,
    ItemCanceled,
    ItemSkipped,
    ItemPendingAtCancellation,
    QueueCancellationRequested,
    QueueCompleted,
    QueueFailed
}

public sealed record V2EncodingQueueProgress(
    int ItemOrdinal,
    int ItemCount,
    string? ActiveItemId,
    V2LifecycleState? ActiveItemState,
    int CompletedItems,
    int FailedItems,
    int CanceledItems,
    int PendingItems,
    V2FfmpegProgress? ActiveItemProgress = null);

public sealed record V2EncodingQueueEvent(
    int SchemaVersion,
    string QueueId,
    long Sequence,
    DateTime TimestampUtc,
    V2EncodingQueueEventType Type,
    string? ItemId = null,
    int? ItemOrdinal = null,
    string? TerminalClassification = null,
    string? Message = null,
    V2LifecycleEvent? ItemEvent = null,
    V2EncodingQueueProgress? Progress = null);

public sealed record V2EncodingQueueItemResult(
    string ItemId,
    int ItemOrdinal,
    V2EncodingQueueItemState State,
    double QueueWaitMilliseconds,
    double InterItemDispatchLatencyMilliseconds,
    double ActiveItemMilliseconds,
    V2ExecutionResult? Execution,
    V2Failure? Failure = null);

public sealed record V2EncodingQueueTimings(
    double TotalMilliseconds,
    double TotalFfmpegMilliseconds,
    double OrchestrationOverheadMilliseconds,
    double AverageInterItemDispatchLatencyMilliseconds,
    double MaximumInterItemDispatchLatencyMilliseconds);

public sealed class V2EncodingQueueResult
{
    public int SchemaVersion { get; init; } = 1;
    public required string QueueId { get; init; }
    public required DateTime StartedUtc { get; init; }
    public required DateTime EndedUtc { get; init; }
    public required V2EncodingQueueState State { get; init; }
    public int ItemCount { get; init; }
    public int AttemptedItems { get; init; }
    public int CompletedItems { get; init; }
    public int FailedItems { get; init; }
    public int CanceledItems { get; init; }
    public int SkippedItems { get; init; }
    public int PendingItems { get; init; }
    public int WarningCount { get; init; }
    public bool ContinuedAfterFailures { get; init; }
    public V2Failure? Failure { get; init; }
    public V2EncodingQueueTimings Timings { get; init; } = new(0, 0, 0, 0, 0);
    public IReadOnlyList<V2EncodingQueueItemResult> Items { get; init; } = Array.Empty<V2EncodingQueueItemResult>();
    public IReadOnlyList<V2EncodingQueueEvent> Events { get; init; } = Array.Empty<V2EncodingQueueEvent>();
    public V2QueueCertificationEvidence? Certification { get; init; }
}

/// <summary>One explicitly configured failure for the certification-only headless command.</summary>
public sealed class V2QueueCertificationRequest
{
    public string CertificationId { get; init; } = "";
    public List<V2QueueFailureInjection> FailureInjections { get; init; } = [];
}

public sealed class V2QueueFailureInjection
{
    public string ItemId { get; init; } = "";
    public V2QueueFailurePoint Point { get; init; }
}

public enum V2QueueFailurePoint { BeforeProduction, ProductionExecution, Validation, Promotion, SourceDeletion }

public sealed record V2QueueFailureInjectionEvidence(
    string ItemId,
    V2QueueFailurePoint Point,
    bool Triggered,
    string Outcome);

public sealed record V2QueueCertificationEvidence(
    string CertificationId,
    IReadOnlyList<V2QueueFailureInjectionEvidence> FailureInjections);

public interface IV2QueueItemExecutor
{
    Task<V2ExecutionResult> RunAsync(
        string itemId,
        EncodingV2Request request,
        V2QueueFailureInjection? failureInjection,
        Action<V2LifecycleEvent>? eventObserver,
        Action failureInjectionTriggered,
        CancellationToken cancellationToken);
}

/// <summary>Adapts the queue to the existing single-job V2 lifecycle owner.</summary>
public sealed class V2CoreQueueItemExecutor(Func<EncodingV2Request, V2QueueFailureInjection?, Action, V2EncodingCore> coreFactory)
    : IV2QueueItemExecutor
{
    private readonly Func<EncodingV2Request, V2QueueFailureInjection?, Action, V2EncodingCore> _coreFactory =
        coreFactory ?? throw new ArgumentNullException(nameof(coreFactory));

    public Task<V2ExecutionResult> RunAsync(
        string itemId,
        EncodingV2Request request,
        V2QueueFailureInjection? failureInjection,
        Action<V2LifecycleEvent>? eventObserver,
        Action failureInjectionTriggered,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(failureInjectionTriggered);
        return _coreFactory(request, failureInjection, failureInjectionTriggered)
            .RunAsync(request, eventObserver, cancellationToken);
    }
}

/// <summary>Coordinates fixed-plan V2 jobs sequentially. It owns no media-processing or source-deletion behavior.</summary>
public sealed class V2EncodingQueueRunner(IV2QueueItemExecutor itemExecutor)
{
    private readonly IV2QueueItemExecutor _itemExecutor = itemExecutor ?? throw new ArgumentNullException(nameof(itemExecutor));

    public async Task<V2EncodingQueueResult> RunAsync(
        V2EncodingQueueRequest request,
        Action<V2EncodingQueueEvent>? eventObserver = null,
        CancellationToken cancellationToken = default,
        bool allowCertificationInjection = false)
    {
        ArgumentNullException.ThrowIfNull(request);
        DateTime startedUtc = DateTime.UtcNow;
        long queueStarted = Stopwatch.GetTimestamp();
        string queueId = string.IsNullOrWhiteSpace(request.QueueId) ? Guid.NewGuid().ToString("N") : request.QueueId.Trim();
        List<V2EncodingQueueEvent> events = [];
        List<V2EncodingQueueItemResult> results = [];
        List<double> dispatchLatencies = [];
        object eventGate = new();
        long sequence = 0;
        int completed = 0, failed = 0, canceled = 0, skipped = 0, pending = 0, attempted = 0, warnings = 0;
        string? activeItemId = null;
        V2LifecycleState? activeState = null;
        V2FfmpegProgress? activeProgress = null;
        long previousTerminalTimestamp = 0;
        V2Failure? queueFailure = null;
        bool continuedAfterFailure = false;
        V2EncodingQueueState state = V2EncodingQueueState.Running;
        Dictionary<string, V2QueueFailureInjection> injections = new(StringComparer.Ordinal);
        Dictionary<string, (V2QueueFailureInjection Injection, bool Triggered, string Outcome)> injectionEvidence =
            new(StringComparer.Ordinal);

        V2EncodingQueueProgress Progress(int ordinal = 0) => new(
            ordinal, request.Items?.Count ?? 0, activeItemId, activeState, completed, failed, canceled, pending, activeProgress);

        void Emit(V2EncodingQueueEventType type, string? itemId = null, int? ordinal = null,
            string? terminal = null, string? message = null, V2LifecycleEvent? itemEvent = null,
            V2EncodingQueueProgress? progress = null)
        {
            lock (eventGate)
            {
                var value = new V2EncodingQueueEvent(1, queueId, ++sequence, DateTime.UtcNow,
                    type, itemId, ordinal, terminal, message, itemEvent, progress ?? Progress(ordinal ?? 0));
                events.Add(value);
                try { eventObserver?.Invoke(value); } catch { /* Observers cannot control queue execution. */ }
            }
        }

        Emit(V2EncodingQueueEventType.QueueStarted, message: "V2 queue execution started.");
        using CancellationTokenRegistration cancellationRegistration = cancellationToken.Register(() =>
            Emit(V2EncodingQueueEventType.QueueCancellationRequested,
                message: "Queue cancellation was requested; the active item will stop safely and pending items will not start."));

        string? requestError = ValidateRequest(request, allowCertificationInjection);
        if (requestError is not null)
        {
            queueFailure = new V2Failure("InvalidQueueRequest", requestError);
            state = V2EncodingQueueState.Failed;
            cancellationRegistration.Dispose();
            Emit(V2EncodingQueueEventType.QueueFailed, terminal: state.ToString(), message: requestError);
            return BuildResult();
        }

        injections = request.Certification?.FailureInjections
            .ToDictionary(item => item.ItemId, StringComparer.Ordinal) ?? new(StringComparer.Ordinal);
        injectionEvidence = injections.ToDictionary(pair => pair.Key,
            pair => (pair.Value, false, "NotReached"), StringComparer.Ordinal);

        for (int index = 0; index < request.Items.Count; index++)
        {
            V2EncodingQueueItemRequest item = request.Items[index];
            int ordinal = index + 1;
            string itemId = item.ItemId;

            if (cancellationToken.IsCancellationRequested)
            {
                pending++;
                results.Add(new V2EncodingQueueItemResult(itemId, ordinal,
                    V2EncodingQueueItemState.PendingAtCancellation, 0, 0, 0, null,
                    new V2Failure("PendingAtCancellation", "The item was not started because queue cancellation was requested.")));
                Emit(V2EncodingQueueEventType.ItemPendingAtCancellation, itemId, ordinal,
                    nameof(V2EncodingQueueItemState.PendingAtCancellation), "Item was not started.",
                    progress: Progress(ordinal));
                continue;
            }

            activeItemId = itemId;
            activeState = V2LifecycleState.Queued;
            activeProgress = null;
            attempted++;
            Emit(V2EncodingQueueEventType.ItemStarted, itemId, ordinal,
                message: $"Starting item {ordinal} of {request.Items.Count}.", progress: Progress(ordinal));

            double interItemDispatchMs = previousTerminalTimestamp == 0
                ? 0
                : Stopwatch.GetElapsedTime(previousTerminalTimestamp).TotalMilliseconds;
            if (previousTerminalTimestamp != 0) dispatchLatencies.Add(interItemDispatchMs);
            double queueWaitMs = Stopwatch.GetElapsedTime(queueStarted).TotalMilliseconds;
            long itemStarted = Stopwatch.GetTimestamp();
            V2ExecutionResult execution;
            V2QueueFailureInjectionEvidence? itemInjectionEvidence = null;
            try
            {
                if (item.Request is null)
                    throw new V2PlanningException("InvalidItemRequest", "The queue item does not contain an existing V2 request.");
                if (item.Request.SchemaVersion != 1)
                    throw new V2PlanningException("UnsupportedSchema", $"Unsupported V2 request schema version {item.Request.SchemaVersion}.");
                if (!string.IsNullOrWhiteSpace(item.Request.EventLogPath))
                    throw new V2PlanningException("PerItemEventLogUnsupported", "Queue lifecycle events use the queue-level eventLogPath.");

                V2QueueFailureInjection? injection = injections.GetValueOrDefault(itemId);
                bool injectionTriggered = false;
                execution = await _itemExecutor.RunAsync(itemId, item.Request, injection, itemEvent =>
                {
                    activeState = itemEvent.State;
                    activeProgress = itemEvent.State == V2LifecycleState.Encoding
                        ? itemEvent.FfmpegProgress ?? activeProgress
                        : null;
                    Emit(V2EncodingQueueEventType.ItemLifecycle, itemId, ordinal,
                        message: itemEvent.Stage, itemEvent: itemEvent, progress: Progress(ordinal));
                }, () => injectionTriggered = true, cancellationToken).ConfigureAwait(false);

                if (injection is not null)
                {
                    string outcome = injectionTriggered
                        ? execution.State == V2LifecycleState.Completed && injection.Point == V2QueueFailurePoint.SourceDeletion
                            ? "Triggered; valid output completed with source-deletion warning."
                            : $"Triggered; item ended {execution.State}."
                        : "Configured but the selected lifecycle point was not reached.";
                    itemInjectionEvidence = new V2QueueFailureInjectionEvidence(itemId, injection.Point, injectionTriggered, outcome);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                execution = SyntheticResult(item, V2LifecycleState.Canceled,
                    new V2Failure("Canceled", "The active V2 item was canceled; its source was retained."));
            }
            catch (Exception ex)
            {
                V2Failure itemFailure = ex is V2PlanningException planning
                    ? new V2Failure(planning.Code, planning.Message)
                    : new V2Failure("QueueItemException", ex.Message);
                execution = SyntheticResult(item, V2LifecycleState.Failed, itemFailure);
                if (injections.TryGetValue(itemId, out V2QueueFailureInjection? thrownInjection))
                    itemInjectionEvidence = new V2QueueFailureInjectionEvidence(itemId, thrownInjection.Point, false,
                        "Item executor threw before returning a V2 lifecycle result.");
            }

            double itemActiveMs = Stopwatch.GetElapsedTime(itemStarted).TotalMilliseconds;
            warnings += execution.Warnings.Count;
            V2EncodingQueueItemState itemState = execution.State switch
            {
                V2LifecycleState.Completed => V2EncodingQueueItemState.Completed,
                V2LifecycleState.Canceled => V2EncodingQueueItemState.Canceled,
                V2LifecycleState.Skipped => V2EncodingQueueItemState.Skipped,
                _ => V2EncodingQueueItemState.Failed
            };
            switch (itemState)
            {
                case V2EncodingQueueItemState.Completed: completed++; break;
                case V2EncodingQueueItemState.Failed: failed++; break;
                case V2EncodingQueueItemState.Canceled: canceled++; break;
                case V2EncodingQueueItemState.Skipped: skipped++; break;
            }
            activeItemId = null;
            activeState = null;
            activeProgress = null;
            results.Add(new V2EncodingQueueItemResult(itemId, ordinal, itemState, queueWaitMs,
                interItemDispatchMs, itemActiveMs, execution, execution.Failure));
            V2EncodingQueueEventType terminalEvent = itemState switch
            {
                V2EncodingQueueItemState.Completed => V2EncodingQueueEventType.ItemCompleted,
                V2EncodingQueueItemState.Canceled => V2EncodingQueueEventType.ItemCanceled,
                V2EncodingQueueItemState.Skipped => V2EncodingQueueEventType.ItemSkipped,
                _ => V2EncodingQueueEventType.ItemFailed
            };
            Emit(terminalEvent, itemId, ordinal, itemState.ToString(),
                execution.Failure?.Message ?? $"Item {ordinal} reached terminal state {itemState}.",
                progress: Progress(ordinal));
            previousTerminalTimestamp = Stopwatch.GetTimestamp();

            if (itemInjectionEvidence is not null)
                injectionEvidence[itemId] = (injections[itemId], itemInjectionEvidence.Triggered, itemInjectionEvidence.Outcome);

        }

        bool wasCanceled = cancellationToken.IsCancellationRequested || canceled > 0 || pending > 0;
        cancellationRegistration.Dispose();
        state = wasCanceled ? V2EncodingQueueState.Canceled
            : failed > 0 ? V2EncodingQueueState.CompletedWithFailures
            : V2EncodingQueueState.Completed;
        Emit(V2EncodingQueueEventType.QueueCompleted, terminal: state.ToString(),
            message: state == V2EncodingQueueState.CompletedWithFailures
                ? "Queue completed after isolating one or more item failures."
                : state == V2EncodingQueueState.Canceled ? "Queue canceled; pending items were not started." : "Queue completed.");
        return BuildResult();

        V2EncodingQueueResult BuildResult()
        {
            DateTime endedUtc = DateTime.UtcNow;
            double totalMs = Stopwatch.GetElapsedTime(queueStarted).TotalMilliseconds;
            double totalFfmpegMs = results.Sum(item => item.Execution?.Timings.EncodeMilliseconds ?? 0);
            double averageDispatchMs = dispatchLatencies.Count == 0 ? 0 : dispatchLatencies.Average();
            var certificationEvidence = request.Certification is null ? null : new V2QueueCertificationEvidence(
                request.Certification.CertificationId,
                (request.Certification.FailureInjections ?? []).Where(injection => injection is not null).Select(injection =>
                {
                    if (!injectionEvidence.TryGetValue(injection.ItemId, out var evidence))
                        return new V2QueueFailureInjectionEvidence(injection.ItemId, injection.Point, false, "NotReached");
                    return new V2QueueFailureInjectionEvidence(injection.ItemId, injection.Point, evidence.Triggered, evidence.Outcome);
                }).ToArray());
            continuedAfterFailure = results.Any(result => result.State == V2EncodingQueueItemState.Failed &&
                results.Any(later => later.ItemOrdinal > result.ItemOrdinal &&
                    later.State != V2EncodingQueueItemState.PendingAtCancellation));
            return new V2EncodingQueueResult
            {
                QueueId = queueId,
                StartedUtc = startedUtc,
                EndedUtc = endedUtc,
                State = state,
                ItemCount = request.Items?.Count ?? 0,
                AttemptedItems = attempted,
                CompletedItems = completed,
                FailedItems = failed,
                CanceledItems = canceled,
                SkippedItems = skipped,
                PendingItems = pending,
                WarningCount = warnings,
                ContinuedAfterFailures = continuedAfterFailure,
                Failure = queueFailure,
                Timings = new V2EncodingQueueTimings(totalMs, totalFfmpegMs,
                    Math.Max(0, totalMs - results.Sum(item => item.ActiveItemMilliseconds)), averageDispatchMs,
                    dispatchLatencies.Count == 0 ? 0 : dispatchLatencies.Max()),
                Items = Array.AsReadOnly(results.ToArray()),
                Events = Array.AsReadOnly(events.ToArray()),
                Certification = certificationEvidence
            };
        }
    }

    private static string? ValidateRequest(V2EncodingQueueRequest request, bool allowCertificationInjection)
    {
        if (request.SchemaVersion != 1) return $"Unsupported V2 queue schema version {request.SchemaVersion}.";
        if (string.IsNullOrWhiteSpace(request.QueueId)) return "Every queue request requires a stable queueId.";
        if (request.Items is null) return "The queue items collection is required.";
        if (request.Items.Any(item => item is null)) return "Queue items cannot contain null entries.";
        if (request.Items.Any(item => string.IsNullOrWhiteSpace(item.ItemId))) return "Every queue item requires a stable itemId.";
        if (request.Items.Select(item => item.ItemId).Distinct(StringComparer.Ordinal).Count() != request.Items.Count)
            return "Queue itemId values must be unique within the queue.";
        if (request.Certification is { } certification)
        {
            if (!allowCertificationInjection)
                return "Certification failure injection is disabled for this queue runner; use the explicit headless certification command.";
            if (string.IsNullOrWhiteSpace(certification.CertificationId)) return "A certificationId is required when certification failure injection is configured.";
            if (certification.FailureInjections is null) return "The certification failureInjections collection is required.";
            if (certification.FailureInjections.Any(injection => injection is null || string.IsNullOrWhiteSpace(injection.ItemId)))
                return "Every failure injection requires a valid itemId.";
            if (certification.FailureInjections.Select(injection => injection.ItemId).Distinct(StringComparer.Ordinal).Count() != certification.FailureInjections.Count)
                return "At most one failure injection may be configured for an item.";
            if (certification.FailureInjections.Any(injection => !request.Items.Any(item => item.ItemId == injection.ItemId)))
                return "Every failure injection must reference an item in the queue.";
        }
        if (!string.IsNullOrWhiteSpace(request.EventLogPath))
        {
            if (!Path.IsPathFullyQualified(request.EventLogPath)) return "eventLogPath must be fully qualified.";
            string eventPath;
            try { eventPath = Path.GetFullPath(request.EventLogPath); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            { return "eventLogPath is invalid: " + ex.Message; }
            foreach (V2EncodingQueueItemRequest item in request.Items)
            {
                string[] paths = [item.Request?.SourcePath ?? "", item.Request?.OutputPath ?? ""];
                foreach (string path in paths.Where(value => !string.IsNullOrWhiteSpace(value)))
                {
                    try
                    {
                        if (string.Equals(eventPath, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
                            return "eventLogPath must differ from every queue source and output path.";
                    }
                    catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                    { /* A malformed item path remains an item-level failure. */ }
                }
            }
        }
        var normalizedSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalizedOutputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (V2EncodingQueueItemRequest item in request.Items)
        {
            if (item.Request is null) continue;
            try
            {
                if (!string.IsNullOrWhiteSpace(item.Request.SourcePath))
                    normalizedSources.Add(Path.GetFullPath(item.Request.SourcePath));
                if (!string.IsNullOrWhiteSpace(item.Request.OutputPath))
                    normalizedOutputs.Add(Path.GetFullPath(item.Request.OutputPath));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            { /* Malformed media paths are reported by the owning item when it reaches its turn. */ }
        }
        if (normalizedOutputs.Overlaps(normalizedSources))
            return "A queue output path cannot also be a source path for an item in the same queue.";
        return null;
    }

    private static V2ExecutionResult SyntheticResult(V2EncodingQueueItemRequest item,
        V2LifecycleState state, V2Failure failure) => new()
    {
        JobId = item.Request?.JobId ?? item.ItemId,
        State = state,
        SourcePath = item.Request?.SourcePath ?? "",
        OutputPath = item.Request?.OutputPath ?? "",
        Failure = failure,
        SourceDeletionRequested = item.Request?.DeleteSourceOnSuccess ?? false,
        SourceDeleted = item.Request?.DeleteSourceOnSuccess == true ? false : null
    };
}
