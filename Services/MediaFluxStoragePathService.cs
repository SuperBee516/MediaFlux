using System.Text.Json;

namespace MediaFlux.Services;

/// <summary>
/// The single authority for MediaFlux-owned storage.  Media paths supplied by a user are
/// deliberately not represented here.
/// </summary>
public sealed class MediaFluxStoragePathService
{
    private const string LocationFileName = "storage-location.json";
    private readonly string _defaultRoot;
    private readonly string _locationFile;

    public MediaFluxStoragePathService(string? defaultRoot = null, string? locationFile = null)
    {
        _defaultRoot = Normalize(defaultRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MediaFlux", "UserData"));
        _locationFile = locationFile ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MediaFlux", LocationFileName);
    }

    public string DefaultRoot => _defaultRoot;
    public string LocationFile => _locationFile;
    public string Root => ReadConfiguredRoot() ?? _defaultRoot;
    public string Data => Path.Combine(Root, "data");
    public string Temp => Path.Combine(Root, "temp");
    public string Config => Path.Combine(Root, "config.json");
    public string Backups => Path.Combine(Root, "Backups");
    public string AiIntermediates => Path.Combine(Data, "ai-intermediates");
    public string RestorationPreviews => Path.Combine(Data, "restoration-previews");
    public string FramePreviews => Path.Combine(Data, "frame-previews");
    public string DuplicatePreviews => Path.Combine(Data, "duplicate-previews");
    public string TensorRtEngines => Path.Combine(Data, "tensorrt-engines");
    public string AiBenchmarkReruns => Path.Combine(Data, "ai-benchmark-reruns");
    public string Logs => Path.Combine(Data, "logs");

    public void InitializeDirectories()
    { Directory.CreateDirectory(Root); Directory.CreateDirectory(Data); Directory.CreateDirectory(Temp); Directory.CreateDirectory(Backups); }

    public bool TryValidateNewRoot(string candidate, out string normalized, out string error)
    {
        normalized = string.Empty; error = string.Empty;
        if (string.IsNullOrWhiteSpace(candidate)) { error = "Choose a storage folder."; return false; }
        try { normalized = Normalize(candidate); }
        catch (Exception ex) { error = "The storage folder is invalid: " + ex.Message; return false; }
        string source = Root;
        if (Same(normalized, source)) { error = "That folder is already the active MediaFlux storage root."; return false; }
        if (IsWithin(normalized, source) || IsWithin(source, normalized)) { error = "The new storage root cannot contain, or be contained by, the current root."; return false; }
        if (Directory.Exists(normalized) && Directory.EnumerateFileSystemEntries(normalized).Any()) { error = "The destination folder must be empty to prevent collisions."; return false; }
        try { string? parent = Path.GetDirectoryName(normalized); if (string.IsNullOrEmpty(parent)) throw new IOException("A parent folder is required."); Directory.CreateDirectory(parent); string probe = Path.Combine(parent, ".mediaflux-write-test-" + Guid.NewGuid().ToString("N")); using (File.Open(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { } File.Delete(probe); }
        catch (Exception ex) { error = "The destination cannot be created or written: " + ex.Message; return false; }
        return true;
    }

    public void WriteConfiguredRoot(string root)
    {
        string normalized = Normalize(root); Directory.CreateDirectory(Path.GetDirectoryName(_locationFile)!);
        string temporary = _locationFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new StorageLocation(normalized)));
        File.Move(temporary, _locationFile, true);
    }

    private string? ReadConfiguredRoot()
    {
        try { return File.Exists(_locationFile) ? Normalize(JsonSerializer.Deserialize<StorageLocation>(File.ReadAllText(_locationFile))?.Root ?? "") : null; }
        catch { return null; } // A corrupt pointer must retain the compatible default location.
    }
    private static string Normalize(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    internal static bool Same(string left, string right) => string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);
    internal static bool IsWithin(string child, string parent) { string p = Normalize(parent) + Path.DirectorySeparatorChar; return Normalize(child).StartsWith(p, StringComparison.OrdinalIgnoreCase); }
    private sealed record StorageLocation(string Root);
}
