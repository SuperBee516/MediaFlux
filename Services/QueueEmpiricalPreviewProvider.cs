using MediaFlux.Models;
using MediaFlux.Services.Encoders;

namespace MediaFlux.Services;

internal sealed record QueueProductionPlanEstimate(
    PreparedProductionPlan PreparedPlan,
    EmpiricalPreviewResult? Preview,
    string Diagnostic,
    MediaProbeResult SourceMetadata);

internal readonly record struct QueueMetadataProbeDiagnostics(
    long ProbesStarted,
    long CacheHits,
    long CoalescedWaiters,
    int MaximumConcurrentProbes);

internal sealed class QueueEmpiricalPreviewProvider(EncodingService encoding, IMediaProbeService probe,
    EncodeExecutionSnapshotBuilder snapshots, string toolSignature,
    Func<EncodingDecisionContext, EncodingPlan, bool, IAdaptiveVideoSampleRunner>? runnerFactory = null,
    Func<int, string>? signatureOverride = null,
    PreviewSamplingCoordinator? coordinator = null)
{
    private const int ProbeMetadataCacheCapacity = 512;
    private readonly EmpiricalSizePreviewService _preview = new(new PreviewVideoEvidenceCache());
    private readonly object _probeMetadataSync = new();
    private readonly Dictionary<string, (PreviewSourceBinding Binding, Lazy<Task<MediaProbeResult>> Probe)> _probeMetadata =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _probeMetadataLru = new();
    private long _metadataProbesStarted;
    private long _metadataCacheHits;
    private long _metadataCoalescedWaiters;
    private int _activeMetadataProbes;
    private int _maximumConcurrentMetadataProbes;

    internal QueueMetadataProbeDiagnostics MetadataProbeDiagnostics => new(
        Interlocked.Read(ref _metadataProbesStarted),
        Interlocked.Read(ref _metadataCacheHits),
        Interlocked.Read(ref _metadataCoalescedWaiters),
        Volatile.Read(ref _maximumConcurrentMetadataProbes));

    internal Task<QueueProductionPlanEstimate> PrepareAndEstimateAsync(
        EncodeExecutionSnapshotSettings settings,
        EncodeExecutionSnapshotItem item,
        long estimateAuthorityRevision,
        CancellationToken token,
        Action<int>? sampleActivity = null,
        bool productionPriority = false)
    {
        async Task<QueueProductionPlanEstimate> PrepareAsync(CancellationToken cancellation)
        {
            string[] sourcePaths = item.PreparedInput is { SourceFiles.Count: > 0 } preparedInput
                ? preparedInput.SourceFiles.ToArray()
                : [item.SourceFilePath];
            PreviewSourceBinding binding = PreviewSourceBinding.Capture(sourcePaths);
            string probePath = item.Dvd is not null && sourcePaths.Length > 0
                ? sourcePaths[0]
                : item.SourceFilePath;
            PreviewSourceBinding probeBinding = PreviewSourceBinding.Capture(probePath);
            MediaProbeResult source = await ProbeSourceMetadataAsync(probePath, probeBinding, cancellation)
                .ConfigureAwait(false);
            MediaProbeStreamInfo? video = source.Streams.FirstOrDefault(stream =>
                stream.CodecType.Equals("video", StringComparison.OrdinalIgnoreCase));
            double? sourceSeconds = item.Dvd?.CombinedDurationSeconds is > 0
                ? item.Dvd.CombinedDurationSeconds
                : ProgramDurationResolver.Resolve(source).DurationSeconds;
            if (!source.Success || sourceSeconds is not > 0 || video is null)
                throw new PreviewUnavailableException("Production plan preparation could not resolve source timing and video facts.");

            EncodeExecutionSnapshotBuildResult build = snapshots.BuildWithDetails(
                new EncodeExecutionSnapshotIdentity
                {
                    OperationId = "queue-plan-preparation",
                    StatisticsStartUtc = DateTime.UtcNow,
                    MediaFluxVersion = "",
                    StatisticsPath = "",
                    ContainerCompatibilityConfirmed = false
                },
                settings,
                item with
                {
                    SourceSizeBytes = item.Dvd?.CombinedSizeBytes is > 0
                        ? item.Dvd.CombinedSizeBytes
                        : binding.Bytes,
                    MediaDurationSeconds = sourceSeconds
                });
            EncodeExecutionSnapshot snapshot = build.Snapshot;
            EncodingInputSource input = snapshot.Input;
            var context = new EncodingDecisionContext(
                source, input, snapshot.Encoder, snapshot.UseGpu, snapshot.TargetMb,
                snapshot.ScaleMode, snapshot.Restoration, snapshot.EncoderPreset ?? "", snapshot.QualityValue,
                snapshot.TenBit, snapshot.AudioChannels, snapshot.MapMode, snapshot.CopySubtitles,
                snapshot.CopyDataStreams, snapshot.CopyAttachments, snapshot.OutputContainer,
                snapshot.CompatibilityPolicy, TimeSpan.FromSeconds(sourceSeconds.Value),
                QualityIntent: snapshot.QualityIntent)
            {
                ConcurrentEncoderSessions = snapshot.ConcurrentEncoderSessions,
                StorageSavingsContract = snapshot.StorageSavingsContract,
                StorageSavingsPolicy = snapshot.StorageSavingsPolicy.CloneNormalized()
            };
            EncodingPlan plan = EncodingPlanService.Create(context);
            if (!plan.IsAvailable)
                throw new PreviewUnavailableException(
                    string.IsNullOrWhiteSpace(plan.UnavailableReason)
                        ? "The prepared production plan is unavailable."
                        : plan.UnavailableReason);
            EncodingPlanService.EncodingPlanExecutionValues values = EncodingPlanService.GetExecutionValues(plan);
            ProductionPlanIdentity identity = plan.ProductionPlan ??
                throw new InvalidDataException("The prepared production plan has no fingerprint.");
            snapshot = snapshot with
            {
                QualityValue = values.QualityResolution.EffectiveQuality ?? snapshot.QualityValue,
                OutputWidth = values.Geometry?.Width ?? snapshot.OutputWidth,
                OutputHeight = values.Geometry?.Height ?? snapshot.OutputHeight,
                CopySubtitles = values.CopySubtitles,
                CopyDataStreams = values.CopyDataStreams,
                CopyAttachments = values.CopyAttachments,
                ProductionPlan = identity,
                PreparedPlan = plan,
                PreparedAdaptiveProductionPlan = null
            };
            build = build with { Snapshot = snapshot };

            EmpiricalPreviewResult? preview = null;
            PlanBoundEstimate? estimate = null;
            string diagnostic = $"Prepared exact production plan {identity.PlanFingerprint}.";
            if (item.Dvd is not null)
            {
                diagnostic += " DVD title sampling is not supported; the deterministic production plan is prepared without a measured size estimate.";
            }
            else if (values.AutomaticTargetPlan is { } automaticTarget)
            {
                if (automaticTarget.ProjectedOutputBytes is not > 0)
                    throw new PreviewUnavailableException(automaticTarget.Reason);
                estimate = new PlanBoundEstimate(
                    identity,
                    0,
                    "AutomaticTargetProjection",
                    automaticTarget.ProjectedOutputBytes.Value,
                    EstimateAuthorityLevel.Preliminary,
                    Confidence: automaticTarget.Disposition.ToString());
                diagnostic += $" Metadata-only VBR projection: target={automaticTarget.TargetVideoBitrateKbps:0} kbps, maxrate={automaticTarget.MaximumVideoBitrateKbps:0} kbps, buffer={automaticTarget.VideoBufferSizeKbps:0} kbps, projected={automaticTarget.ProjectedOutputBytes.Value:N0} bytes. {automaticTarget.Reason}";
            }
            else if (snapshot.TargetMb is > 0)
            {
                diagnostic += " Measured constant-quality sampling is not applicable to a target-size plan.";
            }
            else if (VideoRestorationPipeline.BuildPlan(snapshot.Restoration, snapshot.ScaleMode).UsesAi)
            {
                diagnostic += " Measured sampling is not available for an AI restoration plan.";
            }
            else if (values.QualityResolution.EffectiveQuality is not int quality ||
                     values.QualityResolution.Mechanism is not (EncoderQualityMechanism.Cq or EncoderQualityMechanism.Crf or EncoderQualityMechanism.Icq) ||
                     values.Geometry is null || string.IsNullOrWhiteSpace(video.PixelFormat))
            {
                diagnostic += " The resolved encoder plan does not support bounded constant-quality sampling.";
            }
            else if (!AdaptiveStorageSavingsPolicy.TryAncillaryAllowance(
                         source, values.ContainerDecision, values.AudioChannels,
                         sourceSeconds.Value, out AdaptiveAncillaryAllowance? ancillary, out string ancillaryReason))
            {
                diagnostic += " Measured estimate unavailable: " + ancillaryReason;
            }
            else
            {
                var request = new AdaptiveQualitySelectionRequest(
                    snapshot.StorageSavingsContract,
                    new AdaptiveQualityEnvelope(
                        snapshot.Encoder,
                        snapshot.QualityIntent?.Target ?? QualityTarget.Balanced,
                        values.QualityResolution.Mechanism,
                        quality,
                        quality),
                    input,
                    TimeSpan.FromSeconds(sourceSeconds.Value),
                    new AdaptiveVideoSettings(
                        snapshot.Encoder, snapshot.UseGpu, values.EncoderPreset, snapshot.TenBit,
                        snapshot.ConcurrentEncoderSessions, values.Geometry, video.PixelFormat,
                        snapshot.ScaleMode, values.ContainerDecision.Resolved,
                        video.ColorRange, video.ColorSpace, video.ColorTransfer, video.ColorPrimaries),
                    ancillary!);
                var runner = runnerFactory?.Invoke(context, plan, snapshot.ConcurrentEncoderSessions) ??
                    encoding.CreatePreviewSampleRunner(context, plan, snapshot.ConcurrentEncoderSessions);
                if (sampleActivity is not null)
                    runner = new ObservedSampleRunner(runner, sampleActivity);
                try
                {
                    preview = await _preview.EstimateAsync(
                        request,
                        adaptive: false,
                        runner,
                        q => signatureOverride?.Invoke(q) ?? toolSignature + "|" +
                            encoding.BuildPreviewSampleArguments(context, plan, snapshot.ConcurrentEncoderSessions,
                                request, q, new("Signature", TimeSpan.Zero, TimeSpan.FromSeconds(5)), "preview-signature"),
                        cancellation,
                        identity).ConfigureAwait(false);
                    if (binding != PreviewSourceBinding.Capture(sourcePaths))
                        throw new PreviewUnavailableException("Source changed during measured plan estimation.");

                    EmpiricalPreviewCandidate candidate = preview.Candidates.Single(value => value.Quality == quality);
                    long bytes = checked((long)Math.Round(preview.ProjectedTotalBytes, MidpointRounding.AwayFromZero));
                    estimate = new PlanBoundEstimate(identity, 0, EmpiricalPreviewResult.ModelId,
                        bytes, EstimateAuthorityLevel.Measured,
                        preview.RegionsPerAnchor * preview.Anchors.Count,
                        checked((long)Math.Round(candidate.ProjectedLowerBytes, MidpointRounding.AwayFromZero)),
                        checked((long)Math.Round(candidate.ProjectedUpperBytes, MidpointRounding.AwayFromZero)),
                        preview.Confidence)
                    {
                        RangeMethod = preview.RangeMethod,
                        SampleEvidence = preview.SampleEvidence
                    };
                    diagnostic += $" Measured {estimate.SampleCount} samples of {values.QualityResolution.Mechanism} {quality}.";
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    cancellation.ThrowIfCancellationRequested();
                    diagnostic += " Measured estimate unavailable: " +
                        (ex.Message.Length <= 300 ? ex.Message : ex.Message[..300]);
                }
            }

            cancellation.ThrowIfCancellationRequested();
            if (binding != PreviewSourceBinding.Capture(sourcePaths))
                throw new PreviewUnavailableException("Source changed during plan preparation.");
            if (estimate is not null)
                snapshot = snapshot with { BoundEstimate = estimate };
            build = build with { Snapshot = snapshot };
            var prepared = new PreparedProductionPlan(build, plan, estimate, estimateAuthorityRevision);
            return new QueueProductionPlanEstimate(prepared, preview, diagnostic, source);
        }

        return productionPriority || settings.UseAutomaticTargetBitrate && settings.AutomaticQuality
            ? PrepareAsync(token)
            : (coordinator ?? PreviewSamplingCoordinator.Shared).PreviewAsync(PrepareAsync, token);
    }

    private async Task<MediaProbeResult> ProbeSourceMetadataAsync(
        string path,
        PreviewSourceBinding binding,
        CancellationToken cancellationToken)
    {
        string key = Path.GetFullPath(path).ToUpperInvariant();
        Lazy<Task<MediaProbeResult>> sharedProbe;
        lock (_probeMetadataSync)
        {
            if (_probeMetadata.TryGetValue(key, out var cached) && cached.Binding == binding)
            {
                _probeMetadataLru.Remove(key);
                _probeMetadataLru.AddLast(key);
                sharedProbe = cached.Probe;
                if (sharedProbe.IsValueCreated && sharedProbe.Value.IsCompleted)
                    Interlocked.Increment(ref _metadataCacheHits);
                else
                    Interlocked.Increment(ref _metadataCoalescedWaiters);
            }
            else
            {
                sharedProbe = new Lazy<Task<MediaProbeResult>>(
                    () => ProbeAndValidateSourceAsync(path, binding),
                    LazyThreadSafetyMode.ExecutionAndPublication);
                _probeMetadata[key] = (binding, sharedProbe);
                _probeMetadataLru.Remove(key);
                _probeMetadataLru.AddLast(key);
                while (_probeMetadata.Count > ProbeMetadataCacheCapacity)
                {
                    string oldest = _probeMetadataLru.First!.Value;
                    _probeMetadataLru.RemoveFirst();
                    _probeMetadata.Remove(oldest);
                }
            }
        }

        try
        {
            MediaProbeResult source = await sharedProbe.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (!source.Success)
                RemoveFailedProbe(key, sharedProbe);
            return source;
        }
        catch
        {
            if (sharedProbe.IsValueCreated && (sharedProbe.Value.IsFaulted || sharedProbe.Value.IsCanceled))
                RemoveFailedProbe(key, sharedProbe);
            throw;
        }
    }

    private async Task<MediaProbeResult> ProbeAndValidateSourceAsync(
        string path,
        PreviewSourceBinding binding)
    {
        if (PreviewSourceBinding.Capture(path) != binding)
            throw new PreviewUnavailableException("Source changed before metadata inspection.");
        // The metadata probe is shared across concurrent consumers and selector
        // changes; each caller still observes cancellation while awaiting it.
        Interlocked.Increment(ref _metadataProbesStarted);
        int active = Interlocked.Increment(ref _activeMetadataProbes);
        int observed;
        while (active > (observed = Volatile.Read(ref _maximumConcurrentMetadataProbes)) &&
               Interlocked.CompareExchange(ref _maximumConcurrentMetadataProbes, active, observed) != observed) { }
        try
        {
            MediaProbeResult source = await probe.ProbeAsync(path, CancellationToken.None).ConfigureAwait(false);
            if (PreviewSourceBinding.Capture(path) != binding)
                throw new PreviewUnavailableException("Source changed during metadata inspection.");
            return source;
        }
        finally
        {
            Interlocked.Decrement(ref _activeMetadataProbes);
        }
    }

    private void RemoveFailedProbe(string key, Lazy<Task<MediaProbeResult>> expected)
    {
        lock (_probeMetadataSync)
        {
            if (_probeMetadata.TryGetValue(key, out var cached) && ReferenceEquals(cached.Probe, expected))
            {
                _probeMetadata.Remove(key);
                _probeMetadataLru.Remove(key);
            }
        }
    }

    private sealed class ObservedSampleRunner(IAdaptiveVideoSampleRunner inner, Action<int> sampleActivity)
        : IAdaptiveVideoSampleRunner
    {
        public async Task<RepresentativeSampleEvidence> MeasureAsync(
            AdaptiveQualitySelectionRequest request,
            int quality,
            RepresentativeSample sample,
            CancellationToken cancellationToken)
        {
            sampleActivity(1);
            try
            {
                return await inner.MeasureAsync(request, quality, sample, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                sampleActivity(-1);
            }
        }
    }
}
