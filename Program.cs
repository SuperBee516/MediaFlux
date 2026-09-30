using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Runtime.Versioning;
using System.Runtime.InteropServices;
using MediaFlux.Services;
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
            args.Length > 0 && args[0].StartsWith("--", StringComparison.Ordinal) &&
            (args[0].StartsWith("--run-saved-job", StringComparison.Ordinal) ||
             args[0].StartsWith("--preflight-saved-job", StringComparison.Ordinal));

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
                string userData = AppPaths.UserDataDirectory;
                Config config = Config.Load(AppPaths.ConfigFile);
                var jobs = new EncodeJobService(AppPaths.EncodeJobsFile).LoadStrict();
                var pipeline = new HeadlessSavedJobItemPipeline(config, userData, AppPaths.InstallDirectory);
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
                        command!, jobs, pipeline, Console.WriteLine, cancellation.Token)
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
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AttachConsole(int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AllocConsole();
    }
}
