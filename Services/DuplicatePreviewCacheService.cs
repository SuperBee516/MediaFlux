using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace MediaFlux.Services
{
    public static class DuplicatePreviewCacheService
    {
        public static string GetPreviewDirectory(string baseDirectory)
        {
            return Path.Combine(baseDirectory, "data", "duplicate-previews");
        }

        public static string GetThumbnailPath(string baseDirectory, string sourcePath)
        {
            string safeName = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sourcePath)));
            return Path.Combine(GetPreviewDirectory(baseDirectory), $"{safeName}.jpg");
        }

        public static CacheClearResult Clear(string baseDirectory)
        {
            string previewDir = GetPreviewDirectory(baseDirectory);
            if (!Directory.Exists(previewDir))
                return new CacheClearResult(0, 0);

            int deleted = 0;
            long bytes = 0;
            foreach (var file in Directory.EnumerateFiles(previewDir, "*.jpg", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var info = new FileInfo(file);
                    bytes += info.Exists ? info.Length : 0;
                    File.Delete(file);
                    deleted++;
                }
                catch
                {
                    // Best effort cache cleanup; locked files can remain for the next cleanup.
                }
            }

            return new CacheClearResult(deleted, bytes);
        }

        public static CacheClearResult PruneOlderThan(string baseDirectory, TimeSpan maxAge)
        {
            string previewDir = GetPreviewDirectory(baseDirectory);
            if (!Directory.Exists(previewDir))
                return new CacheClearResult(0, 0);

            DateTime cutoffUtc = DateTime.UtcNow.Subtract(maxAge);
            int deleted = 0;
            long bytes = 0;
            foreach (var file in Directory.EnumerateFiles(previewDir, "*.jpg", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var info = new FileInfo(file);
                    if (!info.Exists || info.LastWriteTimeUtc >= cutoffUtc)
                        continue;

                    bytes += info.Length;
                    info.Delete();
                    deleted++;
                }
                catch
                {
                    // Best effort cache cleanup.
                }
            }

            return new CacheClearResult(deleted, bytes);
        }
    }

    public sealed record CacheClearResult(int DeletedFiles, long FreedBytes);
}
