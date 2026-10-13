using MediaFlux.Services.LibraryCatalog;
using Xunit;

namespace MediaFlux.Tests;

public sealed class LibraryFileRelocationTests
{
    [Fact]
    public void LeftToRightUpdatesExistingCatalogItemWithoutChangingReviewData()
    {
        var catalog = new Catalog(); var files = new Files();
        IndexedFileRecord left = catalog.Add(1, @"C:\left\movie.mkv"); IndexedFileRecord right = catalog.Add(2, @"D:\right\other.mkv"); files.Add(left.FullPath); files.Add(right.FullPath);
        LibraryFileRelocationPreview preview = new LibraryFileRelocationService(catalog, files, new Identity()).Preview(left.Id, right.Id);
        LibraryFileRelocationResult result = new LibraryFileRelocationService(catalog, files, new Identity()).Execute(preview);
        Assert.True(result.Succeeded); Assert.Equal(@"D:\right\movie.mkv", catalog.GetFile(left.Id)!.FullPath); Assert.Equal(1, catalog.GetFile(left.Id)!.Id); Assert.Equal("review-state-kept", catalog.ReviewState);
    }

    [Fact]
    public void RightToLeftAndCollisionUseDeterministicAvailableName()
    {
        var catalog = new Catalog(); var files = new Files(); var left=catalog.Add(1,@"C:\left\other.mkv"); var right=catalog.Add(2,@"D:\right\movie.mkv"); files.Add(left.FullPath); files.Add(right.FullPath); files.Add(@"C:\left\movie.mkv");
        LibraryFileRelocationPreview preview=new LibraryFileRelocationService(catalog,files,new Identity()).Preview(right.Id,left.Id);
        Assert.Equal(@"C:\left\movie (2).mkv",preview.DestinationPath); Assert.True(new LibraryFileRelocationService(catalog,files,new Identity()).Execute(preview).Succeeded); Assert.True(files.Exists(@"C:\left\movie.mkv"));
    }

    [Fact]
    public void MissingSourceAndInvalidDestinationAreRejected()
    {
        var catalog=new Catalog(); var files=new Files(); var source=catalog.Add(1,@"C:\left\movie.mkv"); var anchor=catalog.Add(2,@"D:\right\other.mkv"); files.Add(anchor.FullPath); files.Directories.Remove(@"D:\right");
        Assert.Throws<InvalidOperationException>(() => new LibraryFileRelocationService(catalog,files,new Identity()).Preview(source.Id,anchor.Id));
        files.Directories.Add(@"D:\right"); LibraryFileRelocationPreview preview=new LibraryFileRelocationService(catalog,files,new Identity()).Preview(source.Id,anchor.Id); Assert.False(new LibraryFileRelocationService(catalog,files,new Identity()).Execute(preview).Succeeded);
    }

    [Fact]
    public void DatabaseFailureRollsBackFilesystemMove()
    {
        var catalog=new Catalog { ThrowOnRelocate=true }; var files=new Files(); var source=catalog.Add(1,@"C:\left\movie.mkv"); var anchor=catalog.Add(2,@"D:\right\other.mkv"); files.Add(source.FullPath); files.Add(anchor.FullPath);
        var service=new LibraryFileRelocationService(catalog,files,new Identity()); LibraryFileRelocationResult result=service.Execute(service.Preview(source.Id,anchor.Id));
        Assert.False(result.Succeeded); Assert.True(result.RollbackSucceeded); Assert.True(files.Exists(source.FullPath)); Assert.False(files.Exists(@"D:\right\movie.mkv")); Assert.Equal(source.FullPath,catalog.GetFile(source.Id)!.FullPath);
    }

    [Fact]
    public void CrossVolumeUsesMoveAbstractionAndFilesystemFailureDoesNotUpdateCatalog()
    {
        var catalog=new Catalog(); var files=new Files { ThrowOnMove=true }; var source=catalog.Add(1,@"C:\left\movie.mkv"); var anchor=catalog.Add(2,@"D:\right\other.mkv"); files.Add(source.FullPath); files.Add(anchor.FullPath);
        var service=new LibraryFileRelocationService(catalog,files,new Identity()); LibraryFileRelocationResult result=service.Execute(service.Preview(source.Id,anchor.Id));
        Assert.False(result.Succeeded); Assert.Equal(source.FullPath,catalog.GetFile(source.Id)!.FullPath); Assert.Equal(0,catalog.Relocations);
    }

    private sealed class Identity : ILibraryFileIdentityProvider { public LibraryFileIdentity GetIdentity(string path) => new("vol", "id"); }
    private sealed class Files : ILibraryFileRelocationActions
    {
        private readonly HashSet<string> _files=new(StringComparer.OrdinalIgnoreCase); public HashSet<string> Directories { get; }=new(StringComparer.OrdinalIgnoreCase){@"C:\left",@"D:\right"}; public bool ThrowOnMove;
        public void Add(string path)=>_files.Add(path); public bool Exists(string p)=>_files.Contains(p); public bool FileExists(string p)=>Exists(p); public bool DirectoryExists(string p)=>Directories.Contains(p); public bool IsReparsePoint(string p)=>false; public DateTime GetLastWriteTimeUtc(string p)=>DateTime.UtcNow;
        public void Move(string source,string destination){if(ThrowOnMove)throw new IOException("move failed");if(!_files.Remove(source))throw new IOException("missing");if(!_files.Add(destination))throw new IOException("collision");}
    }
    private sealed class Catalog : ILibraryFileRelocationCatalog
    {
        private readonly Dictionary<long,IndexedFileRecord> _items=[]; public bool ThrowOnRelocate; public int Relocations; public string ReviewState="review-state-kept";
        public IndexedFileRecord Add(long id,string path){var item=new IndexedFileRecord(id,path,path.ToUpperInvariant(),Path.GetFileName(path),Path.GetExtension(path),1,null,DateTime.UtcNow,"","",IndexedFileAvailability.Present,DateTime.UtcNow,DateTime.UtcNow,DateTime.UtcNow);_items.Add(id,item);return item;}
        public IndexedFileRecord? GetFile(long id)=>_items.GetValueOrDefault(id); public IndexedFileRecord? GetFileByPath(string path)=>_items.Values.FirstOrDefault(v=>string.Equals(v.FullPath,path,StringComparison.OrdinalIgnoreCase));
        public void RelocateFile(long id,long anchor,string source,string destination,LibraryFileIdentity identity,DateTime write){if(ThrowOnRelocate)throw new InvalidOperationException("db failed");var v=_items[id];_items[id]=v with { FullPath=destination,PathKey=destination.ToUpperInvariant(),FileName=Path.GetFileName(destination),VolumeId=identity.VolumeId,FileIdentity=identity.FileId,LastWriteTimeUtc=write};Relocations++;}
    }
}
