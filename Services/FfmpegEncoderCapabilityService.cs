using System.Collections.Concurrent;
using System.Diagnostics;

namespace MediaFlux.Services
{
    public sealed class FfmpegEncoderCapabilities
    {
        private readonly IReadOnlySet<string> _encoderNames;

        internal FfmpegEncoderCapabilities(
            string ffmpegPath,
            bool inspectionSucceeded,
            IEnumerable<string> encoderNames,
            string? errorMessage)
        {
            FfmpegPath = ffmpegPath;
            InspectionSucceeded = inspectionSucceeded;
            _encoderNames = new HashSet<string>(
                encoderNames,
                StringComparer.OrdinalIgnoreCase);
            ErrorMessage = errorMessage;
        }

        public string FfmpegPath { get; }
        public bool InspectionSucceeded { get; }
        public string? ErrorMessage { get; }
        public IReadOnlySet<string> EncoderNames => _encoderNames;

        public bool Contains(string? ffmpegEncoderName) =>
            !string.IsNullOrWhiteSpace(ffmpegEncoderName) &&
            _encoderNames.Contains(ffmpegEncoderName);
    }

    public static class FfmpegEncoderCapabilityService
    {
        private static readonly ConcurrentDictionary<
            string,
            Lazy<FfmpegEncoderCapabilities>> Cache =
            new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<
            string,
            Lazy<bool>> EncoderOptionCache =
            new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<
            string,
            Lazy<bool>> EncoderPixelFormatCache =
            new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<
            string,
            Lazy<bool>> FilterCache =
            new(StringComparer.OrdinalIgnoreCase);

        public static FfmpegEncoderCapabilities GetCapabilities(
            string ffmpegPath)
        {
            string key = CreateCacheKey(ffmpegPath);
            return Cache.GetOrAdd(
                key,
                _ => new Lazy<FfmpegEncoderCapabilities>(
                    () => Inspect(ffmpegPath),
                    LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        }

        internal static IReadOnlySet<string> ParseEncoderNames(string output)
        {
            var encoders = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(output))
                return encoders;

            using var reader = new StringReader(output);
            while (reader.ReadLine() is { } line)
            {
                string trimmed = line.TrimStart();
                if (trimmed.Length < 3 || trimmed[0] != 'V')
                    continue;

                string[] parts = trimmed.Split(
                    (char[]?)null,
                    StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2 ||
                    parts[0].Length < 2 ||
                    parts[0][0] != 'V' ||
                    parts[1] == "=")
                {
                    continue;
                }

                encoders.Add(parts[1]);
            }

            return encoders;
        }

        internal static IReadOnlySet<string> ParseEncoderOptionNames(
            string output)
        {
            var options = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(output))
                return options;

            using var reader = new StringReader(output);
            while (reader.ReadLine() is { } line)
            {
                string trimmed = line.TrimStart();
                if (trimmed.Length < 2 || trimmed[0] != '-')
                {
                    continue;
                }

                int end = trimmed.IndexOfAny([' ', '\t'], 1);
                string option = (end > 1
                        ? trimmed[1..end]
                        : trimmed[1..])
                    .Trim();
                if (option.Length > 0)
                    options.Add(option);
            }

            return options;
        }

        internal static IReadOnlySet<string> ParseEncoderPixelFormats(string output)
        {
            string? line = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(value => value.Contains(
                    "Supported pixel formats:", StringComparison.OrdinalIgnoreCase));
            if (line == null)
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            string values = line.Split(':', 2).Last();
            return new HashSet<string>(
                values.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries),
                StringComparer.OrdinalIgnoreCase);
        }

        internal static bool SupportsEncoderOption(
            string ffmpegPath,
            string encoderName,
            string optionName)
        {
            if (string.IsNullOrWhiteSpace(encoderName) ||
                encoderName.Any(character =>
                    !char.IsLetterOrDigit(character) &&
                    character is not '_' and not '-') ||
                string.IsNullOrWhiteSpace(optionName))
            {
                return false;
            }

            string normalizedOption = optionName.Trim().TrimStart('-');
            if (normalizedOption.Length == 0)
                return false;

            string key =
                $"{CreateCacheKey(ffmpegPath)}|{encoderName}|{normalizedOption}";
            return EncoderOptionCache.GetOrAdd(
                key,
                _ => new Lazy<bool>(
                    () => InspectEncoderOption(
                        ffmpegPath,
                        encoderName,
                        normalizedOption),
                    LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        }

        internal static bool SupportsEncoderPixelFormat(
            string ffmpegPath,
            string encoderName,
            string pixelFormat)
        {
            if (string.IsNullOrWhiteSpace(encoderName) ||
                string.IsNullOrWhiteSpace(pixelFormat))
                return false;

            string key = $"{CreateCacheKey(ffmpegPath)}|{encoderName}|pixfmt|{pixelFormat}";
            return EncoderPixelFormatCache.GetOrAdd(
                key,
                _ => new Lazy<bool>(
                    () => InspectEncoderPixelFormat(ffmpegPath, encoderName, pixelFormat),
                    LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        }

        internal static bool SupportsFilter(string ffmpegPath, string filterName)
        {
            if (string.IsNullOrWhiteSpace(filterName))
                return false;

            string key = $"{CreateCacheKey(ffmpegPath)}|filter|{filterName}";
            return FilterCache.GetOrAdd(
                key,
                _ => new Lazy<bool>(
                    () => InspectFilter(ffmpegPath, filterName),
                    LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        }

        internal static void ClearCache()
        {
            Cache.Clear();
            EncoderOptionCache.Clear();
            EncoderPixelFormatCache.Clear();
            FilterCache.Clear();
        }

        private static string CreateCacheKey(string ffmpegPath)
        {
            if (string.IsNullOrWhiteSpace(ffmpegPath))
                return "<missing>";

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(ffmpegPath);
            }
            catch
            {
                fullPath = ffmpegPath.Trim();
            }

            try
            {
                var file = new FileInfo(fullPath);
                return $"{fullPath}|{file.Length}|{file.LastWriteTimeUtc.Ticks}";
            }
            catch
            {
                return fullPath;
            }
        }

        private static FfmpegEncoderCapabilities Inspect(string ffmpegPath)
        {
            if (string.IsNullOrWhiteSpace(ffmpegPath) ||
                !File.Exists(ffmpegPath))
            {
                return new FfmpegEncoderCapabilities(
                    ffmpegPath ?? string.Empty,
                    inspectionSucceeded: false,
                    [],
                    "ffmpeg.exe could not be found.");
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = "-hide_banner -encoders",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                ErrorDialog = false,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            using var process = new Process { StartInfo = startInfo };
            try
            {
                process.Start();
                Task<string> standardOutput =
                    process.StandardOutput.ReadToEndAsync();
                Task<string> standardError =
                    process.StandardError.ReadToEndAsync();
                using var timeout = new CancellationTokenSource(
                    TimeSpan.FromSeconds(10));
                process.WaitForExitAsync(timeout.Token)
                    .GetAwaiter()
                    .GetResult();
                string output = standardOutput.GetAwaiter().GetResult() +
                                Environment.NewLine +
                                standardError.GetAwaiter().GetResult();
                IReadOnlySet<string> encoders = ParseEncoderNames(output);
                if (process.ExitCode != 0 || encoders.Count == 0)
                {
                    return new FfmpegEncoderCapabilities(
                        ffmpegPath,
                        inspectionSucceeded: false,
                        encoders,
                        "FFmpeg did not return a usable encoder list.");
                }

                return new FfmpegEncoderCapabilities(
                    ffmpegPath,
                    inspectionSucceeded: true,
                    encoders,
                    errorMessage: null);
            }
            catch (OperationCanceledException)
            {
                TryTerminate(process);
                return new FfmpegEncoderCapabilities(
                    ffmpegPath,
                    inspectionSucceeded: false,
                    [],
                    "FFmpeg encoder inspection timed out.");
            }
            catch (Exception ex)
            {
                TryTerminate(process);
                return new FfmpegEncoderCapabilities(
                    ffmpegPath,
                    inspectionSucceeded: false,
                    [],
                    $"FFmpeg encoder inspection failed: {ex.Message}");
            }
        }

        private static bool InspectEncoderOption(
            string ffmpegPath,
            string encoderName,
            string optionName)
        {
            if (string.IsNullOrWhiteSpace(ffmpegPath) ||
                !File.Exists(ffmpegPath))
            {
                return false;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                ErrorDialog = false,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            startInfo.ArgumentList.Add("-hide_banner");
            startInfo.ArgumentList.Add("-h");
            startInfo.ArgumentList.Add($"encoder={encoderName}");

            using var process = new Process { StartInfo = startInfo };
            try
            {
                process.Start();
                Task<string> standardOutput =
                    process.StandardOutput.ReadToEndAsync();
                Task<string> standardError =
                    process.StandardError.ReadToEndAsync();
                using var timeout = new CancellationTokenSource(
                    TimeSpan.FromSeconds(10));
                process.WaitForExitAsync(timeout.Token)
                    .GetAwaiter()
                    .GetResult();
                string output = standardOutput.GetAwaiter().GetResult() +
                                Environment.NewLine +
                                standardError.GetAwaiter().GetResult();
                return process.ExitCode == 0 &&
                       ParseEncoderOptionNames(output).Contains(optionName);
            }
            catch (OperationCanceledException)
            {
                TryTerminate(process);
                return false;
            }
            catch
            {
                TryTerminate(process);
                return false;
            }
        }

        private static bool InspectEncoderPixelFormat(
            string ffmpegPath,
            string encoderName,
            string pixelFormat)
        {
            string output = InspectEncoderHelp(ffmpegPath, encoderName);
            return ParseEncoderPixelFormats(output).Contains(pixelFormat);
        }

        private static bool InspectFilter(string ffmpegPath, string filterName)
        {
            string output = RunFfmpegText(ffmpegPath, "-hide_banner -filters");
            return output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Any(line => line.TrimStart().Split(
                    (char[]?)null,
                    StringSplitOptions.RemoveEmptyEntries)
                    .Contains(filterName, StringComparer.OrdinalIgnoreCase));
        }

        private static string InspectEncoderHelp(string ffmpegPath, string encoderName) =>
            RunFfmpegText(ffmpegPath, $"-hide_banner -h encoder={encoderName}");

        private static string RunFfmpegText(string ffmpegPath, string arguments)
        {
            if (string.IsNullOrWhiteSpace(ffmpegPath) || !File.Exists(ffmpegPath))
                return "";

            var startInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            using var process = new Process { StartInfo = startInfo };
            try
            {
                process.Start();
                Task<string> stdout = process.StandardOutput.ReadToEndAsync();
                Task<string> stderr = process.StandardError.ReadToEndAsync();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                process.WaitForExitAsync(timeout.Token).GetAwaiter().GetResult();
                return stdout.GetAwaiter().GetResult() + Environment.NewLine +
                       stderr.GetAwaiter().GetResult();
            }
            catch
            {
                TryTerminate(process);
                return "";
            }
        }

        private static void TryTerminate(Process process)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best effort only; the inspection process has no user data.
            }
        }
    }
}
