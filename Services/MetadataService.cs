using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace Encode.Services
{
    public class MetadataService
    {
        private readonly string _appPath;
        private readonly Action<string> _log;

        public MetadataService(string applicationDirectory, Action<string> logCallback)
        {
            _appPath = applicationDirectory;
            _log = logCallback;
        }

        public async Task<VideoMetadata?> FetchAsync(string url)
        {
            var psi = new ProcessStartInfo
            {
                FileName = Path.Combine(_appPath, "yt-dlp.exe"),
                Arguments = $"-J --no-playlist \"{url}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                ErrorDialog = false,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            using var p = new Process { StartInfo = psi };
            p.Start();
            string stdout = await p.StandardOutput.ReadToEndAsync();
            string stderr = await p.StandardError.ReadToEndAsync();
            p.WaitForExit();

            if (p.ExitCode != 0)
            {
                _log($"Metadata fetch failed: {stderr}");
                return null;
            }

            try
            {
                using var doc = JsonDocument.Parse(stdout);
                var root = doc.RootElement;

                // Prefer single-video node, but some sites still wrap in entries[]
                var node = root.TryGetProperty("title", out _)
                    ? root
                    : (root.TryGetProperty("entries", out var entries) &&
                       entries.ValueKind == JsonValueKind.Array &&
                       entries.GetArrayLength() > 0)
                        ? entries[0]
                        : root;

                string title = node.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                string channel =
                    node.TryGetProperty("channel", out var ch) ? ch.GetString() ?? "" :
                    node.TryGetProperty("uploader", out var up) ? up.GetString() ?? "" : "";
                double durationSec = node.TryGetProperty("duration", out var du) ? du.GetDouble() : 0;

                return new VideoMetadata
                {
                    Title = title,
                    Channel = channel,
                    Duration = TimeSpan.FromSeconds(durationSec)
                };
            }
            catch (Exception ex)
            {
                _log($"Metadata parse error: {ex.Message}");
                return null;
            }
        }
    }

    public class VideoMetadata
    {
        public string Title { get; set; } = "";
        public string Channel { get; set; } = "";
        public TimeSpan Duration { get; set; } = TimeSpan.Zero;
    }
}
