using MediaFlux;
using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class ProgramStartupClassificationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MediaFlux-StartupRouting", Guid.NewGuid().ToString("N"));
    private readonly MediaFluxStoragePathService _normal;
    private static readonly Guid JobId = Guid.Parse("3c9b0ad3-690a-4f25-9ca6-e3e35ff183aa");

    public ProgramStartupClassificationTests()
    {
        Directory.CreateDirectory(_root);
        _normal = new(Path.Combine(_root, "Normal"), Path.Combine(_root, "storage-location.json"));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }

    [Fact]
    public void NoArgumentsAndExplorerArgumentsClassifyAsGui()
    {
        Assert.Equal(Program.StartupCommandKind.Gui, Program.ClassifyStartupCommand([]));
        Assert.Equal(Program.StartupCommandKind.Gui,
            Program.ClassifyStartupCommand(["--enqueue-file", "C:\\media\\clip.mp4"]));
    }

    [Fact]
    public void ExactReportedUserDataInvocationClassifiesAsGuiAndResolvesIsolation()
    {
        string profile = Path.Combine(_root, "MediaFlux-191-TestProfile");
        string[] args = ["--user-data", profile];

        Assert.Equal(Program.StartupCommandKind.Gui, Program.ClassifyStartupCommand(args));
        Assert.False(HeadlessSavedJobCommand.IsRequested(args));
        Assert.True(GuiUserDataCommand.TryResolve(args, _normal, out var storage, out string error), error);
        Assert.Equal(Path.GetFullPath(profile), storage!.Root);
    }

    [Theory]
    [InlineData("--run-saved-job-item", HeadlessSavedJobMode.Run, false)]
    [InlineData("--run-saved-job-item", HeadlessSavedJobMode.Run, true)]
    [InlineData("--preflight-saved-job-item", HeadlessSavedJobMode.Preflight, false)]
    [InlineData("--preflight-saved-job-item", HeadlessSavedJobMode.Preflight, true)]
    public void ValidSavedJobCommandsRemainHeadlessWithOrWithoutUserData(
        string mode, HeadlessSavedJobMode expectedMode, bool withUserData)
    {
        string[] args = [mode, JobId.ToString(), "--item", "path=C:\\media\\clip.mp4"];
        if (withUserData)
            args = [.. args, "--user-data", Path.Combine(_root, "Prepared")];

        Assert.Equal(Program.StartupCommandKind.SavedJobHeadless, Program.ClassifyStartupCommand(args));
        Assert.True(HeadlessSavedJobCommand.TryParse(args, out var command, out string error), error);
        Assert.Equal(expectedMode, command!.Mode);
        Assert.Equal(withUserData, command.UserDataRoot is not null);
    }

    [Theory]
    [InlineData("--user-data")]
    [InlineData("--user-data", "relative")]
    [InlineData("--user-data=C:\\isolated")]
    public void InvalidGuiUserDataArgumentsStayOnGuiRouteAndFailClearly(params string[] args)
    {
        Assert.Equal(Program.StartupCommandKind.Gui, Program.ClassifyStartupCommand(args));
        Assert.True(GuiUserDataCommand.IsRequested(args));
        Assert.False(GuiUserDataCommand.TryResolve(args, _normal, out _, out string error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void OtherHeadlessCommandFamiliesKeepTheirTopLevelRoutes()
    {
        Assert.Equal(Program.StartupCommandKind.EncodingV2Headless,
            Program.ClassifyStartupCommand(["--v2-probe", "request.json"]));
        Assert.Equal(Program.StartupCommandKind.QueueEstimateBenchmark,
            Program.ClassifyStartupCommand(["--benchmark-queue-estimates"]));
        Assert.Equal(Program.StartupCommandKind.HeadlessEncodeBatch,
            Program.ClassifyStartupCommand(["--headless-encode", "--source", "C:\\media\\clip.mp4"]));
    }
}
