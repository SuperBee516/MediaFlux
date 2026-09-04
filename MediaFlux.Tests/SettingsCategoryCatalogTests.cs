using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class SettingsCategoryCatalogTests
{
    [Fact]
    public void CategoriesMatchSettingsNavigationContract() => Assert.Equal(
        new[] { "General", "Encoding", "FFmpeg & Tools", "Automation", "Duplicates", "Storage & Cache", "Backup & Restore", "Integrations", "Updates" },
        SettingsCategoryCatalog.Names);
}
