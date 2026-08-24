using MediaFlux.Models;

namespace MediaFlux.Services;

internal sealed record TrayStatusInfo(string Status, string? NextRun);

internal static class TrayStatusFormatter
{
    public static TrayStatusInfo Build(int runningEncodeCount, bool encodingActive, IEnumerable<EncodeJob>? jobs, DateTime localNow)
    {
        int active = Math.Max(runningEncodeCount, encodingActive ? 1 : 0);
        if (active > 0)
            return new TrayStatusInfo($"Encoding {active} job{(active == 1 ? "" : "s")}", null);

        var scheduled = (jobs ?? Array.Empty<EncodeJob>())
            .Where(job => job.Enabled && job.ScheduleType == EncodeJobScheduleType.Once &&
                          job.Status == EncodeJobStatus.Scheduled && job.ScheduledLocalTime.HasValue)
            .OrderBy(job => job.ScheduledLocalTime)
            .ToArray();
        var next = scheduled.FirstOrDefault(job => job.ScheduledLocalTime >= localNow);
        if (next != null)
            return new TrayStatusInfo(
                scheduled.Length == 1 ? "1 job scheduled" : $"{scheduled.Length} jobs scheduled",
                $"{next.Name} — {next.ScheduledLocalTime!.Value:g}");

        return new TrayStatusInfo("Ready", null);
    }

    public static string ToNotifyIconText(string status, int maximumLength = 63)
    {
        string value = $"MediaFlux — {status}";
        return value.Length <= maximumLength ? value : value[..Math.Max(0, maximumLength - 1)] + "…";
    }
}
