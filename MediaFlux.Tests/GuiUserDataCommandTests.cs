using MediaFlux.Models;
using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class GuiUserDataCommandTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MediaFlux-GuiUserData", Guid.NewGuid().ToString("N"));
    private readonly MediaFluxStoragePathService _normal;

    public GuiUserDataCommandTests()
    {
        Directory.CreateDirectory(_root);
        _normal = new(Path.Combine(_root, "Production"), Path.Combine(_root, "storage-location.json"));
        _normal.InitializeDirectories();
        new Config { OutputSuffix = "_PRODUCTION" }.Save(_normal.Config);
        new EncodeJobService(Path.Combine(_normal.Data, "encode-jobs.json")).Save([new EncodeJob { Name = "production" }]);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }

    [Fact]
    public void NoOverrideKeepsNormalGuiStorageResolution()
    {
        Assert.True(GuiUserDataCommand.TryResolve([], _normal, out var isolated, out var error));
        Assert.Null(isolated);
        Assert.Empty(error);
        Assert.Equal(Path.Combine(_root, "Production"), _normal.Root);
        Assert.Equal(Path.Combine(_normal.Root, "config.json"), _normal.Config);
    }

    [Fact]
    public void ExplicitOverrideRoutesConfigJobsAndLogsOnlyToIsolatedProfile()
    {
        string isolatedRoot = Path.Combine(_root, "Gui-Test-Profile");
        Assert.True(GuiUserDataCommand.TryResolve(["--user-data", isolatedRoot], _normal, out var paths, out var error));
        Assert.Empty(error);
        using (AppPaths.UseIsolatedGuiStorage(paths!))
        {
            AppPaths.Initialize();
            Assert.Equal(Path.GetFullPath(isolatedRoot), AppPaths.UserDataDirectory);
            Assert.Equal(Path.Combine(isolatedRoot, "config.json"), AppPaths.ConfigFile);
            Assert.Equal(Path.Combine(isolatedRoot, "data", "encode-jobs.json"), AppPaths.EncodeJobsFile);
            Assert.True(MediaFluxStoragePathService.IsWithin(AppPaths.LogsDirectory, isolatedRoot));
            new Config { OutputSuffix = "_ISOLATED" }.Save(AppPaths.ConfigFile);
            new EncodeJobService(AppPaths.EncodeJobsFile).Save([new EncodeJob { Name = "isolated" }]);
            ErrorLogService.Append(AppPaths.UserDataDirectory, "isolated test log");
            Assert.Equal("_ISOLATED", Config.Load(AppPaths.ConfigFile).OutputSuffix);
            Assert.Equal("isolated", Assert.Single(new EncodeJobService(AppPaths.EncodeJobsFile).Load()).Name);
            Assert.True(File.Exists(ErrorLogService.GetDefaultLogPath(AppPaths.UserDataDirectory)));
        }
        Assert.Equal("_PRODUCTION", Config.Load(_normal.Config).OutputSuffix);
        Assert.Equal("production", Assert.Single(new EncodeJobService(Path.Combine(_normal.Data, "encode-jobs.json")).Load()).Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative")]
    [InlineData("--user-data")]
    [InlineData("C:relative")]
    public void InvalidOverrideFailsWithoutCreatingOrFallingBack(string root)
    {
        string[] args = root == "--user-data" ? ["--user-data", "--user-data"] : ["--user-data", root];
        Assert.False(GuiUserDataCommand.TryResolve(args, _normal, out var paths, out string error));
        Assert.Null(paths);
        Assert.NotEmpty(error);
        Assert.Equal("_PRODUCTION", Config.Load(_normal.Config).OutputSuffix);
    }

    [Fact]
    public void OverrideRejectsNormalProfileAndExistingFile()
    {
        Assert.False(GuiUserDataCommand.TryResolve(["--user-data", _normal.Root], _normal, out _, out _));
        string file = Path.Combine(_root, "profile-file");
        File.WriteAllText(file, "keep");
        Assert.False(GuiUserDataCommand.TryResolve(["--user-data", file], _normal, out _, out _));
        Assert.Equal("keep", File.ReadAllText(file));
    }
}
