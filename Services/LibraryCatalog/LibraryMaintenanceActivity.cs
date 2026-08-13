namespace MediaFlux.Services.LibraryCatalog;

public sealed record MaintenanceActivitySnapshot(bool Active,bool WaitingForEncoding,string Stage,string StorageKey);
public static class LibraryMaintenanceActivity
{
    private static readonly object Sync=new();private static MaintenanceActivitySnapshot _current=new(false,false,"","");private static DateTime _deferredUntilUtc;
    public static MaintenanceActivitySnapshot Current{get{lock(Sync){if(!_current.Active&&_current.WaitingForEncoding&&DateTime.UtcNow>=_deferredUntilUtc)_current=new(false,false,"","");return _current;}}}
    public static void Update(bool active,bool waiting,string stage,string path){string key=string.IsNullOrWhiteSpace(path)?"":new WindowsLibraryStorageKeyResolver().ResolveStorageKey(path);lock(Sync){_current=new(active,waiting,stage,key);if(waiting)_deferredUntilUtc=DateTime.UtcNow.AddMinutes(10);}}
    public static void Defer(string stage,string path)=>Update(false,true,stage,path);
    public static void Clear(){lock(Sync){if(!_current.WaitingForEncoding)_current=new(false,false,"","");else _current=_current with{Active=false};}}
}
