using Microsoft.Data.Sqlite;

namespace MediaFlux.Services.LibraryCatalog;

public sealed partial class SqliteLibraryCatalog
{
    public IReadOnlyList<LibraryStorageOptimizationCandidate> QueryStorageOptimizationCandidates(int limit = 500)
    {
        ThrowIfDisposed();
        limit = Math.Clamp(limit, 1, 2_000);
        using SqliteConnection connection = _database.OpenConnection(readOnly: true);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT f.id,f.full_path,f.size_bytes,m.video_codec,m.width,m.height,m.total_bitrate,m.duration_seconds,
                   COALESCE(m.color_transfer,''),COALESCE(m.color_primaries,'')
            FROM indexed_files f
            JOIN media_metadata m ON m.file_id=f.id
            WHERE f.availability_state=0 AND m.probe_status=2 AND m.duration_seconds>=60
              AND m.total_bitrate IS NOT NULL AND m.total_bitrate>0
              AND NOT EXISTS(
                SELECT 1 FROM exact_duplicate_members em
                JOIN exact_duplicate_groups eg ON eg.id=em.group_id
                JOIN duplicate_analysis_runs er ON er.id=eg.analysis_run_id AND er.status=1
                WHERE em.file_id=f.id)
              AND NOT EXISTS(
                SELECT 1 FROM visual_similarity_groups vg
                JOIN visual_analysis_runs vr ON vr.id=vg.analysis_run_id AND vr.status=1
                WHERE f.id IN(vg.left_file_id,vg.right_file_id) AND vg.lifecycle_state=0)
            ORDER BY f.size_bytes DESC LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit * 4);
        using SqliteDataReader reader = command.ExecuteReader();
        var candidates = new List<LibraryStorageOptimizationCandidate>();
        while (reader.Read())
        {
            long size = reader.GetInt64(2);
            string codec = reader.GetString(3);
            int? width = reader.IsDBNull(4) ? null : reader.GetInt32(4);
            int? height = reader.IsDBNull(5) ? null : reader.GetInt32(5);
            long bitrate = reader.GetInt64(6);
            double duration = reader.GetDouble(7);
            long pixels = (long)(width ?? 0) * (height ?? 0);
            double bitsPerPixelTime = pixels > 0 ? bitrate / (double)pixels : 0;
            double baseline = CodecBaseline(codec);
            double ratio = baseline > 0 ? bitsPerPixelTime / baseline : 0;
            if (ratio < 1.25 && size < 2L * 1024 * 1024 * 1024) continue;
            bool hdr = IsHdr(reader.GetString(8), reader.GetString(9));
            double score = Math.Round(Math.Min(100, ratio * 45 + Math.Log10(Math.Max(1, size / (1024d * 1024d))) * 12 + (hdr ? 5 : 0)), 1);
            string resolution = width.HasValue && height.HasValue ? $"{width}×{height}" : "unknown resolution";
            string rationale = $"{codec.ToUpperInvariant()} {resolution}; {bitrate / 1_000_000d:0.##} Mbps, {bitsPerPixelTime:0.####} bits/pixel-time" +
                (hdr ? "; HDR source requires a compatible encode choice." : "");
            candidates.Add(new LibraryStorageOptimizationCandidate(reader.GetInt64(0), reader.GetString(1), codec, width, height,
                size, bitrate, duration, hdr, score, rationale));
        }
        return candidates.OrderByDescending(x => x.OpportunityScore).ThenByDescending(x => x.SizeBytes).Take(limit).ToArray();
    }

    private static bool IsHdr(string transfer, string primaries) =>
        transfer.Contains("pq", StringComparison.OrdinalIgnoreCase) ||
        transfer.Contains("hlg", StringComparison.OrdinalIgnoreCase) ||
        primaries.Contains("bt2020", StringComparison.OrdinalIgnoreCase);

    private static double CodecBaseline(string codec)
    {
        string value = codec.ToLowerInvariant();
        if (value.Contains("av1")) return 0.035;
        if (value.Contains("hevc") || value.Contains("h265")) return 0.055;
        if (value.Contains("vp9")) return 0.06;
        if (value.Contains("h264") || value.Contains("avc")) return 0.10;
        return 0.08;
    }
}
