using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Encode.Services
{
    public static class ErrorLogService
    {
        private static readonly object Sync = new();

        public static string GetDefaultLogPath(string applicationDirectory)
        {
            var appPath = string.IsNullOrWhiteSpace(applicationDirectory)
                ? AppDomain.CurrentDomain.BaseDirectory
                : applicationDirectory;

            return Path.Combine(appPath, "data", "logs", "encode-errors.log");
        }

        public static string Append(
            string applicationDirectory,
            string title,
            string? sourcePath = null,
            Exception? exception = null,
            string? details = null)
        {
            var logPath = GetDefaultLogPath(applicationDirectory);

            try
            {
                var dir = Path.GetDirectoryName(logPath);
                if (!string.IsNullOrWhiteSpace(dir))
                    Directory.CreateDirectory(dir);

                var sb = new StringBuilder();
                sb.AppendLine("================================================================================");
                sb.AppendLine($"Local Time : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                sb.AppendLine($"UTC Time   : {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z");
                sb.AppendLine($"Title      : {title}");

                if (!string.IsNullOrWhiteSpace(sourcePath))
                    sb.AppendLine($"Source     : {sourcePath}");

                if (exception != null)
                {
                    sb.AppendLine($"Exception  : {exception.GetType().FullName}");
                    sb.AppendLine($"Message    : {exception.Message}");
                }

                if (!string.IsNullOrWhiteSpace(details))
                {
                    sb.AppendLine();
                    sb.AppendLine(details.TrimEnd());
                }

                if (exception?.StackTrace != null)
                {
                    sb.AppendLine();
                    sb.AppendLine("Stack Trace:");
                    sb.AppendLine(exception.StackTrace);
                }

                sb.AppendLine();

                lock (Sync)
                {
                    File.AppendAllText(logPath, sb.ToString());
                }
            }
            catch (Exception logEx)
            {
                Debug.WriteLine($"Failed to append central encode error log: {logEx}");
            }

            return logPath;
        }
    }
}
