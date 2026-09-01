using MediaFlux.Services;
using Xunit;
namespace MediaFlux.Tests;
public sealed class FfmpegShowInfoParserTests
{
    [Fact] public void ParsesInvariantShowInfoRecords() { var p = new FfmpegShowInfoParser(); p.Consume("[Parsed_showinfo_0] n:   0 pts:      42 pts_time:0.042 pos:0 fmt:yuv420p s:640x480"); p.Consume("[Parsed_showinfo_0] n:   1 pts:      84 pts_time:0.084 pos:0 fmt:yuv420p s:640x480"); Assert.Equal(2, p.Frames.Count); Assert.Equal(42, p.Frames[0].Pts); }
    [Fact] public void IgnoresUnrelatedStderr() { var p = new FfmpegShowInfoParser(); p.Consume("frame= 10 fps=25"); Assert.Empty(p.Frames); }
    [Fact] public void IgnoresShowInfoConfigurationLines() { var p = new FfmpegShowInfoParser(); p.Consume("[Parsed_showinfo_0 @ 000001] config in time_base: 1/1000, frame_rate: 30/1"); Assert.Empty(p.Frames); }
    [Fact] public void RejectsDuplicateRecords() { var p = new FfmpegShowInfoParser(); p.Consume("showinfo n: 0 pts: 0 pts_time:0"); Assert.Throws<AiRestorationValidationException>(() => p.Consume("showinfo n: 0 pts: 1 pts_time:0.04")); }
}
