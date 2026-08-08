using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MediaFlux.Services.LibraryCatalog
{
    public sealed record LibraryChangeJournalCheckpoint(
        string VolumeIdentity,
        string FileSystemName,
        long JournalId,
        long NextUsn,
        long LowestValidUsn);

    public interface ILibraryChangeJournalProvider
    {
        bool TryGetCheckpoint(string rootPath, out LibraryChangeJournalCheckpoint checkpoint, out string error);
    }

    public static class LibraryChangeJournalSafety
    {
        public static bool ProvesNoVolumeChanges(
            LibraryScanAcceleratorState previous,
            LibraryChangeJournalCheckpoint current)
        {
            return string.Equals(previous.AcceleratorKind, "usn-volume-checkpoint-v1", StringComparison.Ordinal) &&
                   string.Equals(previous.VolumeIdentity, current.VolumeIdentity, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(previous.FileSystemName, current.FileSystemName, StringComparison.OrdinalIgnoreCase) &&
                   previous.JournalId == current.JournalId &&
                   previous.NextUsn == current.NextUsn &&
                   current.LowestValidUsn <= previous.NextUsn;
        }
    }

    public sealed class WindowsUsnChangeJournalProvider : ILibraryChangeJournalProvider
    {
        private const uint ShareRead = 0x00000001;
        private const uint ShareWrite = 0x00000002;
        private const uint ShareDelete = 0x00000004;
        private const uint OpenExisting = 3;
        private const uint FsctlQueryUsnJournal = 0x000900F4;

        public bool TryGetCheckpoint(
            string rootPath,
            out LibraryChangeJournalCheckpoint checkpoint,
            out string error)
        {
            checkpoint = new LibraryChangeJournalCheckpoint("", "", 0, 0, 0);
            error = "";
            if (!OperatingSystem.IsWindows())
            {
                error = "USN change journals are available only on Windows.";
                return false;
            }

            try
            {
                string root = Path.GetPathRoot(Path.GetFullPath(rootPath)) ?? "";
                if (root.StartsWith(@"\\", StringComparison.Ordinal) || root.Length < 2 || root[1] != ':')
                {
                    error = "USN acceleration is not used for network or non-drive paths.";
                    return false;
                }

                var drive = new DriveInfo(root);
                if (drive.DriveType == DriveType.Network)
                {
                    error = "USN acceleration is not used for mapped network drives.";
                    return false;
                }
                string fileSystem = drive.DriveFormat;
                // The Win32 contract guarantees this volume-handle operation for NTFS.
                // ReFS remains on the authoritative traversal until a record-reading
                // implementation can validate its 128-bit file identity semantics.
                if (!string.Equals(fileSystem, "NTFS", StringComparison.OrdinalIgnoreCase))
                {
                    error = $"USN acceleration is not enabled for {fileSystem}.";
                    return false;
                }

                string volumeIdentity = root.TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant();
                using SafeFileHandle handle = CreateFile(
                    $@"\\.\{volumeIdentity}",
                    0,
                    ShareRead | ShareWrite | ShareDelete,
                    IntPtr.Zero,
                    OpenExisting,
                    0,
                    IntPtr.Zero);
                if (handle.IsInvalid)
                {
                    error = $"The volume journal could not be opened (Win32 {Marshal.GetLastWin32Error()}).";
                    return false;
                }

                if (!DeviceIoControl(
                        handle,
                        FsctlQueryUsnJournal,
                        IntPtr.Zero,
                        0,
                        out UsnJournalData data,
                        Marshal.SizeOf<UsnJournalData>(),
                        out _,
                        IntPtr.Zero))
                {
                    error = $"The volume journal could not be queried (Win32 {Marshal.GetLastWin32Error()}).";
                    return false;
                }

                checkpoint = new LibraryChangeJournalCheckpoint(
                    volumeIdentity,
                    fileSystem,
                    unchecked((long)data.UsnJournalId),
                    data.NextUsn,
                    data.LowestValidUsn);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct UsnJournalData
        {
            public ulong UsnJournalId;
            public long FirstUsn;
            public long NextUsn;
            public long LowestValidUsn;
            public long MaxUsn;
            public ulong MaximumSize;
            public ulong AllocationDelta;
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
            out UsnJournalData outputBuffer,
            int outputBufferSize,
            out int bytesReturned,
            IntPtr overlapped);
    }
}
