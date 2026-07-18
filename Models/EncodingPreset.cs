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
        public string ScaleMode { get; set; } = "";
        public string NvencPreset { get; set; } = "";
        public bool TenBit { get; set; }
        public string AudioChannels { get; set; } = "";
        public bool? LimitGpuEncodingQueueToOneJob { get; set; }
        public bool DualNvenc { get; set; }
        public bool EnableOutputSuffix { get; set; }
        public bool EnableCodecSuffix { get; set; }
        public string OutputSuffix { get; set; } = "";
    }
}
