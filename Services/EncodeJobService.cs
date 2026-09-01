using System.Text.Json;
using MediaFlux.Models;

namespace MediaFlux.Services;

public sealed class EncodeJobService
{
    private readonly string _path;
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true };
    public EncodeJobService(string path) => _path = path;

    public List<EncodeJob> Load()
    {
        try
        {
            if (!File.Exists(_path)) return new();
            string json = File.ReadAllText(_path);
            List<EncodeJob> jobs = JsonSerializer.Deserialize<List<EncodeJob>>(json, _json) ?? new();
            foreach (EncodeJob job in jobs)
            {
                job.Settings.Restoration ??= new VideoRestorationSettings();
                if (!json.Contains("\"Mode\"", StringComparison.OrdinalIgnoreCase) &&
                    job.Settings.Restoration.Preset != VideoRestorationPreset.Off)
                    job.Settings.Restoration.Mode = VideoRestorationMode.Custom;
            }
            return jobs;
        }
        catch (Exception ex)
        {
            ErrorLogService.Append(AppPaths.UserDataDirectory, "Load encode jobs failed", _path, ex);
            return new();
        }
    }

    public void Save(IEnumerable<EncodeJob> jobs)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        string temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(jobs, _json));
        File.Move(temporary, _path, overwrite: true); // same-volume atomic replace
    }

    public static IReadOnlyList<EncodeJob> Due(IEnumerable<EncodeJob> jobs, DateTime localNow) =>
        jobs.Where(job => job.Enabled && job.ScheduleType == EncodeJobScheduleType.Once &&
                          job.ScheduledLocalTime <= localNow && job.Status == EncodeJobStatus.Scheduled)
            .OrderBy(job => job.ScheduledLocalTime).ToArray();

    public static IReadOnlyList<T> SelectScope<T>(IEnumerable<T> orderedEligible, IEnumerable<T> selected, bool selectedOnly)
    {
        var all = orderedEligible.ToArray();
        if (!selectedOnly) return all;
        var selectedSet = new HashSet<T>(selected);
        return all.Where(selectedSet.Contains).ToArray();
    }

    public static (IReadOnlyList<EncodeJobFile> Available, IReadOnlyList<EncodeJobFile> Missing) SplitAvailableFiles(IEnumerable<EncodeJobFile> files)
    {
        var available = new List<EncodeJobFile>(); var missing = new List<EncodeJobFile>();
        foreach (var file in files) (File.Exists(file.SourcePath) ? available : missing).Add(file);
        return (available, missing);
    }

    public static void RefreshStatus(EncodeJob job)
    {
        if (!job.Enabled) job.Status = EncodeJobStatus.Disabled;
        else if (job.ScheduleType == EncodeJobScheduleType.Once && job.Status is EncodeJobStatus.Ready or EncodeJobStatus.Scheduled)
            job.Status = EncodeJobStatus.Scheduled;
        else if (job.Status == EncodeJobStatus.Disabled) job.Status = EncodeJobStatus.Ready;
    }

    public static EncodeJob? FindById(IEnumerable<EncodeJob> jobs, Guid jobId) =>
        jobs.FirstOrDefault(job => job.Id == jobId);

    public static EncodeJob CreateExecutionSnapshot(EncodeJob job) => new()
    {
        Id = job.Id,
        Name = job.Name,
        Files = job.Files.Select(file => new EncodeJobFile
        {
            SourcePath = file.SourcePath,
            CustomCompressionProfile = file.CustomCompressionProfile,
            CustomTargetMb = file.CustomTargetMb
        }).ToList(),
        Settings = job.Settings.Clone(),
        ScheduleType = job.ScheduleType,
        ScheduledLocalTime = job.ScheduledLocalTime,
        Enabled = job.Enabled,
        CreatedUtc = job.CreatedUtc,
        ModifiedUtc = job.ModifiedUtc,
        Status = job.Status,
        LastRunUtc = job.LastRunUtc,
        LastResult = job.LastResult,
        EstimatedOutputBytes = job.EstimatedOutputBytes,
        EstimatedSavingsBytes = job.EstimatedSavingsBytes
    };
}
