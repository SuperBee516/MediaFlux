using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace MediaFlux.Services
{
    public sealed class MediaInfoService
    {
        private const int PersistentCacheVersion = 2;
        private readonly string _ffprobePath;
        private readonly string? _cachePath;
        private readonly bool _persistentCacheEnabled;
        private readonly object _cacheSaveLock = new();
        private DateTime _lastCacheSaveUtc = DateTime.MinValue;
        private bool _cacheDirty;

        private readonly ConcurrentDictionary<string, CacheEntry> _cache =
            new(StringComparer.OrdinalIgnoreCase);

        public MediaInfoService(
            string? baseDirectory = null,
            string? ffprobePath = null,
            bool persistentCacheEnabled = true,
            string? dataDirectory = null)
        {
            var root = string.IsNullOrWhiteSpace(baseDirectory)
                ? AppDomain.CurrentDomain.BaseDirectory
                : baseDirectory;

            _ffprobePath = FfmpegToolResolver.Resolve(root, configuredFfprobePath: ffprobePath).FfprobePath;
            _persistentCacheEnabled = persistentCacheEnabled;
            if (_persistentCacheEnabled)
            {
                string cacheRoot = string.IsNullOrWhiteSpace(dataDirectory)
                    ? Path.Combine(root, "data")
                    : dataDirectory;
                _cachePath = Path.Combine(cacheRoot, "media-info-cache.json");
                LoadPersistentCache();
            }
        }

        public MediaInfo GetInfo(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Path must not be empty.", nameof(path));

            if (!TryGetFileSignature(path, out var length, out var lastWriteUtc))
                return new MediaInfo();

            if (_cache.TryGetValue(path, out var cached) &&
                cached.Length == length &&
                cached.LastWriteUtc == lastWriteUtc)
            {
                return cached.Info;
            }

            var info = Probe(path);

            // A failed probe is often a file that another encode is still writing.
            // Never preserve that failure, and tie successful results to the exact
            // file state so a growing/replaced output is probed again automatically.
            if (HasUsefulMetadata(info) &&
                TryGetFileSignature(path, out length, out lastWriteUtc))
            {
                _cache[path] = new CacheEntry(info, length, lastWriteUtc);
                SavePersistentCacheIfDue(force: false);
            }
            else
            {
                _cache.TryRemove(path, out _);
            }

            return info;
        }

        public void Invalidate(string path)
        {
            if (!string.IsNullOrWhiteSpace(path))
                _cache.TryRemove(path, out _);
        }

        public void ClearCache()
        {
            _cache.Clear();
            if (_persistentCacheEnabled)
                LoadPersistentCache();
        }

        public void FlushCache()
        {
            SavePersistentCacheIfDue(force: true);
        }

        private static bool TryGetFileSignature(string path, out long length, out DateTime lastWriteUtc)
        {
            length = 0;
            lastWriteUtc = default;

            try
            {
                var file = new FileInfo(path);
                if (!file.Exists)
                    return false;

                length = file.Length;
                lastWriteUtc = file.LastWriteTimeUtc;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool HasUsefulMetadata(MediaInfo info)
        {
            return !string.IsNullOrWhiteSpace(info.VideoCodec) ||
                   info.Width.HasValue ||
                   info.Height.HasValue ||
                   info.Fps.HasValue ||
                   info.DurationSeconds.HasValue ||
                   info.BitrateKbps.HasValue;
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
            {
                Debug.WriteLine($"ffprobe not found at '{_ffprobePath}'. Media info will use fallbacks.");
                return info; // silently fail; callers already have fallbacks
            }

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
                    CreateNoWindow = true,
                    ErrorDialog = false,
                    WindowStyle = ProcessWindowStyle.Hidden
                };

                using var proc = Process.Start(psi);
                if (proc == null)
                    return info;

                string output = ReadProcessOutputWithTimeout(proc, TimeSpan.FromSeconds(15));

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

                        // Prefer the video stream bitrate over the container bitrate.
                        // Container bitrate can include high-bitrate audio and is a
                        // weaker quality proxy when comparing duplicate video encodes.
                        if (s.TryGetProperty("bit_rate", out var videoBitrateProp) &&
                            int.TryParse(videoBitrateProp.GetString(), out var videoBitsPerSec) &&
                            videoBitsPerSec > 0)
                        {
                            info.BitrateKbps = videoBitsPerSec / 1000;
                        }

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
            catch (Exception ex)
            {
                Debug.WriteLine($"ffprobe failed for '{path}': {ex.Message}");
                // Swallow: probes are best-effort only. Callers have fallbacks.
            }

            return info;
        }

        private static string ReadProcessOutputWithTimeout(Process proc, TimeSpan timeout)
        {
            Task<string> outputTask = proc.StandardOutput.ReadToEndAsync();
            Task<string> errorTask = proc.StandardError.ReadToEndAsync();

            if (!proc.WaitForExit((int)timeout.TotalMilliseconds))
            {
                try
                {
                    proc.Kill(entireProcessTree: true);
                }
                catch
                {
                    // best-effort cleanup only
                }

                Debug.WriteLine("ffprobe timed out while reading media info.");
                return string.Empty;
            }

            proc.WaitForExit();
            _ = errorTask.GetAwaiter().GetResult();
            return outputTask.GetAwaiter().GetResult();
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

        private void LoadPersistentCache()
        {
            if (string.IsNullOrWhiteSpace(_cachePath) || !File.Exists(_cachePath))
                return;

            try
            {
                var json = File.ReadAllText(_cachePath);
                var entries = JsonSerializer.Deserialize<Dictionary<string, PersistentCacheEntry>>(json);
                if (entries == null)
                    return;

                foreach (var item in entries)
                {
                    if (!string.IsNullOrWhiteSpace(item.Key) &&
                        item.Value.Version == PersistentCacheVersion &&
                        item.Value.Info != null)
                    {
                        _cache[item.Key] = new CacheEntry(
                            item.Value.Info,
                            item.Value.Length,
                            item.Value.LastWriteUtc);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to load media info cache: {ex.Message}");
            }
        }

        private void SavePersistentCacheIfDue(bool force)
        {
            if (!_persistentCacheEnabled || string.IsNullOrWhiteSpace(_cachePath))
                return;

            _cacheDirty = true;
            if (!force && (DateTime.UtcNow - _lastCacheSaveUtc).TotalSeconds < 5)
                return;

            lock (_cacheSaveLock)
            {
                if (!force && !_cacheDirty)
                    return;

                try
                {
                    var directory = Path.GetDirectoryName(_cachePath);
                    if (!string.IsNullOrWhiteSpace(directory))
                        Directory.CreateDirectory(directory);

                    var snapshot = _cache
                        .Take(10000)
                        .ToDictionary(
                            kvp => kvp.Key,
                            kvp => new PersistentCacheEntry
                            {
                                Version = PersistentCacheVersion,
                                Info = kvp.Value.Info,
                                Length = kvp.Value.Length,
                                LastWriteUtc = kvp.Value.LastWriteUtc
                            },
                            StringComparer.OrdinalIgnoreCase);

                    var options = new JsonSerializerOptions { WriteIndented = false };
                    File.WriteAllText(_cachePath, JsonSerializer.Serialize(snapshot, options));
                    _lastCacheSaveUtc = DateTime.UtcNow;
                    _cacheDirty = false;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Failed to save media info cache: {ex.Message}");
                }
            }
        }

        private sealed record CacheEntry(MediaInfo Info, long Length, DateTime LastWriteUtc);

        private sealed class PersistentCacheEntry
        {
            public int Version { get; set; }
            public MediaInfo? Info { get; set; }
            public long Length { get; set; }
            public DateTime LastWriteUtc { get; set; }
        }
    }
}
