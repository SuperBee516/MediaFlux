namespace MediaFlux.Services;

public sealed record MediaFluxStorageMigrationResult(bool Succeeded, bool Cancelled, string Message, long CopiedBytes, string? PreviousRoot = null, string? NewRoot = null);

/// <summary>Copies into a private sibling staging folder, verifies every file, then publishes one small location pointer.</summary>
public sealed class MediaFluxStorageMigrationService
{
    private readonly MediaFluxStoragePathService _paths;
    private readonly Func<bool> _hasActiveWork;
    public MediaFluxStorageMigrationService(MediaFluxStoragePathService paths, Func<bool>? hasActiveWork = null) { _paths = paths; _hasActiveWork = hasActiveWork ?? (() => false); }

    public async Task<MediaFluxStorageMigrationResult> MigrateAsync(string destination, CancellationToken token = default)
    {
        if (token.IsCancellationRequested) return new(false, true, "Storage migration was cancelled. The existing root remains active.", 0);
        if (_hasActiveWork()) return new(false, false, "Stop active encoding, AI processing, previews, and scheduled work before moving storage.", 0);
        if (!_paths.TryValidateNewRoot(destination, out string target, out string error)) return new(false, false, error, 0);
        string source = _paths.Root; long required;
        try { required = Measure(source, token); string root = Path.GetPathRoot(target)!; if (new DriveInfo(root).AvailableFreeSpace < required) return new(false, false, "The destination drive does not have enough free space for a verified copy.", 0); }
        catch (Exception ex) { return new(false, false, "Could not inspect the source or destination: " + ex.Message, 0); }
        string staging = target + ".mediaflux-migration-" + Guid.NewGuid().ToString("N");
        try
        {
            await Task.Run(() => Copy(source, staging, token), token).ConfigureAwait(false);
            Verify(source, staging, token);
            token.ThrowIfCancellationRequested();
            Directory.Move(staging, target); // target was verified absent; this prevents a partial destination from becoming live.
            _paths.WriteConfiguredRoot(target); // configuration publication is the final, atomic operation.
            return new(true, false, "Storage was copied and verified. Restart MediaFlux before further work so all open services use the new root.", required, source, target);
        }
        catch (OperationCanceledException) { TryDelete(staging); return new(false, true, "Storage migration was cancelled. The existing root remains active.", 0, source, target); }
        catch (Exception ex) { TryDelete(staging); return new(false, false, "Storage migration failed; the existing root remains active. " + ex.Message, 0, source, target); }
    }
    private static void Copy(string source, string destination, CancellationToken token) { Directory.CreateDirectory(destination); foreach (string dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories)) { token.ThrowIfCancellationRequested(); Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, dir))); } foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)) { token.ThrowIfCancellationRequested(); string target = Path.Combine(destination, Path.GetRelativePath(source, file)); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(file, target, false); } }
    private static void Verify(string source, string destination, CancellationToken token) { foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)) { token.ThrowIfCancellationRequested(); string copy = Path.Combine(destination, Path.GetRelativePath(source, file)); if (!File.Exists(copy) || new FileInfo(file).Length != new FileInfo(copy).Length || !HashesMatch(file, copy, token)) throw new IOException("Copied files did not verify: " + Path.GetFileName(file)); } }
    private static bool HashesMatch(string left, string right, CancellationToken token) { using var a = File.OpenRead(left); using var b = File.OpenRead(right); using var h = System.Security.Cryptography.SHA256.Create(); byte[] ah = h.ComputeHash(a); token.ThrowIfCancellationRequested(); byte[] bh = h.ComputeHash(b); return ah.AsSpan().SequenceEqual(bh); }
    private static long Measure(string root, CancellationToken token) { long bytes = 0; if (!Directory.Exists(root)) return 0; foreach (string f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) { token.ThrowIfCancellationRequested(); bytes = checked(bytes + new FileInfo(f).Length); } return bytes; }
    private static void TryDelete(string path) { try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { } }
}
