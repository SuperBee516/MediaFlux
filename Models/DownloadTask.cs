using System;

namespace Encode.Models
{
    public enum OutputType { Audio, Video }

    public class DownloadTask
    {
        public string Url { get; set; } = "";
        public OutputType OutputType { get; set; }
        public int AudioBitrateKbps { get; set; }
        public string Resolution { get; set; } = "";
        public TimeSpan? Start { get; set; }
        public TimeSpan? End { get; set; }
        public bool AllowPlaylist { get; set; } = false;
        public string OutputPath { get; set; } = "";
        public override string ToString() =>
          $"{OutputType} | {Url} | " +
          (Start.HasValue && End.HasValue
            ? $"{Start:hh\\:mm\\:ss}-{End:hh\\:mm\\:ss}"
            : "full") +
          $" => {OutputPath}";
    }
}
