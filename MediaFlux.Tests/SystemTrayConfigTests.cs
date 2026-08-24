using MediaFlux.Models;
using Xunit;

namespace MediaFlux.Tests;

public sealed class SystemTrayConfigTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MediaFluxTests", Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [Fact]
    public void TrayMinimizeSettingDefaultsOffAndRoundTrips()
    {
        var defaults = new Config(); Assert.False(defaults.MinimizeToSystemTrayWhenMinimized);
        Directory.CreateDirectory(_root); string path = Path.Combine(_root, "config.json");
        defaults.MinimizeToSystemTrayWhenMinimized = true; defaults.Save(path);
        Assert.True(Config.Load(path).MinimizeToSystemTrayWhenMinimized);
    }
}
