using System.Globalization;
using System.IO;
using System.Drawing;

namespace MediaFlux.Services;

public enum JobHistoryDateFilter { All, Today, Last7Days, Last30Days }

public static class JobHistoryPresentation
{
    public static Rectangle RestoreWindowBounds(int x, int y, int width, int height, Size minimumSize, Size defaultSize, IEnumerable<Rectangle> workingAreas)
    {
        int safeWidth = Math.Clamp(width > 0 ? width : defaultSize.Width, minimumSize.Width, 3000);
        int safeHeight = Math.Clamp(height > 0 ? height : defaultSize.Height, minimumSize.Height, 2000);
        Rectangle[] areas = workingAreas.Where(area => area.Width > 0 && area.Height > 0).ToArray();
        if (areas.Length == 0) areas = new[] { new Rectangle(0, 0, 1920, 1080) };
        Rectangle saved = new(x, y, safeWidth, safeHeight);
        Rectangle[] matchingAreas = areas.Where(area => area.IntersectsWith(saved) && Rectangle.Intersect(area, saved).Width >= Math.Min(80, saved.Width) && Rectangle.Intersect(area, saved).Height >= Math.Min(80, saved.Height)).ToArray();
        if (matchingAreas.Length > 0)
        {
            Rectangle area = matchingAreas[0];
            int clampedX = Math.Clamp(saved.X, area.Left, Math.Max(area.Left, area.Right - saved.Width));
            int clampedY = Math.Clamp(saved.Y, area.Top, Math.Max(area.Top, area.Bottom - saved.Height));
            return new Rectangle(clampedX, clampedY, saved.Width, saved.Height);
        }
        Rectangle fallback = areas[0];
        return new Rectangle(fallback.Left + Math.Max(0, (fallback.Width - safeWidth) / 2), fallback.Top + Math.Max(0, (fallback.Height - safeHeight) / 2), safeWidth, safeHeight);
    }

    public static IReadOnlyList<JobHistoryRecord> Filter(
        IEnumerable<JobHistoryRecord> records, string? search, string? status, string? type, JobHistoryDateFilter date, DateTime? now = null, TimeZoneInfo? timeZone = null)
    {
        DateTime localNow = ToDisplayTime(now ?? DateTime.Now, timeZone, timeZone is not null);
        string query = (search ?? "").Trim();
        return records.Where(record =>
            (string.IsNullOrEmpty(status) || status.Equals("All", StringComparison.OrdinalIgnoreCase) || StatusLabel(record.Status).Equals(status, StringComparison.OrdinalIgnoreCase) || record.Status.ToString().Equals(status, StringComparison.OrdinalIgnoreCase)) &&
            (string.IsNullOrEmpty(type) || type.Equals("All", StringComparison.OrdinalIgnoreCase) || FormatType(record.Type).Equals(type, StringComparison.OrdinalIgnoreCase)) &&
            MatchesDate(ToDisplayTime(record.EndUtc, timeZone, timeZone is not null), date, localNow) &&
            (string.IsNullOrEmpty(query) || SearchText(record).Contains(query, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(record => record.EndUtc)
            .ToArray();
    }

    public static (int Total, int Successful, int Failed, int Canceled) Counts(IEnumerable<JobHistoryRecord> records)
    {
        var list = records.ToArray();
        return (list.Length, list.Count(x => x.Status == JobStatus.Success), list.Count(x => x.Status == JobStatus.Failed), list.Count(x => x.Status == JobStatus.Canceled));
    }

    public static string FormatType(JobType type) => type switch
    {
        JobType.DvdEncode => "DVD Encode",
        JobType.DvdRemux => "DVD Remux",
        JobType.Remux => "Remux",
        _ => type.ToString()
    };

    public static string StatusLabel(JobStatus status) => status switch
    {
        JobStatus.Success => "Successful",
        _ => status.ToString()
    };

    public static string FileNameOrUnavailable(string? path) => string.IsNullOrWhiteSpace(path) ? "Unavailable" : Path.GetFileName(path);

    public static string FormatFinished(DateTime endUtc, DateTime? now = null, TimeZoneInfo? timeZone = null)
    {
        DateTime value = ToDisplayTime(endUtc, timeZone, true);
        DateTime localNow = ToDisplayTime(now ?? DateTime.Now, timeZone, timeZone is not null);
        if (value.Date == localNow.Date) return $"Today {value:h:mm tt}";
        if (value.Date == localNow.Date.AddDays(-1)) return $"Yesterday {value:h:mm tt}";
        return value.Year == localNow.Year ? value.ToString("MMM d, h:mm tt", CultureInfo.CurrentCulture) : value.ToString("MMM d, yyyy, h:mm tt", CultureInfo.CurrentCulture);
    }

    public static string SearchText(JobHistoryRecord record) => string.Join(" ", record.SourcePath, record.OutputPath, record.Status, FormatType(record.Type), record.Notes, record.ErrorSummary, record.FinalizationOutcome);

    private static DateTime ToDisplayTime(DateTime value, TimeZoneInfo? timeZone, bool treatAsUtc)
    {
        if (timeZone is null) return treatAsUtc ? value.ToLocalTime() : value;
        return TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(value, DateTimeKind.Utc), timeZone);
    }

    private static bool MatchesDate(DateTime value, JobHistoryDateFilter filter, DateTime now) => filter switch
    {
        JobHistoryDateFilter.Today => value.Date == now.Date,
        JobHistoryDateFilter.Last7Days => value >= now.Date.AddDays(-6),
        JobHistoryDateFilter.Last30Days => value >= now.Date.AddDays(-29),
        _ => true
    };
}
