using System;

namespace MediaFlux.Models
{
    public sealed class DuplicateKeeperPreferences
    {
        public const string QualityFirst = "Quality first (current behavior)";
        public const string Balanced = "Balanced";
        public const string SaveStorage = "Save storage";
        public const string PreferModernCodecs = "Prefer modern codecs";
        public const string Custom = "Custom";

        public const string CodecNoPreference = "No codec preference";
        public const string CodecModernFirst = "Prefer modern codecs";
        public const string CodecH264First = "Prefer H.264 compatibility";

        public string Profile { get; set; } = QualityFirst;
        public int ResolutionWeight { get; set; } = 40;
        public int QualityWeight { get; set; } = 15;
        public int StorageWeight { get; set; } = 30;
        public int CodecWeight { get; set; } = 15;
        public int ModifiedDateWeight { get; set; } = 0;
        public string CodecPreference { get; set; } = CodecModernFirst;
        public bool NeverSacrificeResolution { get; set; } = true;
        public int MinimumScoreMargin { get; set; } = 8;
        public bool PreferSmallerComparableVisualCopy { get; set; } = true;
        public int ComparableVisualBitratePercent { get; set; } = 85;
        public List<string> ExactPreferredLocations { get; set; } = new();

        public DuplicateKeeperPreferences Clone()
        {
            return new DuplicateKeeperPreferences
            {
                Profile = Profile,
                ResolutionWeight = ResolutionWeight,
                QualityWeight = QualityWeight,
                StorageWeight = StorageWeight,
                CodecWeight = CodecWeight,
                ModifiedDateWeight = ModifiedDateWeight,
                CodecPreference = CodecPreference,
                NeverSacrificeResolution = NeverSacrificeResolution,
                MinimumScoreMargin = MinimumScoreMargin,
                PreferSmallerComparableVisualCopy = PreferSmallerComparableVisualCopy,
                ComparableVisualBitratePercent = ComparableVisualBitratePercent,
                ExactPreferredLocations = ExactPreferredLocations?.ToList() ?? new()
            };
        }

        public void Normalize()
        {
            Profile = Profile switch
            {
                Balanced => Balanced,
                SaveStorage => SaveStorage,
                PreferModernCodecs => PreferModernCodecs,
                Custom => Custom,
                _ => QualityFirst
            };
            CodecPreference = CodecPreference switch
            {
                CodecNoPreference => CodecNoPreference,
                CodecH264First => CodecH264First,
                _ => CodecModernFirst
            };
            ResolutionWeight = Math.Clamp(ResolutionWeight, 0, 100);
            QualityWeight = Math.Clamp(QualityWeight, 0, 100);
            StorageWeight = Math.Clamp(StorageWeight, 0, 100);
            CodecWeight = Math.Clamp(CodecWeight, 0, 100);
            ModifiedDateWeight = Math.Clamp(ModifiedDateWeight, 0, 100);
            MinimumScoreMargin = Math.Clamp(MinimumScoreMargin, 0, 25);
            ComparableVisualBitratePercent = Math.Clamp(ComparableVisualBitratePercent, 50, 100);
            ExactPreferredLocations = (ExactPreferredLocations ?? new())
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => path.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (ResolutionWeight + QualityWeight + StorageWeight + CodecWeight + ModifiedDateWeight == 0)
                ResolutionWeight = 1;
        }
    }
}
