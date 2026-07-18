using System.Text.Json;
using System.Text.Json.Serialization;

namespace MediaFlux.Services
{
    public enum JobType { Encode, Download, Audio }
    public enum JobStatus { Success, Failed, Canceled }

    public sealed class JobHistoryRecord
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public JobType Type { get; set; }
        public JobStatus Status { get; set; }
        public DateTime StartUtc { get; set; }
        public DateTime EndUtc { get; set; }
        public string SourcePath { get; set; } = "";
        public string OutputPath { get; set; } = "";
        public string EncoderMode { get; set; } = "";
        public double? TargetMb { get; set; }
        public double? DurationSec { get; set; }
        public string Log { get; set; } = "";
        public string LogPath { get; set; } = "";
        public string Notes { get; set; } = ""; // error text, etc.
    }

    public sealed class HistoryService
    {
        private readonly string _path;
        private readonly string _jsonlPath;
        private readonly string _logDir;
        private readonly JsonSerializerOptions _opts = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        private readonly JsonSerializerOptions _jsonlOpts = new()
        {
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public HistoryService(string storagePath)
        {
            _path = storagePath;
            var dir = Path.GetDirectoryName(_path)!;
            Directory.CreateDirectory(dir);
            _jsonlPath = Path.Combine(dir, Path.GetFileNameWithoutExtension(_path) + ".jsonl");
            _logDir = Path.Combine(dir, "history-logs");
            Directory.CreateDirectory(_logDir);

            if (!File.Exists(_jsonlPath) && File.Exists(_path))
                WriteJsonl(LoadLegacyJson());
        }

        public List<JobHistoryRecord> LoadAll()
        {
            try
            {
                var list = File.Exists(_jsonlPath)
                    ? LoadJsonl()
                    : LoadLegacyJson();

                foreach (var rec in list)
                    HydrateLog(rec);

                return list.OrderByDescending(x => x.EndUtc).ToList();
            }
            catch { return new(); }
        }

        public void SaveAll(IEnumerable<JobHistoryRecord> records)
        {
            WriteJsonl(records);
        }

        public void Append(JobHistoryRecord rec)
        {
            PersistLog(rec);
            File.AppendAllText(_jsonlPath, JsonSerializer.Serialize(rec, _jsonlOpts) + Environment.NewLine);
        }

        public void Clear()
        {
            foreach (var rec in LoadAll())
                TryDeleteLog(rec);
            SaveAll(Array.Empty<JobHistoryRecord>());
        }

        public void DeleteByIds(IEnumerable<string> ids)
        {
            var set = new HashSet<string>(ids);
            var all = LoadAll().Where(r =>
            {
                bool delete = set.Contains(r.Id);
                if (delete)
                    TryDeleteLog(r);
                return !delete;
            }).ToList();
            SaveAll(all);
        }

        private List<JobHistoryRecord> LoadJsonl()
        {
            var list = new List<JobHistoryRecord>();
            foreach (var line in File.ReadLines(_jsonlPath))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                try
                {
                    var rec = JsonSerializer.Deserialize<JobHistoryRecord>(line, _opts);
                    if (rec != null)
                        list.Add(rec);
                }
                catch
                {
                    // Keep loading the remaining records if one line is damaged.
                }
            }

            if (list.Count > 0)
                return list;

            return LoadConcatenatedJsonRecords(_jsonlPath);
        }

        private List<JobHistoryRecord> LoadLegacyJson()
        {
            if (!File.Exists(_path))
                return new();

            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<List<JobHistoryRecord>>(json, _opts) ?? new();
        }

        private void WriteJsonl(IEnumerable<JobHistoryRecord> records)
        {
            using var writer = new StreamWriter(_jsonlPath, append: false);
            foreach (var rec in records)
            {
                PersistLog(rec);
                writer.WriteLine(JsonSerializer.Serialize(rec, _jsonlOpts));
            }
        }

        private void PersistLog(JobHistoryRecord rec)
        {
            if (string.IsNullOrEmpty(rec.Id))
                rec.Id = Guid.NewGuid().ToString("N");

            if (string.IsNullOrWhiteSpace(rec.Log))
                return;

            string logPath = Path.Combine(_logDir, rec.Id + ".log");
            File.WriteAllText(logPath, rec.Log);
            rec.LogPath = logPath;
            rec.Log = "";
        }

        private static void HydrateLog(JobHistoryRecord rec)
        {
            if (!string.IsNullOrWhiteSpace(rec.Log))
                return;

            try
            {
                if (!string.IsNullOrWhiteSpace(rec.LogPath) && File.Exists(rec.LogPath))
                    rec.Log = File.ReadAllText(rec.LogPath);
            }
            catch
            {
                rec.Log = "";
            }
        }

        private static void TryDeleteLog(JobHistoryRecord rec)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(rec.LogPath) && File.Exists(rec.LogPath))
                    File.Delete(rec.LogPath);
            }
            catch
            {
                // History deletion should not fail because a log file is locked.
            }
        }

        private List<JobHistoryRecord> LoadConcatenatedJsonRecords(string path)
        {
            try
            {
                var json = File.ReadAllText(path);
                var records = new List<JobHistoryRecord>();

                foreach (var chunk in SplitTopLevelJsonObjects(json))
                {
                    try
                    {
                        var rec = JsonSerializer.Deserialize<JobHistoryRecord>(chunk, _opts);
                        if (rec != null)
                            records.Add(rec);
                    }
                    catch
                    {
                        // Keep scanning the remaining recovered objects.
                    }
                }

                if (records.Count > 0)
                    SaveAll(records);

                return records;
            }
            catch
            {
                return new();
            }
        }

        private static IEnumerable<string> SplitTopLevelJsonObjects(string json)
        {
            var depth = 0;
            var start = -1;
            var inString = false;
            var escaping = false;

            for (var i = 0; i < json.Length; i++)
            {
                var ch = json[i];

                if (inString)
                {
                    if (escaping)
                    {
                        escaping = false;
                    }
                    else if (ch == '\\')
                    {
                        escaping = true;
                    }
                    else if (ch == '"')
                    {
                        inString = false;
                    }

                    continue;
                }

                if (ch == '"')
                {
                    inString = true;
                    continue;
                }

                if (ch == '{')
                {
                    if (depth == 0)
                        start = i;
                    depth++;
                }
                else if (ch == '}' && depth > 0)
                {
                    depth--;
                    if (depth == 0 && start >= 0)
                    {
                        yield return json.Substring(start, i - start + 1);
                        start = -1;
                    }
                }
            }
        }
    }
}
