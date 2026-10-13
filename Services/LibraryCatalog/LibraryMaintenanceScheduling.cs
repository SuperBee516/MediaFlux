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
        if (isStartup && profile.MissedRun == LibraryMaintenanceMissedRun.RunOnNextStartup) return true;
        zone ??= TimeZoneInfo.Local;
        DateTime local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc), zone);
        if (profile.MissedRun == LibraryMaintenanceMissedRun.Skip) return true; // Persist the missed/skipped occurrence even after its window.
        if (!IsWithinWindow(local, profile.StartTime, profile.EndTime))
        {
            bool windowClosed=profile.StartTime<profile.EndTime
                ? local.TimeOfDay>=profile.EndTime
                : profile.StartTime>profile.EndTime&&local.TimeOfDay>=profile.EndTime&&local.TimeOfDay<profile.StartTime;
            return windowClosed; // Let the coordinator record a deferred missed-window outcome.
        }
        return profile.MissedRun switch
        {
            _ => true
        };
    }

    private static bool Includes(LibraryMaintenanceDays days, DayOfWeek day) => (days & (LibraryMaintenanceDays)(1 << (int)day)) != 0;

    private static DateTime ToUtcSafely(DateTime local, TimeZoneInfo zone)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        // Shift a missing wall time by the actual DST gap (including non-hour changes).
        if (zone.IsInvalidTime(local))
        {
            DateTime before=local,after=local;
            for(int i=0;i<180&&zone.IsInvalidTime(before);i++)before=before.AddMinutes(-1);
            for(int i=0;i<180&&zone.IsInvalidTime(after);i++)after=after.AddMinutes(1);
            if(!zone.IsInvalidTime(before)&&!zone.IsInvalidTime(after))
            {
                TimeSpan gap=zone.GetUtcOffset(after)-zone.GetUtcOffset(before);
                local=gap>TimeSpan.Zero?local+gap:after;
            }
            else local=after;
        }
        // A repeated wall-clock time is one occurrence. Choose its first real instant.
        if (zone.IsAmbiguousTime(local)) return new DateTimeOffset(local, zone.GetAmbiguousTimeOffsets(local).Max()).UtcDateTime;
        return TimeZoneInfo.ConvertTimeToUtc(local, zone);
    }
}
