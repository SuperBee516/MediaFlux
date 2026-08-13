using System.Text.Json;
using Microsoft.VisualBasic.FileIO;

namespace MediaFlux.Services.LibraryCatalog;

public sealed record LibraryGeneralFileSnapshot(
    long FileId, string FullPath, string LocationPath, long SizeBytes, DateTime LastWriteUtc,
    string VolumeId, string FileIdentity, IndexedFileAvailability Availability, bool IsProtected,
    DateTime? CreationUtc = null);

public sealed record LibraryGeneralFileRemovalExclusion(long FileId, string FullPath, string Reason);

public sealed record LibraryGeneralFileRemovalPreview(
    DuplicateCleanupAction Action,
    IReadOnlyList<LibraryGeneralFileSnapshot> Eligible,
    IReadOnlyList<LibraryGeneralFileRemovalExclusion> Excluded,
    long SelectedBytes,
    long ExpectedReclaimableBytes,
    IReadOnlyList<string> AffectedLocations)
{
    public int ProtectedExcluded => Excluded.Count(item => item.Reason == LibraryGeneralFileRemovalService.ProtectedReason);
    public int UnavailableExcluded => Excluded.Count(item => item.Reason == LibraryGeneralFileRemovalService.UnavailableReason);
}

public sealed record LibraryGeneralFileRemovalItemResult(
    long FileId, string FullPath, DuplicateCleanupItemStatus Status, string DestinationPath, string Message);

public sealed record LibraryGeneralFileRemovalResult(
    int Succeeded, int Excluded, int Failed, long ReclaimedBytes, bool Cancelled,
    IReadOnlyList<LibraryGeneralFileRemovalItemResult> Items, IReadOnlyList<string> LocationsRequiringRescan);

public interface ILibraryGeneralFileActions
{
    void Recycle(string path);
    void DeletePermanent(string path);
    string Quarantine(string path, string quarantineRoot, long fileId);
}

internal sealed class WindowsLibraryGeneralFileActions : ILibraryGeneralFileActions
{
    public void Recycle(string path) => FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs,
        RecycleOption.SendToRecycleBin, UICancelOption.ThrowException);

    public void DeletePermanent(string path) => File.Delete(path);

    public string Quarantine(string path, string quarantineRoot, long fileId)
    {
        if (string.IsNullOrWhiteSpace(quarantineRoot)) throw new InvalidOperationException("A quarantine folder is not configured.");
        string root = Path.GetFullPath(quarantineRoot);
        Directory.CreateDirectory(root);
        string destination = Path.Combine(root, $"{fileId}-{Path.GetFileName(path)}");
        if (File.Exists(destination)) destination = Path.Combine(root, $"{fileId}-{Guid.NewGuid():N}-{Path.GetFileName(path)}");
        File.Move(path, destination);
        return destination;
    }
}

public interface ILibraryGeneralFileRemovalAudit
{
    void Append(LibraryGeneralFileRemovalAuditEntry entry);
}

public sealed record LibraryGeneralFileRemovalAuditEntry(
    DateTime OccurredUtc, string BatchId, long FileId, string SourcePath, string DestinationPath,
    DuplicateCleanupAction Action, DuplicateCleanupItemStatus Status, long SizeBytes, string Message);

public sealed class JsonLibraryGeneralFileRemovalAudit(string path) : ILibraryGeneralFileRemovalAudit
{
    private readonly object _gate = new();
    public void Append(LibraryGeneralFileRemovalAuditEntry entry)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        string line = JsonSerializer.Serialize(entry) + Environment.NewLine;
        lock (_gate) File.AppendAllText(path, line);
    }
}

public sealed class LibraryGeneralFileRemovalService
{
    public const string ProtectedReason = "Protected";
    public const string UnavailableReason = "Unavailable or missing";
    private readonly Func<long, string, LibraryGeneralFileSnapshot?> _resolve;
    private readonly ILibraryGeneralFileActions _actions;
    private readonly ILibraryGeneralFileRemovalAudit _audit;
    private readonly Func<DateTime> _utcNow;

    public LibraryGeneralFileRemovalService(
        Func<long, string, LibraryGeneralFileSnapshot?> resolve,
        ILibraryGeneralFileRemovalAudit audit,
        ILibraryGeneralFileActions? actions = null,
        Func<DateTime>? utcNow = null)
    {
        _resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _actions = actions ?? new WindowsLibraryGeneralFileActions();
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public LibraryGeneralFileRemovalPreview Preview(IEnumerable<(long FileId, string FullPath)> selection, DuplicateCleanupAction action)
    {
        var eligible = new List<LibraryGeneralFileSnapshot>();
        var excluded = new List<LibraryGeneralFileRemovalExclusion>();
        long selectedBytes = 0;
        foreach ((long id, string path) in selection.DistinctBy(item => item.FileId))
        {
            LibraryGeneralFileSnapshot? current = _resolve(id, path);
            if (current != null) selectedBytes += Math.Max(0, current.SizeBytes);
            if (current == null || current.FileId != id || current.Availability != IndexedFileAvailability.Present || !File.Exists(current.FullPath))
                excluded.Add(new(id, path, UnavailableReason));
            else if (current.IsProtected)
                excluded.Add(new(id, current.FullPath, ProtectedReason));
            else
                eligible.Add(current);
        }
        long bytes = eligible.GroupBy(PhysicalKey, StringComparer.OrdinalIgnoreCase).Sum(group => group.First().SizeBytes);
        return new(action, eligible, excluded, selectedBytes, bytes,
            eligible.Select(item => item.LocationPath).Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    public Task<LibraryGeneralFileRemovalResult> ExecuteAsync(
        LibraryGeneralFileRemovalPreview preview, string quarantineRoot = "", CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        string batch = Guid.NewGuid().ToString("N");
        var results = new List<LibraryGeneralFileRemovalItemResult>();
        long reclaimed = 0;
        bool cancelled = false;
        foreach (LibraryGeneralFileSnapshot planned in preview.Eligible)
        {
            if (cancellationToken.IsCancellationRequested) { cancelled = true; break; }
            LibraryGeneralFileSnapshot? current = _resolve(planned.FileId, planned.FullPath);
            string? validation = Validate(planned, current);
            if (validation != null)
            {
                Record(planned, "", DuplicateCleanupItemStatus.Excluded, validation);
                continue;
            }
            try
            {
                string destination = "";
                if (preview.Action == DuplicateCleanupAction.RecycleBin) _actions.Recycle(planned.FullPath);
                else if (preview.Action == DuplicateCleanupAction.Quarantine) destination = _actions.Quarantine(planned.FullPath, quarantineRoot, planned.FileId);
                else _actions.DeletePermanent(planned.FullPath);
                reclaimed += planned.SizeBytes;
                Record(planned, destination, DuplicateCleanupItemStatus.Succeeded, "");
            }
            catch (Exception ex) { Record(planned, "", DuplicateCleanupItemStatus.Failed, ex.Message); }
        }
        return new LibraryGeneralFileRemovalResult(results.Count(item => item.Status == DuplicateCleanupItemStatus.Succeeded),
            results.Count(item => item.Status == DuplicateCleanupItemStatus.Excluded) + preview.Excluded.Count,
            results.Count(item => item.Status == DuplicateCleanupItemStatus.Failed), reclaimed, cancelled, results,
            preview.AffectedLocations);

        void Record(LibraryGeneralFileSnapshot item, string destination, DuplicateCleanupItemStatus status, string message)
        {
            results.Add(new(item.FileId, item.FullPath, status, destination, message));
            try { _audit.Append(new(_utcNow(), batch, item.FileId, item.FullPath, destination, preview.Action, status, item.SizeBytes, message)); }
            catch { /* A failed audit write must not retry or misreport an already-completed file action. */ }
        }
    }, cancellationToken);

    private static string? Validate(LibraryGeneralFileSnapshot planned, LibraryGeneralFileSnapshot? current)
    {
        if (current == null || current.FileId != planned.FileId || current.Availability != IndexedFileAvailability.Present || !File.Exists(planned.FullPath)) return UnavailableReason;
        if (current.IsProtected) return ProtectedReason;
        if (!string.Equals(current.FullPath, planned.FullPath, StringComparison.OrdinalIgnoreCase) || current.SizeBytes != planned.SizeBytes ||
            current.LastWriteUtc != planned.LastWriteUtc || !string.Equals(current.VolumeId, planned.VolumeId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(current.FileIdentity, planned.FileIdentity, StringComparison.OrdinalIgnoreCase)) return "Catalog identity or file state changed";
        return null;
    }

    private static string PhysicalKey(LibraryGeneralFileSnapshot item) =>
        !string.IsNullOrWhiteSpace(item.VolumeId) && !string.IsNullOrWhiteSpace(item.FileIdentity)
            ? item.VolumeId + "|" + item.FileIdentity : item.FullPath;
}
