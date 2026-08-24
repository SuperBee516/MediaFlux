using System.Globalization;
using System.Text.Json;
using MediaFlux.Models;

namespace MediaFlux.Services
{
    public interface IMediaProbeService
    {
        Task<MediaProbeResult> ProbeAsync(
            string path,
            CancellationToken cancellationToken = default);
    }

    public sealed class FfprobeService : IMediaProbeService
    {
        private readonly string _ffprobePath;
        private readonly IMediaToolProcessRunner _processRunner;
        private readonly TimeSpan _timeout;

        public FfprobeService(
            string applicationDirectory,
            string? configuredFfprobePath = null,
            IMediaToolProcessRunner? processRunner = null,
            TimeSpan? timeout = null)
        {
            _ffprobePath = FfmpegToolResolver.Resolve(
                applicationDirectory,
                configuredFfprobePath: configuredFfprobePath).FfprobePath;
            _processRunner = processRunner ?? new MediaToolProcessRunner();
            _timeout = timeout ?? TimeSpan.FromSeconds(45);
        }

        public FfprobeService(
            string ffprobePath,
            IMediaToolProcessRunner processRunner,
            TimeSpan? timeout = null)
        {
            if (string.IsNullOrWhiteSpace(ffprobePath))
                throw new ArgumentException("FFprobe path must be provided.", nameof(ffprobePath));

            _ffprobePath = ffprobePath;
            _processRunner = processRunner ??
                throw new ArgumentNullException(nameof(processRunner));
            _timeout = timeout ?? TimeSpan.FromSeconds(45);
        }

        public async Task<MediaProbeResult> ProbeAsync(
            string path,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(path))
                return MediaProbeResult.Failed("The media path is empty.");
            if (!File.Exists(path))
                return MediaProbeResult.Failed("The media file does not exist.");
            if (!File.Exists(_ffprobePath))
                return MediaProbeResult.Failed($"FFprobe was not found at '{_ffprobePath}'.");

            MediaToolProcessResult processResult;
            try
            {
                processResult = await _processRunner.RunAsync(
                    new MediaToolProcessRequest
                    {
                        FileName = _ffprobePath,
                        Timeout = _timeout,
                        Arguments = new[]
                        {
                            "-v", "error",
                            "-print_format", "json",
                            "-show_error",
                            "-show_format",
                            "-show_streams",
                            "-show_chapters",
                            path
                        }
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return MediaProbeResult.Failed($"FFprobe could not be started: {ex.Message}");
            }

            if (processResult.TimedOut)
                return MediaProbeResult.Failed($"FFprobe timed out after {_timeout.TotalSeconds:0} seconds.");

            MediaProbeResult parsed;
            try
            {
                parsed = ParseProbeJson(processResult.StandardOutput, path);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                string details = FirstUsefulError(processResult.StandardError, ex.Message);
                return MediaProbeResult.Failed($"FFprobe returned invalid analysis data: {details}");
            }

            if (processResult.ExitCode != 0 || !parsed.Success)
            {
                string details = FirstUsefulError(
                    parsed.ErrorMessage,
                    processResult.StandardError,
                    $"FFprobe exited with code {processResult.ExitCode}.");
                return MediaProbeResult.Failed(details);
            }

            return parsed;
        }

        internal static MediaProbeResult ParseProbeJson(string json, string sourcePath)
        {
            if (string.IsNullOrWhiteSpace(json))
                return MediaProbeResult.Failed("FFprobe returned no analysis data.");

            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            string probeError = ParseProbeError(root);
            var streams = ParseStreams(root);
            var chapters = ParseChapters(root);

            string formatName = "";
            double? formatDuration = null;
            long? formatSize = null;
            long? formatBitRate = null;
            var formatTags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("format", out JsonElement format) &&
                format.ValueKind == JsonValueKind.Object)
            {
                formatName = GetString(format, "format_name");
                formatDuration = GetPositiveDouble(format, "duration");
                formatSize = GetPositiveLong(format, "size");
                formatBitRate = GetPositiveLong(format, "bit_rate");
                if (format.TryGetProperty("tags", out JsonElement tags) &&
                    tags.ValueKind == JsonValueKind.Object)
                {
                    foreach (JsonProperty property in tags.EnumerateObject())
                    {
                        string value = property.Value.ValueKind switch
                        {
                            JsonValueKind.String => property.Value.GetString() ?? "",
                            JsonValueKind.Number => property.Value.GetRawText(),
                            JsonValueKind.True => "true",
                            JsonValueKind.False => "false",
                            _ => ""
                        };
                        if (!string.IsNullOrWhiteSpace(value))
                            formatTags[property.Name] = value;
                    }
                }
            }

            double? duration = formatDuration;
            if (duration is not > 0)
            {
                duration = streams
                    .Where(stream => stream.DurationSeconds is > 0)
                    .Select(stream => stream.DurationSeconds)
                    .Max();
            }

            long? size = formatSize;
            if (size is not > 0)
            {
                try
                {
                    var file = new FileInfo(sourcePath);
                    if (file.Exists && file.Length > 0)
                        size = file.Length;
                }
                catch
                {
                    // File size is supplementary probe information only.
                }
            }

            bool hasUsefulData = streams.Count > 0 || duration is > 0;
            return new MediaProbeResult
            {
                Success = string.IsNullOrWhiteSpace(probeError) && hasUsefulData,
                ErrorMessage = string.IsNullOrWhiteSpace(probeError) && !hasUsefulData
                    ? "FFprobe found no readable media streams."
                    : probeError,
                FormatName = formatName,
                SizeBytes = size,
                DurationSeconds = duration,
                BitRate = formatBitRate,
                FormatTags = formatTags,
                Streams = streams,
                Chapters = chapters
            };
        }

        private static List<MediaProbeStreamInfo> ParseStreams(JsonElement root)
        {
            var streams = new List<MediaProbeStreamInfo>();
            if (!root.TryGetProperty("streams", out JsonElement array) ||
                array.ValueKind != JsonValueKind.Array)
            {
                return streams;
            }

            foreach (JsonElement stream in array.EnumerateArray())
            {
                var dispositions = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                if (stream.TryGetProperty("disposition", out JsonElement disposition) &&
                    disposition.ValueKind == JsonValueKind.Object)
                {
                    foreach (JsonProperty property in disposition.EnumerateObject())
                    {
                        dispositions[property.Name] =
                            property.Value.ValueKind == JsonValueKind.True ||
                            (property.Value.TryGetInt32(out int flag) && flag != 0);
                    }
                }

                string language = "";
                var streamTags = new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase);
                if (stream.TryGetProperty("tags", out JsonElement tags) &&
                    tags.ValueKind == JsonValueKind.Object)
                {
                    foreach (JsonProperty property in tags.EnumerateObject())
                    {
                        string value = property.Value.ValueKind switch
                        {
                            JsonValueKind.String => property.Value.GetString() ?? "",
                            JsonValueKind.Number => property.Value.GetRawText(),
                            JsonValueKind.True => "true",
                            JsonValueKind.False => "false",
                            _ => ""
                        };
                        if (!string.IsNullOrWhiteSpace(value))
                            streamTags[property.Name] = value;
                    }
                    language = streamTags.TryGetValue("language", out string? tagLanguage)
                        ? tagLanguage ?? ""
                        : "";
                }

                streams.Add(new MediaProbeStreamInfo
                {
                    Index = GetInt32(stream, "index") ?? streams.Count,
                    Id = GetString(stream, "id"),
                    CodecType = GetString(stream, "codec_type"),
                    CodecName = GetString(stream, "codec_name"),
                    CodecLongName = GetString(stream, "codec_long_name"),
                    Profile = GetString(stream, "profile"),
                    Level = GetInt32(stream, "level"),
                    BitRate = GetPositiveLong(stream, "bit_rate"),
                    TimeBase = GetString(stream, "time_base"),
                    DisplayAspectRatio = GetString(stream, "display_aspect_ratio"),
                    FieldOrder = GetString(stream, "field_order"),
                    PixelFormat = GetString(stream, "pix_fmt"),
                    BitsPerRawSample = GetInt32(stream, "bits_per_raw_sample"),
                    ColorRange = GetString(stream, "color_range"),
                    ColorSpace = GetString(stream, "color_space"),
                    ColorTransfer = GetString(stream, "color_transfer"),
                    ColorPrimaries = GetString(stream, "color_primaries"),
                    Language = language,
                    Tags = streamTags,
                    ChannelLayout = GetString(stream, "channel_layout"),
                    Width = GetInt32(stream, "width"),
                    Height = GetInt32(stream, "height"),
                    Channels = GetInt32(stream, "channels"),
                    DurationSeconds = GetPositiveDouble(stream, "duration"),
                    FrameRate = ParseFrameRate(
                        GetString(stream, "avg_frame_rate"),
                        GetString(stream, "r_frame_rate")),
                    Dispositions = dispositions
                });
            }

            return streams;
        }

        private static List<MediaProbeChapterInfo> ParseChapters(JsonElement root)
        {
            var chapters = new List<MediaProbeChapterInfo>();
            if (!root.TryGetProperty("chapters", out JsonElement array) ||
                array.ValueKind != JsonValueKind.Array)
            {
                return chapters;
            }

            foreach (JsonElement chapter in array.EnumerateArray())
            {
                string title = "";
                if (chapter.TryGetProperty("tags", out JsonElement tags) &&
                    tags.ValueKind == JsonValueKind.Object)
                {
                    title = GetString(tags, "title");
                }

                chapters.Add(new MediaProbeChapterInfo
                {
                    Id = GetInt32(chapter, "id") ?? chapters.Count,
                    StartSeconds = GetNonNegativeDouble(chapter, "start_time"),
                    EndSeconds = GetNonNegativeDouble(chapter, "end_time"),
                    Title = title
                });
            }

            return chapters;
        }

        private static string ParseProbeError(JsonElement root)
        {
            if (!root.TryGetProperty("error", out JsonElement error))
                return "";

            if (error.ValueKind == JsonValueKind.String)
                return error.GetString() ?? "";
            if (error.ValueKind != JsonValueKind.Object)
                return "FFprobe reported an unspecified media error.";

            string message = GetString(error, "string");
            if (!string.IsNullOrWhiteSpace(message))
                return message;

            string code = GetString(error, "code");
            return string.IsNullOrWhiteSpace(code)
                ? "FFprobe reported an unspecified media error."
                : $"FFprobe error {code}.";
        }

        private static string GetString(JsonElement element, string name)
        {
            if (!element.TryGetProperty(name, out JsonElement value))
                return "";

            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString() ?? "",
                JsonValueKind.Number => value.GetRawText(),
                _ => ""
            };
        }

        private static int? GetInt32(JsonElement element, string name)
        {
            if (!element.TryGetProperty(name, out JsonElement value))
                return null;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number))
                return number;
            if (value.ValueKind == JsonValueKind.String &&
                int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
            {
                return number;
            }

            return null;
        }

        private static long? GetPositiveLong(JsonElement element, string name)
        {
            string value = GetString(element, name);
            return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long number) &&
                   number > 0
                ? number
                : null;
        }

        private static double? GetPositiveDouble(JsonElement element, string name)
        {
            double? value = GetNonNegativeDouble(element, name);
            return value is > 0 ? value : null;
        }

        private static double? GetNonNegativeDouble(JsonElement element, string name)
        {
            string value = GetString(element, name);
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) &&
                   number >= 0 &&
                   !double.IsNaN(number) &&
                   !double.IsInfinity(number)
                ? number
                : null;
        }

        private static double? ParseFrameRate(string average, string nominal)
        {
            return TryParseFraction(average, out double parsed) && parsed > 0
                ? parsed
                : TryParseFraction(nominal, out parsed) && parsed > 0
                    ? parsed
                    : null;
        }

        private static bool TryParseFraction(string value, out double parsed)
        {
            parsed = 0;
            if (string.IsNullOrWhiteSpace(value))
                return false;

            string[] parts = value.Split('/');
            if (parts.Length == 1)
            {
                return double.TryParse(
                    parts[0],
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out parsed);
            }

            if (parts.Length != 2 ||
                !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double numerator) ||
                !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double denominator) ||
                denominator == 0)
            {
                return false;
            }

            parsed = numerator / denominator;
            return true;
        }

        private static string FirstUsefulError(params string[] values)
        {
            foreach (string value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                    return value.Trim();
            }

            return "FFprobe could not analyze the media.";
        }
    }
}
