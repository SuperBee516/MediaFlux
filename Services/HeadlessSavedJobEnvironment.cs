using MediaFlux.Models;

namespace MediaFlux.Services;

/// <summary>Read-only startup over the same config and saved-job formats used by the GUI.</summary>
internal sealed record HeadlessSavedJobEnvironment(
    MediaFluxStoragePathService Paths,
    Config Config,
    IReadOnlyList<EncodeJob> Jobs)
{
    internal static MediaFluxStoragePathService ResolvePaths(
        HeadlessSavedJobCommand command, MediaFluxStoragePathService normalPaths) =>
        command.UserDataRoot is null
            ? normalPaths
            : MediaFluxStoragePathService.ForIsolatedHeadlessRoot(command.UserDataRoot);

    internal static HeadlessSavedJobEnvironment Load(MediaFluxStoragePathService paths)
    {
        string jobsPath = Path.Combine(paths.Data, "encode-jobs.json");
        try
        {
            Config config = paths.IsProcessScoped ? Config.LoadRequired(paths.Config) : Config.Load(paths.Config);
            var store = new EncodeJobService(jobsPath);
            return new(paths, config, paths.IsProcessScoped ? store.LoadRequired() : store.LoadStrict());
        }
        catch (Exception ex) when (paths.IsProcessScoped)
        {
            throw new HeadlessSavedJobValidationException(
                $"Could not load the prepared isolated UserData tree at '{paths.Root}'. " +
                "Readable config.json and data/encode-jobs.json are required; normal UserData was not used. " + ex.Message, ex);
        }
    }

    internal static void WriteDiagnostics(MediaFluxStoragePathService paths, Action<string> write)
    {
        write($"Headless UserData: {paths.Root} ({(paths.IsProcessScoped ? "isolated, process-scoped; storage pointers ignored" : "normal storage resolution")})");
        write($"Headless config: {paths.Config}");
        write($"Headless saved jobs: {Path.Combine(paths.Data, "encode-jobs.json")}");
        write($"Headless statistics: {Path.Combine(paths.Data, "encoding-statistics.jsonl")}");
        write($"Headless logs: {paths.Logs}; runtime temp: {(paths.IsProcessScoped ? paths.Temp : Path.Combine(Path.GetTempPath(), "MediaFlux"))}");
    }
}
