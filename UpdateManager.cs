using System.Diagnostics;
using System.Reflection;
using System.Windows.Forms;
using MediaFlux.Services;
using Velopack.Exceptions;
using Velopack.Locators;
using Velopack.Sources;

namespace MediaFlux
{
    internal static class UpdateManager
    {
        public const string RepositoryUrl = "https://github.com/SuperBee516/MediaFlux";
        public const string ReleasesUrl = RepositoryUrl + "/releases";

        public static string CurrentVersion
        {
            get
            {
                try
                {
                    var installed = VelopackLocator.Current.CurrentlyInstalledVersion;
                    if (installed != null)
                        return installed.ToString();
                }
                catch
                {
                    // Development and legacy portable builds have no Velopack metadata.
                }

                string? informational = Assembly.GetExecutingAssembly()
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                    .InformationalVersion;
                if (!string.IsNullOrWhiteSpace(informational))
                    return informational.Split('+')[0];

                var version = Assembly.GetExecutingAssembly().GetName().Version;
                return version == null ? "unknown" : $"{version.Major}.{version.Minor}.{version.Build}";
            }
        }

        public static async Task<bool> CheckAndPromptAsync(
            Form owner,
            bool automaticallyBackupBeforeUpdates,
            string backupFolder,
            int backupsToKeep,
            Action<string>? reportStatus = null,
            CancellationToken cancellationToken = default)
        {
            reportStatus?.Invoke("Checking GitHub for updates…");

            try
            {
                var source = new GithubSource(
                    RepositoryUrl,
                    accessToken: null,
                    prerelease: false);
                var manager = new Velopack.UpdateManager(source);

                if (!manager.IsInstalled)
                {
                    reportStatus?.Invoke("This copy is not installed with the MediaFlux installer.");
                    var result = MessageBox.Show(
                        owner,
                        "This copy of MediaFlux is a legacy or development build. Automatic updates become available after MediaFlux is installed with the new installer.\r\n\r\nOpen the GitHub Releases page to download the installer?",
                        "Installer required",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Information);
                    if (result == DialogResult.Yes)
                        OpenReleasesPage();
                    return false;
                }

                var update = await manager.CheckForUpdatesAsync();
                cancellationToken.ThrowIfCancellationRequested();

                if (update == null)
                {
                    reportStatus?.Invoke($"MediaFlux {CurrentVersion} is up to date.");
                    MessageBox.Show(
                        owner,
                        $"You are running the latest stable version of MediaFlux ({CurrentVersion}).",
                        "No updates available",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                    return false;
                }

                string targetVersion = update.TargetFullRelease.Version.ToString();
                using var prompt = new UpdateAvailableForm(
                    CurrentVersion,
                    targetVersion,
                    update.TargetFullRelease.NotesMarkdown);
                if (prompt.ShowDialog(owner) != DialogResult.OK)
                {
                    reportStatus?.Invoke("Update canceled.");
                    return false;
                }

                if (automaticallyBackupBeforeUpdates)
                {
                    reportStatus?.Invoke("Backing up MediaFlux user data…");
                    await Task.Run(
                        () => BackupManager.CreateBackup(
                            AppPaths.UserDataDirectory,
                            backupFolder,
                            backupsToKeep),
                        cancellationToken);
                }

                reportStatus?.Invoke($"Downloading MediaFlux {targetVersion}… 0%");
                IProgress<int> progress = new Progress<int>(percent =>
                    reportStatus?.Invoke($"Downloading MediaFlux {targetVersion}… {percent}%"));
                await manager.DownloadUpdatesAsync(
                    update,
                    percent => progress.Report(percent),
                    cancellationToken);

                reportStatus?.Invoke("Installing update and restarting MediaFlux…");
                manager.ApplyUpdatesAndRestart(update.TargetFullRelease);
                return true;
            }
            catch (OperationCanceledException)
            {
                reportStatus?.Invoke("Update canceled.");
                return false;
            }
            catch (NotInstalledException)
            {
                reportStatus?.Invoke("This copy is not installed with the MediaFlux installer.");
                return false;
            }
            catch (Exception ex)
            {
                reportStatus?.Invoke("Update check failed.");
                ErrorLogService.Append(AppPaths.UserDataDirectory, "GitHub update failed", exception: ex);
                MessageBox.Show(
                    owner,
                    "MediaFlux could not complete the update check.\r\n\r\n" +
                    ex.Message +
                    "\r\n\r\nIf the repository is still private, automatic updates will become available after it is made public.",
                    "Update failed",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return false;
            }
        }

        private static void OpenReleasesPage()
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = ReleasesUrl,
                UseShellExecute = true
            });
        }
    }
}
