using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace Encode
{
    internal static class UpdateManager
    {
        /// <summary>
        /// Check if a newer exe exists in the updateFolder. If the user accepts, spawn a
        /// detached updater script that replaces files and relaunches the app. Returns true
        /// if an update was initiated (the app should assume it will exit).
        /// </summary>
        public static bool CheckAndPrompt(Form owner, string updateFolder, string? processNameOverride = null)
        {
            if (string.IsNullOrWhiteSpace(updateFolder) || !Directory.Exists(updateFolder))
            {
                MessageBox.Show(owner, "Please configure a valid update folder in Settings.", "Update Check",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            var localExe = new FileInfo(Application.ExecutablePath);
            var remoteExe = new FileInfo(Path.Combine(updateFolder, localExe.Name));
            if (!remoteExe.Exists)
            {
                MessageBox.Show(owner, "No build found in update folder.", "Update Check",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            }

            if (remoteExe.LastWriteTime <= localExe.LastWriteTime)
            {
                MessageBox.Show(owner, "You are already running the latest build.", "Update Check",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            }

            var prompt = $"New version detected (built {remoteExe.LastWriteTime:G}). Replace current build?";
            if (MessageBox.Show(owner, prompt, "Update Available", MessageBoxButtons.YesNo, MessageBoxIcon.Question)
                != DialogResult.Yes)
                return false;

            try
            {
                StartExternalUpdaterAndExit(updateFolder, targetDir: localExe.Directory!.FullName,
                    exeName: localExe.Name, processName: processNameOverride ?? Path.GetFileName(localExe.Name));
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(owner, "Failed to start updater:\r\n" + ex.Message,
                    "Update", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
        }

        /// <summary>
        /// Creates a temporary folder, copies the update payload there, writes a .cmd that:
        /// 1) waits for the app to exit, 2) backs up the old exe, 3) copies all new files, 4) relaunches the app.
        /// Then runs the .cmd in a detached cmd.exe and exits the current process.
        /// </summary>
        private static void StartExternalUpdaterAndExit(
    string updateSourceDir,
    string targetDir,
    string exeName,
    string processName)
        {
            var tempBase = Path.Combine(
                Path.GetTempPath(),
                "Encode_Update_" + Guid.NewGuid().ToString("N"));

            var stageDir = Path.Combine(tempBase, "payload");
            Directory.CreateDirectory(stageDir);

            // Stage the new build
            CopyDirectory(updateSourceDir, stageDir);

            // Build the .cmd script
            var scriptPath = Path.Combine(tempBase, "run_update.cmd");
            File.WriteAllText(
                scriptPath,
                BuildBatch(processName, stageDir, targetDir, exeName),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"\"{scriptPath}\"\"",   // IMPORTANT: /c, not /k
                UseShellExecute = false,
                CreateNoWindow = true,                   // hide console
                WindowStyle = ProcessWindowStyle.Hidden  // belt + suspenders
            };

            Process.Start(psi);

            // Kill the current process; the batch script will copy and relaunch
            Environment.Exit(0);
        }


        private static string BuildBatch(string processName, string src, string dest, string exeName)
        {
            return
        $@"@echo off
setlocal enableextensions

rem Values passed from C#
set ""SRC={src}""
set ""DEST={dest}""
set ""EXE_NAME={exeName}""
set ""PROC={processName}""
set ""LOG=%TEMP%\Encode_update.log""

echo ==== UPDATE START %DATE% %TIME% ==== > ""%LOG%""
echo SRC=%SRC% >> ""%LOG%""
echo DEST=%DEST% >> ""%LOG%""
echo EXE_NAME=%EXE_NAME% >> ""%LOG%""
echo PROC=%PROC% >> ""%LOG%""

rem --- 1) Wait for the running app to exit ---
:WAIT_APP
tasklist /FI ""IMAGENAME eq %PROC%"" | find /I ""%PROC%"" >nul
if %ERRORLEVEL%==0 (
    echo Waiting for %PROC% to exit... >> ""%LOG%""
    timeout /t 1 /nobreak >nul
    goto WAIT_APP
)

rem --- 2) Copy updated files from SRC (staging) to DEST (install dir) ---
echo Copying files from %SRC% to %DEST% >> ""%LOG%""

robocopy ""%SRC%"" ""%DEST%"" /E /R:3 /W:1 /NFL /NDL /NP /NS /NC >> ""%LOG%"" 2>&1
set RC=%ERRORLEVEL%
echo ROBOCOPY RC=%RC% >> ""%LOG%""
if %RC% GEQ 8 (
    echo ERROR: robocopy failed with code %RC% >> ""%LOG%""
    goto END
)

rem --- 3) Relaunch the app from DEST ---
echo Relaunching ""%DEST%\%EXE_NAME%"" >> ""%LOG%""

if exist ""%DEST%\%EXE_NAME%"" (
    pushd ""%DEST%""
    start "" ""%EXE_NAME%""
    popd
) else (
    echo ERROR: ""%DEST%\%EXE_NAME%"" not found, not relaunching >> ""%LOG%""
)

:END
echo ==== UPDATE END %DATE% %TIME% ==== >> ""%LOG%""
endlocal
exit /b 0
";
        }

        private static void CopyDirectory(string sourceDir, string destDir)
        {
            foreach (var dir in Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories))
            {
                Directory.CreateDirectory(dir.Replace(sourceDir, destDir));
            }
            foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                var target = file.Replace(sourceDir, destDir);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, overwrite: true);
            }
        }
    }
}
