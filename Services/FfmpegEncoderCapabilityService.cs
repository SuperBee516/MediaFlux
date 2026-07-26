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

        internal static void ClearCache() => Cache.Clear();

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
