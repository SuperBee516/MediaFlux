using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace MediaFlux.Services
{
    public static class ErrorLogService
    {
        private static readonly object Sync = new();
        private const long MaxLogBytes = 10L * 1024 * 1024;
        private const int MaxFieldCharacters = 256 * 1024;

        public static string GetDefaultLogPath(string applicationDirectory)
        {
            return Path.Combine(AppPaths.LogsDirectory, "mediaflux-errors.log");
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
                    sb.AppendLine($"Message    : {Limit(exception.Message)}");
                }

                if (!string.IsNullOrWhiteSpace(details))
                {
                    sb.AppendLine();
                    sb.AppendLine(Limit(details.TrimEnd()));
                }

                if (exception?.StackTrace != null)
                {
                    sb.AppendLine();
                    sb.AppendLine("Stack Trace:");
                    sb.AppendLine(Limit(exception.StackTrace));
                }

                sb.AppendLine();

                lock (Sync)
                {
                    RotateOversizedLog(logPath);
                    File.AppendAllText(logPath, sb.ToString());
                }
            }
            catch (Exception logEx)
            {
                Debug.WriteLine($"Failed to append central encode error log: {logEx}");
            }

            return logPath;
        }

        public static string ReadTail(string logPath, int maxBytes, out bool truncated)
        {
            truncated = false;
            if (!File.Exists(logPath))
                return "No error log has been created yet.";

            maxBytes = Math.Clamp(maxBytes, 4 * 1024, 8 * 1024 * 1024);
            using var stream = new FileStream(
                logPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            long start = Math.Max(0, stream.Length - maxBytes);
            truncated = start > 0;
            stream.Seek(start, SeekOrigin.Begin);

            var buffer = new byte[stream.Length - start];
            int totalRead = 0;
            while (totalRead < buffer.Length)
            {
                int read = stream.Read(buffer, totalRead, buffer.Length - totalRead);
                if (read == 0)
                    break;
                totalRead += read;
            }

            string text = Encoding.UTF8.GetString(buffer, 0, totalRead);
            if (truncated)
            {
                int firstNewLine = text.IndexOf('\n');
                if (firstNewLine >= 0 && firstNewLine + 1 < text.Length)
                    text = text[(firstNewLine + 1)..];
            }

            return text;
        }

        private static string Limit(string value)
        {
            if (value.Length <= MaxFieldCharacters)
                return value;

            return value[..MaxFieldCharacters] +
                   $"{Environment.NewLine}[Diagnostic text truncated by MediaFlux.]";
        }

        private static void RotateOversizedLog(string logPath)
        {
            if (!File.Exists(logPath) || new FileInfo(logPath).Length < MaxLogBytes)
                return;

            string archivePath = Path.Combine(
                Path.GetDirectoryName(logPath)!,
                Path.GetFileNameWithoutExtension(logPath) + ".previous" + Path.GetExtension(logPath));
            File.Move(logPath, archivePath, overwrite: true);
        }
    }
}
