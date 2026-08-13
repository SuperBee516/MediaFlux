namespace MediaFlux.Services.LibraryCatalog;

[Flags]
public enum LibraryMaintenanceDays { None = 0, Sunday = 1, Monday = 2, Tuesday = 4, Wednesday = 8, Thursday = 16, Friday = 32, Saturday = 64, All = 127 }
public enum LibraryMaintenanceCadence { ManualOnly = 0, Daily = 1, Weekly = 2, OnStartup = 3 }
public enum LibraryMaintenanceMissedRun { Skip = 0, RunOnNextStartup = 1, RunAtNextWindow = 2 }
[Flags]
public enum LibraryMaintenanceActions
{
    None = 0, IncrementalScan = 1, Metadata = 2, ExactDuplicates = 4, VisualDuplicates = 8,
    QuickScrubNew = 16, QuickScrubNeverChecked = 32, QuickScrubStale = 64, QuickScrubFailed = 128,
    Default = IncrementalScan | Metadata | QuickScrubNew | QuickScrubStale
}
public enum LibraryMaintenanceTrigger { Scheduled = 0, Manual = 1, Startup = 2, Recovery = 3 }
public enum LibraryMaintenanceOutcome { Running = 0, Completed = 1, Deferred = 2, Unavailable = 3, Cancelled = 4, Failed = 5, Interrupted = 6 }

public sealed record LibraryMaintenanceProfile(
    long LocationId, int Version, bool Enabled, LibraryMaintenanceCadence Cadence,
    LibraryMaintenanceDays Days, TimeSpan StartTime, TimeSpan EndTime,
    LibraryMaintenanceMissedRun MissedRun, LibraryMaintenanceActions Actions,
    int PeriodicQuickScrubDays, DateTime CreatedUtc, DateTime UpdatedUtc,
    DateTime? LastScheduledUtc = null);

public sealed record LibraryMaintenanceProfileView(
    LibraryMaintenanceProfile Profile, string LocationPath, LibraryLocationAvailability Availability,
    DateTime? LastRunUtc, LibraryMaintenanceOutcome? LastOutcome, string LastStatus, DateTime? NextRunUtc);

public sealed record LibraryMaintenanceRun(
    long Id, long LocationId, LibraryMaintenanceTrigger Trigger, LibraryMaintenanceOutcome Outcome,
    string Stage, DateTime StartedUtc, DateTime? CompletedUtc, long NewFiles, long ChangedFiles,
    long MissingFiles, long MetadataQueued, long ExactProcessed, long VisualProcessed,
    long IntegrityQueued, long WarningCount, string Details);

public sealed record LibraryMaintenanceProgress(long RunId, long LocationId, string Stage, string Details);

public interface ILibraryMaintenanceCatalog
{
    IReadOnlyList<LibraryMaintenanceProfileView> GetMaintenanceProfiles(DateTime utcNow, TimeZoneInfo? timeZone = null);
    LibraryMaintenanceProfile GetMaintenanceProfile(long locationId);
    void SaveMaintenanceProfile(LibraryMaintenanceProfile profile);
    long BeginMaintenanceRun(long locationId, LibraryMaintenanceTrigger trigger, DateTime utcNow);
    void UpdateMaintenanceRunStage(long runId, string stage, string details);
    void RecordMaintenanceCandidates(long runId, IReadOnlyCollection<LibraryInventoryMutation> mutations);
    IReadOnlyList<long> GetMaintenanceCandidateFileIds(long runId, LibraryInventoryChangeKind? kind, int limit = 50_000);
    IReadOnlyList<long> GetMaintenanceCandidateFileIdsPage(long runId, LibraryInventoryChangeKind? kind, long afterFileId, int limit = 1_000);
    IReadOnlyList<LibraryEnrichmentCandidate> GetMaintenanceEnrichmentCandidates(long runId, long afterFileId, int limit = 1_000);
    IReadOnlyList<long> GetMaintenanceIntegrityFileIds(long runId, LibraryMaintenanceProfile profile, DateTime utcNow, int limit = 50_000);
    IReadOnlyList<long> GetMaintenanceIntegrityFileIdsPage(long runId, LibraryMaintenanceProfile profile, DateTime utcNow, long afterFileId, int limit = 1_000);
    void CompleteMaintenanceRun(LibraryMaintenanceRun run);
    IReadOnlyList<LibraryMaintenanceRun> GetMaintenanceHistory(long? locationId = null, int limit = 100);
    int RecoverInterruptedMaintenance(DateTime utcNow);
}
