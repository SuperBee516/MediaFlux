using System.Text.Json;

namespace Encode.Models
{
    public class Config
    {
        public string UpdateFolderPath { get; set; } = "";
        public string AutoNamingPattern { get; set; } = "clip_%TITLE%_%START%-%END%";

        // Filename suffixes
        public string OutputSuffix { get; set; } = "_2";
        public bool EnableOutputSuffix { get; set; } = false;

        // Persist column visibility
        public bool ShowSizeColumn { get; set; } = true;
        public bool ShowCreatedColumn { get; set; } = false;

        // Persist delete–source setting
        public bool DeleteSourceAfterCompression { get; set; } = true;

        // Persist the last widths the user set
        public int NameColumnWidth { get; set; } = 0;
        public int SizeColumnWidth { get; set; } = 0;
        public int CreatedColumnWidth { get; set; } = 0;

        // Keep history of up to N folders
        public List<string> LastInputFolders { get; set; } = new();
        public List<string> LastOutputFolders { get; set; } = new();
        public int FolderHistoryLimit { get; set; } = 5;

        public bool WarnOnDuplicate { get; set; } = true;
        public bool AutoFetchMetadata { get; set; } = true;
        public string ExternalPlayerPath { get; set; } = ""; // e.g. "C:\\Program Files\\VideoLAN\\VLC\\vlc.exe"

        // lightweight history to help duplicate detection (keep it short)
        public List<string> DownloadHistory { get; set; } = new List<string>(); // store normalized output paths

        public static Config Load(string path)
        {
            if (!File.Exists(path))
                return new Config();
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<Config>(json)!;
        }

        // Models/Config.cs  (add inside class)
        public bool RememberCheckboxStates { get; set; } = false;

        // Per-checkbox “last used” values:
        public bool LastChkAutoTargetSize { get; set; } = false;
        public bool LastChkDeleteSource { get; set; } = true;
        public bool LastChkFilterX264 { get; set; } = true;
        public bool LastChkFilterX265 { get; set; } = true;
        public bool LastChkDownloadPlaylist { get; set; } = false;
        public bool LastChkProcessAll { get; set; } = true;  // NEW: default to checked

        // Persist which codecs to show
        public bool ShowX264Files { get; set; } = true;
        public bool ShowX265Files { get; set; } = true;

        public void Save(string path)
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(this, options);
            File.WriteAllText(path, json);
        }
    }
}
