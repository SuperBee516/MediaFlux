namespace MediaFlux.Services.LibraryCatalog
{
    internal enum CatalogSearchSqlKind { Scalar, LocationMembership, JsonCodec }

    internal sealed record CatalogSearchPropertyDefinition(
        CatalogSearchPropertyInfo Info,
        string Expression,
        CatalogSearchSqlKind SqlKind = CatalogSearchSqlKind.Scalar,
        bool Integral = false,
        IReadOnlyDictionary<string, decimal>? UnitFactors = null,
        IReadOnlyDictionary<string, object>? ChoiceValues = null);

    // These expressions are shared with Statistics. A change in resolution or
    // dynamic-range classification must affect both views of the catalog.
    internal static class LibraryCatalogSqlExpressions
    {
        private const string MaximumFiniteReal = "1.7976931348623157e308";
        private const string PixelsPerSecondProduct = "CAST(metadata.width AS REAL)*metadata.height*metadata.frame_rate";

        internal static string ResolutionTier(string alias) =>
            $"CASE WHEN {alias}.width IS NULL OR {alias}.height IS NULL OR {alias}.width<=0 OR {alias}.height<=0 THEN 'Unknown' WHEN {alias}.width>=7680 OR {alias}.height>=4320 THEN '8K+' WHEN {alias}.width>=3840 OR {alias}.height>=2160 THEN '4K' WHEN {alias}.width>=2560 OR {alias}.height>=1440 THEN '1440p' WHEN {alias}.width>=1920 OR {alias}.height>=1080 THEN '1080p' WHEN {alias}.width>=1280 OR {alias}.height>=720 THEN '720p' ELSE 'SD' END";

        internal static string DynamicRange(string alias) =>
            $"CASE WHEN {alias}.file_id IS NULL THEN 'Unknown' " +
            $"WHEN lower({alias}.color_transfer) IN ('smpte2084','arib-std-b67') THEN 'HDR' " +
            $"WHEN lower({alias}.color_transfer) IN ('bt709','smpte170m','bt470bg','gamma22','gamma28','iec61966-2-1','bt2020-10','bt2020-12') THEN 'SDR' " +
            "ELSE 'Unknown' END";

        internal const string CurrentV1 =
            "metadata.file_id IS NOT NULL AND metadata.probe_status=2 " +
            "AND metadata.metadata_version>=1 " +
            "AND metadata.source_size_bytes=file.size_bytes " +
            "AND metadata.source_last_write_utc_ticks=file.last_write_utc_ticks";

        internal static string Current(int version) => version == 1
            ? CurrentV1
            : CurrentV1 + $" AND metadata.metadata_version>={version}";

        internal static string Guard(string expression, int version) =>
            $"CASE WHEN {Current(version)} THEN ({expression}) END";

        internal static string KnownText(string expression) =>
            $"CASE WHEN trim({expression})='' OR lower(trim({expression})) IN ('unknown','n/a') THEN NULL ELSE {expression} END";

        internal static string PositiveFinite(string expression) =>
            $"CASE WHEN {expression}>0 AND {expression}<{MaximumFiniteReal} THEN {expression} END";

        internal static readonly string PixelsPerSecond =
            $"CASE WHEN metadata.width>0 AND metadata.height>0 AND metadata.frame_rate>0 " +
            $"AND ({PixelsPerSecondProduct})<{MaximumFiniteReal} THEN ({PixelsPerSecondProduct}) END";

        internal static readonly string SourceVideoBpp =
            $"CASE WHEN metadata.video_bitrate_bps>0 AND metadata.width>0 " +
            $"AND metadata.height>0 AND metadata.frame_rate>0 " +
            $"AND ({PixelsPerSecondProduct})>0 AND ({PixelsPerSecondProduct})<{MaximumFiniteReal} " +
            $"THEN CAST(metadata.video_bitrate_bps AS REAL)/({PixelsPerSecondProduct}) END";

        internal const string CodedOrientation =
            "CASE WHEN metadata.width>0 AND metadata.height>0 THEN " +
            "CASE WHEN metadata.width>metadata.height THEN 'landscape' " +
            "WHEN metadata.width<metadata.height THEN 'portrait' ELSE 'square' END END";

        internal const string ScanType =
            "CASE WHEN lower(metadata.field_order)='progressive' THEN 'progressive' " +
            "WHEN lower(metadata.field_order) IN ('tt','bb','tb','bt') THEN 'interlaced' END";

        internal static string JsonArrayContainsOnlyStreamObjects(string column) =>
            $"CASE WHEN json_valid({column}) THEN CASE WHEN json_type({column})='array' " +
            $"THEN CASE WHEN NOT EXISTS(SELECT 1 FROM json_each({column}) validated_stream " +
            "WHERE validated_stream.type<>'object') THEN 1 ELSE 0 END ELSE 0 END ELSE 0 END";

        internal static string JsonCount(string column) =>
            $"CASE WHEN ({JsonArrayContainsOnlyStreamObjects(column)}) THEN json_array_length({column}) END";

        private static string ChromaValues(string family) => string.Join(",",
            new[] { family, "yuvj" + family[3..] }
                .Concat(new[] { 10, 12, 14, 16 }.SelectMany(depth =>
                    new[] { $"{family}{depth}le", $"{family}{depth}be" }))
                .Select(value => $"'{value}'"));

        internal static readonly string ChromaSubsampling =
            $"CASE WHEN lower(metadata.pixel_format) IN ({ChromaValues("yuv420p")},'nv12','nv21','p010le','p016le') THEN '4:2:0' " +
            $"WHEN lower(metadata.pixel_format) IN ({ChromaValues("yuv422p")},'yuyv422','uyvy422') THEN '4:2:2' " +
            $"WHEN lower(metadata.pixel_format) IN ({ChromaValues("yuv444p")}) THEN '4:4:4' END";

        internal const string MetadataState =
            "CASE WHEN metadata.file_id IS NULL THEN 'not_enriched' " +
            "WHEN metadata.source_size_bytes<>file.size_bytes " +
            "OR metadata.source_last_write_utc_ticks<>file.last_write_utc_ticks THEN 'stale' " +
            "WHEN metadata.probe_status=3 THEN 'failed' " +
            "WHEN metadata.probe_status=1 THEN 'in_progress' " +
            "WHEN metadata.probe_status=0 THEN 'pending' " +
            "WHEN metadata.metadata_version<2 THEN 'needs_v2' " +
            "WHEN metadata.probe_status=2 THEN 'current' ELSE 'pending' END";
    }

    public static class CatalogSearchRegistry
    {
        private static readonly CatalogSearchOperator[] NumberOperators =
        {
            CatalogSearchOperator.Equal, CatalogSearchOperator.NotEqual,
            CatalogSearchOperator.LessThan, CatalogSearchOperator.LessThanOrEqual,
            CatalogSearchOperator.GreaterThan, CatalogSearchOperator.GreaterThanOrEqual,
            CatalogSearchOperator.Between, CatalogSearchOperator.Known, CatalogSearchOperator.Unknown
        };

        private static readonly CatalogSearchOperator[] TextOperators =
        {
            CatalogSearchOperator.Contains, CatalogSearchOperator.NotContains,
            CatalogSearchOperator.StartsWith, CatalogSearchOperator.NotStartsWith,
            CatalogSearchOperator.Equal, CatalogSearchOperator.NotEqual,
            CatalogSearchOperator.Known, CatalogSearchOperator.Unknown
        };

        private static readonly CatalogSearchOperator[] ChoiceOperators =
        {
            CatalogSearchOperator.Is, CatalogSearchOperator.IsNot, CatalogSearchOperator.OneOf,
            CatalogSearchOperator.Known, CatalogSearchOperator.Unknown
        };

        private static readonly CatalogSearchOperator[] DateOperators = NumberOperators;
        private static readonly CatalogSearchOperator[] BooleanOperators =
        {
            CatalogSearchOperator.Yes, CatalogSearchOperator.No, CatalogSearchOperator.Unknown
        };

        private static readonly IReadOnlyDictionary<string, CatalogSearchPropertyDefinition> Definitions = Build();

        public static IReadOnlyList<CatalogSearchPropertyInfo> Properties { get; } =
            Array.AsReadOnly(Definitions.Values.Select(value => value.Info).ToArray());

        public static bool TryGet(string id, out CatalogSearchPropertyInfo? property)
        {
            bool found = Definitions.TryGetValue(id ?? "", out CatalogSearchPropertyDefinition? definition);
            property = definition?.Info;
            return found;
        }

        internal static CatalogSearchPropertyDefinition Get(string id) =>
            Definitions.TryGetValue(id ?? "", out CatalogSearchPropertyDefinition? definition)
                ? definition
                : throw new ArgumentException($"Unknown catalog search property '{id}'.", nameof(id));

        private static IReadOnlyDictionary<string, CatalogSearchPropertyDefinition> Build()
        {
            var definitions = new Dictionary<string, CatalogSearchPropertyDefinition>(StringComparer.Ordinal);

            static IReadOnlyDictionary<string, decimal> Units(params (string Unit, decimal Factor)[] values) =>
                values.ToDictionary(value => value.Unit, value => value.Factor, StringComparer.OrdinalIgnoreCase);
            static IReadOnlyDictionary<string, object> Choices(params (string Name, object Value)[] values) =>
                values.ToDictionary(value => value.Name, value => value.Value, StringComparer.OrdinalIgnoreCase);

            void Add(string id, string name, CatalogSearchDataType type, string expression,
                int version = 0, bool sort = false, bool integral = false,
                CatalogSearchSqlKind kind = CatalogSearchSqlKind.Scalar,
                IReadOnlyDictionary<string, decimal>? units = null,
                IReadOnlyDictionary<string, object>? choices = null,
                CatalogSearchOperator[]? operators = null)
            {
                CatalogSearchOperator[] allowed = operators ?? type switch
                {
                    CatalogSearchDataType.Number => NumberOperators,
                    CatalogSearchDataType.Date => DateOperators,
                    CatalogSearchDataType.Choice => ChoiceOperators,
                    CatalogSearchDataType.Boolean => BooleanOperators,
                    _ => TextOperators
                };
                var info = new CatalogSearchPropertyInfo(id, name, type,
                    Array.AsReadOnly(allowed.ToArray()),
                    Array.AsReadOnly(units?.Keys.ToArray() ?? Array.Empty<string>()),
                    Array.AsReadOnly(choices?.Keys.ToArray() ?? Array.Empty<string>()),
                    sort, version);
                definitions.Add(id, new CatalogSearchPropertyDefinition(info, expression, kind, integral, units, choices));
            }

            var bytes = Units(("bytes", 1m), ("kib", 1024m), ("mib", 1048576m), ("gib", 1073741824m));
            var bitrate = Units(("bps", 1m), ("kbps", 1000m), ("mbps", 1000000m));
            var seconds = Units(("seconds", 1m), ("minutes", 60m), ("hours", 3600m));
            var pixels = Units(("pixels", 1m));
            var count = Units(("count", 1m));
            var fps = Units(("fps", 1m));
            var pps = Units(("px/s", 1m));
            var bpp = Units(("bpp", 1m));

            Add("file.name", "Filename", CatalogSearchDataType.Text, "file.file_name", sort: true);
            Add("file.path", "Full path", CatalogSearchDataType.Text, "file.full_path", sort: true);
            Add("file.extension", "Extension", CatalogSearchDataType.Choice, "NULLIF(file.extension,'')", sort: true);
            Add("file.size", "File size", CatalogSearchDataType.Number, "file.size_bytes", sort: true, integral: true, units: bytes);
            Add("file.created_utc", "Created (UTC date)", CatalogSearchDataType.Date, "file.creation_utc_ticks", sort: true);
            Add("file.modified_utc", "Modified (UTC date)", CatalogSearchDataType.Date, "file.last_write_utc_ticks", sort: true);
            Add("file.location_id", "Configured location", CatalogSearchDataType.Number, "", integral: true,
                kind: CatalogSearchSqlKind.LocationMembership, units: Units(("id", 1m)),
                operators: new[] { CatalogSearchOperator.Equal, CatalogSearchOperator.NotEqual,
                    CatalogSearchOperator.Known, CatalogSearchOperator.Unknown });
            Add("file.availability", "Catalog availability", CatalogSearchDataType.Choice, "file.availability_state",
                sort: true, choices: Choices(("present", 0), ("missing", 1), ("unavailable", 2)),
                operators: new[] { CatalogSearchOperator.Is, CatalogSearchOperator.IsNot, CatalogSearchOperator.OneOf });
            Add("metadata.state", "Metadata state", CatalogSearchDataType.Choice, LibraryCatalogSqlExpressions.MetadataState,
                choices: Choices(("not_enriched", "not_enriched"), ("pending", "pending"),
                    ("in_progress", "in_progress"), ("failed", "failed"), ("stale", "stale"),
                    ("needs_v2", "needs_v2"), ("current", "current")),
                operators: new[] { CatalogSearchOperator.Is, CatalogSearchOperator.IsNot, CatalogSearchOperator.OneOf });

            void MetadataText(string id, string name, string column, int version = 1, bool sort = false) =>
                Add(id, name, CatalogSearchDataType.Text, LibraryCatalogSqlExpressions.KnownText(column), version, sort);
            void MetadataChoice(string id, string name, string expression, int version = 1,
                IReadOnlyDictionary<string, object>? choices = null, bool sort = false) =>
                Add(id, name, CatalogSearchDataType.Choice, expression, version, sort, choices: choices);
            void MetadataNumber(string id, string name, string expression, IReadOnlyDictionary<string, decimal> units,
                int version = 1, bool integral = false, bool sort = false) =>
                Add(id, name, CatalogSearchDataType.Number, expression, version, sort, integral, units: units);

            MetadataText("media.container", "Container / format", "metadata.format_name", sort: true);
            MetadataChoice("video.codec", "Video codec", LibraryCatalogSqlExpressions.KnownText("metadata.video_codec"), sort: true);
            MetadataText("video.profile", "Video profile", "metadata.video_profile");
            MetadataNumber("video.level", "Video level", "CASE WHEN metadata.video_level>=0 THEN metadata.video_level END", count, integral: true);
            MetadataNumber("video.width", "Coded width", "CASE WHEN metadata.width>0 THEN metadata.width END", pixels, integral: true, sort: true);
            MetadataNumber("video.height", "Coded height", "CASE WHEN metadata.height>0 THEN metadata.height END", pixels, integral: true, sort: true);
            MetadataChoice("video.resolution", "Coded resolution",
                "CASE WHEN metadata.width>0 AND metadata.height>0 THEN printf('%dx%d',metadata.width,metadata.height) END");
            MetadataChoice("video.resolution_class", "Resolution class",
                $"NULLIF(({LibraryCatalogSqlExpressions.ResolutionTier("metadata")}), 'Unknown')",
                choices: Choices(("SD", "SD"), ("720p", "720p"), ("1080p", "1080p"),
                    ("1440p", "1440p"), ("4K", "4K"), ("8K+", "8K+")), sort: true);
            MetadataNumber("video.fps", "Effective FPS", LibraryCatalogSqlExpressions.PositiveFinite("metadata.frame_rate"), fps, sort: true);
            MetadataNumber("video.average_fps", "Average FPS", LibraryCatalogSqlExpressions.PositiveFinite("metadata.average_frame_rate"), fps, version: 2);
            MetadataNumber("video.nominal_fps", "Nominal FPS", LibraryCatalogSqlExpressions.PositiveFinite("metadata.nominal_frame_rate"), fps, version: 2);
            MetadataChoice("video.frame_rate_basis", "Frame-rate basis", "metadata.frame_rate_basis", version: 2,
                choices: Choices(("average", "average"), ("nominal", "nominal")));
            MetadataNumber("video.bitrate", "Video-stream bitrate", "CASE WHEN metadata.video_bitrate_bps>0 THEN metadata.video_bitrate_bps END",
                bitrate, version: 2, integral: true, sort: true);
            MetadataNumber("media.total_bitrate", "Total bitrate", "CASE WHEN metadata.total_bitrate>0 THEN metadata.total_bitrate END",
                bitrate, integral: true, sort: true);
            MetadataNumber("video.bit_depth", "Reliable bit depth", "CASE WHEN metadata.bit_depth>0 THEN metadata.bit_depth END", count, version: 2, integral: true);
            MetadataChoice("video.pixel_format", "Pixel format", LibraryCatalogSqlExpressions.KnownText("metadata.pixel_format"));
            MetadataChoice("video.chroma_subsampling", "Chroma subsampling", LibraryCatalogSqlExpressions.ChromaSubsampling,
                choices: Choices(("4:2:0", "4:2:0"), ("4:2:2", "4:2:2"), ("4:4:4", "4:4:4")));
            MetadataChoice("video.sample_aspect_ratio", "Sample aspect ratio", "metadata.sample_aspect_ratio", version: 2);
            MetadataChoice("video.display_aspect_ratio", "Display aspect ratio", "metadata.display_aspect_ratio", version: 2);
            MetadataChoice("video.field_order", "Field order", LibraryCatalogSqlExpressions.KnownText("metadata.field_order"));
            MetadataChoice("video.scan_type", "Progressive / interlaced", LibraryCatalogSqlExpressions.ScanType,
                choices: Choices(("progressive", "progressive"), ("interlaced", "interlaced")));
            MetadataNumber("media.duration", "Duration", "CASE WHEN metadata.duration_seconds>0 THEN metadata.duration_seconds END", seconds, sort: true);
            MetadataNumber("video.stream_count", "Video stream count", "metadata.video_stream_count", count, version: 2, integral: true);
            MetadataChoice("video.color_range", "Color range", LibraryCatalogSqlExpressions.KnownText("metadata.color_range"));
            MetadataChoice("video.color_space", "Color space", LibraryCatalogSqlExpressions.KnownText("metadata.color_space"));
            MetadataChoice("video.color_transfer", "Color transfer", LibraryCatalogSqlExpressions.KnownText("metadata.color_transfer"));
            MetadataChoice("video.color_primaries", "Color primaries", LibraryCatalogSqlExpressions.KnownText("metadata.color_primaries"));
            MetadataChoice("video.dynamic_range", "Dynamic range",
                $"NULLIF(({LibraryCatalogSqlExpressions.DynamicRange("metadata")}), 'Unknown')",
                choices: Choices(("HDR", "HDR"), ("SDR", "SDR")), sort: true);

            string audioCount = LibraryCatalogSqlExpressions.JsonCount("metadata.audio_streams_json");
            string subtitleCount = LibraryCatalogSqlExpressions.JsonCount("metadata.subtitle_streams_json");
            MetadataNumber("stream.audio_count", "Audio stream count", audioCount, count, integral: true);
            MetadataNumber("stream.subtitle_count", "Subtitle stream count", subtitleCount, count, integral: true);
            Add("stream.audio_present", "Has audio", CatalogSearchDataType.Boolean,
                $"CASE WHEN ({audioCount}) IS NOT NULL THEN CASE WHEN ({audioCount})>0 THEN 1 ELSE 0 END END", version: 1);
            Add("stream.subtitle_present", "Has subtitles", CatalogSearchDataType.Boolean,
                $"CASE WHEN ({subtitleCount}) IS NOT NULL THEN CASE WHEN ({subtitleCount})>0 THEN 1 ELSE 0 END END", version: 1);
            Add("stream.audio_codec", "Audio codec present", CatalogSearchDataType.Choice,
                "metadata.audio_streams_json", version: 1, kind: CatalogSearchSqlKind.JsonCodec,
                operators: new[] { CatalogSearchOperator.Is, CatalogSearchOperator.OneOf });
            Add("stream.subtitle_codec", "Subtitle codec present", CatalogSearchDataType.Choice,
                "metadata.subtitle_streams_json", version: 1, kind: CatalogSearchSqlKind.JsonCodec,
                operators: new[] { CatalogSearchOperator.Is, CatalogSearchOperator.OneOf });

            MetadataNumber("video.pixels_per_second", "Pixels per second", LibraryCatalogSqlExpressions.PixelsPerSecond,
                pps, sort: true);
            MetadataNumber("video.source_bpp", "Source video BPP", LibraryCatalogSqlExpressions.SourceVideoBpp,
                bpp, version: 2, sort: true);
            MetadataChoice("video.coded_orientation", "Coded orientation", LibraryCatalogSqlExpressions.CodedOrientation,
                choices: Choices(("landscape", "landscape"), ("portrait", "portrait"), ("square", "square")));

            return definitions;
        }
    }
}
