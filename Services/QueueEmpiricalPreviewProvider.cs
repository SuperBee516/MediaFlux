using MediaFlux.Models;
using MediaFlux.Services.Encoders;

namespace MediaFlux.Services;

internal sealed class QueueEmpiricalPreviewProvider(EncodingService encoding, IMediaProbeService probe,
    EncodeExecutionSnapshotBuilder snapshots, string toolSignature,
    Func<EncodingDecisionContext, EncodingPlan, bool, IAdaptiveVideoSampleRunner>? runnerFactory = null,
    Func<int, string>? signatureOverride = null,
    PreviewSamplingCoordinator? coordinator = null)
{
    private readonly EmpiricalSizePreviewService _preview = new(new PreviewVideoEvidenceCache());

    internal Task<(EmpiricalPreviewResult Result, EncodingQualityResolution Quality)> EstimateAsync(
        EncodeExecutionSnapshotSettings settings, EncodeExecutionSnapshotItem item, CancellationToken token) =>
        (coordinator ?? PreviewSamplingCoordinator.Shared).PreviewAsync(async cancellation =>
        {
            var binding = PreviewSourceBinding.Capture(item.SourceFilePath);
            var source = await probe.ProbeAsync(item.SourceFilePath, cancellation).ConfigureAwait(false);
            var video = source.Streams.FirstOrDefault(s => s.CodecType == "video");
            double? sourceSeconds = ProgramDurationResolver.Resolve(source).DurationSeconds;
            if (!source.Success || sourceSeconds is not > 0 || video is null)
                throw new PreviewUnavailableException("Preview source probe is unavailable.");
            var snapshot = snapshots.Build(new EncodeExecutionSnapshotIdentity
            {
                OperationId = "advisory-preview", StatisticsStartUtc = DateTime.UtcNow,
                MediaFluxVersion = "", StatisticsPath = "", ContainerCompatibilityConfirmed = false
            }, settings, item with { SourceSizeBytes = binding.Bytes, MediaDurationSeconds = sourceSeconds });
            if (snapshot.TargetMb is > 0)
                throw new PreviewUnavailableException("This request has a target/bitrate budget, not constant quality.");
            if (VideoRestorationPipeline.BuildPlan(snapshot.Restoration, snapshot.ScaleMode).UsesAi)
                throw new PreviewUnavailableException("AI intermediate generation is outside the bounded direct-source preview.");
            var context = new EncodingDecisionContext(source, snapshot.Input, snapshot.Encoder, snapshot.UseGpu, null,
                snapshot.ScaleMode, snapshot.Restoration, snapshot.EncoderPreset ?? "", snapshot.QualityValue,
                snapshot.TenBit, snapshot.AudioChannels, snapshot.MapMode, snapshot.CopySubtitles,
                snapshot.CopyDataStreams, snapshot.CopyAttachments, snapshot.OutputContainer, snapshot.CompatibilityPolicy,
                TimeSpan.FromSeconds(sourceSeconds.Value), QualityIntent: snapshot.QualityIntent);
            var plan = EncodingPlanService.Create(context);
            var values = EncodingPlanService.GetExecutionValues(plan);
            if (values.QualityResolution.Mechanism is not (EncoderQualityMechanism.Cq or EncoderQualityMechanism.Crf) ||
                values.Geometry is null || string.IsNullOrWhiteSpace(video.PixelFormat) ||
                values.QualityResolution.EffectiveQuality is not { } quality)
                throw new PreviewUnavailableException("Encoder quality or video geometry is unsupported for preview.");
            if (!AdaptiveStorageSavingsPolicy.TryAncillaryAllowance(source, values.ContainerDecision, values.AudioChannels,
                sourceSeconds.Value, out var ancillary, out string reason))
                throw new PreviewUnavailableException(reason);
            bool adaptive = AdaptiveStorageSavingsPolicy.TryCreateRequest(snapshot.AdaptiveStorageSavingsEnabled,
                snapshot.ConcurrentEncoderSessions, snapshot.StorageSavingsContract, context, plan, out var request, out _);
            request ??= new(StorageSavingsContract.Disabled,
                new AdaptiveQualityEnvelope(snapshot.Encoder, snapshot.QualityIntent?.Target ?? QualityTarget.Balanced,
                    values.QualityResolution.Mechanism, quality, quality), snapshot.Input, context.KnownDuration,
                new(snapshot.Encoder, snapshot.UseGpu, values.EncoderPreset, snapshot.TenBit, snapshot.ConcurrentEncoderSessions,
                    values.Geometry, video.PixelFormat, snapshot.ScaleMode, values.ContainerDecision.Resolved,
                    video.ColorRange, video.ColorSpace, video.ColorTransfer, video.ColorPrimaries), ancillary!);
            var runner = runnerFactory?.Invoke(context, plan, snapshot.ConcurrentEncoderSessions) ??
                encoding.CreatePreviewSampleRunner(context, plan, snapshot.ConcurrentEncoderSessions);
            var result = await _preview.EstimateAsync(request, adaptive, runner, q => signatureOverride?.Invoke(q) ?? toolSignature + "|" +
                encoding.BuildPreviewSampleArguments(context, plan, snapshot.ConcurrentEncoderSessions, request, q,
                    new("Signature", TimeSpan.Zero, TimeSpan.FromSeconds(5)), "preview-signature"), cancellation).ConfigureAwait(false);
            if (binding != PreviewSourceBinding.Capture(item.SourceFilePath))
                throw new PreviewUnavailableException("Source changed during preview preparation.");
            return (result, values.QualityResolution);
        }, token);
}
