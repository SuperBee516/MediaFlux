using System.Text.Json;
using MediaFlux.Services.LibraryCatalog;
using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace MediaFlux.Tests;

public sealed class CatalogSearchQueryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MediaFlux-CatalogSearch", Guid.NewGuid().ToString("N"));
    private readonly string _databasePath;
    private readonly SqliteLibraryCatalog _catalog;
    private readonly long _locationId;
    private readonly LibraryScanHandle _scan;
    private readonly DateTime _modified = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
    private readonly ITestOutputHelper _output;

    public CatalogSearchQueryTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_root);
        _databasePath = Path.Combine(_root, "catalog.db");
        _catalog = new SqliteLibraryCatalog(_databasePath, Path.Combine(_root, "backups"), Path.Combine(_root, "recovery"));
        _catalog.Initialize();
        _locationId = _catalog.UpsertLocation(new LibraryLocationUpsert(Path.Combine(_root, "library"))).Id;
        _scan = _catalog.BeginScan(_locationId);
    }

    [Fact]
    public void RepresentativeSearchUsesOnlyCurrentTrueVideoBitrateAndInclusiveBounds()
    {
        Add("fractional.mkv", fps: 30000d / 1001, videoBitrate: 14_000_000);
        Add("lower.mkv", fps: 29, videoBitrate: 13_000_000);
        Add("upper.mkv", fps: 30, videoBitrate: 15_000_000);
        Add("below-fps.mkv", fps: 28.999, videoBitrate: 14_000_000);
        Add("above-fps.mkv", fps: 30.001, videoBitrate: 14_000_000);
        Add("below-bitrate.mkv", fps: 29.97, videoBitrate: 12_999_999);
        Add("above-bitrate.mkv", fps: 29.97, videoBitrate: 15_000_001);
        Add("total-only.mkv", fps: 29.97, videoBitrate: null, totalBitrate: 14_000_000);
        Add("total-only-outside.mkv", fps: 29.97, videoBitrate: 17_000_000, totalBitrate: 14_000_000);
        Add("stale.mkv", fps: 29.97, videoBitrate: 14_000_000, stale: true);
        Add("failed.mkv", fps: 29.97, videoBitrate: 14_000_000, status: LibraryProbeStatus.Failed);
        Add("pending.mkv", fps: 29.97, videoBitrate: 14_000_000, status: LibraryProbeStatus.Pending);
        Add("v1.mkv", fps: 29.97, videoBitrate: 14_000_000, version: 1);
        Add("hevc.mkv", codec: "hevc", fps: 29.97, videoBitrate: 14_000_000);
        Add("interlaced.mkv", fps: 29.97, videoBitrate: 14_000_000, fieldOrder: "tt");
        Add("ten-bit.mkv", fps: 29.97, videoBitrate: 14_000_000, bitDepth: 10);
        Add("missing.mkv", fps: 29.97, videoBitrate: 14_000_000,
            availability: IndexedFileAvailability.Missing);
        Add("unavailable.mkv", fps: 29.97, videoBitrate: 14_000_000,
            availability: IndexedFileAvailability.Unavailable);

        CatalogSearchDefinition definition = Search(
            Is("video.codec", "h264"),
            Equal("video.width", 1920, "pixels"),
            Equal("video.height", 1080, "pixels"),
            Between("video.fps", 29, 30, "fps"),
            Between("video.bitrate", 13, 15, "mbps"),
            Is("video.field_order", "progressive"),
            Equal("video.bit_depth", 8, "count"));
        definition = definition with { Availability = IndexedFileAvailability.Present };

        LibraryFilePage page = Query(definition);
        Assert.Equal(3, page.TotalCount);
        Assert.Equal(new[] { "fractional.mkv", "lower.mkv", "upper.mkv" },
            page.Files.Select(file => file.FileName).OrderBy(name => name));
        Assert.All(page.Files, file => Assert.InRange(file.SearchFacts!.VideoBitRateBps!.Value,
            13_000_000, 15_000_000));
        Assert.All(page.Files, file => Assert.False(File.Exists(file.FullPath)));
    }

    [Fact]
    public void UnknownMeansMissingInSuccessfulCurrentMetadataOnly()
    {
        Add("unknown.mkv", videoBitrate: null, totalBitrate: 14_000_000);
        Add("known.mkv", videoBitrate: 14_000_000);
        Add("v1.mkv", videoBitrate: null, version: 1);
        Add("stale.mkv", videoBitrate: null, stale: true);
        Add("stale-timestamp.mkv", videoBitrate: null, staleTimestamp: true);
        Add("failed.mkv", videoBitrate: null, status: LibraryProbeStatus.Failed);
        Add("pending.mkv", videoBitrate: null, status: LibraryProbeStatus.Pending);
        Add("in-progress.mkv", videoBitrate: null, status: LibraryProbeStatus.InProgress);
        Add("unenriched.mkv", metadata: false);

        Assert.Equal("unknown.mkv", Assert.Single(Query(Search(new CatalogSearchCondition("video.bitrate", CatalogSearchOperator.Unknown))).Files).FileName);
        Assert.Equal("known.mkv", Assert.Single(Query(Search(new CatalogSearchCondition("video.bitrate", CatalogSearchOperator.Known))).Files).FileName);
        Assert.Equal("known.mkv", Assert.Single(Query(Search(new CatalogSearchCondition("video.bitrate", CatalogSearchOperator.NotEqual,
            new CatalogSearchValue(Number: 13, Unit: "mbps")))).Files).FileName);
        Assert.Empty(Query(Search(Equal("video.bitrate", 14, "mbps"),
            new CatalogSearchCondition("video.bitrate", CatalogSearchOperator.Unknown))).Files);

        Assert.Equal(2, Query(Search(Is("metadata.state", "stale"))).TotalCount);
        Assert.Equal("v1.mkv", Assert.Single(Query(Search(Is("metadata.state", "needs_v2"))).Files).FileName);
        Assert.Equal("failed.mkv", Assert.Single(Query(Search(Is("metadata.state", "failed"))).Files).FileName);
        Assert.Equal("pending.mkv", Assert.Single(Query(Search(Is("metadata.state", "pending"))).Files).FileName);
        Assert.Equal("in-progress.mkv", Assert.Single(Query(Search(Is("metadata.state", "in_progress"))).Files).FileName);
        Assert.Equal("stale-timestamp.mkv", Assert.Single(Query(Search(Is("metadata.state", "stale"),
            TextEquals("file.name", "stale-timestamp.mkv"))).Files).FileName);
        Assert.Empty(Query(Search(Equal("video.bitrate", 14, "mbps"),
            TextEquals("file.name", "stale-timestamp.mkv"))).Files);
        Assert.Equal("unenriched.mkv", Assert.Single(Query(Search(Is("metadata.state", "not_enriched"))).Files).FileName);
        Assert.Null(Assert.Single(Query(Search(TextEquals("file.name", "unknown.mkv"))).Files).SearchFacts!.SourceVideoBpp);
        LibraryFileViewRecord staleAdvanced = Assert.Single(Query(Search(TextEquals("file.name", "stale.mkv"))).Files);
        Assert.Null(staleAdvanced.SearchFacts!.VideoBitRateBps);
        Assert.Equal("", staleAdvanced.VideoCodec);
        Assert.Equal("h264", Assert.Single(_catalog.QueryFiles(new LibraryFileQuery(Search: "stale.mkv")).Files).VideoCodec);
    }

    [Fact]
    public void LegacyMetadataCanUseLegacyFactsButNotV2OrReliableDepth()
    {
        Add("legacy.mkv", version: 1, videoBitrate: null, totalBitrate: 14_000_000,
            fps: 30000d / 1001, bitDepth: 8);
        Assert.Equal("legacy.mkv", Assert.Single(Query(Search(Between("media.total_bitrate", 13, 15, "mbps"))).Files).FileName);
        Assert.Equal("legacy.mkv", Assert.Single(Query(Search(Between("video.fps", 29, 30, "fps"))).Files).FileName);
        Assert.Empty(Query(Search(new CatalogSearchCondition("video.bitrate", CatalogSearchOperator.Unknown))).Files);
        Assert.Empty(Query(Search(new CatalogSearchCondition("video.bit_depth", CatalogSearchOperator.Known))).Files);
        Assert.Empty(Query(Search(new CatalogSearchCondition("video.average_fps", CatalogSearchOperator.Known))).Files);
        Assert.Null(Assert.Single(Query(Search(TextEquals("file.name", "legacy.mkv"))).Files).SearchFacts!.BitDepth);
    }

    [Fact]
    public void LocationNotEqualDoesNotTreatMissingMembershipAsKnown()
    {
        Add("located.mkv");
        long unlocatedId = Add("unlocated.mkv");
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "DELETE FROM file_location_memberships WHERE file_id=$id";
        command.Parameters.AddWithValue("$id", unlocatedId);
        command.ExecuteNonQuery();

        Assert.Empty(Query(Search(new CatalogSearchCondition("file.location_id", CatalogSearchOperator.NotEqual,
            new CatalogSearchValue(Number: _locationId, Unit: "id")))).Files);
        Assert.Equal("unlocated.mkv", Assert.Single(Query(Search(new CatalogSearchCondition(
            "file.location_id", CatalogSearchOperator.Unknown))).Files).FileName);
    }

    [Fact]
    public void DerivedFactsAreGuardedAndResolutionUsesStatisticsTier()
    {
        Add("normal.mkv", fps: 30, videoBitrate: 14_000_000);
        Add("portrait.mkv", width: 1080, height: 1920, fps: 30, videoBitrate: 14_000_000);
        Add("square.mkv", width: 1080, height: 1080, fps: 30, videoBitrate: 14_000_000);
        Add("zero.mkv", width: 0, height: 1080, fps: 30, videoBitrate: 14_000_000);
        Add("negative.mkv", width: 1920, height: -1080, fps: 30, videoBitrate: 14_000_000);
        Add("missing-width.mkv", width: null, height: 1080, fps: 30, videoBitrate: 14_000_000);
        Add("missing-height.mkv", width: 1920, height: null, fps: 30, videoBitrate: 14_000_000);
        Add("no-fps.mkv", fps: null, videoBitrate: 14_000_000);
        Add("infinite-fps.mkv", fps: double.PositiveInfinity, videoBitrate: 14_000_000);
        Add("no-bitrate.mkv", fps: 30, videoBitrate: null);
        Add("stale.mkv", fps: 30, videoBitrate: 14_000_000, stale: true);

        LibraryFileViewRecord normal = Assert.Single(Query(Search(TextEquals("file.name", "normal.mkv"))).Files);
        Assert.Equal(1920d * 1080 * 30, normal.SearchFacts!.PixelsPerSecond);
        Assert.Equal(14_000_000d / (1920 * 1080 * 30d), normal.SearchFacts.SourceVideoBpp!.Value, 8);
        Assert.Equal("landscape", normal.SearchFacts.CodedOrientation);
        Assert.Equal("1080p", normal.SearchFacts.ResolutionClass);
        Assert.Equal("portrait", Assert.Single(Query(Search(TextEquals("file.name", "portrait.mkv"))).Files).SearchFacts!.CodedOrientation);
        Assert.Equal("square", Assert.Single(Query(Search(TextEquals("file.name", "square.mkv"))).Files).SearchFacts!.CodedOrientation);
        Assert.Null(Assert.Single(Query(Search(TextEquals("file.name", "zero.mkv"))).Files).SearchFacts!.ResolutionClass);
        Assert.Null(Assert.Single(Query(Search(TextEquals("file.name", "zero.mkv"))).Files).SearchFacts!.PixelsPerSecond);
        foreach (string name in new[] { "negative.mkv", "missing-width.mkv", "missing-height.mkv" })
        {
            CatalogSearchProjection facts = Assert.Single(Query(Search(TextEquals("file.name", name))).Files).SearchFacts!;
            Assert.Null(facts.ResolutionClass);
            Assert.Null(facts.CodedOrientation);
            Assert.Null(facts.PixelsPerSecond);
            Assert.Null(facts.SourceVideoBpp);
        }
        Assert.Null(Assert.Single(Query(Search(TextEquals("file.name", "no-fps.mkv"))).Files).SearchFacts!.SourceVideoBpp);
        CatalogSearchProjection infinite = Assert.Single(Query(Search(TextEquals("file.name", "infinite-fps.mkv"))).Files).SearchFacts!;
        Assert.Null(infinite.EffectiveFps);
        Assert.Null(infinite.PixelsPerSecond);
        Assert.Null(infinite.SourceVideoBpp);
        Assert.Equal("infinite-fps.mkv", Assert.Single(Query(Search(new CatalogSearchCondition(
            "video.fps", CatalogSearchOperator.Unknown), TextEquals("file.name", "infinite-fps.mkv"))).Files).FileName);
        Assert.Null(Assert.Single(Query(Search(TextEquals("file.name", "no-bitrate.mkv"))).Files).SearchFacts!.SourceVideoBpp);
        Assert.Null(Assert.Single(Query(Search(TextEquals("file.name", "stale.mkv"))).Files).SearchFacts!.SourceVideoBpp);
        Assert.Equal(3, Query(Search(Between("video.pixels_per_second", 62_208_000, 62_208_000, "px/s"))).TotalCount);
        Assert.Equal(2, Query(Search(Between("video.source_bpp", .22m, .23m, "bpp"))).TotalCount);
        Assert.Equal(5, Query(Search(Is("video.resolution_class", "1080p"))).TotalCount);
        Assert.Contains(_catalog.GetLibraryStatistics(topCount: 10).ByResolution,
            bucket => bucket.Label == "Unknown" && bucket.FileCount == 4);
        Assert.Contains(_catalog.GetOverviewSnapshot(2).ResolutionDistribution,
            bucket => bucket.Label == "Unknown" && bucket.FileCount == 4);
    }

    [Fact]
    public void ValidationAndSqlParametersRejectInvalidDefinitions()
    {
        Assert.Contains(CatalogSearchRegistry.Properties, item => item.Id == "video.bitrate" && item.RequiredMetadataVersion == 2);
        Assert.Equal("CASE WHEN metadata.video_bitrate_bps>0 THEN metadata.video_bitrate_bps END",
            CatalogSearchRegistry.Get("video.bitrate").Expression);
        Assert.Contains("metadata.total_bitrate", CatalogSearchRegistry.Get("media.total_bitrate").Expression);
        Assert.DoesNotContain(CatalogSearchRegistry.Properties, item => item.Id == "video.total_bitrate");
        Assert.Equal(CatalogSearchRegistry.Properties.Count,
            CatalogSearchRegistry.Properties.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Throws<ArgumentException>(() => CatalogSearchDefinitionValidator.Validate(Search(Is("invented.sql", "x"))));
        Assert.Throws<ArgumentException>(() => CatalogSearchDefinitionValidator.Validate(Search(Is("video.width", "x"))));
        Assert.Throws<ArgumentException>(() => CatalogSearchDefinitionValidator.Validate(Search(new CatalogSearchCondition("video.bitrate", CatalogSearchOperator.Between,
            new(Number: 16, Unit: "mbps"), new(Number: 13, Unit: "mbps")))));
        Assert.Throws<ArgumentException>(() => CatalogSearchDefinitionValidator.Validate(Search(new CatalogSearchCondition("video.width", CatalogSearchOperator.Equal,
            new(Number: 1.5m, Unit: "pixels")))));
        Assert.Throws<ArgumentException>(() => CatalogSearchDefinitionValidator.Validate(Search(new CatalogSearchCondition("video.width", CatalogSearchOperator.Equal,
            new(Number: 1, Unit: "mbps")))));
        Assert.Throws<ArgumentException>(() => CatalogSearchDefinitionValidator.Validate(new(2, Array.Empty<CatalogSearchCondition>())));
        Assert.Throws<ArgumentException>(() => CatalogSearchDefinitionValidator.Validate(new(1,
            Enumerable.Repeat(Equal("video.width", 1920), 33).ToArray())));
        Assert.Throws<ArgumentException>(() => CatalogSearchDefinitionValidator.Validate(Search(new CatalogSearchCondition("video.codec", CatalogSearchOperator.OneOf,
            Values: Enumerable.Repeat(new CatalogSearchValue(Text: "h264"), 17).ToArray()))));
        Assert.Throws<ArgumentException>(() => CatalogSearchDefinitionValidator.Validate(Search(Is("video.codec", new string('x', 513)))));
        Assert.Throws<ArgumentException>(() => CatalogSearchDefinitionValidator.Validate(
            Search() with { Sort = new CatalogSearchSort("file.name;DROP TABLE indexed_files") }));
        Assert.Throws<ArgumentException>(() => CatalogSearchDefinitionValidator.Validate(Search(
            Between("file.location_id", 1, 2, "id"))));
        Assert.Throws<ArgumentException>(() => CatalogSearchValue.ParseNumber("NaN"));
        Assert.Throws<ArgumentException>(() => CatalogSearchValue.ParseNumber("Infinity"));
        Assert.Throws<ArgumentException>(() => CatalogSearchValue.ParseNumber("-Infinity"));
        Assert.Throws<ArgumentException>(() => CatalogSearchValue.ParseNumber("1e999"));
        Assert.Throws<ArgumentException>(() => CatalogSearchDefinitionValidator.Validate(
            Search(new CatalogSearchCondition("video.width", (CatalogSearchOperator)999,
                new CatalogSearchValue(Number: 1920, Unit: "pixels")))));
        Assert.Equal(29.97m, CatalogSearchValue.ParseNumber("29.97", "fps").Number);
        CompiledCatalogSearch kbps = CatalogSearchSqlCompiler.Compile(Search(Equal("video.bitrate", 14_000, "kbps")));
        Assert.Equal(14_000_000L, Assert.Single(kbps.Parameters).Value);
        CompiledCatalogSearch mbps = CatalogSearchSqlCompiler.Compile(Search(Equal("video.bitrate", 14, "mbps")));
        Assert.Equal(14_000_000L, Assert.Single(mbps.Parameters).Value);
        string malformedJson = "{\"Version\":1,\"Conditions\":[{\"PropertyId\":\"video.width\",\"Operator\":\"Between\",\"Value\":{\"Number\":1920}}]}";
        CatalogSearchDefinition malformed = JsonSerializer.Deserialize<CatalogSearchDefinition>(malformedJson)!;
        Assert.Throws<ArgumentException>(() => CatalogSearchDefinitionValidator.Validate(malformed));

        CompiledCatalogSearch compiled = CatalogSearchSqlCompiler.Compile(Search(
            Is("video.codec", "h264' OR 1=1 --"),
            new CatalogSearchCondition("video.pixel_format", CatalogSearchOperator.OneOf,
                Values: new[] { new CatalogSearchValue(Text: "yuv420p"), new CatalogSearchValue(Text: "nv12") })));
        Assert.DoesNotContain("OR 1=1", compiled.PredicateSql);
        Assert.Contains(compiled.Parameters, parameter => Equals(parameter.Value, "h264' OR 1=1 --"));
        Assert.Equal(3, compiled.Parameters.Count);
        string json = JsonSerializer.Serialize(Search(Equal("video.width", 1920, "pixels")));
        Assert.Equal("video.width", Assert.Single(JsonSerializer.Deserialize<CatalogSearchDefinition>(json)!.Conditions).PropertyId);
    }

    [Fact]
    public void LiteralLikeEscapingAndOneOfDoNotTreatInputAsSql()
    {
        Add("100%_real.mkv", codec: "h264");
        Add("100AXreal.mkv", codec: "hevc");
        Assert.Equal("100%_real.mkv", Assert.Single(Query(Search(new CatalogSearchCondition("file.name", CatalogSearchOperator.Contains,
            new(Text: "%_")))).Files).FileName);
        Assert.Equal("100%_real.mkv", Assert.Single(Query(new(1, Array.Empty<CatalogSearchCondition>(), Search: "%_real")).Files).FileName);
        Assert.Equal("100%_real.mkv", Assert.Single(Query(new(1, Array.Empty<CatalogSearchCondition>(),
            Search: Path.Combine(_root, "library", "100%_real"))).Files).FileName);
        Assert.Equal("100%_real.mkv", Assert.Single(Query(Search(new CatalogSearchCondition("video.codec", CatalogSearchOperator.OneOf,
            Values: new[] { new CatalogSearchValue(Text: "h264"), new CatalogSearchValue(Text: "av1") }))).Files).FileName);
        Assert.Empty(Query(Search(Is("video.codec", "h264' OR 1=1 --"))).Files);
    }

    [Fact]
    public void StreamSummaryFpsAndDateConditionsUseCatalogFacts()
    {
        Add("media.mkv", fps: 30000d / 1001, audioJson: "[{\"codec\":\"aac\"}]",
            subtitleJson: "[{\"codec\":\"subrip\"}]");
        Add("silent.mkv", fps: 24, audioJson: "[]", subtitleJson: "[]");
        Assert.Equal("media.mkv", Assert.Single(Query(Search(Is("stream.audio_codec", "aac"),
            Is("stream.subtitle_codec", "subrip"),
            new CatalogSearchCondition("stream.audio_present", CatalogSearchOperator.Yes),
            new CatalogSearchCondition("stream.subtitle_present", CatalogSearchOperator.Yes),
            Between("video.average_fps", 29, 30, "fps"),
            new CatalogSearchCondition("file.modified_utc", CatalogSearchOperator.Equal,
                new(Date: DateOnly.FromDateTime(_modified))))).Files).FileName);
        Assert.Equal("silent.mkv", Assert.Single(Query(Search(new CatalogSearchCondition("stream.audio_present", CatalogSearchOperator.No),
            new CatalogSearchCondition("stream.subtitle_present", CatalogSearchOperator.No))).Files).FileName);
        Assert.Equal(1, Assert.Single(Query(Search(TextEquals("file.name", "media.mkv"))).Files).SearchFacts!.AudioStreamCount);
    }

    [Fact]
    public void InvalidOrNonObjectStreamJsonStaysUnknownWithoutBreakingSearch()
    {
        Add("bad-json.mkv", audioJson: "not-json");
        Add("string-array.mkv", audioJson: "[\"aac\"]");
        Add("no-audio.mkv", audioJson: "[]");
        Assert.Empty(Query(Search(Is("stream.audio_codec", "aac"))).Files);
        Assert.Equal("bad-json.mkv", Assert.Single(Query(Search(TextEquals("file.name", "bad-json.mkv"),
            new CatalogSearchCondition("stream.audio_present", CatalogSearchOperator.Unknown))).Files).FileName);
        Assert.Equal("string-array.mkv", Assert.Single(Query(Search(TextEquals("file.name", "string-array.mkv"),
            new CatalogSearchCondition("stream.audio_count", CatalogSearchOperator.Unknown))).Files).FileName);
        Assert.Equal("string-array.mkv", Assert.Single(Query(Search(TextEquals("file.name", "string-array.mkv"),
            new CatalogSearchCondition("stream.audio_present", CatalogSearchOperator.Unknown))).Files).FileName);
        Assert.Equal("no-audio.mkv", Assert.Single(Query(Search(new CatalogSearchCondition(
            "stream.audio_present", CatalogSearchOperator.No))).Files).FileName);
    }

    [Fact]
    public void ChromaAndDynamicRangeStayConservativeForUnrecognizedValues()
    {
        Add("chroma-422.mkv", width: null, height: null, fps: null, videoBitrate: null,
            pixelFormat: "yuv422p10le");
        Add("chroma-unknown.mkv", width: null, height: null, fps: null, videoBitrate: null,
            pixelFormat: "opaque-hardware-format");
        Add("hdr.mkv", width: null, height: null, fps: null, videoBitrate: null,
            colorTransfer: "smpte2084", colorPrimaries: "bt2020");
        Add("sdr.mkv", width: null, height: null, fps: null, videoBitrate: null,
            colorTransfer: "bt709", colorPrimaries: "bt2020");
        Add("bt2020-primaries-only.mkv", width: null, height: null, fps: null, videoBitrate: null,
            colorPrimaries: "bt2020");

        Assert.Equal("chroma-422.mkv", Assert.Single(Query(Search(Is("video.chroma_subsampling", "4:2:2"),
            TextEquals("file.name", "chroma-422.mkv"))).Files).FileName);
        Assert.Equal("chroma-unknown.mkv", Assert.Single(Query(Search(TextEquals("file.name", "chroma-unknown.mkv"),
            new CatalogSearchCondition("video.chroma_subsampling", CatalogSearchOperator.Unknown))).Files).FileName);
        Assert.Equal("HDR", Assert.Single(Query(Search(TextEquals("file.name", "hdr.mkv"))).Files).DynamicRange);
        Assert.Equal("SDR", Assert.Single(Query(Search(Is("video.dynamic_range", "SDR"),
            TextEquals("file.name", "sdr.mkv"))).Files).DynamicRange);
        Assert.Equal("Unknown", Assert.Single(Query(Search(TextEquals("file.name", "bt2020-primaries-only.mkv"))).Files).DynamicRange);
        Assert.Empty(Query(Search(Is("video.dynamic_range", "SDR"),
            TextEquals("file.name", "bt2020-primaries-only.mkv"))).Files);
    }

    [Fact]
    public void CountAndPageShareReadSnapshotWhileWriterCommitsBetweenThem()
    {
        Add("first.mkv");
        var definition = Search();
        LibraryFilePage snapshot = _catalog.QueryFilesWithCountPageGapForTesting(
            new LibraryFileQuery(AdvancedSearch: definition, Limit: 10),
            () => Add("second.mkv"));
        Assert.Equal(1, snapshot.TotalCount);
        Assert.Single(snapshot.Files);
        Assert.Equal(2, Query(definition).TotalCount);
    }

    [Fact]
    public void CountAndPageShareSnapshotAcrossMetadataEnrichment()
    {
        long fileId = Add("pending.mkv", status: LibraryProbeStatus.Pending);
        CatalogSearchDefinition search = Search(Equal("video.bitrate", 14, "mbps"));
        LibraryFilePage snapshot = _catalog.QueryFilesWithCountPageGapForTesting(
            new LibraryFileQuery(AdvancedSearch: search), () =>
            {
                using SqliteConnection connection = Open();
                using SqliteCommand command = connection.CreateCommand();
                command.CommandText = "UPDATE media_metadata SET probe_status=2 WHERE file_id=$id";
                command.Parameters.AddWithValue("$id", fileId);
                command.ExecuteNonQuery();
            });
        Assert.Equal(0, snapshot.TotalCount);
        Assert.Empty(snapshot.Files);
        Assert.Equal(fileId, Assert.Single(Query(search).Files).FileId);
    }

    [Fact]
    public void StablePagingSortAndOrdinaryQueryCompatibility()
    {
        Add("b.mkv", videoBitrate: 14_000_000);
        Add("a.mkv", videoBitrate: 14_000_000);
        Add("c.mkv", videoBitrate: 14_000_000);
        CatalogSearchDefinition sorted = Search() with { Sort = new CatalogSearchSort("video.bitrate") };
        LibraryFilePage all = Query(sorted);
        Assert.Equal(3, all.TotalCount);
        Assert.Equal(all.Files.Select(file => file.FileId),
            Enumerable.Range(0, 3).Select(i => Query(sorted, offset: i, limit: 1).Files.Single().FileId));
        LibraryFilePage ordinary = _catalog.QueryFiles(new LibraryFileQuery(SortColumn: "name", Limit: 10));
        Assert.Equal(new[] { "a.mkv", "b.mkv", "c.mkv" }, ordinary.Files.Select(file => file.FileName));
        Assert.All(ordinary.Files, file => Assert.Null(file.SearchFacts));
    }

    [Fact]
    public void CancellationIsObservedBeforeAndDuringAdvancedRead()
    {
        Add("first.mkv");
        using var before = new CancellationTokenSource();
        before.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            _catalog.QueryFiles(new LibraryFileQuery(AdvancedSearch: Search()), before.Token));
        using var between = new CancellationTokenSource();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            _catalog.QueryFilesWithCountPageGapForTesting(
                new LibraryFileQuery(AdvancedSearch: Search()), between.Cancel, between.Token));
        Assert.Equal("first.mkv", Assert.Single(Query(Search(TextEquals("file.name", "first.mkv"))).Files).FileName);
    }

    [Fact]
    public async Task SqliteProgressHandlerInterruptsAnExecutingRead()
    {
        using var cancellation = new CancellationTokenSource();
        using (SqliteConnection connection = Open(pooling: true))
        {
            var searchCancellation = new CatalogSearchReadCancellation(connection, cancellation.Token);
            try
            {
                using SqliteCommand command = connection.CreateCommand();
                command.CommandText = """
                    WITH RECURSIVE numbers(value) AS
                        (SELECT 1 UNION ALL SELECT value+1 FROM numbers WHERE value<1000000000)
                    SELECT COUNT(*) FROM numbers;
                    """;
                Task<long> running = Task.Run(() => searchCancellation.Run(() => (long)command.ExecuteScalar()!));
                cancellation.CancelAfter(TimeSpan.FromMilliseconds(50));
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                    await running.WaitAsync(TimeSpan.FromSeconds(5)));
            }
            finally
            {
                searchCancellation.Dispose();
            }
        }
        using SqliteConnection reusedConnection = Open(pooling: true);
        using SqliteCommand followup = reusedConnection.CreateCommand();
        followup.CommandText = "SELECT COUNT(*) FROM indexed_files;";
        Assert.Equal(0L, (long)followup.ExecuteScalar()!);
    }

    [Fact]
    public void RepresentativeQueryPlanShowsWhetherCompositeMetadataIndexHelps()
    {
        Add("example.mkv");
        using (SqliteConnection connection = Open())
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                WITH RECURSIVE sequence(n) AS
                    (SELECT 1 UNION ALL SELECT n+1 FROM sequence WHERE n<30000)
                INSERT INTO indexed_files(full_path,path_key,file_name,extension,size_bytes,
                    last_write_utc_ticks,availability_state,last_seen_utc_ticks,created_utc_ticks,updated_utc_ticks)
                SELECT 'synthetic-'||n, 'SYNTHETIC-'||n, 'synthetic-'||n||'.mkv', '.mkv',
                    1000000, 638000000000000000, 0, 638000000000000000,
                    638000000000000000, 638000000000000000 FROM sequence;
                INSERT INTO media_metadata(file_id,metadata_version,probe_tool_version,probe_status,
                    source_size_bytes,source_last_write_utc_ticks,video_codec,width,height,
                    frame_rate,video_bitrate_bps,updated_utc_ticks)
                SELECT id,2,'test',2,size_bytes,last_write_utc_ticks,
                    CASE WHEN id%20=0 THEN 'h264' ELSE 'hevc' END,
                    1920,1080,29.97,14000000,last_write_utc_ticks
                FROM indexed_files WHERE path_key LIKE 'SYNTHETIC-%';
                ANALYZE;
                """;
            command.ExecuteNonQuery();
        }
        CatalogSearchDefinition representative = Search(
            Is("video.codec", "h264"), Equal("video.width", 1920), Equal("video.height", 1080),
            Between("video.fps", 29, 30, "fps"), Between("video.bitrate", 13, 15, "mbps"))
            with { Availability = IndexedFileAvailability.Present };
        CompiledCatalogSearch compiled = CatalogSearchSqlCompiler.Compile(representative);
        string before = Explain(compiled);
        var beforeTimer = System.Diagnostics.Stopwatch.StartNew();
        LibraryFilePage beforePage = Query(representative);
        beforeTimer.Stop();
        using (SqliteConnection connection = Open())
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = "CREATE INDEX ix_search_candidate ON media_metadata(video_codec,width,height,frame_rate,video_bitrate_bps); ANALYZE;";
            command.ExecuteNonQuery();
        }
        string after = Explain(compiled);
        var afterTimer = System.Diagnostics.Stopwatch.StartNew();
        LibraryFilePage afterPage = Query(representative);
        afterTimer.Stop();
        _output.WriteLine("Existing plan: " + before);
        _output.WriteLine("Candidate composite plan: " + after);
        _output.WriteLine($"30,001 rows: before={beforeTimer.ElapsedMilliseconds}ms, after={afterTimer.ElapsedMilliseconds}ms");
        Assert.Equal(1501, beforePage.TotalCount);
        Assert.Equal(beforePage.TotalCount, afterPage.TotalCount);
        Assert.NotEmpty(before);
        Assert.Equal(before, after);
    }

    private static CatalogSearchDefinition Search(params CatalogSearchCondition[] conditions) => new(1, conditions);
    private static CatalogSearchCondition Is(string property, string value) =>
        new(property, CatalogSearchOperator.Is, new(Text: value));
    private static CatalogSearchCondition TextEquals(string property, string value) =>
        new(property, CatalogSearchOperator.Equal, new(Text: value));
    private static CatalogSearchCondition Equal(string property, decimal value, string? unit = null) =>
        new(property, CatalogSearchOperator.Equal, new(Number: value, Unit: unit));
    private static CatalogSearchCondition Between(string property, decimal lower, decimal upper, string? unit = null) =>
        new(property, CatalogSearchOperator.Between, new(Number: lower, Unit: unit), new(Number: upper, Unit: unit));
    private LibraryFilePage Query(CatalogSearchDefinition definition, int offset = 0, int limit = 200) =>
        _catalog.QueryFiles(new LibraryFileQuery(AdvancedSearch: definition, Offset: offset, Limit: limit));

    private long Add(string name, string codec = "h264", int? width = 1920, int? height = 1080,
        double? fps = 30000d / 1001, long? videoBitrate = 14_000_000, long? totalBitrate = 16_000_000,
        string fieldOrder = "progressive", int? bitDepth = 8, string pixelFormat = "yuv420p",
        string colorTransfer = "", string colorPrimaries = "", int version = 2,
        LibraryProbeStatus status = LibraryProbeStatus.Succeeded, bool stale = false,
        bool staleTimestamp = false, bool metadata = true,
        IndexedFileAvailability availability = IndexedFileAvailability.Present,
        string audioJson = "[]", string subtitleJson = "[]")
    {
        string path = Path.Combine(_root, "library", name);
        LibraryInventoryMutation mutation = Assert.Single(_catalog.UpsertInventoryBatchDetailed(_scan,
            new[] { new LibraryInventoryEntry(path, name, 1_000_000, _modified,
                Availability: availability) }, 2).Mutations);
        if (!metadata)
            return mutation.FileId;
        using var connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR REPLACE INTO media_metadata (
                file_id,metadata_version,probe_tool_version,probe_status,source_size_bytes,
                source_last_write_utc_ticks,format_name,duration_seconds,total_bitrate,
                video_codec,video_profile,video_level,width,height,frame_rate,pixel_format,
                bit_depth,field_order,color_transfer,color_primaries,audio_streams_json,subtitle_streams_json,updated_utc_ticks,
                selected_video_stream_index,video_stream_count,video_bitrate_bps,
                average_frame_rate,nominal_frame_rate,frame_rate_basis)
            VALUES ($id,$version,'test',$status,$source_size,$source_modified,'matroska',120,$total,
                $codec,'High',40,$width,$height,$fps,$pixel_format,$depth,$field,$color_transfer,$color_primaries,$audio,$sub,$modified,
                0,1,$video,$fps,30,'average');
            """;
        command.Parameters.AddWithValue("$id", mutation.FileId);
        command.Parameters.AddWithValue("$version", version);
        command.Parameters.AddWithValue("$status", (int)status);
        command.Parameters.AddWithValue("$source_size", stale ? 999_999 : 1_000_000);
        command.Parameters.AddWithValue("$source_modified", staleTimestamp ? mutation.LastWriteUtcTicks - 1 : mutation.LastWriteUtcTicks);
        command.Parameters.AddWithValue("$modified", mutation.LastWriteUtcTicks);
        command.Parameters.AddWithValue("$total", (object?)totalBitrate ?? DBNull.Value);
        command.Parameters.AddWithValue("$video", (object?)videoBitrate ?? DBNull.Value);
        command.Parameters.AddWithValue("$codec", codec);
        command.Parameters.AddWithValue("$width", (object?)width ?? DBNull.Value);
        command.Parameters.AddWithValue("$height", (object?)height ?? DBNull.Value);
        command.Parameters.AddWithValue("$fps", (object?)fps ?? DBNull.Value);
        command.Parameters.AddWithValue("$depth", (object?)bitDepth ?? DBNull.Value);
        command.Parameters.AddWithValue("$field", fieldOrder);
        command.Parameters.AddWithValue("$pixel_format", pixelFormat);
        command.Parameters.AddWithValue("$color_transfer", colorTransfer);
        command.Parameters.AddWithValue("$color_primaries", colorPrimaries);
        command.Parameters.AddWithValue("$audio", audioJson);
        command.Parameters.AddWithValue("$sub", subtitleJson);
        command.ExecuteNonQuery();
        return mutation.FileId;
    }

    private SqliteConnection Open(bool pooling = false)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = pooling
        }.ToString());
        connection.Open();
        return connection;
    }

    private string Explain(CompiledCatalogSearch compiled)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN SELECT file.id FROM indexed_files file " +
            "LEFT JOIN media_metadata metadata ON metadata.file_id=file.id WHERE 1=1" +
            compiled.PredicateSql + ";";
        compiled.Bind(command);
        using SqliteDataReader reader = command.ExecuteReader();
        var details = new List<string>();
        while (reader.Read())
            details.Add(reader.GetString(3));
        return string.Join(" | ", details);
    }

    public void Dispose()
    {
        _catalog.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_root, recursive: true);
    }
}
