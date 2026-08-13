using System.Text.Json;
using MediaFlux.Models;

namespace MediaFlux.Services;

public sealed class LibraryPolicyStore
{
    private sealed class Document
    {
        public int SchemaVersion { get; set; } = LibraryPolicyDefinition.CurrentSchemaVersion;
        public List<LibraryPolicyDefinition> Policies { get; set; } = new();
    }

    private readonly string _path;
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true, AllowTrailingCommas = true };

    public LibraryPolicyStore(string path) => _path = path ?? throw new ArgumentNullException(nameof(path));

    public IReadOnlyList<LibraryPolicyDefinition> LoadCustom()
    {
        try
        {
            if (!File.Exists(_path)) return Array.Empty<LibraryPolicyDefinition>();
            Document document = JsonSerializer.Deserialize<Document>(File.ReadAllText(_path), _json) ?? new();
            return document.Policies.Where(policy => policy != null).Select(NormalizedCustom)
                .GroupBy(policy => policy.Id, StringComparer.OrdinalIgnoreCase).Select(group => group.Last())
                .OrderBy(policy => policy.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch
        {
            return Array.Empty<LibraryPolicyDefinition>();
        }
    }

    public IReadOnlyList<LibraryPolicyDefinition> LoadAll() =>
        LibraryPolicyBuiltIns.All.Concat(LoadCustom()).ToArray();

    public LibraryPolicyDefinition Clone(string sourceId, string name)
    {
        LibraryPolicyDefinition source = LoadAll().FirstOrDefault(policy => policy.Id.Equals(sourceId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("The policy to clone was not found.");
        LibraryPolicyDefinition clone = source.CloneAsCustom(name);
        Save(clone);
        return clone;
    }

    public void Save(LibraryPolicyDefinition policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (policy.IsBuiltIn || LibraryPolicyBuiltIns.All.Any(item => item.Id.Equals(policy.Id, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Built-in policies cannot be edited. Clone the policy first.");
        LibraryPolicyDefinition normalized = NormalizedCustom(policy);
        List<LibraryPolicyDefinition> policies = LoadCustom().Where(item => !item.Id.Equals(normalized.Id, StringComparison.OrdinalIgnoreCase)).ToList();
        policies.Add(normalized);
        Write(policies);
    }

    public void Delete(string id)
    {
        if (LibraryPolicyBuiltIns.All.Any(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Built-in policies cannot be deleted.");
        Write(LoadCustom().Where(item => !item.Id.Equals(id, StringComparison.OrdinalIgnoreCase)));
    }

    private void Write(IEnumerable<LibraryPolicyDefinition> policies)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path) ?? ".");
        string temporary = _path + ".tmp";
        var document = new Document { Policies = policies.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToList() };
        File.WriteAllText(temporary, JsonSerializer.Serialize(document, _json));
        File.Move(temporary, _path, overwrite: true);
    }

    private static LibraryPolicyDefinition NormalizedCustom(LibraryPolicyDefinition policy)
    {
        LibraryPolicyDefinition copy = policy.CloneAsCustom(policy.Name);
        copy.Id = string.IsNullOrWhiteSpace(policy.Id) ? Guid.NewGuid().ToString("N") : policy.Id;
        copy.IsBuiltIn = false;
        copy.Normalize();
        return copy;
    }
}

public static class LibraryPolicyBuiltIns
{
    public const string GeneralArchiveId = "builtin-general-archive-v1";
    public const string MaximumStorageSavingsId = "builtin-maximum-storage-savings-v1";
    public const string LegacyModernizationId = "builtin-legacy-modernization-v1";

    public static IReadOnlyList<LibraryPolicyDefinition> All { get; } = new[]
    {
        BuiltIn(GeneralArchiveId, "General Archive", VideoCodecFamily.Hevc, VideoEncoderIds.Libx265, "slow", 22,
            500L * 1024 * 1024, 20, 250L * 1024 * 1024, OutputContainerSelection.Auto, 10),
        BuiltIn(MaximumStorageSavingsId, "Maximum Storage Savings", VideoCodecFamily.Av1, VideoEncoderIds.SvtAv1, "6", 28,
            1024L * 1024 * 1024, 30, 500L * 1024 * 1024, OutputContainerSelection.Auto, 10),
        BuiltIn(LegacyModernizationId, "Legacy Modernization", VideoCodecFamily.Hevc, VideoEncoderIds.Libx265, "slow", 22,
            250L * 1024 * 1024, 15, 100L * 1024 * 1024, OutputContainerSelection.Matroska, 10,
            new[] { "mpeg2video", "mpeg4", "wmv3", "vc1", "h264", "avc" })
    };

    private static LibraryPolicyDefinition BuiltIn(string id, string name, VideoCodecFamily codec, string encoder,
        string preset, int quality, long minimumSize, double savingsPercent, long savingsBytes,
        OutputContainerSelection container, int bitDepth, IEnumerable<string>? included = null)
    {
        var policy = new LibraryPolicyDefinition
        {
            Id = id, Name = name, IsBuiltIn = true, PreferredCodec = codec, EncoderId = encoder,
            EncoderPreset = preset, QualityValue = quality, MinimumFileSizeBytes = minimumSize,
            MinimumExpectedSavingsPercent = savingsPercent, MinimumExpectedSavingsBytes = savingsBytes,
            TargetContainer = container, PreferredBitDepth = bitDepth,
            IncludedSourceCodecs = included?.ToList() ?? new List<string>()
        };
        policy.Normalize();
        policy.IsBuiltIn = true;
        return policy;
    }
}
