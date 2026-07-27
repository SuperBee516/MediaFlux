using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace MediaFlux.Services
{
    public sealed class MediaInfoService
    {
        private const int PersistentCacheVersion = 3;
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
                   !string.IsNullOrWhiteSpace(info.FormatName) ||
                   info.Width.HasValue ||
                   info.Height.HasValue ||
                   info.Fps.HasValue ||
                   info.DurationSeconds.HasValue ||
                   info.BitrateKbps.HasValue ||
                   info.TotalBitrateKbps.HasValue;
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
            if (info.DurationSeconds is > 0)
                return info.DurationSeconds.Value;

            // Some containers omit format.duration even though the video stream has
            // a usable duration. A partially populated MediaInfo result may also be
            // cached, so explicitly retry the duration-only probe before reporting
            // that target-size estimation is unavailable.
            double durationSeconds = ProbeDurationSeconds(path);
            if (durationSeconds <= 0)
                return 0;

            info.DurationSeconds = durationSeconds;
            if (TryGetFileSignature(path, out var length, out var lastWriteUtc))
            {
                _cache[path] = new CacheEntry(info, length, lastWriteUtc);
                SavePersistentCacheIfDue(force: false);
            }

            return durationSeconds;
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

                string output = ReadProcessOutputWithTimeout(proc, TimeSpan.FromSeconds(30));

                if (string.IsNullOrWhiteSpace(output))
                    return info;

                using var doc = JsonDocument.Parse(output);
                var root = doc.RootElement;

                // Format section (duration, bitrate)
                if (root.TryGetProperty("format", out var fmt))
                {
                    if (fmt.TryGetProperty("format_name", out var formatNameProp))
                        info.FormatName = formatNameProp.GetString();

                    if (fmt.TryGetProperty("duration", out var durProp) &&
                        TryParsePositiveSeconds(durProp.GetString(), out var durSec) &&
                        durSec > 0)
                    {
                        info.DurationSeconds = durSec;
                    }

                    if (fmt.TryGetProperty("bit_rate", out var brProp) &&
                        int.TryParse(brProp.GetString(), out var bitsPerSec) &&
                        bitsPerSec > 0)
                    {
                        info.TotalBitrateKbps = bitsPerSec / 1000;
                    }
                }

                // Streams: retain the primary video's properties while also
                // aggregating stream counts and audio bitrate for recommendation work.
                if (root.TryGetProperty("streams", out var streams) &&
                    streams.ValueKind == JsonValueKind.Array)
                {
                    foreach (var s in streams.EnumerateArray())
                    {
                        if (!s.TryGetProperty("codec_type", out var typeProp))
                            continue;

                        string streamType = typeProp.GetString() ?? "";
                        if (streamType.Equals("audio", StringComparison.OrdinalIgnoreCase))
                        {
                            info.AudioStreamCount++;
                            if (s.TryGetProperty("bit_rate", out var audioBitrateProp) &&
                                int.TryParse(audioBitrateProp.GetString(), out var audioBitsPerSec) &&
                                audioBitsPerSec > 0)
                            {
                                info.AudioBitrateKbps =
                                    (info.AudioBitrateKbps ?? 0) + audioBitsPerSec / 1000;
                            }
                            continue;
                        }

                        if (streamType.Equals("subtitle", StringComparison.OrdinalIgnoreCase))
                        {
                            info.SubtitleStreamCount++;
                            continue;
                        }

                        if (!streamType.Equals("video", StringComparison.OrdinalIgnoreCase))
                            continue;

                        info.VideoStreamCount++;
                        if (info.VideoStreamCount > 1)
                            continue;

                        if (s.TryGetProperty("codec_name", out var codecProp))
                            info.VideoCodec = codecProp.GetString();

                        if (s.TryGetProperty("field_order", out var fieldOrderProp))
                            info.FieldOrder = fieldOrderProp.GetString();

                        if (s.TryGetProperty("width", out var wProp) &&
                            wProp.TryGetInt32(out var w) && w > 0)
                            info.Width = w;

                        if (s.TryGetProperty("height", out var hProp) &&
                            hProp.TryGetInt32(out var h) && h > 0)
                            info.Height = h;

                        if (info.DurationSeconds is not > 0 &&
                            s.TryGetProperty("duration", out var streamDurationProp) &&
                            TryParsePositiveSeconds(streamDurationProp.GetString(), out var streamDurationSec))
                        {
                            info.DurationSeconds = streamDurationSec;
                        }

                        // Keep video bitrate separate from container bitrate. The
                        // recommendation engine needs both to detect audio-heavy files.
                        if (s.TryGetProperty("bit_rate", out var videoBitrateProp) &&
                            int.TryParse(videoBitrateProp.GetString(), out var videoBitsPerSec) &&
                            videoBitsPerSec > 0)
                        {
                            info.BitrateKbps = videoBitsPerSec / 1000;
                        }

                        // Prefer average frame rate, falling back to nominal rate.
                        string? fpsText = null;
                        if (s.TryGetProperty("avg_frame_rate", out var averageFpsProp))
                            fpsText = averageFpsProp.GetString();
                        if ((!TryParseFraction(fpsText, out var parsedFps) ||
                             parsedFps <= 0) &&
                            s.TryGetProperty("r_frame_rate", out var nominalFpsProp))
                        {
                            fpsText = nominalFpsProp.GetString();
                        }

                        if (!string.IsNullOrWhiteSpace(fpsText) &&
                            TryParseFraction(fpsText, out var fps) &&
                            fps > 0)
                        {
                            info.Fps = fps;
                        }
                    }
                }

                if (info.AudioStreamCount > 0 &&
                    info.AudioBitrateKbps is not > 0 &&
                    info.TotalBitrateKbps is > 0 &&
                    info.BitrateKbps is > 0)
                {
                    int remaining = info.TotalBitrateKbps.Value - info.BitrateKbps.Value;
                    if (remaining > 0)
                        info.AudioBitrateKbps = remaining;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ffprobe failed for '{path}': {ex.Message}");
                // Swallow: probes are best-effort only. Callers have fallbacks.
            }

            return info;
        }

        private double ProbeDurationSeconds(string path)
        {
            if (!File.Exists(path) || !File.Exists(_ffprobePath))
                return 0;

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = _ffprobePath,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    ErrorDialog = false,
                    WindowStyle = ProcessWindowStyle.Hidden
                };

                psi.ArgumentList.Add("-v");
                psi.ArgumentList.Add("error");
                psi.ArgumentList.Add("-show_entries");
                psi.ArgumentList.Add("format=duration:stream=duration");
                psi.ArgumentList.Add("-of");
                psi.ArgumentList.Add("default=noprint_wrappers=1:nokey=1");
                psi.ArgumentList.Add(path);

                using var proc = Process.Start(psi);
                if (proc == null)
                    return 0;

                string output = ReadProcessOutputWithTimeout(proc, TimeSpan.FromSeconds(30));
                double bestDuration = 0;
                foreach (string line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (TryParsePositiveSeconds(line, out double seconds))
                        bestDuration = Math.Max(bestDuration, seconds);
                }

                return bestDuration;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ffprobe duration fallback failed for '{path}': {ex.Message}");
                return 0;
            }
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

        private static bool TryParsePositiveSeconds(string? text, out double seconds)
        {
            return double.TryParse(
                       text,
                       NumberStyles.Float,
                       CultureInfo.InvariantCulture,
                       out seconds) &&
                   seconds > 0 &&
                   !double.IsNaN(seconds) &&
                   !double.IsInfinity(seconds);
        }

        public sealed class MediaInfo
        {
            public string? FormatName { get; set; }
            public string? VideoCodec { get; set; }
            public string? FieldOrder { get; set; }
            public int? Width { get; set; }
            public int? Height { get; set; }
            public double? Fps { get; set; }
            public double? DurationSeconds { get; set; }
            // Primary video stream bitrate.
            public int? BitrateKbps { get; set; }
            public int? TotalBitrateKbps { get; set; }
            public int? AudioBitrateKbps { get; set; }
            public int VideoStreamCount { get; set; }
            public int AudioStreamCount { get; set; }
            public int SubtitleStreamCount { get; set; }
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
