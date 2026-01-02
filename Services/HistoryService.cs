using System.Text.Json;
using System.Text.Json.Serialization;

namespace Encode.Services
{
    public enum JobType { Encode, Download }
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
        public string Notes { get; set; } = ""; // error text, etc.
    }

    public sealed class HistoryService
    {
        private readonly string _path;
        private readonly JsonSerializerOptions _opts = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public HistoryService(string storagePath)
        {
            _path = storagePath;
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            if (!File.Exists(_path))
                File.WriteAllText(_path, "[]");
        }

        public List<JobHistoryRecord> LoadAll()
        {
            try
            {
                var json = File.ReadAllText(_path);
                var list = JsonSerializer.Deserialize<List<JobHistoryRecord>>(json, _opts) ?? new();
                return list.OrderByDescending(x => x.EndUtc).ToList();
            }
            catch { return new(); }
        }

        public void SaveAll(IEnumerable<JobHistoryRecord> records)
        {
            var json = JsonSerializer.Serialize(records, _opts);
            File.WriteAllText(_path, json);
        }

        public void Append(JobHistoryRecord rec)
        {
            var all = LoadAll();
            all.Add(rec);
            SaveAll(all);
        }

        public void Clear()
        {
            SaveAll(Array.Empty<JobHistoryRecord>());
        }

        public void DeleteByIds(IEnumerable<string> ids)
        {
            var set = new HashSet<string>(ids);
            var all = LoadAll().Where(r => !set.Contains(r.Id)).ToList();
            SaveAll(all);
        }
    }
}
