using System.Diagnostics;
using System.Text;

namespace MediaFlux.Services
{
    public sealed class MediaToolProcessRequest
    {
        public string FileName { get; init; } = "";
        public IReadOnlyList<string> Arguments { get; init; } = Array.Empty<string>();
        public string WorkingDirectory { get; init; } = "";
        public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
        public bool SendQuitOnCancellation { get; init; }
        public Action<string>? StandardOutputLineCallback { get; init; }
        public Action<string>? StandardErrorLineCallback { get; init; }
    }

    public sealed class MediaToolProcessResult
    {
        public int ExitCode { get; init; }
        public string StandardOutput { get; init; } = "";
        public string StandardError { get; init; } = "";
        public bool TimedOut { get; init; }
    }

    public interface IMediaToolProcessRunner
    {
        Task<MediaToolProcessResult> RunAsync(
            MediaToolProcessRequest request,
            CancellationToken cancellationToken = default);
    }

    public sealed class MediaToolProcessRunner : IMediaToolProcessRunner
    {
        private const int MaxCapturedCharacters = 512 * 1024;

        public async Task<MediaToolProcessResult> RunAsync(
            MediaToolProcessRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            if (string.IsNullOrWhiteSpace(request.FileName))
                throw new ArgumentException("A media-tool executable path is required.", nameof(request));

            var startInfo = new ProcessStartInfo
            {
                FileName = request.FileName,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = request.SendQuitOnCancellation,
                CreateNoWindow = true,
                ErrorDialog = false,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            if (!string.IsNullOrWhiteSpace(request.WorkingDirectory))
                startInfo.WorkingDirectory = request.WorkingDirectory;

            foreach (string argument in request.Arguments)
                startInfo.ArgumentList.Add(argument);

            using var process = new Process { StartInfo = startInfo };
            process.Start();

            Task<string> stdoutTask = ReadBoundedAsync(
                process.StandardOutput,
                request.StandardOutputLineCallback);
            Task<string> stderrTask = ReadBoundedAsync(
                process.StandardError,
                request.StandardErrorLineCallback);
            using var timeoutCts = new CancellationTokenSource();
            if (request.Timeout > TimeSpan.Zero && request.Timeout != Timeout.InfiniteTimeSpan)
                timeoutCts.CancelAfter(request.Timeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeoutCts.Token);

            bool timedOut = false;
            try
            {
                await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                timedOut = !cancellationToken.IsCancellationRequested &&
                           timeoutCts.IsCancellationRequested;
                await StopProcessAsync(process, request.SendQuitOnCancellation).ConfigureAwait(false);

                if (!timedOut)
                    throw new OperationCanceledException(cancellationToken);
            }

            string stdout = await stdoutTask.ConfigureAwait(false);
            string stderr = await stderrTask.ConfigureAwait(false);
            return new MediaToolProcessResult
            {
                ExitCode = process.HasExited ? process.ExitCode : -1,
                StandardOutput = stdout,
                StandardError = stderr,
                TimedOut = timedOut
            };
        }

        private static async Task<string> ReadBoundedAsync(
            StreamReader reader,
            Action<string>? lineCallback)
        {
            var builder = new StringBuilder();
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                try
                {
                    lineCallback?.Invoke(line);
                }
                catch
                {
                    // Progress observers must not terminate the media process.
                }

                if (builder.Length >= MaxCapturedCharacters)
                    continue;

                int remaining = MaxCapturedCharacters - builder.Length;
                if (line.Length + Environment.NewLine.Length <= remaining)
                {
                    builder.AppendLine(line);
                }
                else
                {
                    builder.Append(line.AsSpan(0, Math.Min(line.Length, remaining)));
                    builder.AppendLine();
                    builder.AppendLine("[Additional media-tool output truncated by MediaFlux.]");
                }
            }

            return builder.ToString();
        }

        private static async Task StopProcessAsync(Process process, bool sendQuit)
        {
            try
            {
                if (process.HasExited)
                    return;

                if (sendQuit)
                {
                    try
                    {
                        await process.StandardInput.WriteLineAsync("q").ConfigureAwait(false);
                        await process.StandardInput.FlushAsync().ConfigureAwait(false);
                    }
                    catch
                    {
                        // The process may have closed stdin while cancellation was being handled.
                    }

                    Task gracefulExit = process.WaitForExitAsync();
                    if (await Task.WhenAny(
                            gracefulExit,
                            Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false) == gracefulExit)
                    {
                        await gracefulExit.ConfigureAwait(false);
                        return;
                    }
                }

                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().ConfigureAwait(false);
                }
            }
            catch
            {
                // Cancellation cleanup is best-effort; the caller still receives cancellation.
            }
        }
    }
}
