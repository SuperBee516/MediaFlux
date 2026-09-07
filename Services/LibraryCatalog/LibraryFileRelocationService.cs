namespace MediaFlux.Services.LibraryCatalog;

public sealed record LibraryFileRelocationPreview(long FileId, long DestinationAnchorFileId, string SourcePath, string DestinationPath);
public sealed record LibraryFileRelocationResult(bool Succeeded, string SourcePath, string DestinationPath, string ErrorMessage, bool RollbackAttempted = false, bool RollbackSucceeded = false);

public interface ILibraryFileRelocationActions
{
    bool FileExists(string path); bool DirectoryExists(string path); bool IsReparsePoint(string path); void Move(string source, string destination); DateTime GetLastWriteTimeUtc(string path);
}

internal sealed class WindowsLibraryFileRelocationActions : ILibraryFileRelocationActions
{
    public bool FileExists(string path) => File.Exists(path); public bool DirectoryExists(string path) => Directory.Exists(path);
    public bool IsReparsePoint(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
    public void Move(string source, string destination) => File.Move(source, destination);
    public DateTime GetLastWriteTimeUtc(string path) => File.GetLastWriteTimeUtc(path);
}

public sealed class LibraryFileRelocationService
{
    private readonly ILibraryFileRelocationCatalog _catalog; private readonly ILibraryFileRelocationActions _files; private readonly ILibraryFileIdentityProvider _identity;
    public LibraryFileRelocationService(ILibraryFileRelocationCatalog catalog, ILibraryFileRelocationActions? files = null, ILibraryFileIdentityProvider? identity = null) { _catalog=catalog; _files=files ?? new WindowsLibraryFileRelocationActions(); _identity=identity ?? new WindowsLibraryFileIdentityProvider(); }

    public LibraryFileRelocationPreview Preview(long sourceFileId, long destinationAnchorFileId)
    {
        IndexedFileRecord source = RequirePresent(sourceFileId); IndexedFileRecord anchor = RequirePresent(destinationAnchorFileId);
        string? directory=Path.GetDirectoryName(anchor.FullPath); if (string.IsNullOrWhiteSpace(directory) || !_files.DirectoryExists(directory)) throw new InvalidOperationException("The destination folder is unavailable.");
        if (_files.IsReparsePoint(source.FullPath) || _files.IsReparsePoint(directory)) throw new InvalidOperationException("Relocation through a link or reparse point is not allowed.");
        string destination = ResolveUnique(Path.Combine(directory, Path.GetFileName(source.FullPath)));
        if (string.Equals(Path.GetFullPath(source.FullPath), destination, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("The destination is the source path.");
        return new(sourceFileId,destinationAnchorFileId,source.FullPath,destination);
    }

    public LibraryFileRelocationResult Execute(LibraryFileRelocationPreview preview)
    {
        try
        {
            IndexedFileRecord source=RequirePresent(preview.FileId); IndexedFileRecord anchor=RequirePresent(preview.DestinationAnchorFileId);
            if (!string.Equals(source.FullPath,preview.SourcePath,StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("The source catalog path changed before relocation.");
            if (!string.Equals(Path.GetDirectoryName(anchor.FullPath),Path.GetDirectoryName(preview.DestinationPath),StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("The destination folder changed before relocation.");
            if (!_files.FileExists(source.FullPath)) throw new InvalidOperationException("The source file is missing.");
            if (!_files.DirectoryExists(Path.GetDirectoryName(preview.DestinationPath)!)) throw new InvalidOperationException("The destination folder is unavailable.");
            if (_files.FileExists(preview.DestinationPath) || _catalog.GetFileByPath(preview.DestinationPath) != null) throw new InvalidOperationException("The destination name is no longer available; review the new collision proposal.");
            if (_files.IsReparsePoint(source.FullPath) || _files.IsReparsePoint(Path.GetDirectoryName(preview.DestinationPath)!)) throw new InvalidOperationException("Relocation through a link or reparse point is not allowed.");
            _files.Move(source.FullPath,preview.DestinationPath);
            try { _catalog.RelocateFile(preview.FileId,preview.DestinationAnchorFileId,preview.SourcePath,preview.DestinationPath,_identity.GetIdentity(preview.DestinationPath),_files.GetLastWriteTimeUtc(preview.DestinationPath)); return new(true,preview.SourcePath,preview.DestinationPath,""); }
            catch (Exception db)
            {
                bool rolled=false; try { if (!_files.FileExists(preview.SourcePath) && _files.FileExists(preview.DestinationPath)) { _files.Move(preview.DestinationPath,preview.SourcePath); rolled=true; } } catch (Exception rollback) { return new(false,preview.SourcePath,preview.DestinationPath,$"Catalog update failed and rollback failed: {db.Message} / {rollback.Message}",true,false); }
                return new(false,preview.SourcePath,preview.DestinationPath,$"Catalog update failed; the file was restored to its original path. {db.Message}",true,rolled);
            }
        }
        catch (Exception ex) { return new(false,preview.SourcePath,preview.DestinationPath,ex.Message); }
    }
    private IndexedFileRecord RequirePresent(long id) { IndexedFileRecord? file=_catalog.GetFile(id); if(file==null||file.Availability!=IndexedFileAvailability.Present) throw new InvalidOperationException("The catalog item is missing or unavailable."); return file; }
    private string ResolveUnique(string desired) { string dir=Path.GetDirectoryName(desired)!; string stem=Path.GetFileNameWithoutExtension(desired); string ext=Path.GetExtension(desired); for(int n=1;n<10000;n++){string p=n==1?desired:Path.Combine(dir,$"{stem} ({n}).{ext.TrimStart('.')}"); if(!_files.FileExists(p)&&_catalog.GetFileByPath(p)==null)return Path.GetFullPath(p);} throw new IOException("Unable to find an available destination filename."); }
}
