using Microsoft.Data.Sqlite;

namespace MediaFlux.Services.LibraryCatalog;

public sealed partial class SqliteLibraryCatalog
{
    public void RelocateFile(long fileId, long destinationAnchorFileId, string expectedSourcePath,
        string destinationPath, LibraryFileIdentity identity, DateTime lastWriteUtc)
    {
        ThrowIfDisposed();
        (string expectedSource, string expectedKey) = LibraryCatalogPathNormalizer.NormalizeFullPath(expectedSourcePath);
        (string destination, string destinationKey) = LibraryCatalogPathNormalizer.NormalizeFullPath(destinationPath);
        if (string.Equals(expectedKey, destinationKey, StringComparison.Ordinal))
            throw new InvalidOperationException("The destination is the current catalog path.");

        WithWriteTransaction<object?>((connection, transaction) =>
        {
            IndexedFileRecord? source = ReadFileById(connection, transaction, fileId);
            IndexedFileRecord? anchor = ReadFileById(connection, transaction, destinationAnchorFileId);
            if (source == null || anchor == null)
                throw new InvalidOperationException("The selected catalog item is no longer available.");
            if (!string.Equals(source.PathKey, expectedKey, StringComparison.Ordinal) ||
                source.Availability != IndexedFileAvailability.Present)
                throw new InvalidOperationException("The source catalog record changed before relocation.");
            if (!string.Equals(Path.GetDirectoryName(anchor.FullPath), Path.GetDirectoryName(destination), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The destination folder no longer matches the compared catalog item.");
            if (ReadFileByPathKey(connection, transaction, destinationKey) != null)
                throw new InvalidOperationException("The destination path is already cataloged.");

            var memberships = new List<(long LocationId, string RelativePath, string RelativeKey, long Generation)>();
            using (SqliteCommand members = connection.CreateCommand())
            {
                members.Transaction = transaction;
                members.CommandText =
                    "SELECT m.location_id, l.path, l.include_subfolders, l.current_generation FROM file_location_memberships m JOIN library_locations l ON l.id=m.location_id WHERE m.file_id=$id AND l.is_enabled=1 AND l.availability_state<>$unavailable;";
                members.Parameters.AddWithValue("$id", destinationAnchorFileId);
                members.Parameters.AddWithValue("$unavailable", (int)LibraryLocationAvailability.Unavailable);
                using SqliteDataReader reader = members.ExecuteReader();
                while (reader.Read())
                {
                    string root = reader.GetString(1);
                    bool recursive = reader.GetInt32(2) != 0;
                    string relative = Path.GetRelativePath(root, destination);
                    if (relative == "." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                        (!recursive && relative.Contains(Path.DirectorySeparatorChar))) continue;
                    (string normalized, string key) = LibraryCatalogPathNormalizer.NormalizeRelativePath(relative);
                    memberships.Add((reader.GetInt64(0), normalized, key, reader.GetInt64(3)));
                }
            }
            if (memberships.Count == 0)
                throw new InvalidOperationException("The destination folder is not represented by an active catalog location.");

            using (SqliteCommand update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText = "UPDATE indexed_files SET full_path=$path,path_key=$key,file_name=$name,extension=$extension,last_write_utc_ticks=$last_write,volume_id=$volume,file_identity=$identity,updated_utc_ticks=$updated WHERE id=$id;";
                update.Parameters.AddWithValue("$path", destination); update.Parameters.AddWithValue("$key", destinationKey);
                update.Parameters.AddWithValue("$name", Path.GetFileName(destination)); update.Parameters.AddWithValue("$extension", Path.GetExtension(destination).ToLowerInvariant());
                update.Parameters.AddWithValue("$last_write", ToUtcTicks(lastWriteUtc)); update.Parameters.AddWithValue("$volume", identity.VolumeId ?? ""); update.Parameters.AddWithValue("$identity", identity.FileId ?? "");
                update.Parameters.AddWithValue("$updated", ToUtcTicks(DateTime.UtcNow)); update.Parameters.AddWithValue("$id", fileId); update.ExecuteNonQuery();
            }
            using (SqliteCommand remove = connection.CreateCommand()) { remove.Transaction = transaction; remove.CommandText = "DELETE FROM file_location_memberships WHERE file_id=$id;"; remove.Parameters.AddWithValue("$id", fileId); remove.ExecuteNonQuery(); }
            foreach (var membership in memberships)
            {
                using SqliteCommand insert = connection.CreateCommand(); insert.Transaction = transaction;
                insert.CommandText = "INSERT INTO file_location_memberships(location_id,file_id,relative_path,relative_path_key,last_seen_generation,availability_state,last_seen_utc_ticks) VALUES($location,$file,$relative,$key,$generation,$availability,$seen);";
                insert.Parameters.AddWithValue("$location", membership.LocationId); insert.Parameters.AddWithValue("$file", fileId); insert.Parameters.AddWithValue("$relative", membership.RelativePath); insert.Parameters.AddWithValue("$key", membership.RelativeKey); insert.Parameters.AddWithValue("$generation", membership.Generation); insert.Parameters.AddWithValue("$availability", (int)IndexedFileAvailability.Present); insert.Parameters.AddWithValue("$seen", ToUtcTicks(DateTime.UtcNow)); insert.ExecuteNonQuery();
            }
            return null;
        });
    }

    private static IndexedFileRecord? ReadFileById(SqliteConnection c, SqliteTransaction t, long id) { using SqliteCommand q=c.CreateCommand(); q.Transaction=t; q.CommandText=FileSelectSql+" WHERE id=$id;"; q.Parameters.AddWithValue("$id",id); using SqliteDataReader r=q.ExecuteReader(); return r.Read()?ReadFile(r):null; }
    private static IndexedFileRecord? ReadFileByPathKey(SqliteConnection c, SqliteTransaction t, string key) { using SqliteCommand q=c.CreateCommand(); q.Transaction=t; q.CommandText=FileSelectSql+" WHERE path_key=$key;"; q.Parameters.AddWithValue("$key",key); using SqliteDataReader r=q.ExecuteReader(); return r.Read()?ReadFile(r):null; }
}
