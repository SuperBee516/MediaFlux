using Microsoft.Win32;
using System.Runtime.InteropServices;

namespace MediaFlux.Services
{
    internal enum ExplorerIntegrationStatus
    {
        Disabled,
        Partial,
        Enabled
    }

    internal static class ExplorerContextMenuService
    {
        private const string LegacyFileKey = @"Software\Classes\SystemFileAssociations\video\shell\Encode.AddToQueue";
        private const string FolderKey = @"Software\Classes\Directory\shell\Encode.AddToQueue";
        private const string DuplicateFolderKey = @"Software\Classes\Directory\shell\Encode.CheckDuplicates";
        private const string StateKey = @"Software\Encode\ExplorerIntegration";
        private const string RegisteredExtensionsValue = "RegisteredVideoExtensions";

        public static bool IsFileMenuInstalled
        {
            get
            {
                var extensions = GetRegisteredExtensions();
                return extensions.Length > 0 && extensions.All(extension =>
                    KeyPointsToCurrentExecutable(GetFileKey(extension), "--enqueue-file"));
            }
        }
        public static bool IsFolderQueueMenuInstalled =>
            KeyPointsToCurrentExecutable(FolderKey, "--enqueue-folder");
        public static bool IsDuplicateFolderMenuInstalled =>
            KeyPointsToCurrentExecutable(DuplicateFolderKey, "--check-duplicates-folder");
        public static bool IsAnyFolderMenuInstalled =>
            IsFolderQueueMenuInstalled || IsDuplicateFolderMenuInstalled;
        public static bool HasAnyFolderMenuRegistration =>
            KeyExists(FolderKey) || KeyExists(DuplicateFolderKey);
        public static bool IsFolderMenuInstalled =>
            IsFolderQueueMenuInstalled && IsDuplicateFolderMenuInstalled;

        public static ExplorerIntegrationStatus GetStatus(bool expectFiles, bool expectFolders)
        {
            bool files = IsFileMenuInstalled;
            bool enqueueFolders = IsFolderQueueMenuInstalled;
            bool duplicateFolders = IsDuplicateFolderMenuInstalled;
            bool folders = enqueueFolders && duplicateFolders;
            bool anyExpected = expectFiles || expectFolders;
            bool allExpected = (!expectFiles || files) && (!expectFolders || folders);
            bool anyInstalled = files || enqueueFolders || duplicateFolders;

            if (anyExpected && allExpected)
                return ExplorerIntegrationStatus.Enabled;
            return anyInstalled ? ExplorerIntegrationStatus.Partial : ExplorerIntegrationStatus.Disabled;
        }

        public static void Apply(bool filesEnabled, bool foldersEnabled, IEnumerable<string> videoExtensions)
        {
            var requestedExtensions = NormalizeExtensions(videoExtensions);
            var previouslyRegistered = GetRegisteredExtensions();

            DeleteKey(LegacyFileKey);
            foreach (string extension in previouslyRegistered.Except(requestedExtensions, StringComparer.OrdinalIgnoreCase))
                DeleteKey(GetFileKey(extension));

            if (filesEnabled)
            {
                if (requestedExtensions.Length == 0)
                    throw new InvalidOperationException("At least one enabled video extension is required for the file context menu.");

                foreach (string extension in requestedExtensions)
                    SetVerb(GetFileKey(extension), true, "Add to Encode Queue", "--enqueue-file");
                SaveRegisteredExtensions(requestedExtensions);
            }
            else
            {
                foreach (string extension in previouslyRegistered)
                    DeleteKey(GetFileKey(extension));
                SaveRegisteredExtensions(Array.Empty<string>());
            }

            SetVerb(FolderKey, foldersEnabled, "Add folder to MediaFlux Encode Queue", "--enqueue-folder");
            SetVerb(DuplicateFolderKey, foldersEnabled, "Check folder for duplicates in MediaFlux", "--check-duplicates-folder");
            NotifyShell();
        }

        public static void Remove()
        {
            DeleteKey(LegacyFileKey);
            foreach (string extension in GetRegisteredExtensions())
                DeleteKey(GetFileKey(extension));
            SaveRegisteredExtensions(Array.Empty<string>());
            DeleteKey(FolderKey);
            DeleteKey(DuplicateFolderKey);
            NotifyShell();
        }

        private static string GetFileKey(string extension) =>
            $@"Software\Classes\SystemFileAssociations\{extension}\shell\Encode.AddToQueue";

        private static string[] NormalizeExtensions(IEnumerable<string> extensions) =>
            extensions
                .Where(extension => !string.IsNullOrWhiteSpace(extension))
                .Select(extension => extension.Trim())
                .Select(extension => extension.StartsWith('.') ? extension : "." + extension)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(extension => extension, StringComparer.OrdinalIgnoreCase)
                .ToArray();

        private static string[] GetRegisteredExtensions()
        {
            using var key = Registry.CurrentUser.OpenSubKey(StateKey);
            return key?.GetValue(RegisteredExtensionsValue) switch
            {
                string[] values => NormalizeExtensions(values),
                string value when !string.IsNullOrWhiteSpace(value) => NormalizeExtensions(value.Split(';')),
                _ => Array.Empty<string>()
            };
        }

        private static void SaveRegisteredExtensions(string[] extensions)
        {
            using var key = Registry.CurrentUser.CreateSubKey(StateKey, writable: true)
                ?? throw new InvalidOperationException("Windows could not save the Explorer integration state.");
            key.SetValue(RegisteredExtensionsValue, extensions, RegistryValueKind.MultiString);
        }

        private static void SetVerb(string keyPath, bool enabled, string label, string argument)
        {
            if (!enabled)
            {
                DeleteKey(keyPath);
                return;
            }

            using var key = Registry.CurrentUser.CreateSubKey(keyPath, writable: true)
                ?? throw new InvalidOperationException("Windows could not create the Explorer context-menu registration.");
            key.SetValue(null, label);
            key.SetValue("Icon", $"\"{AppPaths.LauncherExecutablePath}\"");
            key.SetValue("MultiSelectModel", "Player");

            using var command = key.CreateSubKey("command", writable: true)
                ?? throw new InvalidOperationException("Windows could not create the Explorer context-menu command.");
            command.SetValue(null, $"\"{AppPaths.LauncherExecutablePath}\" {argument} \"%1\"");
        }

        private static bool KeyPointsToCurrentExecutable(string keyPath, string argument)
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyPath + @"\command");
            string? command = key?.GetValue(null) as string;
            return command != null &&
                   command.Contains(AppPaths.LauncherExecutablePath, StringComparison.OrdinalIgnoreCase) &&
                   command.Contains(argument, StringComparison.OrdinalIgnoreCase);
        }

        private static bool KeyExists(string keyPath)
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyPath);
            return key != null;
        }

        private static void DeleteKey(string keyPath)
        {
            try
            {
                Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
            }
            catch (ArgumentException)
            {
                // Already absent.
            }
        }

        private static void NotifyShell()
        {
            SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero); // SHCNE_ASSOCCHANGED
        }

        [DllImport("shell32.dll")]
        private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
    }
}
