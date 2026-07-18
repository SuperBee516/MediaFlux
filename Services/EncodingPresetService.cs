using System.Text.Json;
using MediaFlux.Models;

namespace MediaFlux.Services
{
    public sealed class EncodingPresetService
    {
        private readonly string _path;
        private readonly JsonSerializerOptions _options = new()
        {
            WriteIndented = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        public EncodingPresetService(string path)
        {
            _path = path;
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);
        }

        public List<EncodingPreset> LoadAll()
        {
            try
            {
                if (!File.Exists(_path))
                    return new();

                var json = File.ReadAllText(_path);
                var list = JsonSerializer.Deserialize<List<EncodingPreset>>(json, _options) ?? new();
                return list
                    .Where(p => !string.IsNullOrWhiteSpace(p.Name))
                    .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch
            {
                return new();
            }
        }

        public void SaveOrReplace(EncodingPreset preset)
        {
            if (preset == null)
                throw new ArgumentNullException(nameof(preset));

            preset.Name = preset.Name.Trim();
            if (string.IsNullOrWhiteSpace(preset.Name))
                throw new ArgumentException("Preset name is required.", nameof(preset));

            var all = LoadAll();
            all.RemoveAll(p => string.Equals(p.Name, preset.Name, StringComparison.OrdinalIgnoreCase));
            all.Add(preset);
            SaveAll(all);
        }

        public void Delete(string name)
        {
            var all = LoadAll();
            all.RemoveAll(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            SaveAll(all);
        }

        private void SaveAll(IEnumerable<EncodingPreset> presets)
        {
            var ordered = presets
                .Where(p => !string.IsNullOrWhiteSpace(p.Name))
                .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            File.WriteAllText(_path, JsonSerializer.Serialize(ordered, _options));
        }
    }
}
