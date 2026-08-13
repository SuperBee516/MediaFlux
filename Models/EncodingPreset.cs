using System.Text.Json.Serialization;

namespace MediaFlux.Models
{
    public sealed class EncodingPreset
    {
        public string Name { get; set; } = "";
        public bool AutoTargetSize { get; set; } = true;
        public double? ManualTargetMb { get; set; }
        public string CompressionProfile { get; set; } = "";
        public string EncoderMode { get; set; } = "";
        public string VideoFormat { get; set; } = "";
        public string EncoderId { get; set; } = "";
        public string VideoCodec { get; set; } = "";
        public string ScaleMode { get; set; } = "";
        // Legacy JSON field retained only so pre-provider preset files migrate.
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? NvencPreset { get; set; }
        public string EncoderPreset { get; set; } = "";
        public int? QualityValue { get; set; }
        public bool TenBit { get; set; }
        public string AudioChannels { get; set; } = "";
        public bool? LimitGpuEncodingQueueToOneJob { get; set; }
        // Legacy JSON field retained only so older concurrency choices migrate.
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool DualNvenc { get; set; }
        public bool EnableOutputSuffix { get; set; }
        public bool EnableCodecSuffix { get; set; }
        public string OutputSuffix { get; set; } = "";
        public string OutputContainer { get; set; } = nameof(OutputContainerSelection.Mp4);
    }
}
