using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Runtime.Versioning;
using System.Runtime.InteropServices;
using MediaFlux.Services;
using MediaFlux.Services.EncodingV2;
using MediaFlux.Models;
using Velopack;

[assembly: SupportedOSPlatform("windows")]


namespace MediaFlux
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            if (EncodingV2HeadlessCommand.IsRequested(args))
            {
                HeadlessConsole.AttachToParent();
                return EncodingV2HeadlessCommand.Run(args);
            }

            if (HeadlessEncodeBatchCommand.IsRequested(args))
            {
                HeadlessConsole.AttachToParent();
                return RunHeadlessEncodeBatchCommand(args);
            }

            if (IsHeadlessSavedJobCommand(args))
            {
                HeadlessConsole.AttachToParent();
                return RunHeadlessSavedJobCommand(args);
            }

            VelopackApp.Build().Run();

            try
            {
                AppPaths.Initialize();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "MediaFlux could not prepare its application-data folder and cannot start safely.\r\n\r\n" + ex.Message,
                    "MediaFlux startup failed",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return 1;
            }

            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += Application_ThreadException;
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;

            var startupRequest = ParseExplorerRequest(args);
            using var primaryMutex = new Mutex(true, @"Local\Encode.ExplorerQueue.Primary", out bool isPrimary);
            if (!isPrimary)
            {
                var request = startupRequest ?? new ExplorerQueueRequest("activate", Array.Empty<string>());
                bool forwarded = ExplorerQueueBridge.SendToExistingInstanceAsync(request, TimeSpan.FromSeconds(8))
                    .GetAwaiter()
                    .GetResult();
                if (!forwarded)
                {
                    MessageBox.Show(
                        "Encode is already running, but Windows could not contact the existing instance. " +
                        "Close the existing Encode process and try again.",
                        "Encode",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                }
                return 0;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using var mainForm = new MainForm();
            using var bridge = isPrimary
                ? new ExplorerQueueBridge(request => mainForm.ReceiveExplorerQueueRequest(request))
                : null;
            bridge?.Start();
            if (startupRequest != null)
                mainForm.QueueInitialExplorerRequest(startupRequest);
            Application.Run(mainForm);
            return 0;
        }

        private static bool IsHeadlessSavedJobCommand(string[] args) =>
            HeadlessSavedJobCommand.IsRequested(args);

        private static bool PathsOverlap(string first, string second) =>
            MediaFluxStoragePathService.Same(first, second) ||
            MediaFluxStoragePathService.IsWithin(first, second) ||
            MediaFluxStoragePathService.IsWithin(second, first);

        private static int RunHeadlessEncodeBatchCommand(string[] args)
        {
            DateTime startedUtc = DateTime.UtcNow;
            if (!HeadlessEncodeBatchCommand.TryParse(args, out HeadlessEncodeBatchCommand? command, out string parseError))
            {
                Console.Error.WriteLine("Headless encode command rejected: " + parseError);
                var rejected = new HeadlessEncodeBatchResult(1, "CommandRejected", 2, startedUtc, DateTime.UtcNow,
                    Array.Empty<HeadlessEncodeJobResult>(), parseError);
                Console.Out.WriteLine(rejected.ToJson());
                return rejected.ExitCode;
            }

            using var cancellation = new CancellationTokenSource();
            ConsoleCancelEventHandler cancel = (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                cancellation.Cancel();
                Console.Error.WriteLine("Cancellation requested; waiting for the active operation to stop safely.");
            };
            Console.CancelKeyPress += cancel;
            try
            {
                using var primaryMutex = new Mutex(true, @"Local\Encode.ExplorerQueue.Primary", out bool isPrimary);
                if (!isPrimary)
                    throw new HeadlessEncodeFatalException("MediaFlux is already running. Close the GUI instance before starting headless execution to prevent overlapping source/output work.");
                if (command!.OutputDirectory is { } outputDirectory && !Directory.Exists(outputDirectory))
                    throw new HeadlessEncodeFatalException("The explicit output directory must already exist; headless execution will not create it.");

                // Isolated campaign storage is prepared by the caller. Resolve the normal
                // root read-only only to reject overlap; never write normal UserData state.
                MediaFluxStoragePathService paths = command!.UserDataRoot is { } userDataRoot
                    ? MediaFluxStoragePathService.ForIsolatedHeadlessRoot(userDataRoot)
                    : AppPaths.StoragePaths;
                MediaFluxStoragePathService normalPaths = AppPaths.StoragePaths;
                if (paths.IsProcessScoped && PathsOverlap(paths.Root, normalPaths.Root))
                    throw new HeadlessEncodeFatalException("The isolated UserData directory must not overlap normal UserData.");
                if (paths.IsProcessScoped && command.OutputDirectory is { } isolatedOutput &&
                    PathsOverlap(paths.Root, isolatedOutput))
                    throw new HeadlessEncodeFatalException("The isolated output directory must not overlap isolated UserData.");
                using IDisposable? isolatedStorage = paths.IsProcessScoped ? AppPaths.UseIsolatedHeadlessStorage(paths) : null;
                if (paths.IsProcessScoped && !File.Exists(paths.Config))
                    throw new HeadlessEncodeFatalException("The prepared isolated UserData directory must contain a config.json copy.");
                // No GUI startup, migrations, saved-job loading, or writes to saved-job definitions.
                Config config = Config.Load(paths.Config);
                if (paths.IsProcessScoped)
                    Console.Error.WriteLine("Headless UserData: " + paths.Root + " (isolated, process-scoped)");
                string applicationDirectory = HeadlessSavedJobEnvironment.ResolveApplicationDirectory(
                    AppPaths.InstallDirectory,
                    AppPaths.LauncherExecutablePath,
                    config,
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MediaFlux", "current"));
                Console.Error.WriteLine("Headless media-tool base: " + applicationDirectory);
                var pipeline = new HeadlessSavedJobItemPipeline(config, paths.Root, applicationDirectory);
                HeadlessEncodeBatchResult result = HeadlessEncodeBatchRunner.RunAsync(
                    command,
                    pipeline,
                    cancellation.Token,
                    line => Console.Error.WriteLine(line))
                    .GetAwaiter().GetResult();
                Console.Out.WriteLine(result.ToJson());
                return result.ExitCode;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Headless encode batch stopped: " + ex.Message);
                var failed = new HeadlessEncodeBatchResult(1, "FatalFailure", 8, startedUtc, DateTime.UtcNow,
                    Array.Empty<HeadlessEncodeJobResult>(), ex.Message);
                Console.Out.WriteLine(failed.ToJson());
                return failed.ExitCode;
            }
            finally { Console.CancelKeyPress -= cancel; }
        }

        private static int RunHeadlessSavedJobCommand(string[] args)
        {
            if (!HeadlessSavedJobCommand.TryParse(args, out HeadlessSavedJobCommand? command, out string parseError))
            {
                Console.Error.WriteLine("Command rejected: " + parseError);
                return (int)HeadlessSavedJobExitCode.CommandOrSelectorError;
            }

            try
            {
                // Headless mode deliberately avoids AppPaths.Initialize: it must not
                // create directories, migrate state, start the scheduler, or open WinForms.
                using var primaryMutex = new Mutex(true, @"Local\Encode.ExplorerQueue.Primary", out bool isPrimary);
                if (!isPrimary)
                    throw new InvalidOperationException("MediaFlux is already running. Close the GUI instance before starting a headless saved-job item to prevent overlapping source/output work.");
                MediaFluxStoragePathService paths = HeadlessSavedJobEnvironment.ResolvePaths(command!, AppPaths.StoragePaths);
                using IDisposable? isolatedStorage = paths.IsProcessScoped ? AppPaths.UseIsolatedHeadlessStorage(paths) : null;
                HeadlessSavedJobEnvironment.WriteDiagnostics(paths, Console.WriteLine);
                HeadlessSavedJobEnvironment environment = HeadlessSavedJobEnvironment.Load(paths);
                var pipeline = new HeadlessSavedJobItemPipeline(environment.Config, paths.Root, AppPaths.InstallDirectory);
                using var cancellation = new CancellationTokenSource();
                ConsoleCancelEventHandler cancel = (_, eventArgs) =>
                {
                    eventArgs.Cancel = true;
                    cancellation.Cancel();
                    Console.Error.WriteLine("Cancellation requested; waiting for the active operation to stop safely.");
                };
                Console.CancelKeyPress += cancel;
                try
                {
                    HeadlessSavedJobReport report = HeadlessSavedJobItemRunner.RunAsync(
                        command!, environment.Jobs, pipeline, Console.WriteLine, cancellation.Token)
                        .GetAwaiter().GetResult();
                    if (report.ExitCode == HeadlessSavedJobExitCode.Success)
                        Console.WriteLine(report.Message);
                    return (int)report.ExitCode;
                }
                finally { Console.CancelKeyPress -= cancel; }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Headless command rejected: " + ex.Message);
                return (int)HeadlessSavedJobExitCode.PreflightOrValidationRejected;
            }
        }

        private static ExplorerQueueRequest? ParseExplorerRequest(string[] args)
        {
            if (args.Length < 2)
                return null;

            string kind = args[0] switch
            {
                "--enqueue-file" => "file",
                "--enqueue-folder" => "folder",
                "--check-duplicates-folder" => "duplicate-folder",
                _ => ""
            };
            if (kind.Length == 0)
                return null;

            var paths = args.Skip(1).Where(path => !string.IsNullOrWhiteSpace(path)).ToArray();
            return paths.Length == 0 ? null : new ExplorerQueueRequest(kind, paths);
        }

        private static void Application_ThreadException(object sender, ThreadExceptionEventArgs e)
        {
            ErrorLogService.Append(AppPaths.UserDataDirectory, "Unhandled UI thread exception", exception: e.Exception);
        }

        private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            ErrorLogService.Append(
                AppPaths.UserDataDirectory,
                "Unhandled application exception",
                exception: e.ExceptionObject as Exception,
                details: e.ExceptionObject?.ToString());
        }

        private static void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            ErrorLogService.Append(AppPaths.UserDataDirectory, "Unobserved task exception", exception: e.Exception);
            e.SetObserved();
        }
    }

    internal static class HeadlessConsole
    {
        private const int AttachParentProcess = -1;

        public static void AttachToParent()
        {
            if (!AttachConsole(AttachParentProcess))
                AllocConsole();

            // WinExe apphosts may cache null standard streams before attaching to a console.
            // Reopen them after attachment so redirected headless JSON output is preserved.
            try
            {
                Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false))
                {
                    AutoFlush = true
                });
                Console.SetError(new StreamWriter(Console.OpenStandardError(), new System.Text.UTF8Encoding(false))
                {
                    AutoFlush = true
                });
            }
            catch
            {
                // Headless requests still run if a console stream is unavailable; their event log remains independent.
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AttachConsole(int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AllocConsole();
    }
}
