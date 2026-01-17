using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Encode
{
    internal static class SupportedExtensionsStore
    {
        private sealed class Payload
        {
            [JsonPropertyName("extensions")]
            public List<string> Extensions { get; set; } = new();
        }

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        // .m2ts, .mp4, .webm etc. Letters + digits only, 1-10 chars.
        private static readonly Regex ExtRegex = new(@"^\.?[a-z0-9]{1,10}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static List<string> Load(string path, IEnumerable<string> defaults)
        {
            var fallback = Normalize(defaults).ToList();
            try
            {
                if (string.IsNullOrWhiteSpace(path)) return fallback;
                if (!File.Exists(path)) return fallback;

                var json = File.ReadAllText(path);
                var payload = JsonSerializer.Deserialize<Payload>(json, JsonOptions);
                var list = Normalize(payload?.Extensions ?? new List<string>()).ToList();

                return list.Count > 0 ? list : fallback;
            }
            catch
            {
                return fallback;
            }
        }

        public static void Save(string path, IEnumerable<string> extensions)
        {
            if (string.IsNullOrWhiteSpace(path)) return;

            var normalized = Normalize(extensions).ToList();
            if (normalized.Count == 0) return;

            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            var payload = new Payload { Extensions = normalized };
            var json = JsonSerializer.Serialize(payload, JsonOptions);
            File.WriteAllText(path, json);
        }

        public static IEnumerable<string> Normalize(IEnumerable<string> extensions)
        {
            if (extensions == null) yield break;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var ext in extensions)
            {
                var s = (ext ?? string.Empty).Trim();
                if (s.Length == 0) continue;
                if (!ExtRegex.IsMatch(s)) continue;

                if (!s.StartsWith(".", StringComparison.Ordinal))
                    s = "." + s;

                s = s.ToLowerInvariant();
                if (seen.Add(s))
                    yield return s;
            }
        }
    }
}
