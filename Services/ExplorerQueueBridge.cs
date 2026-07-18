using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace MediaFlux.Services
{
    internal sealed record ExplorerQueueRequest(string Kind, string[] Paths);

    internal sealed class ExplorerQueueBridge : IDisposable
    {
        private const string PipeName = "Encode.ExplorerQueue.v1";
        private readonly CancellationTokenSource _stop = new();
        private readonly Action<ExplorerQueueRequest> _onRequest;
        private Task? _listener;

        public ExplorerQueueBridge(Action<ExplorerQueueRequest> onRequest)
        {
            _onRequest = onRequest;
        }

        public void Start() => _listener ??= Task.Run(ListenAsync);

        public static async Task<bool> TrySendAsync(ExplorerQueueRequest request, CancellationToken cancellationToken)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(1000, cancellationToken);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
                await writer.WriteLineAsync(JsonSerializer.Serialize(request).AsMemory(), cancellationToken);
                string? acknowledgment = await reader.ReadLineAsync(cancellationToken);
                return string.Equals(acknowledgment, "OK", StringComparison.Ordinal);
            }
            catch
            {
                return false;
            }
        }

        public static async Task<bool> SendToExistingInstanceAsync(
            ExplorerQueueRequest request,
            TimeSpan timeout)
        {
            using var timeoutCancellation = new CancellationTokenSource(timeout);
            while (!timeoutCancellation.IsCancellationRequested)
            {
                if (await TrySendAsync(request, timeoutCancellation.Token))
                    return true;

                try
                {
                    await Task.Delay(150, timeoutCancellation.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
            return false;
        }

        private async Task ListenAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    using var pipe = new NamedPipeServerStream(
                        PipeName,
                        PipeDirection.InOut,
                        1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await pipe.WaitForConnectionAsync(_stop.Token);
                    using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
                    using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                    string? json = await reader.ReadLineAsync(_stop.Token);
                    var request = string.IsNullOrWhiteSpace(json)
                        ? null
                        : JsonSerializer.Deserialize<ExplorerQueueRequest>(json);
                    if (request != null)
                    {
                        _onRequest(request);
                        await writer.WriteLineAsync("OK".AsMemory(), _stop.Token);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    // Keep the integration listener available after a malformed request.
                }
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _stop.Dispose();
        }
    }
}
