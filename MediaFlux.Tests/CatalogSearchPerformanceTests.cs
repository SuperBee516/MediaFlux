using System.Collections.Concurrent;
using System.Diagnostics;
using MediaFlux.Models;
using MediaFlux.Services.LibraryCatalog;
using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace MediaFlux.Tests;

/// <summary>
/// Opt-in, catalog-only scale measurements. The normal suite takes the fast exit;
/// set MEDIAFLUX_RUN_CATALOG_SEARCH_PERFORMANCE=1 and filter Category=Performance
/// to build 10k/50k/100k synthetic catalogs and emit plans and timings.
/// </summary>
public sealed class CatalogSearchPerformanceTests
{
    private const string RunVariable = "MEDIAFLUX_RUN_CATALOG_SEARCH_PERFORMANCE";
    private const string CandidateIndexName = "ix_search_candidate_metadata";
    private readonly ITestOutputHelper _output;

    public CatalogSearchPerformanceTests(ITestOutputHelper output) => _output = output;

    [Fact]
    [Trait("Category", "Performance")]
    public async Task CatalogSearchScaleAndConcurrencyMeasurements()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(RunVariable), "1", StringComparison.Ordinal))
        {
            _output.WriteLine($"Opt-in workload not run. Set {RunVariable}=1 to execute it.");
            return;
        }

        foreach (int size in new[] { 10_000, 50_000, 100_000 })
        {
            using var fixture = new CatalogFixture(size);
            fixture.Seed();
            _output.WriteLine("");
            _output.WriteLine($"CATALOG rows={size:N0}; schema={fixture.SchemaVersion}; journal={fixture.JournalMode}; " +
                              $"db-bytes={fixture.DatabaseBytes:N0}; v1-backfill-candidates={fixture.BackfillCandidateCount():N0}");

            IReadOnlyList<SearchWorkload> workloads = CreateWorkloads();
            var baselineResults = new Dictionary<string, WorkloadMeasurement>(StringComparer.Ordinal);
            foreach (SearchWorkload workload in workloads)
            {
                WorkloadMeasurement measurement = Measure(fixture, workload, repetitions: 5);
                baselineResults.Add(workload.Name, measurement);
                _output.WriteLine(
                    $"BASE {workload.Name}: total={measurement.TotalCount:N0}; page={measurement.PageCount:N0}; " +
                    $"first-query={measurement.FirstQueryMs:F1}ms; warm-median={measurement.WarmMedianMs:F1}ms; " +
                    $"warm-count-median={measurement.WarmCountMedianMs:F1}ms; " +
                    $"warm-page-phase-median={measurement.WarmPagePhaseMedianMs:F1}ms; " +
                    $"warm-allocated-median={measurement.WarmAllocatedMedianBytes:N0} bytes; " +
                    $"count-plan=[{measurement.CountPlan}]; page-plan=[{measurement.PagePlan}]");
            }

            CandidateIndexMeasurement candidate = MeasureCandidateIndex(fixture, workloads, baselineResults);
            _output.WriteLine(
                $"CANDIDATE index={CandidateIndexName}; create={candidate.CreateMs:F1}ms; " +
                $"catalog-size-delta={candidate.SizeDeltaBytes:N0} bytes; schema-after={fixture.SchemaVersion}; " +
                $"index-present={candidate.IndexPresent}; baseline-plan=[{candidate.BaselinePlan}]; " +
                $"candidate-plan=[{candidate.CandidatePlan}]; baseline-median={candidate.BaselineMedianMs:F1}ms; " +
                $"candidate-median={candidate.CandidateMedianMs:F1}ms");

            if (size == 100_000)
            {
                await MeasureCancellationAsync(fixture, workloads.Single(value => value.Name == "A motivating technical"));
                await MeasureEnrichmentConcurrencyAsync(
                    fixture,
                    workloads.Single(value => value.Name == "B broad technical"));
            }
        }
    }

    private static IReadOnlyList<SearchWorkload> CreateWorkloads()
    {
        static CatalogSearchCondition Is(string id, string value) =>
            new(id, CatalogSearchOperator.Is, new CatalogSearchValue(Text: value));
        static CatalogSearchCondition Number(string id, CatalogSearchOperator operation, decimal value, string? unit = null) =>
            new(id, operation, CatalogSearchValue.ParseNumber(value.ToString(System.Globalization.CultureInfo.InvariantCulture), unit));
        static CatalogSearchCondition Between(string id, decimal lower, decimal upper, string unit) =>
            new(id, CatalogSearchOperator.Between,
                CatalogSearchValue.ParseNumber(lower.ToString(System.Globalization.CultureInfo.InvariantCulture), unit),
                CatalogSearchValue.ParseNumber(upper.ToString(System.Globalization.CultureInfo.InvariantCulture), unit));
        static CatalogSearchDefinition Search(params CatalogSearchCondition[] conditions) =>
            new(CatalogSearchDefinition.CurrentVersion, conditions, Availability: IndexedFileAvailability.Present);

        return new[]
        {
            new SearchWorkload("A motivating technical", Search(
                Is("video.codec", "h264"),
                Number("video.width", CatalogSearchOperator.Equal, 1920, "pixels"),
                Number("video.height", CatalogSearchOperator.Equal, 1080, "pixels"),
                Between("video.fps", 29, 30, "fps"),
                Between("video.bitrate", 13, 15, "mbps"),
                Is("video.field_order", "progressive"),
                Number("video.bit_depth", CatalogSearchOperator.Equal, 8, "count"))),
            new SearchWorkload("B broad technical", Search(
                Is("video.codec", "hevc"),
                Number("video.height", CatalogSearchOperator.GreaterThanOrEqual, 1080, "pixels"),
                new CatalogSearchCondition("video.bitrate", CatalogSearchOperator.Known))),
            new SearchWorkload("C derived expressions", Search(
                Between("video.source_bpp", 0.04m, 0.16m, "bpp"),
                Between("video.pixels_per_second", 40_000_000m, 250_000_000m, "px/s"))),
            new SearchWorkload("H derived classifications", Search(
                Is("video.coded_orientation", "landscape"),
                Is("video.resolution_class", "1080p"),
                Is("video.dynamic_range", "HDR"),
                Is("video.chroma_subsampling", "4:2:0"))),
            new SearchWorkload("D filename/path contains", new CatalogSearchDefinition(
                CatalogSearchDefinition.CurrentVersion, Array.Empty<CatalogSearchCondition>(), Search: "clip-000123")),
            new SearchWorkload("E audio/subtitle JSON", Search(
                Is("stream.audio_codec", "eac3"),
                Is("stream.subtitle_codec", "ass"))),
            new SearchWorkload("F current bitrate unknown", Search(
                new CatalogSearchCondition("video.bitrate", CatalogSearchOperator.Unknown))),
            new SearchWorkload("G selective no-match", Search(
                Is("video.codec", "h264"),
                Number("video.width", CatalogSearchOperator.GreaterThan, 8000, "pixels"),
                Number("video.bitrate", CatalogSearchOperator.GreaterThan, 100_000, "mbps")))
        };
    }

    private static WorkloadMeasurement Measure(CatalogFixture fixture, SearchWorkload workload, int repetitions)
    {
        var query = new LibraryFileQuery(AdvancedSearch: workload.Definition, Limit: 200);
        string countPlan = Explain(fixture, workload.Definition, page: false);
        string pagePlan = Explain(fixture, workload.Definition, page: true);

        QueryTiming firstTiming = MeasureQuery(fixture, query);
        LibraryFilePage first = firstTiming.Result;
        var warm = new double[repetitions];
        var warmCount = new double[repetitions];
        var warmPagePhase = new double[repetitions];
        var warmAllocated = new long[repetitions];
        LibraryFilePage last = first;
        for (int index = 0; index < repetitions; index++)
        {
            QueryTiming timing = MeasureQuery(fixture, query);
            last = timing.Result;
            warm[index] = timing.TotalMs;
            warmCount[index] = timing.CountPhaseMs;
            warmPagePhase[index] = timing.TotalMs - timing.CountPhaseMs;
            warmAllocated[index] = timing.AllocatedBytes;
        }
        if (last.TotalCount != first.TotalCount)
            throw new InvalidOperationException($"Synthetic query count changed between repeats: {workload.Name}.");

        return new WorkloadMeasurement(
            first.TotalCount,
            first.Files.Count,
            firstTiming.TotalMs,
            Median(warm),
            Median(warmCount),
            Median(warmPagePhase),
            (long)Median(warmAllocated.Select(value => (double)value).ToArray()),
            countPlan,
            pagePlan);
    }

    private static QueryTiming MeasureQuery(CatalogFixture fixture, LibraryFileQuery query)
    {
        var countTimer = Stopwatch.StartNew();
        var totalTimer = Stopwatch.StartNew();
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        LibraryFilePage result = fixture.Catalog.QueryFilesWithCountPageGapForTesting(
            query,
            countTimer.Stop);
        long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        totalTimer.Stop();
        return new QueryTiming(result, totalTimer.Elapsed.TotalMilliseconds, countTimer.Elapsed.TotalMilliseconds, allocatedBytes);
    }

    private static CandidateIndexMeasurement MeasureCandidateIndex(
        CatalogFixture fixture,
        IReadOnlyList<SearchWorkload> workloads,
        IReadOnlyDictionary<string, WorkloadMeasurement> baseline)
    {
        SearchWorkload motivating = workloads.Single(value => value.Name == "A motivating technical");
        long beforeBytes = fixture.DatabaseBytes;
        var create = Stopwatch.StartNew();
        using (SqliteConnection connection = fixture.OpenConnection())
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = $"CREATE INDEX {CandidateIndexName} ON media_metadata(video_codec,width,height,frame_rate,video_bitrate_bps); ANALYZE;";
            command.ExecuteNonQuery();
        }
        create.Stop();
        fixture.Checkpoint();
        double[] candidateTimes = MeasureRepeated(fixture, motivating.Definition, repetitions: 5);
        string candidatePlan = Explain(fixture, motivating.Definition, page: false);
        long afterBytes = fixture.DatabaseBytes;
        bool present = fixture.HasIndex(CandidateIndexName);
        var result = new CandidateIndexMeasurement(
            create.Elapsed.TotalMilliseconds,
            afterBytes - beforeBytes,
            present,
            baseline[motivating.Name].CountPlan,
            candidatePlan,
            baseline[motivating.Name].WarmMedianMs,
            Median(candidateTimes));
        using (SqliteConnection connection = fixture.OpenConnection())
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = $"DROP INDEX {CandidateIndexName}; ANALYZE;";
            command.ExecuteNonQuery();
        }
        fixture.Checkpoint();
        return result;
    }

    private static double[] MeasureRepeated(CatalogFixture fixture, CatalogSearchDefinition definition, int repetitions)
    {
        var values = new double[repetitions];
        var query = new LibraryFileQuery(AdvancedSearch: definition, Limit: 200);
        for (int index = 0; index < repetitions; index++)
        {
            var timer = Stopwatch.StartNew();
            _ = fixture.Catalog.QueryFiles(query);
            timer.Stop();
            values[index] = timer.Elapsed.TotalMilliseconds;
        }
        return values;
    }

    private static string Explain(CatalogFixture fixture, CatalogSearchDefinition definition, bool page)
    {
        CompiledCatalogSearch compiled = CatalogSearchSqlCompiler.Compile(definition);
        using SqliteConnection connection = fixture.OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        string sql = page
            ? "SELECT file.id FROM indexed_files file LEFT JOIN media_metadata metadata ON metadata.file_id=file.id " +
              "WHERE 1=1" + compiled.PredicateSql + " ORDER BY file.path_key ASC,file.id ASC LIMIT $limit OFFSET 0;"
            : "SELECT COUNT(*) FROM indexed_files file LEFT JOIN media_metadata metadata ON metadata.file_id=file.id " +
              "WHERE 1=1" + compiled.PredicateSql + ";";
        command.CommandText = "EXPLAIN QUERY PLAN " + sql;
        compiled.Bind(command);
        if (page)
            command.Parameters.AddWithValue("$limit", 200);
        using SqliteDataReader reader = command.ExecuteReader();
        var details = new List<string>();
        while (reader.Read())
            details.Add(reader.GetString(3));
        return string.Join(" | ", details);
    }

    private async Task MeasureCancellationAsync(CatalogFixture fixture, SearchWorkload motivating)
    {
        CatalogSearchCondition[] repeatedJson = Enumerable.Range(0, CatalogSearchDefinitionValidator.MaximumConditions - 1)
            .Select(_ => new CatalogSearchCondition("stream.audio_codec", CatalogSearchOperator.Is,
                new CatalogSearchValue(Text: "eac3")))
            .ToArray();
        CatalogSearchCondition[] conditions = repeatedJson
            .Append(new CatalogSearchCondition("video.width", CatalogSearchOperator.GreaterThan,
                CatalogSearchValue.ParseNumber("8000", "pixels")))
            .ToArray();
        var definition = new CatalogSearchDefinition(
            CatalogSearchDefinition.CurrentVersion,
            conditions,
            Availability: IndexedFileAvailability.Present);
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        long cancellationRequestedAt = 0;
        using var cancelTimer = new Timer(_ =>
        {
            Interlocked.Exchange(ref cancellationRequestedAt, Stopwatch.GetTimestamp());
            cancellation.Cancel();
        }, null, Timeout.Infinite, Timeout.Infinite);
        Task<LibraryFilePage> running = Task.Run(() =>
        {
            entered.TrySetResult();
            return fixture.Catalog.QueryFiles(new LibraryFileQuery(AdvancedSearch: definition, Limit: 200), cancellation.Token);
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancelTimer.Change(TimeSpan.FromMilliseconds(20), Timeout.InfiniteTimeSpan);
        bool interrupted = false;
        try
        {
            await running.WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch (OperationCanceledException)
        {
            interrupted = true;
        }
        Assert.True(interrupted, "The deliberately expensive catalog search should be interrupted.");
        double cancellationLatencyMs = cancellationRequestedAt == 0
            ? double.NaN
            : Stopwatch.GetElapsedTime(Interlocked.Read(ref cancellationRequestedAt)).TotalMilliseconds;
        LibraryFilePage followup = fixture.Catalog.QueryFiles(
            new LibraryFileQuery(AdvancedSearch: motivating.Definition, Limit: 200));
        Assert.True(followup.TotalCount > 0, "A post-cancellation search should still use a healthy connection and the motivating matches.");
        _output.WriteLine(
            $"CANCEL in-flight repeated-conditions={conditions.Length}; json-conditions={repeatedJson.Length}; interrupted={interrupted}; " +
            $"request-to-completion={(double.IsFinite(cancellationLatencyMs) ? $"{cancellationLatencyMs:F1}ms" : "not observed")}; " +
            $"follow-up-count={followup.TotalCount:N0}");
    }

    private async Task MeasureEnrichmentConcurrencyAsync(CatalogFixture fixture, SearchWorkload broad)
    {
        long target = fixture.BackfillCandidateCount();
        if (target == 0)
        {
            _output.WriteLine("ENRICHMENT no synthetic metadata-v2 backfill candidates.");
            return;
        }

        var probe = new SyntheticProbe();
        var complete = new TaskCompletionSource<LibraryEnrichmentProgress>(TaskCreationOptions.RunContinuationsAsynchronously);
        var measurements = new ConcurrentQueue<double>();
        using var stopReader = new CancellationTokenSource();
        await using var coordinator = new LibraryEnrichmentCoordinator(
            fixture.Catalog,
            probe,
            new LibraryEnrichmentOptions(
                WorkerCount: 2,
                QueueCapacity: 1024,
                PendingClaimBatchSize: 512,
                RetryPollInterval: TimeSpan.FromMilliseconds(50)));
        coordinator.ProgressChanged += (_, progress) =>
        {
            if (progress.Completed + progress.Failed >= target)
                complete.TrySetResult(progress);
        };

        Task reader = Task.Run(async () =>
        {
            await probe.FirstProbe.WaitAsync(TimeSpan.FromSeconds(30));
            var query = new LibraryFileQuery(AdvancedSearch: broad.Definition, Limit: 200);
            while (!stopReader.IsCancellationRequested)
            {
                var timer = Stopwatch.StartNew();
                _ = fixture.Catalog.QueryFiles(query);
                timer.Stop();
                measurements.Enqueue(timer.Elapsed.TotalMilliseconds);
                await Task.Yield();
            }
        });

        coordinator.Start();
        var queueTimer = Stopwatch.StartNew();
        while (await coordinator.QueuePendingAsync() > 0) { }
        LibraryEnrichmentProgress completed = await complete.Task.WaitAsync(TimeSpan.FromMinutes(5));
        queueTimer.Stop();
        stopReader.Cancel();
        await reader;
        double[] readTimes = measurements.ToArray();
        _output.WriteLine(
            $"ENRICHMENT target={target:N0}; completed={completed.Completed:N0}; failed={completed.Failed:N0}; " +
            $"wall={queueTimer.Elapsed.TotalSeconds:F2}s; concurrent-searches={readTimes.Length:N0}; " +
            $"search-median={Median(readTimes):F1}ms; search-p95={Percentile(readTimes, 0.95):F1}ms; " +
            $"probe-calls={probe.Calls:N0}; probes are synthetic and do no file I/O");
    }

    private static double Median(IReadOnlyCollection<double> values)
    {
        if (values.Count == 0) return 0;
        double[] sorted = values.OrderBy(value => value).ToArray();
        int middle = sorted.Length / 2;
        return sorted.Length % 2 == 0 ? (sorted[middle - 1] + sorted[middle]) / 2 : sorted[middle];
    }

    private static double Percentile(IReadOnlyCollection<double> values, double percentile)
    {
        if (values.Count == 0) return 0;
        double[] sorted = values.OrderBy(value => value).ToArray();
        int index = Math.Clamp((int)Math.Ceiling(percentile * sorted.Length) - 1, 0, sorted.Length - 1);
        return sorted[index];
    }

    private sealed record SearchWorkload(string Name, CatalogSearchDefinition Definition);

    private sealed record QueryTiming(LibraryFilePage Result, double TotalMs, double CountPhaseMs, long AllocatedBytes);

    private sealed record WorkloadMeasurement(
        long TotalCount,
        int PageCount,
        double FirstQueryMs,
        double WarmMedianMs,
        double WarmCountMedianMs,
        double WarmPagePhaseMedianMs,
        long WarmAllocatedMedianBytes,
        string CountPlan,
        string PagePlan);

    private sealed record CandidateIndexMeasurement(
        double CreateMs,
        long SizeDeltaBytes,
        bool IndexPresent,
        string BaselinePlan,
        string CandidatePlan,
        double BaselineMedianMs,
        double CandidateMedianMs);

    private sealed class CatalogFixture : IDisposable
    {
        private readonly string _root;
        private readonly string _databasePath;
        private readonly int _fileCount;
        private readonly long[] _locationIds;

        internal CatalogFixture(int fileCount)
        {
            _fileCount = fileCount;
            _root = Path.Combine(Path.GetTempPath(), "MediaFlux-CatalogSearchPerformance", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _databasePath = Path.Combine(_root, "catalog.db");
            Catalog = new SqliteLibraryCatalog(_databasePath, Path.Combine(_root, "backups"), Path.Combine(_root, "recovery"));
            Catalog.Initialize();
            _locationIds = Enumerable.Range(0, 10)
                .Select(index => Catalog.UpsertLocation(new LibraryLocationUpsert(Path.Combine(_root, $"library-{index:D2}"))).Id)
                .ToArray();
        }

        internal SqliteLibraryCatalog Catalog { get; }
        internal int SchemaVersion => Convert.ToInt32(Scalar("PRAGMA user_version;"));
        internal string JournalMode => Convert.ToString(Scalar("PRAGMA journal_mode;")) ?? "unknown";
        internal long DatabaseBytes => new FileInfo(_databasePath).Length;

        internal void Seed()
        {
            using SqliteConnection connection = OpenConnection();
            using SqliteTransaction transaction = connection.BeginTransaction();
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                WITH RECURSIVE sequence(n) AS (
                    SELECT 1 UNION ALL SELECT n+1 FROM sequence WHERE n<$count
                )
                INSERT INTO indexed_files(full_path,path_key,file_name,extension,size_bytes,creation_utc_ticks,
                    last_write_utc_ticks,volume_id,file_identity,availability_state,last_seen_utc_ticks,created_utc_ticks,updated_utc_ticks)
                SELECT $root || '/library-' || printf('%02d',(n-1)%10) || '/clip-' || printf('%06d',n) || '.mkv',
                    upper($root || '/library-' || printf('%02d',(n-1)%10) || '/clip-' || printf('%06d',n) || '.mkv'),
                    'clip-' || printf('%06d',n) || '.mkv','.mkv',
                    500000000 + (n%200000000),$ticks,$ticks,
                    'synthetic-volume-' || printf('%02d',(n-1)%10),'',
                    CASE WHEN (n*53)%100<94 THEN 0 WHEN (n*53)%100<97 THEN 1 ELSE 2 END,
                    $ticks,$ticks,$ticks
                FROM sequence;

                WITH slots(slot,location_id) AS (VALUES
                    (0,$location0),(1,$location1),(2,$location2),(3,$location3),(4,$location4),
                    (5,$location5),(6,$location6),(7,$location7),(8,$location8),(9,$location9))
                INSERT INTO file_location_memberships(location_id,file_id,relative_path,relative_path_key,
                    last_seen_generation,availability_state,last_seen_utc_ticks)
                SELECT slots.location_id,file.id,file.file_name,upper(file.file_name),1,
                    file.availability_state,file.last_seen_utc_ticks
                FROM indexed_files file JOIN slots ON slots.slot=(file.id-1)%10;

                INSERT INTO media_metadata(file_id,metadata_version,probe_tool_version,probe_status,
                    attempt_count,next_retry_utc_ticks,last_attempt_utc_ticks,last_success_utc_ticks,
                    source_size_bytes,source_last_write_utc_ticks,format_name,duration_seconds,total_bitrate,
                    video_codec,video_profile,video_level,width,height,frame_rate,pixel_format,bit_depth,
                    field_order,color_range,color_space,color_transfer,color_primaries,audio_streams_json,
                    subtitle_streams_json,chapter_count,attachment_count,error_message,updated_utc_ticks,
                    selected_video_stream_index,video_stream_count,video_bitrate_bps,average_frame_rate,
                    nominal_frame_rate,frame_rate_basis,sample_aspect_ratio,display_aspect_ratio)
                SELECT file.id,
                    CASE WHEN (file.id*31)%100 BETWEEN 80 AND 87 THEN 1 ELSE 2 END,
                    'benchmark-tool',
                    CASE WHEN (file.id*43)%100<88 THEN 2 WHEN (file.id*43)%100<92 THEN 3
                         WHEN (file.id*43)%100<95 THEN 0 WHEN (file.id*43)%100<97 THEN 1 ELSE 2 END,
                    0,NULL,NULL,$ticks,
                    CASE WHEN (file.id*17)%100>=97 THEN file.size_bytes-1 ELSE file.size_bytes END,
                    file.last_write_utc_ticks,'matroska',600+(file.id%7200),
                    (CASE WHEN (file.id*29)%100<6 THEN 800000 + ((file.id*29)%20)*50000
                          WHEN (file.id*29)%100<20 THEN 2500000 + ((file.id*29)%40)*150000
                          WHEN (file.id*29)%100<50 THEN 6000000 + ((file.id*29)%100)*75000
                          WHEN (file.id*29)%100<62 THEN 13000000 + (((file.id*29)%100)-50)*160000
                          WHEN (file.id*29)%100<90 THEN 18000000 + ((file.id*29)%100)*500000
                          ELSE 45000000 + ((file.id*29)%10)*2000000 END) + 256000,
                    CASE WHEN (file.id*61)%100<44 THEN 'h264' WHEN (file.id*61)%100<84 THEN 'hevc'
                         WHEN (file.id*61)%100<94 THEN 'av1' ELSE 'mpeg2video' END,
                    'Synthetic Main',40,
                    CASE WHEN (file.id*37)%100<6 THEN 640 WHEN (file.id*37)%100<20 THEN 1280
                         WHEN (file.id*37)%100<70 THEN 1920 WHEN (file.id*37)%100<84 THEN 2560
                         WHEN (file.id*37)%100<98 THEN 3840 ELSE 7680 END,
                    CASE WHEN (file.id*37)%100<6 THEN 480 WHEN (file.id*37)%100<20 THEN 720
                         WHEN (file.id*37)%100<70 THEN 1080 WHEN (file.id*37)%100<84 THEN 1440
                         WHEN (file.id*37)%100<98 THEN 2160 ELSE 4320 END,
                    CASE (file.id*43)%8 WHEN 0 THEN 23.976 WHEN 1 THEN 24.0 WHEN 2 THEN 25.0
                         WHEN 3 THEN 29.97 WHEN 4 THEN 30.0 WHEN 5 THEN 50.0
                         WHEN 6 THEN 59.94 ELSE 60.0 END,
                    CASE (file.id*43)%8 WHEN 0 THEN 'yuv420p' WHEN 1 THEN 'yuv420p10le'
                         WHEN 2 THEN 'yuv422p' WHEN 3 THEN 'yuv420p'
                         WHEN 4 THEN 'yuv444p' WHEN 5 THEN 'p010le'
                         WHEN 6 THEN 'nv12' ELSE 'yuvj420p' END,
                    CASE WHEN (file.id*71)%100<85 THEN 8 WHEN (file.id*71)%100<98 THEN 10
                         WHEN (file.id*71)%100=98 THEN 12 ELSE NULL END,
                    CASE WHEN (file.id*13)%20=0 THEN 'tt' ELSE 'progressive' END,
                    'tv','bt709',
                    CASE WHEN (file.id*7)%1000<50 THEN 'smpte2084' WHEN (file.id*7)%1000<100 THEN 'arib-std-b67'
                         WHEN (file.id*7)%1000<950 THEN 'bt709' ELSE '' END,
                    'bt709',
                    CASE (file.id*11)%6 WHEN 0 THEN '[]' WHEN 1 THEN '[{"codec":"aac"}]'
                         WHEN 2 THEN '[{"codec":"eac3"},{"codec":"aac"}]'
                         WHEN 3 THEN '[{"codec":"opus"}]'
                         WHEN 4 THEN '[{"codec":"dts"},{"codec":"aac"},{"codec":"aac"}]'
                         ELSE '[{"codec":"flac"}]' END,
                    CASE (file.id*17)%5 WHEN 0 THEN '[]' WHEN 1 THEN '[{"codec":"subrip"}]'
                         WHEN 2 THEN '[{"codec":"ass"}]' WHEN 3 THEN '[{"codec":"webvtt"}]'
                         ELSE '[{"codec":"dvd_subtitle"},{"codec":"subrip"}]' END,
                    file.id%8,CASE WHEN file.id%31=0 THEN 1 ELSE 0 END,'',$ticks,
                    0,CASE WHEN (file.id*23)%50=0 THEN 2 ELSE 1 END,
                    CASE WHEN (file.id*19)%37=0 THEN NULL ELSE
                        CASE WHEN (file.id*29)%100<6 THEN 800000 + ((file.id*29)%20)*50000
                             WHEN (file.id*29)%100<20 THEN 2500000 + ((file.id*29)%40)*150000
                             WHEN (file.id*29)%100<50 THEN 6000000 + ((file.id*29)%100)*75000
                             WHEN (file.id*29)%100<62 THEN 13000000 + (((file.id*29)%100)-50)*160000
                             WHEN (file.id*29)%100<90 THEN 18000000 + ((file.id*29)%100)*500000
                             ELSE 45000000 + ((file.id*29)%10)*2000000 END END,
                    CASE (file.id*43)%8 WHEN 0 THEN 23.976 WHEN 1 THEN 24.0 WHEN 2 THEN 25.0
                         WHEN 3 THEN 29.97 WHEN 4 THEN 30.0 WHEN 5 THEN 50.0
                         WHEN 6 THEN 59.94 ELSE 60.0 END,
                    CASE (file.id*43)%8 WHEN 3 THEN 30.0 ELSE
                        CASE (file.id*43)%8 WHEN 0 THEN 24.0 WHEN 1 THEN 24.0 WHEN 2 THEN 25.0
                             WHEN 4 THEN 30.0 WHEN 5 THEN 50.0 WHEN 6 THEN 60.0 ELSE 60.0 END END,
                    'average','1:1','16:9'
                FROM indexed_files file
                WHERE (file.id*47)%100<>0;
                ANALYZE;
                """;
            command.Parameters.AddWithValue("$count", _fileCount);
            command.Parameters.AddWithValue("$root", _root.Replace('\\', '/'));
            command.Parameters.AddWithValue("$ticks", new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks);
            for (int index = 0; index < _locationIds.Length; index++)
                command.Parameters.AddWithValue($"$location{index}", _locationIds[index]);
            command.ExecuteNonQuery();
            transaction.Commit();
            Checkpoint();
        }

        internal long BackfillCandidateCount()
        {
            using SqliteConnection connection = OpenConnection();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                SELECT COUNT(*) FROM indexed_files file
                LEFT JOIN media_metadata metadata ON metadata.file_id=file.id
                WHERE file.availability_state=0 AND (
                    metadata.file_id IS NULL OR metadata.metadata_version<>2 OR
                    metadata.probe_tool_version<>'benchmark-tool' OR
                    metadata.source_size_bytes<>file.size_bytes OR
                    metadata.source_last_write_utc_ticks<>file.last_write_utc_ticks OR
                    metadata.probe_status=0 OR
                    (metadata.probe_status=3 AND metadata.next_retry_utc_ticks IS NOT NULL AND metadata.next_retry_utc_ticks<=$now))
                    AND (metadata.probe_status IS NULL OR metadata.probe_status<>1);
                """;
            command.Parameters.AddWithValue("$now", DateTime.UtcNow.Ticks);
            return Convert.ToInt64(command.ExecuteScalar());
        }

        internal bool HasIndex(string name)
        {
            using SqliteConnection connection = OpenConnection();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name=$name;";
            command.Parameters.AddWithValue("$name", name);
            return Convert.ToInt32(command.ExecuteScalar()) > 0;
        }

        internal object? Scalar(string sql)
        {
            using SqliteConnection connection = OpenConnection();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = sql;
            return command.ExecuteScalar();
        }

        internal SqliteConnection OpenConnection()
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = _databasePath,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
                ForeignKeys = true
            }.ToString());
            connection.Open();
            return connection;
        }

        internal void Checkpoint()
        {
            using SqliteConnection connection = OpenConnection();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read()) { }
        }

        public void Dispose()
        {
            Catalog.Dispose();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class SyntheticProbe : ILibraryMetadataProbe
    {
        private int _calls;
        private readonly TaskCompletionSource _firstProbe = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private static readonly MediaProbeResult Result = new()
        {
            Success = true,
            FormatName = "matroska",
            DurationSeconds = 600,
            BitRate = 14_256_000,
            Streams = new[]
            {
                new MediaProbeStreamInfo
                {
                    Index = 0, CodecType = "video", CodecName = "h264", Profile = "High", Level = 40,
                    Width = 1920, Height = 1080, FrameRate = 29.97, AverageFrameRate = 29.97,
                    NominalFrameRate = 30, BitRate = 14_000_000, PixelFormat = "yuv420p",
                    BitsPerRawSample = 8, FieldOrder = "progressive", ColorTransfer = "bt709",
                    ColorPrimaries = "bt709", SampleAspectRatio = "1:1", DisplayAspectRatio = "16:9",
                    Dispositions = new Dictionary<string, bool> { ["default"] = true }
                },
                new MediaProbeStreamInfo { Index = 1, CodecType = "audio", CodecName = "aac", Channels = 2 },
                new MediaProbeStreamInfo { Index = 2, CodecType = "subtitle", CodecName = "subrip" }
            }
        };

        public string ToolVersion => "benchmark-tool";
        internal int Calls => Volatile.Read(ref _calls);
        internal Task FirstProbe => _firstProbe.Task;

        public Task<MediaProbeResult> ProbeAsync(string path, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _calls);
            _firstProbe.TrySetResult();
            return Task.FromResult(Result);
        }
    }
}
