using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using MediaFlux.Models;

namespace MediaFlux.Services
{
    /// <summary>
    /// Encapsulates ffmpeg audio operations: extract (copy) and convert (re-encode).
    /// </summary>
    public class AudioService
    {
        private readonly string _appPath;
        private readonly string _ffmpegPath;
        private readonly Action<string> _progress;

        public AudioService(string appPath, Action<string> progressCallback, string? ffmpegPath = null)
        {
            _appPath = appPath ?? throw new ArgumentNullException(nameof(appPath));
            _ffmpegPath = FfmpegToolResolver.Resolve(_appPath, ffmpegPath).FfmpegPath;
            _progress = progressCallback ?? throw new ArgumentNullException(nameof(progressCallback));
        }

        /// <summary>
        /// Extract first audio track via stream copy (no re-encode).
        /// </summary>
        public Task<bool> ExtractAsync(AudioJob job)
        {
            if (job == null) throw new ArgumentNullException(nameof(job));
            return RunFfmpegAsync(job, buildArgsForExtract: true);
        }

        /// <summary>
        /// Convert first audio track using chosen codec/bitrate/filters.
        /// Handles single-pass and two-pass loudnorm.
        /// </summary>
        public async Task<bool> ConvertAsync(AudioJob job)
        {
            if (job == null) throw new ArgumentNullException(nameof(job));

            // Two-pass loudnorm measurement first
            if (job.Loudnorm == LoudnormMode.TwoPass)
            {
                var filter = await BuildTwoPassLoudnormFilterAsync(job);
                if (!string.IsNullOrWhiteSpace(filter))
                {
                    job.LoudnormFilterOverride = filter;
                }
                else
                {
                    // Measurement failed; fall back to single-pass loudnorm
                    job.Loudnorm = LoudnormMode.SinglePass;
                }
            }

            return await RunFfmpegAsync(job, buildArgsForExtract: false);
        }

        /// <summary>
        /// Shared ffmpeg runner for both extract and convert.
        /// </summary>
        private Task<bool> RunFfmpegAsync(AudioJob job, bool buildArgsForExtract)
        {
            var tcs = new TaskCompletionSource<bool>();

            if (string.IsNullOrWhiteSpace(job.InputPath) || !File.Exists(job.InputPath))
            {
                tcs.SetException(new FileNotFoundException("Input file not found.", job.InputPath));
                return tcs.Task;
            }

            var outFolder = string.IsNullOrWhiteSpace(job.OutputFolder)
                ? Path.GetDirectoryName(job.InputPath) ?? Environment.CurrentDirectory
                : job.OutputFolder;

            Directory.CreateDirectory(outFolder);

            var name = Path.GetFileNameWithoutExtension(job.InputPath);

            // Decide codec & extension
            BuildCodecAndExtension(job, buildArgsForExtract, out var codec, out var extension);
            var outputPath = Path.Combine(outFolder, name + extension);

            var sb = new StringBuilder();
            sb.Append("-y ");
            sb.Append($"-i \"{job.InputPath}\" ");

            if (buildArgsForExtract)
            {
                // Extract first audio track, stream copy
                sb.Append("-map 0:a:0 -c:a copy ");
            }
            else
            {
                // Convert: audio-only, first audio track
                sb.Append("-vn ");
                sb.Append("-map 0:a:0 ");

                if (!string.IsNullOrWhiteSpace(codec))
                    sb.Append($"-c:a {codec} ");

                // 1) Respect explicit job bitrate; 2) otherwise use codec/quality-specific defaults
                int? effectiveBitrate = job.BitrateKbps;
                if (!effectiveBitrate.HasValue || effectiveBitrate.Value <= 0)
                {
                    effectiveBitrate = GetDefaultBitrateKbps(codec, extension, job.Quality);
                }

                if (effectiveBitrate.HasValue && effectiveBitrate.Value > 0)
                    sb.Append($"-b:a {effectiveBitrate.Value}k ");

                // Build audio filter chain (loudnorm, rnnoise, future filters)
                var filters = BuildFilterChain(job);

                if (filters.Length > 0)
                {
                    sb.Append("-af \"");
                    sb.Append(filters);
                    sb.Append("\" ");
                }
            }

            sb.Append($"\"{outputPath}\"");

            string args = sb.ToString();
            var stderrBuilder = new StringBuilder();

            var psi = new ProcessStartInfo
            {
                FileName = _ffmpegPath,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                ErrorDialog = false,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };

            proc.OutputDataReceived += (s, e) =>
            {
                if (e.Data != null) _progress(e.Data);
            };
            proc.ErrorDataReceived += (s, e) =>
            {
                if (e.Data != null)
                {
                    _progress(e.Data);
                    stderrBuilder.AppendLine(e.Data);
                }
            };

            proc.Exited += (s, e) =>
            {
                try
                {
                    if (proc.ExitCode != 0)
                    {
                        string logPath = ErrorLogService.Append(
                            _appPath,
                            "FFmpeg audio job failed",
                            job.InputPath,
                            details:
                            $"Output     : {outputPath}{Environment.NewLine}" +
                            $"Exit Code  : {proc.ExitCode}{Environment.NewLine}" +
                            $"Arguments  : {args}{Environment.NewLine}{Environment.NewLine}" +
                            "FFmpeg Output:" + Environment.NewLine +
                            stderrBuilder);

                        tcs.TrySetException(new InvalidOperationException(
                            $"ffmpeg exited with code {proc.ExitCode}. See central log: {logPath}"
                        ));
                    }
                    else
                    {
                        tcs.TrySetResult(true);
                    }
                }
                finally
                {
                    proc.Dispose();
                }
            };

            try
            {
                proc.Start();
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
                return tcs.Task;
            }

            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            return tcs.Task;
        }

        /// <summary>
        /// Assembles the final audio filter chain string from job settings.
        /// </summary>
        private static string BuildFilterChain(AudioJob job)
        {
            var sb = new StringBuilder();
            bool first = true;

            void Add(string filter)
            {
                if (string.IsNullOrWhiteSpace(filter))
                    return;
                if (!first) sb.Append(',');
                sb.Append(filter);
                first = false;
            }

            // Loudnorm
            if (!string.IsNullOrWhiteSpace(job.LoudnormFilterOverride))
            {
                Add(job.LoudnormFilterOverride);
            }
            else if (job.Loudnorm == LoudnormMode.SinglePass)
            {
                Add("loudnorm");
            }

            // RNNoise (arnndn)
            if (job.DenoiseEnabled && !string.IsNullOrWhiteSpace(job.DenoiseModelPath))
            {
                // Normalize path to forward slashes for ffmpeg
                var modelPath = job.DenoiseModelPath.Replace("\\", "/");
                Add($"arnndn=model='{modelPath}'");
            }

            // Additional filters (high-pass, low-pass, EQ, etc.) will be layered here
            // in the next sub-phase.

            return sb.ToString();
        }

        /// <summary>
        /// Decide container and codec based on job + mode.
        /// </summary>
        private static void BuildCodecAndExtension(
            AudioJob job,
            bool extract,
            out string? codec,
            out string extension)
        {
            codec = job.Codec;

            // Extract: always keep source container, stream copy
            if (extract)
            {
                extension = Path.GetExtension(job.InputPath);
                if (string.IsNullOrWhiteSpace(extension))
                    extension = ".mka"; // generic audio container fallback
                return;
            }

            // Convert: start from user-specified extension if provided, otherwise from input.
            extension = job.OutputExtension ?? Path.GetExtension(job.InputPath);
            if (string.IsNullOrWhiteSpace(extension))
                extension = ".m4a";

            if (string.IsNullOrWhiteSpace(codec))
            {
                switch (extension.ToLowerInvariant())
                {
                    case ".mp3":
                        codec = "libmp3lame";
                        break;
                    case ".flac":
                        codec = "flac";
                        break;
                    case ".opus":
                        codec = "libopus";
                        break;
                    case ".wav":
                        codec = "pcm_s16le";
                        break;
                    case ".ac3":
                        codec = "ac3";
                        break;
                    case ".eac3":
                        codec = "eac3";
                        break;
                    case ".m4a":
                    default:
                        codec = "aac";
                        extension = ".m4a";
                        break;
                }
            }
        }

        /// <summary>
        /// Codec-specific default bitrates, used when job.BitrateKbps is not set.
        /// </summary>
        private static int? GetDefaultBitrateKbps(string? codec, string extension, AudioQuality quality)
        {
            codec ??= string.Empty;
            extension = extension?.ToLowerInvariant() ?? string.Empty;
            codec = codec.ToLowerInvariant();

            // Lossless / PCM: ignore quality, let ffmpeg handle it
            if (codec == "flac" || codec.StartsWith("pcm_"))
                return null;

            // Helper to pick bitrate based on quality preset
            static int pick(AudioQuality q, int veryLow, int low, int medium, int high, int veryHigh)
            {
                return q switch
                {
                    AudioQuality.VeryLow => veryLow,
                    AudioQuality.Low => low,
                    AudioQuality.High => high,
                    AudioQuality.VeryHigh => veryHigh,
                    AudioQuality.Medium => medium,
                    AudioQuality.Auto => medium,
                    _ => medium
                };
            }

            // MP3 (libmp3lame)
            if (codec == "libmp3lame" || extension == ".mp3")
            {
                // VeryLow 96, Low 128, Medium 192, High 256, VeryHigh 320
                return pick(quality, 96, 128, 192, 256, 320);
            }

            // AAC (typical streaming / music settings)
            if (codec == "aac" || extension == ".m4a")
            {
                // VeryLow 96, Low 128, Medium 160, High 192, VeryHigh 256
                return pick(quality, 96, 128, 160, 192, 256);
            }

            // Opus (very efficient)
            if (codec == "libopus" || extension == ".opus")
            {
                // VeryLow 64, Low 96, Medium 128, High 160, VeryHigh 192
                return pick(quality, 64, 96, 128, 160, 192);
            }

            // AC3 / E-AC3 – home theater style bitrates
            if (codec == "ac3" || extension == ".ac3")
            {
                // VeryLow 192, Low 256, Medium 384, High 448, VeryHigh 512
                return pick(quality, 192, 256, 384, 448, 512);
            }

            if (codec == "eac3" || extension == ".eac3")
            {
                // VeryLow 256, Low 320, Medium 448, High 512, VeryHigh 640
                return pick(quality, 256, 320, 448, 512, 640);
            }

            // Fallback: medium-ish generic
            return pick(quality, 96, 128, 160, 192, 256);
        }


        /// <summary>
        /// First-pass loudnorm measurement; returns fully parameterized loudnorm filter for second pass.
        /// </summary>
        private Task<string?> BuildTwoPassLoudnormFilterAsync(AudioJob job)
        {
            var tcs = new TaskCompletionSource<string?>();

            var psi = new ProcessStartInfo
            {
                FileName = _ffmpegPath,
                Arguments = $"-hide_banner -i \"{job.InputPath}\" " +
                            "-af loudnorm=I=-16:TP=-1.5:LRA=11:print_format=json -f null -",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                ErrorDialog = false,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            var stderr = new StringBuilder();

            proc.OutputDataReceived += (s, e) => { /* ignore */ };
            proc.ErrorDataReceived += (s, e) =>
            {
                if (e.Data != null)
                    stderr.AppendLine(e.Data);
            };
            proc.Exited += (s, e) =>
            {
                try
                {
                    if (proc.ExitCode != 0)
                    {
                        tcs.TrySetResult(null);
                        return;
                    }

                    var json = ExtractJson(stderr.ToString());
                    if (json == null)
                    {
                        tcs.TrySetResult(null);
                        return;
                    }

                    try
                    {
                        using var doc = JsonDocument.Parse(json);
                        var root = doc.RootElement;

                        string Get(string name) =>
                            root.TryGetProperty(name, out var el) ? el.GetString() ?? "0" : "0";

                        var inputI = Get("input_i");
                        var inputLra = Get("input_lra");
                        var inputTp = Get("input_tp");
                        var inputThresh = Get("input_thresh");
                        var targetOffset = Get("target_offset");

                        var filter =
                            $"loudnorm=I=-16:TP=-1.5:LRA=11:" +
                            $"measured_I={inputI}:measured_LRA={inputLra}:" +
                            $"measured_TP={inputTp}:measured_thresh={inputThresh}:" +
                            $"offset={targetOffset}:linear=true:print_format=summary";

                        tcs.TrySetResult(filter);
                    }
                    catch
                    {
                        tcs.TrySetResult(null);
                    }
                }
                finally
                {
                    proc.Dispose();
                }
            };

            try
            {
                proc.Start();
            }
            catch
            {
                tcs.TrySetResult(null);
                return tcs.Task;
            }

            proc.BeginErrorReadLine();
            proc.BeginOutputReadLine();

            return tcs.Task;
        }

        private static string? ExtractJson(string text)
        {
            int start = text.IndexOf('{');
            int end = text.LastIndexOf('}');
            if (start < 0 || end <= start)
                return null;
            return text.Substring(start, end - start + 1);
        }
    }
}
