using System.Runtime.CompilerServices;
using MediaFlux.Services;

namespace MediaFlux.Tests;

/// <summary>
/// Routes the test process through one fresh, disposable UserData tree before any tests run.
/// Tests that spawn MediaFlux must pass this root explicitly with --user-data.
/// </summary>
internal static class TestUserDataIsolation
{
    private static IDisposable? _scope;
    private static string? _root;

    public static string Root => _root ?? throw new InvalidOperationException("Test UserData isolation was not initialized.");

    [ModuleInitializer]
    internal static void Initialize()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "MediaFlux.Tests",
            "UserData",
            $"{Environment.ProcessId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, ".mediaflux-test-userdata"), Environment.ProcessId.ToString());
        MediaFluxStoragePathService storage = MediaFluxStoragePathService.ForIsolatedHeadlessRoot(root);
        _scope = AppPaths.UseIsolatedTestStorage(storage);
        _root = root;
    }
}
