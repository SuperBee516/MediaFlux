using MediaFlux.Models;
using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class AdaptiveQualityEnvelopeTests
{
    public static IEnumerable<object[]> ApprovedPolicyCases()
    {
        var policy = new[]
        {
            (QualityTarget.SmallerFile, 4, 34, 32), (QualityTarget.Efficient, 3, 31, 30),
            (QualityTarget.Balanced, 3, 29, 28), (QualityTarget.HighQuality, 2, 25, 24),
            (QualityTarget.MaximumQuality, 1, 22, 21)
        };
        foreach (var (target, delta, cq, crf) in policy)
        foreach (bool nvenc in new[] { true, false })
        {
            int cap = nvenc ? cq : crf;
            foreach (int preferred in new[] { 0, cap - delta - 1, cap - 1, cap, cap + 1, 51 })
                yield return [nvenc, target, delta, cap, preferred, preferred > cap ? preferred : Math.Min(preferred + delta, cap)];
        }
    }

    [Theory]
    [MemberData(nameof(ApprovedPolicyCases))]
    public void ApprovedPolicyKeepsProviderCapsAndPreservesPreferredBeyondCap(
        bool nvenc, QualityTarget target, int delta, int cap, int quality, int expectedWorst)
    {
        var encoder = nvenc ? Nvenc : new VideoEncoderSelection(VideoEncoderIds.Libx265, VideoCodecFamily.Hevc, "libx265");
        EncodingQualityResolution preferred = Resolve(encoder, EncodingQualityIntent.Automatic(target)) with { EffectiveQuality = quality };
        AdaptiveQualityEnvelope envelope = AdaptiveQualityEnvelopeService.Resolve(preferred, encoder);
        Assert.Equal(delta, envelope.MaximumIncrease);
        Assert.Equal(cap, envelope.AbsoluteCap);
        Assert.Equal(quality, envelope.PreferredQuality);
        Assert.Equal(expectedWorst, envelope.MaximumCompressionQuality);
        Assert.Equal(quality > cap, envelope.PreferredAlreadyAboveCap);
        Assert.True(envelope.ContainsQuality(quality));
        Assert.True(envelope.ContainsQuality(expectedWorst));
        Assert.False(envelope.ContainsQuality(expectedWorst + 1));
        Assert.False(envelope.ContainsQuality(quality - 1));
        if (quality > cap) Assert.Equal(new[] { quality }, AdaptiveQualityEnvelopeService.Candidates(envelope));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void CandidateLadderIsSmallOrderedDeterministicAndContainsOnlyTestableBounds(int width)
    {
        EncodingQualityResolution preferred = Resolve(Nvenc, EncodingQualityIntent.Automatic(QualityTarget.Balanced));
        AdaptiveQualityEnvelope envelope = AdaptiveQualityEnvelopeService.Create(preferred, Nvenc, 24 + width);
        IReadOnlyList<int> first = AdaptiveQualityEnvelopeService.Candidates(envelope);
        Assert.Equal(first, AdaptiveQualityEnvelopeService.Candidates(envelope));
        Assert.Equal(24, first[0]);
        Assert.Equal(24 + width, first[^1]);
        Assert.InRange(first.Count, 1, 4);
        Assert.Equal(first.Distinct().Order(), first);
        Assert.All(first, q => Assert.True(envelope.ContainsQuality(q)));
    }
    // Bounds in these tests are supplied examples, not product target defaults.
    [Theory]
    [InlineData(VideoEncoderIds.Nvenc, "hevc_nvenc", EncoderQualityMechanism.Cq)]
    [InlineData(VideoEncoderIds.Libx265, "libx265", EncoderQualityMechanism.Crf)]
    public void ExplicitEnvelopePreservesResolvedIntentAndInclusiveCandidateRange(
        string encoderId, string codec, EncoderQualityMechanism mechanism)
    {
        var encoder = new VideoEncoderSelection(encoderId, VideoCodecFamily.Hevc, codec);
        foreach (QualityTarget target in Enum.GetValues<QualityTarget>())
        {
            EncodingQualityResolution preferred = Resolve(encoder, EncodingQualityIntent.Automatic(target));
            int quality = preferred.EffectiveQuality!.Value;
            AdaptiveQualityEnvelope envelope = AdaptiveQualityEnvelopeService.Create(preferred, encoder, quality + 2);

            Assert.Equal(encoder, envelope.Encoder);
            Assert.Equal(target, envelope.Target);
            Assert.Equal(mechanism, envelope.Mechanism);
            Assert.Equal(quality, envelope.PreferredQuality);
            Assert.Equal(quality + 2, envelope.MaximumCompressionQuality);
            Assert.False(envelope.ContainsQuality(quality - 1));
            Assert.True(envelope.ContainsQuality(quality));
            Assert.True(envelope.ContainsQuality(quality + 2));
            Assert.False(envelope.ContainsQuality(quality + 3));
            Assert.Equal(quality, preferred.EffectiveQuality);
        }
    }

    [Fact]
    public void EqualBoundsPermitOnlyPreferredCandidate()
    {
        EncodingQualityResolution preferred = Resolve(Nvenc, EncodingQualityIntent.Automatic(QualityTarget.Balanced));
        int quality = preferred.EffectiveQuality!.Value;
        AdaptiveQualityEnvelope envelope = AdaptiveQualityEnvelopeService.Create(preferred, Nvenc, quality);
        Assert.True(envelope.ContainsQuality(quality));
        Assert.False(envelope.ContainsQuality(quality + 1));
        Assert.False(envelope.ContainsQuality(quality - 1));
    }

    [Theory]
    [InlineData(23)]
    [InlineData(52)]
    [InlineData(int.MaxValue)]
    public void InvalidExplicitBoundsAreRejectedRatherThanClamped(int bound)
    {
        EncodingQualityResolution preferred = Resolve(Nvenc, EncodingQualityIntent.Automatic(QualityTarget.Balanced));
        Assert.Equal(24, preferred.EffectiveQuality);
        Assert.Throws<ArgumentOutOfRangeException>(() => AdaptiveQualityEnvelopeService.Create(preferred, Nvenc, bound));
    }

    [Fact]
    public void ManualQualityAndTargetSizeCannotCreateAdaptiveEnvelope()
    {
        Assert.Throws<InvalidOperationException>(() => AdaptiveQualityEnvelopeService.Create(
            Resolve(Nvenc, EncodingQualityIntent.LegacyNumeric(24)), Nvenc, 26));
        Assert.Throws<InvalidOperationException>(() => AdaptiveQualityEnvelopeService.Create(
            Resolve(Nvenc, EncodingQualityIntent.Automatic(QualityTarget.Balanced), 100), Nvenc, 26));
    }

    [Fact]
    public void ProviderMismatchAndIcqCannotBeTreatedAsCqOrCrf()
    {
        EncodingQualityResolution preferred = Resolve(Nvenc, EncodingQualityIntent.Automatic(QualityTarget.Balanced));
        Assert.Throws<InvalidOperationException>(() => AdaptiveQualityEnvelopeService.Create(
            preferred, Nvenc with { FfmpegCodec = "libx265" }, 26));
        var qsv = new VideoEncoderSelection(VideoEncoderIds.Qsv, VideoCodecFamily.Hevc, "hevc_qsv");
        Assert.Throws<InvalidOperationException>(() => AdaptiveQualityEnvelopeService.Create(
            Resolve(qsv, EncodingQualityIntent.Automatic(QualityTarget.Balanced)), qsv, 26));
        var software = new VideoEncoderSelection(VideoEncoderIds.Libx265, VideoCodecFamily.Hevc, "libx265");
        Assert.Throws<InvalidOperationException>(() => AdaptiveQualityEnvelopeService.Create(preferred, software, 26));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(52)]
    public void InvalidPreferredValueCannotBeHiddenByEnvelope(int quality)
    {
        EncodingQualityResolution preferred = Resolve(Nvenc, EncodingQualityIntent.Automatic(QualityTarget.Balanced));
        Assert.Throws<InvalidOperationException>(() => AdaptiveQualityEnvelopeService.Create(
            preferred with { EffectiveQuality = quality }, Nvenc, 51));
    }

    private static readonly VideoEncoderSelection Nvenc =
        new(VideoEncoderIds.Nvenc, VideoCodecFamily.Hevc, "hevc_nvenc");

    private static EncodingQualityResolution Resolve(
        VideoEncoderSelection encoder, EncodingQualityIntent intent, double? targetMb = null) =>
        new EncodingQualityPolicyService().Resolve(new(intent,
            new MediaProbeResult
            {
                Success = true,
                Streams = [new MediaProbeStreamInfo
                {
                    CodecType = "video", CodecName = "h264", Width = 1920,
                    Height = 1080, FrameRate = 30, BitRate = 4_000_000
                }]
            }, encoder, null, EncodingService.ScaleMode.None, targetMb));
}
