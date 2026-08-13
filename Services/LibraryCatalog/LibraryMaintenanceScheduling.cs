namespace MediaFlux.Services.LibraryCatalog;

public static class LibraryMaintenanceScheduleCalculator
{
    public static bool IsWithinWindow(DateTime local, TimeSpan start, TimeSpan end)
    {
        TimeSpan time = local.TimeOfDay;
        if (start == end) return true;
        return start < end ? time >= start && time < end : time >= start || time < end;
    }

    public static DateTime? GetNextRunUtc(LibraryMaintenanceProfile profile, DateTime utcNow, TimeZoneInfo? zone = null)
    {
        if (!profile.Enabled || profile.Cadence is LibraryMaintenanceCadence.ManualOnly or LibraryMaintenanceCadence.OnStartup) return null;
        zone ??= TimeZoneInfo.Local;
        DateTime localNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc), zone);
        DateTime candidate = localNow.Date + profile.StartTime;
        for (int offset = 0; offset <= 8; offset++)
        {
            DateTime day = candidate.AddDays(offset);
            bool allowed = profile.Cadence == LibraryMaintenanceCadence.Daily || Includes(profile.Days, day.DayOfWeek);
            if (!allowed || day <= localNow) continue;
            return ToUtcSafely(day, zone);
        }
        return null;
    }

    public static DateTime? GetMostRecentOccurrenceUtc(LibraryMaintenanceProfile profile, DateTime utcNow, TimeZoneInfo? zone = null)
    {
        if (!profile.Enabled || profile.Cadence is LibraryMaintenanceCadence.ManualOnly or LibraryMaintenanceCadence.OnStartup) return null;
        zone ??= TimeZoneInfo.Local;
        DateTime localNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc), zone);
        for (int offset = 0; offset <= 7; offset++)
        {
            DateTime candidate = localNow.Date.AddDays(-offset) + profile.StartTime;
            bool allowed = profile.Cadence == LibraryMaintenanceCadence.Daily || Includes(profile.Days, candidate.DayOfWeek);
            if (allowed && candidate <= localNow) return ToUtcSafely(candidate, zone);
        }
        return null;
    }

    public static bool IsDue(LibraryMaintenanceProfile profile, DateTime utcNow, bool isStartup, TimeZoneInfo? zone = null)
    {
        if (!profile.Enabled) return false;
        if (profile.Cadence == LibraryMaintenanceCadence.OnStartup) return isStartup;
        DateTime? occurrence = GetMostRecentOccurrenceUtc(profile, utcNow, zone);
        if (!occurrence.HasValue || profile.LastScheduledUtc >= occurrence) return false;
        zone ??= TimeZoneInfo.Local;
        DateTime local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc), zone);
        if (!IsWithinWindow(local, profile.StartTime, profile.EndTime)) return false;
        return profile.MissedRun switch
        {
            LibraryMaintenanceMissedRun.Skip => utcNow - occurrence.Value <= TimeSpan.FromMinutes(2),
            LibraryMaintenanceMissedRun.RunOnNextStartup => isStartup,
            _ => true
        };
    }

    private static bool Includes(LibraryMaintenanceDays days, DayOfWeek day) => (days & (LibraryMaintenanceDays)(1 << (int)day)) != 0;
    private static DateTime ToUtcSafely(DateTime local, TimeZoneInfo zone)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local)) local = local.AddHours(1);
        if (zone.IsAmbiguousTime(local)) return new DateTimeOffset(local, zone.GetAmbiguousTimeOffsets(local).Min()).UtcDateTime;
        return TimeZoneInfo.ConvertTimeToUtc(local, zone);
    }
}
