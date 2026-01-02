using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace Encode.Services
{
    public sealed class MediaInfoService
    {
        private readonly string _ffprobePath;

        private readonly ConcurrentDictionary<string, MediaInfo> _cache =
            new(StringComparer.OrdinalIgnoreCase);

        public MediaInfoService(string? baseDirectory = null)
        {
            var root = string.IsNullOrWhiteSpace(baseDirectory)
                ? AppDomain.CurrentDomain.BaseDirectory
                : baseDirectory;

            _ffprobePath = Path.Combine(root, "ffprobe.exe");
        }

        public MediaInfo GetInfo(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Path must not be empty.", nameof(path));

            if (_cache.TryGetValue(path, out var cached))
                return cached;

            var info = Probe(path);
            _cache[path] = info;
            return info;
        }

        public (int width, int height) GetResolutionPixels(string path)
        {
            var info = GetInfo(path);
            int w = info.Width ?? 1920;
            int h = info.Height ?? 1080;
            return (w, h);
        }

        public double GetFps(string path)
        {
            var info = GetInfo(path);
            return info.Fps ?? 30.0;
        }

        public double GetDurationSeconds(string path)
        {
            var info = GetInfo(path);
            return info.DurationSeconds ?? 0;
        }

        public TimeSpan GetDuration(string path)
        {
            var sec = GetDurationSeconds(path);
            return sec > 0 ? TimeSpan.FromSeconds(sec) : TimeSpan.Zero;
        }

        public int? GetBitrateKbps(string path)
        {
            var info = GetInfo(path);
            return info.BitrateKbps;
        }

        public string GetVideoCodec(string path)
        {
            var info = GetInfo(path);
            return info.VideoCodec ?? string.Empty;
        }

        // ──────────────────────────────────────────────────────────
        // Internal: actually run ffprobe and parse JSON
        // ──────────────────────────────────────────────────────────

        private MediaInfo Probe(string path)
        {
            var info = new MediaInfo();

            if (!File.Exists(path))
                return info;

            if (!File.Exists(_ffprobePath))
                return info; // silently fail; callers already have fallbacks

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = _ffprobePath,
                    Arguments =
                        $"-v quiet -print_format json -show_streams -show_format -i \"{path}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var proc = Process.Start(psi);
                if (proc == null)
                    return info;

                string output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(15000);

                if (string.IsNullOrWhiteSpace(output))
                    return info;

                using var doc = JsonDocument.Parse(output);
                var root = doc.RootElement;

                // Format section (duration, bitrate)
                if (root.TryGetProperty("format", out var fmt))
                {
                    if (fmt.TryGetProperty("duration", out var durProp) &&
                        double.TryParse(durProp.GetString(), out var durSec) &&
                        durSec > 0)
                    {
                        info.DurationSeconds = durSec;
                    }

                    if (fmt.TryGetProperty("bit_rate", out var brProp) &&
                        int.TryParse(brProp.GetString(), out var bitsPerSec) &&
                        bitsPerSec > 0)
                    {
                        info.BitrateKbps = bitsPerSec / 1000;
                    }
                }

                // Streams: look for the first video stream
                if (root.TryGetProperty("streams", out var streams) &&
                    streams.ValueKind == JsonValueKind.Array)
                {
                    foreach (var s in streams.EnumerateArray())
                    {
                        if (!s.TryGetProperty("codec_type", out var typeProp) ||
                            !string.Equals(typeProp.GetString(), "video",
                                StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        if (s.TryGetProperty("codec_name", out var codecProp))
                            info.VideoCodec = codecProp.GetString();

                        if (s.TryGetProperty("width", out var wProp) &&
                            wProp.TryGetInt32(out var w) && w > 0)
                            info.Width = w;

                        if (s.TryGetProperty("height", out var hProp) &&
                            hProp.TryGetInt32(out var h) && h > 0)
                            info.Height = h;

                        // fps = r_frame_rate or avg_frame_rate like "30000/1001"
                        if (s.TryGetProperty("r_frame_rate", out var fpsProp) ||
                            s.TryGetProperty("avg_frame_rate", out fpsProp))
                        {
                            var fpsStr = fpsProp.GetString();
                            if (!string.IsNullOrWhiteSpace(fpsStr) &&
                                TryParseFraction(fpsStr, out var fps) &&
                                fps > 0)
                            {
                                info.Fps = fps;
                            }
                        }

                        // we only care about the first video stream
                        break;
                    }
                }
            }
            catch
            {
                // Swallow: probes are best-effort only. Callers have fallbacks.
            }

            return info;
        }

        private static bool TryParseFraction(string? text, out double value)
        {
            value = 0;
            if (string.IsNullOrWhiteSpace(text))
                return false;

            var parts = text.Split('/');
            if (parts.Length == 1)
            {
                return double.TryParse(parts[0],
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out value);
            }

            if (parts.Length == 2 &&
                double.TryParse(parts[0],
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var num) &&
                double.TryParse(parts[1],
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var den) &&
                den != 0)
            {
                value = num / den;
                return true;
            }

            return false;
        }

        public sealed class MediaInfo
        {
            public string? VideoCodec { get; set; }
            public int? Width { get; set; }
            public int? Height { get; set; }
            public double? Fps { get; set; }
            public double? DurationSeconds { get; set; }
            public int? BitrateKbps { get; set; }
        }
    }
}
