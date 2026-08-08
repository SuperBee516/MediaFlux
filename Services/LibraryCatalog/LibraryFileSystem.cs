using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MediaFlux.Services.LibraryCatalog
{
    public sealed class LibraryFileSystem : ILibraryFileSystem
    {
        public bool DirectoryExists(string path) => Directory.Exists(path);

        public IEnumerable<LibraryFileSystemEntry> EnumerateFiles(
            string rootPath,
            bool recursive,
            Action<string, Exception> onError,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(onError);
            var pending = new Stack<string>();
            pending.Push(rootPath);

            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string directory = pending.Pop();
                foreach (string filePath in EnumeratePathsSafely(
                             directory,
                             enumerateDirectories: false,
                             onError,
                             cancellationToken))
                {
                    LibraryFileSystemEntry? entry = null;
                    try
                    {
                        var info = new FileInfo(filePath);
                        entry = new LibraryFileSystemEntry(
                            info.FullName,
                            info.Length,
                            info.CreationTimeUtc,
                            info.LastWriteTimeUtc);
                    }
                    catch (Exception ex) when (IsFileSystemException(ex))
                    {
                        onError(filePath, ex);
                    }

                    if (entry != null)
                        yield return entry;
                }

                if (!recursive)
                    continue;

                foreach (string child in EnumeratePathsSafely(
                             directory,
                             enumerateDirectories: true,
                             onError,
                             cancellationToken))
                {
                    try
                    {
                        var info = new DirectoryInfo(child);
                        if ((info.Attributes & FileAttributes.ReparsePoint) == 0)
                            pending.Push(child);
                    }
                    catch (Exception ex) when (IsFileSystemException(ex))
                    {
                        onError(child, ex);
                    }
                }
            }
        }

        private static IEnumerable<string> EnumeratePathsSafely(
            string directory,
            bool enumerateDirectories,
            Action<string, Exception> onError,
            CancellationToken cancellationToken)
        {
            IEnumerator<string>? enumerator = null;
            try
            {
                IEnumerable<string> paths = enumerateDirectories
                    ? Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly)
                    : Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly);
                enumerator = paths.GetEnumerator();
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    bool moved;
                    try
                    {
                        moved = enumerator.MoveNext();
                    }
                    catch (Exception ex) when (IsFileSystemException(ex))
                    {
                        onError(directory, ex);
                        yield break;
                    }
                    if (!moved)
                        yield break;
                    yield return enumerator.Current;
                }
            }
            finally
            {
                enumerator?.Dispose();
            }
        }

        private static bool IsFileSystemException(Exception exception) =>
            exception is IOException or UnauthorizedAccessException or System.Security.SecurityException;
    }

    public sealed class WindowsLibraryFileIdentityProvider : ILibraryFileIdentityProvider
    {
        public LibraryFileIdentity GetIdentity(string path)
        {
            if (!OperatingSystem.IsWindows())
                return LibraryFileIdentity.Empty;

            using SafeFileHandle handle = CreateFile(
                path,
                0,
                FileShare.Read | FileShare.Write | FileShare.Delete,
                IntPtr.Zero,
                3,
                0x02000000,
                IntPtr.Zero);
            if (handle.IsInvalid || !GetFileInformationByHandle(handle, out ByHandleFileInformation info))
                return LibraryFileIdentity.Empty;

            ulong fileIndex = ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
            return new LibraryFileIdentity(
                info.VolumeSerialNumber.ToString("X8"),
                fileIndex.ToString("X16"));
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(
            string fileName,
            uint desiredAccess,
            FileShare shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(
            SafeFileHandle file,
            out ByHandleFileInformation fileInformation);

        [StructLayout(LayoutKind.Sequential)]
        private struct ByHandleFileInformation
        {
            public uint FileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
            public uint VolumeSerialNumber;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint NumberOfLinks;
            public uint FileIndexHigh;
            public uint FileIndexLow;
        }
    }
}
