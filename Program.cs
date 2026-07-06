using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Runtime.Versioning;
using Encode.Services;

[assembly: SupportedOSPlatform("windows")]


namespace Encode
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += Application_ThreadException;
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }

        private static void Application_ThreadException(object sender, ThreadExceptionEventArgs e)
        {
            ErrorLogService.Append(Application.StartupPath, "Unhandled UI thread exception", exception: e.Exception);
        }

        private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            ErrorLogService.Append(
                Application.StartupPath,
                "Unhandled application exception",
                exception: e.ExceptionObject as Exception,
                details: e.ExceptionObject?.ToString());
        }

        private static void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            ErrorLogService.Append(Application.StartupPath, "Unobserved task exception", exception: e.Exception);
            e.SetObserved();
        }
    }
}
