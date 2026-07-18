using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Runtime.Versioning;
using MediaFlux.Services;
using Velopack;

[assembly: SupportedOSPlatform("windows")]


namespace MediaFlux
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
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
                return;
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
                return;
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
}
