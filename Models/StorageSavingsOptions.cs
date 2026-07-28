namespace MediaFlux.Models
{
    public sealed class StorageSavingsOptions
    {
        public const string QualityTarget = "Quality";
        public const string SourceBitrateTarget = "Source video bitrate";

        public bool Enabled { get; set; } = false;
        public string TargetMode { get; set; } = SourceBitrateTarget;
        public int QualityValue { get; set; } = 28;
        public double SourceVideoBitratePercent { get; set; } = 50;

        public StorageSavingsOptions CloneNormalized()
        {
            var clone = new StorageSavingsOptions
            {
                Enabled = Enabled,
                TargetMode = TargetMode,
                QualityValue = QualityValue,
                SourceVideoBitratePercent = SourceVideoBitratePercent
            };
            clone.Normalize();
            return clone;
        }

        public void Normalize()
        {
            TargetMode = string.Equals(
                    TargetMode,
                    QualityTarget,
                    StringComparison.OrdinalIgnoreCase)
                ? QualityTarget
                : SourceBitrateTarget;
            QualityValue = Math.Clamp(QualityValue, 0, 51);
            SourceVideoBitratePercent =
                Math.Clamp(SourceVideoBitratePercent, 10, 95);
        }

        public bool UsesQualityTarget =>
            string.Equals(
                TargetMode,
                QualityTarget,
                StringComparison.OrdinalIgnoreCase);
    }
}
