using System.Collections.Concurrent;
using System.Text.Json;
using MediaFlux.Services.LibraryCatalog;
using Xunit;

namespace MediaFlux.Tests;

public sealed class LibraryAdvancedSearchStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MediaFlux-SavedSearchStore", Guid.NewGuid().ToString("N"));
    private readonly ConcurrentQueue<string> _diagnostics = new();

    public LibraryAdvancedSearchStoreTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void MissingStoreIsEmptyAndSaveReloadPreservesNameCasingAndDefinitionVersion()
    {
        string path = Path.Combine(_root, "searches.json");
        var first = CreateStore(path);
        Assert.Empty(first.Load());

        CatalogSearchDefinition definition = Search(
            Is("video.codec", "h264"),
            Between("video.fps", 29, 30, "fps"));
        LibrarySavedSearch saved = first.SaveNew("  My Library Search  ", definition);

        Assert.Equal("My Library Search", saved.Name);
        LibrarySavedSearch loaded = Assert.Single(CreateStore(path).Load());
        Assert.Equal("My Library Search", loaded.Name);
        Assert.Equal(CatalogSearchDefinition.CurrentVersion, loaded.Definition.Version);
        Assert.True(CatalogSearchDefinitionEquivalence.AreEquivalent(definition, loaded.Definition));
    }

    [Fact]
    public void UpdateRenameAndDeleteUseCaseInsensitiveIdentityAndPreserveDisplayCasing()
    {
        var store = CreateStore(Path.Combine(_root, "searches.json"));
        store.SaveNew("Cinema", Search(Is("video.codec", "h264")));
        Assert.Throws<InvalidOperationException>(() => store.SaveNew("CINEMA", Search(Is("video.codec", "hevc"))));

        LibrarySavedSearch updated = store.Update("cinema", Search(Is("video.codec", "hevc")));
        Assert.Equal("Cinema", updated.Name);
        Assert.Equal("hevc", Assert.Single(updated.Definition.Conditions).Value?.Text);
        store.SaveNew("Archive", Search(Is("video.codec", "av1")));
        Assert.Throws<InvalidOperationException>(() => store.Rename("Cinema", "archive"));

        LibrarySavedSearch renamed = store.Rename("CINEMA", "My Cinema");
        Assert.Equal("My Cinema", renamed.Name);
        Assert.Equal("my cinema", store.Rename("My Cinema", "my cinema").Name);
        Assert.False(store.Delete("Missing"));
        Assert.True(store.Delete("MY CINEMA"));
        Assert.Single(store.Load());
    }

    [Fact]
    public void NamesAreTrimmedBoundedAndRejectControlCharacters()
    {
        var store = CreateStore(Path.Combine(_root, "searches.json"));
        Assert.Throws<ArgumentException>(() => store.SaveNew(" \t ", Search()));
        Assert.Throws<ArgumentException>(() => store.SaveNew(new string('x', LibraryAdvancedSearchStore.MaximumNameLength + 1), Search()));
        Assert.Throws<ArgumentException>(() => store.SaveNew("bad\nname", Search()));

        Assert.Equal("Preserved Case", store.SaveNew("  Preserved Case  ", Search()).Name);
    }

    [Fact]
    public void CorruptStoreIsPreservedAndBackedUpBeforeFirstSuccessfulReplacement()
    {
        string path = Path.Combine(_root, "corrupt.json");
        const string corrupt = "{ definitely not JSON";
        File.WriteAllText(path, corrupt);
        var store = CreateStore(path);

        Assert.Empty(store.Load());
        Assert.Equal(corrupt, File.ReadAllText(path));
        Assert.NotEmpty(_diagnostics);

        store.SaveNew("Recovered", Search(Is("video.codec", "h264")));
        Assert.Single(CreateStore(path).Load());
        string backup = Assert.Single(Directory.GetFiles(_root, "corrupt.json.recovery-*.json"));
        Assert.Equal(corrupt, File.ReadAllText(backup));
    }

    [Fact]
    public void UnsupportedFutureStoreVersionIsNotDowngradedOrOverwritten()
    {
        string path = Path.Combine(_root, "future.json");
        const string future = "{\"Version\":99,\"Searches\":[]}";
        File.WriteAllText(path, future);
        var store = CreateStore(path);

        Assert.Empty(store.Load());
        Assert.Throws<InvalidOperationException>(() => store.SaveNew("New", Search()));
        Assert.Equal(future, File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(_root, "future.json.recovery-*.json"));
    }

    [Fact]
    public void InvalidAndDuplicatePersistedEntriesAreSkippedAndOriginalIsBackedUpBeforeMutation()
    {
        string path = Path.Combine(_root, "mixed.json");
        File.WriteAllText(path, """
            {
              "Version": 1,
              "Searches": [
                { "Name": "Valid", "Definition": { "Version": 1, "Conditions": [ { "PropertyId": "video.width", "Operator": "Equal", "Value": { "Number": 1920, "Unit": "pixels" } } ] } },
                { "Name": "Invalid property", "Definition": { "Version": 1, "Conditions": [ { "PropertyId": "future.property", "Operator": "Unknown" } ] } },
                { "Name": "Invalid operator", "Definition": { "Version": 1, "Conditions": [ { "PropertyId": "video.width", "Operator": 999, "Value": { "Number": 1920, "Unit": "pixels" } } ] } },
                { "Name": "Invalid unit", "Definition": { "Version": 1, "Conditions": [ { "PropertyId": "video.width", "Operator": "Equal", "Value": { "Number": 1920, "Unit": "miles" } } ] } },
                { "Name": "Unsupported definition", "Definition": { "Version": 2, "Conditions": [] } },
                { "Name": "bad\u0001name", "Definition": { "Version": 1, "Conditions": [] } },
                { "Name": "VALID", "Definition": { "Version": 1, "Conditions": [] } }
              ]
            }
            """);
        string original = File.ReadAllText(path);
        var store = CreateStore(path);

        LibrarySavedSearch loaded = Assert.Single(store.Load());
        Assert.Equal("Valid", loaded.Name);
        Assert.Equal(CatalogSearchDefinition.CurrentVersion, loaded.Definition.Version);
        Assert.NotEmpty(_diagnostics);
        Assert.Equal(original, File.ReadAllText(path));

        store.SaveNew("Added", Search(Is("video.codec", "h264")));
        Assert.Single(Directory.GetFiles(_root, "mixed.json.recovery-*.json"));
        Assert.Equal(2, CreateStore(path).Load().Count);
    }

    [Fact]
    public void AtomicWritesLeaveOnlyACompleteDocumentAndSerializeConcurrentUpdates()
    {
        string path = Path.Combine(_root, "concurrent.json");
        var store = CreateStore(path);
        Parallel.For(0, 12, index => store.SaveNew($"Search {index:00}", Search(Is("video.codec", "h264"))));

        Assert.Equal(12, CreateStore(path).Load().Count);
        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(LibraryAdvancedSearchStore.CurrentStoreVersion, json.RootElement.GetProperty("Version").GetInt32());
        Assert.Empty(Directory.GetFiles(_root, ".concurrent.json.*.tmp"));
    }

    [Fact]
    public void FailedAtomicReplacementKeepsThePreviousPrimaryDocumentIntact()
    {
        string path = Path.Combine(_root, "locked.json");
        var store = CreateStore(path);
        store.SaveNew("Existing", Search(Is("video.codec", "h264")));
        byte[] original = File.ReadAllBytes(path);

        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Exception failure = Assert.ThrowsAny<Exception>(() => store.SaveNew("New", Search(Is("video.codec", "hevc"))));
            Assert.True(failure is IOException or UnauthorizedAccessException);
            Assert.Equal(original, File.ReadAllBytes(path));
        }
        Assert.Empty(Directory.GetFiles(_root, ".locked.json.*.tmp"));
        Assert.Single(CreateStore(path).Load());
    }

    [Fact]
    public void MotivatingSearchSurvivesStoreRoundTripAndCompilesAgainstVideoStreamBitrate()
    {
        string path = Path.Combine(_root, "motivating.json");
        CatalogSearchDefinition definition = Search(
            Is("video.codec", "h264"),
            Equal("video.width", 1920, "pixels"),
            Equal("video.height", 1080, "pixels"),
            Between("video.fps", 29, 30, "fps"),
            Between("video.bitrate", 13, 15, "mbps"),
            Is("video.scan_type", "progressive"),
            Equal("video.bit_depth", 8, "count"),
            Is("file.availability", "present"));
        CreateStore(path).SaveNew("1080p H.264", definition);

        CatalogSearchDefinition loaded = Assert.Single(CreateStore(path).Load()).Definition;
        CatalogSearchDefinitionValidator.Validate(loaded);
        Assert.True(CatalogSearchDefinitionEquivalence.AreEquivalent(definition, loaded));
        CompiledCatalogSearch compiled = CatalogSearchSqlCompiler.Compile(loaded);
        Assert.Contains("video_bitrate_bps", compiled.PredicateSql);
        Assert.DoesNotContain("metadata.total_bitrate", compiled.PredicateSql);
        Assert.Contains(compiled.Parameters, parameter => Equals(parameter.Value, 13_000_000L));
        Assert.Contains(compiled.Parameters, parameter => Equals(parameter.Value, 15_000_000L));
    }

    [Fact]
    public void SavedDocumentDoesNotPersistQueryPagingOrSelectionRuntimeState()
    {
        string path = Path.Combine(_root, "no-runtime-state.json");
        CreateStore(path).SaveNew("Only definition", Search(Is("video.codec", "h264")));

        string json = File.ReadAllText(path);
        Assert.DoesNotContain("Offset", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PageSize", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SelectedFile", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CancellationToken", json, StringComparison.OrdinalIgnoreCase);
    }

    private LibraryAdvancedSearchStore CreateStore(string path) =>
        new(path, (message, exception) => _diagnostics.Enqueue(message + " " + exception?.Message));

    private static CatalogSearchDefinition Search(params CatalogSearchCondition[] conditions) =>
        new(CatalogSearchDefinition.CurrentVersion, conditions);

    private static CatalogSearchCondition Is(string propertyId, string value) =>
        new(propertyId, CatalogSearchOperator.Is, new CatalogSearchValue(Text: value));

    private static CatalogSearchCondition Equal(string propertyId, decimal value, string unit) =>
        new(propertyId, CatalogSearchOperator.Equal, CatalogSearchValue.ParseNumber(value.ToString(System.Globalization.CultureInfo.InvariantCulture), unit));

    private static CatalogSearchCondition Between(string propertyId, decimal low, decimal high, string unit) =>
        new(propertyId, CatalogSearchOperator.Between,
            CatalogSearchValue.ParseNumber(low.ToString(System.Globalization.CultureInfo.InvariantCulture), unit),
            CatalogSearchValue.ParseNumber(high.ToString(System.Globalization.CultureInfo.InvariantCulture), unit));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
