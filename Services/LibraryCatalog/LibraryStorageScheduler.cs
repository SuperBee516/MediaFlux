using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace MediaFlux.Services.LibraryCatalog
{
    public interface ILibraryStorageKeyResolver
    {
        string ResolveStorageKey(string path, string reportedVolumeId = "");
    }

    public sealed class LibraryStorageScheduler
    {
        private readonly ILibraryStorageKeyResolver _resolver;
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.OrdinalIgnoreCase);

        public LibraryStorageScheduler(ILibraryStorageKeyResolver? resolver = null)
        {
            _resolver = resolver ?? new WindowsLibraryStorageKeyResolver();
        }

        public string ResolveStorageKey(string path, string reportedVolumeId = "") =>
            _resolver.ResolveStorageKey(path, reportedVolumeId);

        public async ValueTask<IAsyncDisposable> AcquireAsync(
            string path,
            string reportedVolumeId = "",
            CancellationToken cancellationToken = default)
        {
            string key = ResolveStorageKey(path, reportedVolumeId);
            SemaphoreSlim gate = _gates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new Lease(gate);
        }

        private sealed class Lease : IAsyncDisposable
        {
            private SemaphoreSlim? _gate;
            public Lease(SemaphoreSlim gate) => _gate = gate;
            public ValueTask DisposeAsync()
            {
                Interlocked.Exchange(ref _gate, null)?.Release();
                return ValueTask.CompletedTask;
            }
        }
    }

    public sealed class WindowsLibraryStorageKeyResolver : ILibraryStorageKeyResolver
    {
        private const uint ShareRead = 0x00000001;
        private const uint ShareWrite = 0x00000002;
        private const uint ShareDelete = 0x00000004;
        private const uint OpenExisting = 3;
        private const uint IoctlStorageGetDeviceNumber = 0x002D1080;

        public string ResolveStorageKey(string path, string reportedVolumeId = "")
        {
            string normalized = path ?? "";
            if (TryResolveNetworkShare(normalized, out string networkKey))
                return networkKey;

            if (OperatingSystem.IsWindows() && TryResolvePhysicalDevice(normalized, out string key))
                return key;
            if (!string.IsNullOrWhiteSpace(reportedVolumeId))
                return $"volume:{reportedVolumeId}";
            string root = Path.GetPathRoot(normalized) ?? normalized;
            return $"path:{root}".ToUpperInvariant();
        }

        private static bool TryResolvePhysicalDevice(string path, out string key)
        {
            key = "";
            try
            {
                string root = Path.GetPathRoot(Path.GetFullPath(path)) ?? "";
                if (root.Length < 2 || root[1] != ':')
                    return false;
                string devicePath = $@"\\.\{char.ToUpperInvariant(root[0])}:";
                using SafeFileHandle handle = CreateFile(
                    devicePath,
                    0,
                    ShareRead | ShareWrite | ShareDelete,
                    IntPtr.Zero,
                    OpenExisting,
                    0,
                    IntPtr.Zero);
                if (handle.IsInvalid)
                    return false;

                if (!DeviceIoControl(
                        handle,
                        IoctlStorageGetDeviceNumber,
                        IntPtr.Zero,
                        0,
                        out StorageDeviceNumber number,
                        Marshal.SizeOf<StorageDeviceNumber>(),
                        out _,
                        IntPtr.Zero))
                    return false;

                key = $"physical:{number.DeviceType}:{number.DeviceNumber}";
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryResolveNetworkShare(string path, out string key)
        {
            key = "";
            string candidate = path;
            try
            {
                string root = Path.GetPathRoot(path) ?? "";
                if (!candidate.StartsWith(@"\\", StringComparison.Ordinal) &&
                    OperatingSystem.IsWindows() && root.Length >= 2 && root[1] == ':' &&
                    new DriveInfo(root).DriveType == DriveType.Network)
                {
                    uint length = 512;
                    var remote = new StringBuilder((int)length);
                    if (WNetGetConnection(root[..2], remote, ref length) == 0)
                        candidate = remote.ToString();
                }
            }
            catch
            {
                // Continue to the volume/root fallback when mapping lookup fails.
            }
            if (!candidate.StartsWith(@"\\", StringComparison.Ordinal))
                return false;
            string[] parts = candidate.TrimStart('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
                return false;
            key = $"network:{parts[0]}\\{parts[1]}".ToUpperInvariant();
            return true;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct StorageDeviceNumber
        {
            public uint DeviceType;
            public uint DeviceNumber;
            public uint PartitionNumber;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(
            SafeFileHandle device,
            uint controlCode,
            IntPtr inputBuffer,
            int inputBufferSize,
            out StorageDeviceNumber outputBuffer,
            int outputBufferSize,
            out int bytesReturned,
            IntPtr overlapped);

        [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
        private static extern int WNetGetConnection(
            string localName,
            StringBuilder remoteName,
            ref uint length);
    }
}
