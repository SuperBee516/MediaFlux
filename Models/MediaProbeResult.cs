namespace MediaFlux.Models
{
    public sealed class MediaProbeResult
    {
        public bool Success { get; init; }
        public string ErrorMessage { get; init; } = "";
        public string FormatName { get; init; } = "";
        public long? SizeBytes { get; init; }
        public double? DurationSeconds { get; init; }
        public long? BitRate { get; init; }
        public IReadOnlyList<MediaProbeStreamInfo> Streams { get; init; } =
            Array.Empty<MediaProbeStreamInfo>();
        public IReadOnlyList<MediaProbeChapterInfo> Chapters { get; init; } =
            Array.Empty<MediaProbeChapterInfo>();

        public static MediaProbeResult Failed(string message) => new()
        {
            Success = false,
            ErrorMessage = message
        };
    }

    public sealed class MediaProbeStreamInfo
    {
        public int Index { get; init; }
        public string Id { get; init; } = "";
        public string CodecType { get; init; } = "";
        public string CodecName { get; init; } = "";
        public string CodecLongName { get; init; } = "";
        public string Profile { get; init; } = "";
        public int? Level { get; init; }
        public long? BitRate { get; init; }
        public string TimeBase { get; init; } = "";
        public string DisplayAspectRatio { get; init; } = "";
        public string FieldOrder { get; init; } = "";
        public string PixelFormat { get; init; } = "";
        public int? BitsPerRawSample { get; init; }
        public string ColorRange { get; init; } = "";
        public string ColorSpace { get; init; } = "";
        public string ColorTransfer { get; init; } = "";
        public string ColorPrimaries { get; init; } = "";
        public string Language { get; init; } = "";
        public string ChannelLayout { get; init; } = "";
        public int? Width { get; init; }
        public int? Height { get; init; }
        public int? Channels { get; init; }
        public double? DurationSeconds { get; init; }
        public double? FrameRate { get; init; }
        public IReadOnlyDictionary<string, bool> Dispositions { get; init; } =
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
    }

    public sealed class MediaProbeChapterInfo
    {
        public int Id { get; init; }
        public double? StartSeconds { get; init; }
        public double? EndSeconds { get; init; }
        public string Title { get; init; } = "";
    }
}
