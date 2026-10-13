using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using MediaFlux.Models;
using MediaFlux.Services;
using MediaFlux.Services.LibraryCatalog;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MediaFlux.Tests;

public sealed class UserDataTestIsolationTests
{
    [Fact]
    public void TestProcessResolvesUserDataToFreshDisposableRoot()
    {
        Assert.Equal(Path.GetFullPath(TestUserDataIsolation.Root), AppPaths.UserDataDirectory);
        Assert.True(AppPaths.StoragePaths.IsProcessScoped);
        Assert.True(File.Exists(Path.Combine(TestUserDataIsolation.Root, ".mediaflux-test-userdata")));
    }

    [Fact]
    public void IsolatedTestRootDoesNotOverlapRealDefaultUserData()
    {
        string realDefault = new MediaFluxStoragePathService().Root;

        Assert.False(MediaFluxStoragePathService.PathsOverlap(AppPaths.UserDataDirectory, realDefault));
        Assert.False(MediaFluxStoragePathService.PathsOverlap(AppPaths.UserDataDirectory, Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MediaFlux", "UserData")));
    }

    [Fact]
    public async Task MediaFluxChildReceivesExplicitIsolatedUserDataAndCannotFallBack()
    {
        string defaultRoot = new MediaFluxStoragePathService().Root;
        Dictionary<string, string> before = Snapshot(defaultRoot);
        string childRoot = Path.Combine(TestUserDataIsolation.Root, "child", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(childRoot, "data"));
        string source = Path.Combine(childRoot, "source.fixture");
        File.WriteAllBytes(source, new byte[64]);
        Guid jobId = Guid.NewGuid();
        new Config().Save(Path.Combine(childRoot, "config.json"));
        new EncodeJobService(Path.Combine(childRoot, "data", "encode-jobs.json")).Save(
        [
            new EncodeJob { Id = jobId, Name = "isolated child fixture", Files = [new() { SourcePath = source }] }
        ]);

        string unmatchedSource = Path.Combine(childRoot, "does-not-match.fixture");
        ProcessStartInfo start = CreateMediaFluxStartInfo(
            ["--preflight-saved-job-item", jobId.ToString("D"), "--item", "path=" + unmatchedSource],
            childRoot);
        string[] arguments = start.ArgumentList.ToArray();
        Assert.Contains("--user-data", arguments);
        Assert.Equal(childRoot, arguments[Array.IndexOf(arguments, "--user-data") + 1]);
        IDictionary<string, string?> inheritedEnvironment = Environment.GetEnvironmentVariables()
            .Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(entry => (string)entry.Key, entry => (string?)entry.Value, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(inheritedEnvironment.Count, start.Environment.Count);
        Assert.Equal(inheritedEnvironment.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase),
            start.Environment.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase));
        string environmentSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(
            "\n",
            start.Environment.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => pair.Key + "=" + pair.Value)))));
        DateTimeOffset startedUtc = DateTimeOffset.UtcNow;

        using Process process = Process.Start(start) ?? throw new InvalidOperationException("MediaFlux child process did not start.");
        int childProcessId = process.Id;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        string output = (await stdout) + (await stderr);
        File.WriteAllText(Path.Combine(childRoot, "child-process-launch.json"), System.Text.Json.JsonSerializer.Serialize(new
        {
            executable = start.FileName,
            arguments,
            environmentMode = "Inherited parent environment without overrides",
            environmentVariableCount = start.Environment.Count,
            environmentSha256,
            inheritsParentEnvironment = true,
            workingDirectory = start.WorkingDirectory,
            userDataRoot = childRoot,
            processId = childProcessId,
            startedUtc = startedUtc.ToString("o"),
            endedUtc = DateTimeOffset.UtcNow.ToString("o"),
            exitCode = process.ExitCode
        }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

        Assert.Contains(childRoot, output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("isolated, process-scoped", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(defaultRoot, output, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before.OrderBy(pair => pair.Key), Snapshot(defaultRoot).OrderBy(pair => pair.Key));
    }

    [Fact]
    public void ConfigPersistenceUsesIsolatedUserData()
    {
        string path = AppPaths.ConfigFile;
        new Config { OutputSuffix = "_test-only" }.Save(path);

        Assert.True(MediaFluxStoragePathService.IsWithin(path, TestUserDataIsolation.Root));
        Assert.Equal("_test-only", Config.Load(path).OutputSuffix);
    }

    [Fact]
    public void HistoryPersistenceUsesIsolatedUserData()
    {
        var history = new HistoryService(AppPaths.HistoryFile);
        history.Append(new JobHistoryRecord { Id = "test-isolated-history", EndUtc = DateTime.UtcNow });

        Assert.True(MediaFluxStoragePathService.IsWithin(AppPaths.HistoryFile, TestUserDataIsolation.Root));
        Assert.Contains(history.LoadAll(), row => row.Id == "test-isolated-history");
    }

    [Fact]
    public void StatisticsPersistenceUsesIsolatedUserData()
    {
        var statistics = new EncodingStatisticsService(AppPaths.EncodingStatisticsFile);
        Assert.True(statistics.AppendFinalized(new EncodingStatisticsRecord
        {
            Id = "test-isolated-statistics",
            StartUtc = DateTime.UtcNow.AddSeconds(-1),
            EndUtc = DateTime.UtcNow
        }));

        Assert.True(MediaFluxStoragePathService.IsWithin(AppPaths.EncodingStatisticsFile, TestUserDataIsolation.Root));
        Assert.Contains(statistics.GetAll(), row => row.Id == "test-isolated-statistics");
    }

    [Fact]
    public void SqliteWalAndShmStayBesideDatabaseUnderIsolatedUserData()
    {
        string database = Path.Combine(AppPaths.DataDirectory, "test-isolation-wal.db");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());
        connection.Open();
        Execute(connection, "PRAGMA journal_mode=WAL;");
        Execute(connection, "PRAGMA wal_autocheckpoint=0;");
        Execute(connection, "CREATE TABLE isolation_probe(value TEXT NOT NULL);");
        Execute(connection, "INSERT INTO isolation_probe(value) VALUES ('isolated');");

        Assert.True(File.Exists(database + "-wal"));
        Assert.True(File.Exists(database + "-shm"));
        Assert.True(MediaFluxStoragePathService.IsWithin(database + "-wal", TestUserDataIsolation.Root));
        Assert.True(MediaFluxStoragePathService.IsWithin(database + "-shm", TestUserDataIsolation.Root));
    }

    [Fact]
    public void DefaultBenchmarkDatabaseUsesIsolatedUserData()
    {
        var database = new AiBenchmarkDatabase();
        database.Store(new AiBenchmarkDatabaseEntry(
            new AiBenchmarkDatabaseKey("test", "test", "test", "test", "test", "FP32", 1, "test"),
            NcnnRuntimeConfiguration.SafeDefault,
            1,
            null,
            true,
            DateTimeOffset.UtcNow,
            "isolation probe"));

        Assert.True(MediaFluxStoragePathService.IsWithin(AppPaths.AiBenchmarkDatabaseFile, TestUserDataIsolation.Root));
        Assert.True(File.Exists(AppPaths.AiBenchmarkDatabaseFile));
    }

    [Fact]
    public void DefaultLibraryCatalogUsesIsolatedUserData()
    {
        using SqliteLibraryCatalog catalog = SqliteLibraryCatalog.CreateDefault();
        catalog.Initialize();

        Assert.True(MediaFluxStoragePathService.IsWithin(catalog.DatabasePath, TestUserDataIsolation.Root));
        Assert.True(File.Exists(catalog.DatabasePath));
    }

    [Fact]
    public void NormalStorageResolverRetainsItsDefaultAndPointerBehaviorOutsideTestScope()
    {
        string root = Path.Combine(TestUserDataIsolation.Root, "normal-resolver");
        string defaultRoot = Path.Combine(root, "Default", "UserData");
        string locationFile = Path.Combine(root, "storage-location.json");
        string configuredRoot = Path.Combine(root, "Configured", "UserData");
        var normal = new MediaFluxStoragePathService(defaultRoot, locationFile);

        Assert.Equal(Path.GetFullPath(defaultRoot), normal.Root);
        normal.WriteConfiguredRoot(configuredRoot);
        Assert.Equal(Path.GetFullPath(configuredRoot), normal.Root);
        Assert.Equal(Path.GetFullPath(defaultRoot), normal.DefaultRoot);
        Assert.Equal(Path.GetFullPath(TestUserDataIsolation.Root), AppPaths.UserDataDirectory);
    }

    private static ProcessStartInfo CreateMediaFluxStartInfo(string[] arguments, string userDataRoot)
    {
        string executable = Path.Combine(Path.GetDirectoryName(typeof(Config).Assembly.Location)!, "MediaFlux.exe");
        if (!File.Exists(executable))
            throw new FileNotFoundException("MediaFlux apphost is required for the child-process routing test.", executable);
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = Path.GetDirectoryName(executable)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);
        if (!arguments.Contains("--user-data", StringComparer.OrdinalIgnoreCase))
        {
            start.ArgumentList.Add("--user-data");
            start.ArgumentList.Add(userDataRoot);
        }
        return start;
    }

    private static void Execute(SqliteConnection connection, string commandText)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = commandText;
        command.ExecuteNonQuery();
    }

    private static Dictionary<string, string> Snapshot(string root)
    {
        if (!Directory.Exists(root))
            return new(StringComparer.OrdinalIgnoreCase);
        return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(
                path => Path.GetRelativePath(root, path),
                path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),
                StringComparer.OrdinalIgnoreCase);
    }
}
