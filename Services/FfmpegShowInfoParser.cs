using System.Globalization;
using System.Text.RegularExpressions;

namespace MediaFlux.Services;
public sealed record FfmpegShowInfoFrame(int Index, long Pts, double PtsTime, int? Width, int? Height);
/// <summary>Strict parser for records emitted by FFmpeg's showinfo filter on the same output stream as image extraction.</summary>
public sealed class FfmpegShowInfoParser
{
    private static readonly Regex Record = new(@"\bn:\s*(?<n>\d+)\s+pts:\s*(?<pts>-?\d+)\s+pts_time:\s*(?<time>-?[\d.]+).*?(?:\bs:(?<w>\d+)x(?<h>\d+))?", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly List<FfmpegShowInfoFrame> _frames = new(); public IReadOnlyList<FfmpegShowInfoFrame> Frames => _frames;
    public void Consume(string line)
    {
        if (!line.Contains("showinfo", StringComparison.OrdinalIgnoreCase)) return;
        // showinfo also emits filter configuration lines (for example time_base and
        // frame_rate). They are diagnostics, not frame records. A line that declares a
        // frame record but cannot be parsed remains a hard failure.
        if (!Regex.IsMatch(line, @"\bn:\s*", RegexOptions.CultureInvariant)) return;
        Match match = Record.Match(line); if (!match.Success) throw new AiRestorationValidationException("FFmpeg emitted a malformed showinfo timing record.");
        int index = int.Parse(match.Groups["n"].Value, CultureInfo.InvariantCulture); long pts = long.Parse(match.Groups["pts"].Value, CultureInfo.InvariantCulture); double time = double.Parse(match.Groups["time"].Value, CultureInfo.InvariantCulture);
        if (_frames.Count > 0 && index != _frames[^1].Index + 1) throw new AiRestorationValidationException("FFmpeg showinfo records are duplicate or non-contiguous.");
        _frames.Add(new(index, pts, time, match.Groups["w"].Success ? int.Parse(match.Groups["w"].Value, CultureInfo.InvariantCulture) : null, match.Groups["h"].Success ? int.Parse(match.Groups["h"].Value, CultureInfo.InvariantCulture) : null));
    }
}
