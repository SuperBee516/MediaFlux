using System.Text.Json;
using System.Text.Json.Serialization;
using MediaFlux.Models;

namespace MediaFlux.Services;

/// <summary>Versioned, one-file-per-profile storage for user restoration settings.</summary>
public sealed class RestorationProfileService
{
    public const int CurrentVersion = 1;
    private readonly string _directory;
    private readonly JsonSerializerOptions _json = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public RestorationProfileService(string directory)
    {
        _directory = directory ?? throw new ArgumentNullException(nameof(directory));
        Directory.CreateDirectory(_directory);
    }

    public IReadOnlyList<RestorationProfileDocument> LoadAll() => Directory.EnumerateFiles(_directory, "*.json", SearchOption.TopDirectoryOnly)
        .Select(TryLoad).Where(profile => profile != null).Cast<RestorationProfileDocument>()
        .OrderBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase).ToArray();

    public void Save(string name, VideoRestorationSettings settings)
    {
        string normalized = ValidateName(name);
        ArgumentNullException.ThrowIfNull(settings);
        Write(new RestorationProfileDocument(CurrentVersion, normalized, settings.Clone()));
    }

    public void Rename(string currentName, string newName)
    {
        RestorationProfileDocument profile = Find(currentName);
        string normalized = ValidateName(newName);
        if (!string.Equals(profile.Name, normalized, StringComparison.OrdinalIgnoreCase) && File.Exists(PathFor(normalized)))
            throw new InvalidOperationException("A restoration profile with that name already exists.");
        Write(profile with { Name = normalized });
        if (!string.Equals(profile.Name, normalized, StringComparison.OrdinalIgnoreCase)) File.Delete(PathFor(profile.Name));
    }

    public void Delete(string name)
    {
        string path = PathFor(ValidateName(name));
        if (File.Exists(path)) File.Delete(path);
    }

    private RestorationProfileDocument Find(string name) => LoadAll().FirstOrDefault(profile => string.Equals(profile.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? throw new FileNotFoundException("The restoration profile no longer exists.", name);

    private RestorationProfileDocument? TryLoad(string path)
    {
        try
        {
            RestorationProfileDocument? document = JsonSerializer.Deserialize<RestorationProfileDocument>(File.ReadAllText(path), _json);
            if (document == null || document.Version > CurrentVersion || string.IsNullOrWhiteSpace(document.Name) || document.Settings == null) return null;
            return document.Version == CurrentVersion ? document : document with { Version = CurrentVersion, Name = document.Name.Trim() };
        }
        catch { return null; }
    }

    private void Write(RestorationProfileDocument profile)
    {
        string path = PathFor(profile.Name), temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(profile, _json));
        File.Move(temporary, path, true);
    }

    private string PathFor(string name) => Path.Combine(_directory, Uri.EscapeDataString(name) + ".json");
    private static string ValidateName(string? name)
    {
        string normalized = name?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(normalized)) throw new ArgumentException("A profile name is required.", nameof(name));
        if (normalized.Length > 80 || normalized.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new ArgumentException("The profile name contains unsupported characters.", nameof(name));
        return normalized;
    }
}
