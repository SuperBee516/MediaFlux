using Microsoft.Data.Sqlite;
using System.Text.Json;

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

    public IReadOnlyList<LibraryPolicyFileFacts> QueryPolicyFileFacts(int offset, int limit)
    {
        ThrowIfDisposed();
        offset = Math.Max(0, offset);
        limit = Math.Clamp(limit, 1, 1_000);
        using SqliteConnection connection = _database.OpenConnection(readOnly: true);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT f.id,f.full_path,f.file_name,f.size_bytes,
                   COALESCE(m.format_name,''),COALESCE(m.video_codec,''),COALESCE(m.video_profile,''),
                   m.width,m.height,m.frame_rate,m.total_bitrate,m.duration_seconds,m.bit_depth,
                   COALESCE(m.field_order,''),COALESCE(m.color_transfer,''),COALESCE(m.color_primaries,''),
                   COALESCE(m.audio_streams_json,'[]'),COALESCE(m.subtitle_streams_json,'[]'),
                   COALESCE(m.chapter_count,0),COALESCE(m.attachment_count,0),
                   COALESCE(m.probe_status,0),COALESCE(m.error_message,''),
                   EXISTS(SELECT 1 FROM duplicate_file_protections p WHERE p.path_key=f.path_key),
                   EXISTS(SELECT 1 FROM exact_duplicate_members em
                          JOIN exact_duplicate_groups eg ON eg.id=em.group_id
                          JOIN duplicate_analysis_runs er ON er.id=eg.analysis_run_id AND er.status=1
                          LEFT JOIN duplicate_group_decisions ed ON ed.size_bytes=eg.size_bytes AND ed.full_algorithm=eg.full_algorithm
                               AND ed.full_version=eg.full_version AND ed.full_hash=eg.full_hash
                          WHERE em.file_id=f.id AND COALESCE(ed.ignored,0)=0
                            AND f.id<>COALESCE((SELECT id FROM indexed_files WHERE path_key=ed.manual_keeper_path_key LIMIT 1),eg.suggested_keeper_file_id,-1)),
                   EXISTS(SELECT 1 FROM visual_similarity_groups vg
                          JOIN visual_analysis_runs vr ON vr.id=vg.analysis_run_id AND vr.status=1
                          JOIN visual_group_decisions vd ON vd.group_key=vg.group_key AND vd.reviewed=1 AND vd.ignored=0
                          WHERE f.id IN(vg.left_file_id,vg.right_file_id) AND vg.lifecycle_state=0
                            AND f.path_key<>COALESCE(NULLIF(vd.manual_keeper_path_key,''),
                                (SELECT path_key FROM indexed_files WHERE id=vg.suggested_keeper_file_id),'')),
                   EXISTS(SELECT 1 FROM visual_family_members vfm
                          JOIN visual_families vf ON vf.id=vfm.family_id AND vf.lifecycle_state=0
                          JOIN visual_family_decisions vfd ON vfd.family_key=vf.family_key AND vfd.reviewed=1 AND vfd.ignored=0
                          WHERE vfm.file_id=f.id AND f.path_key<>COALESCE(NULLIF(vfd.manual_keeper_path_key,''),
                                (SELECT path_key FROM indexed_files WHERE id=vf.suggested_keeper_file_id),'')),
                   EXISTS(SELECT 1 FROM visual_similarity_groups ug
                          JOIN visual_analysis_runs ur ON ur.id=ug.analysis_run_id AND ur.status=1
                          LEFT JOIN visual_group_decisions ud ON ud.group_key=ug.group_key
                          WHERE f.id IN(ug.left_file_id,ug.right_file_id) AND ug.lifecycle_state=0
                            AND COALESCE(ud.reviewed,0)=0 AND COALESCE(ud.ignored,0)=0)
            FROM indexed_files f
            LEFT JOIN media_metadata m ON m.file_id=f.id
            WHERE f.availability_state=0
            ORDER BY f.id
            LIMIT $limit OFFSET $offset;
            """;
        command.Parameters.AddWithValue("$limit", limit);
        command.Parameters.AddWithValue("$offset", offset);
        using SqliteDataReader reader = command.ExecuteReader();
        var facts = new List<LibraryPolicyFileFacts>();
        while (reader.Read())
        {
            facts.Add(new LibraryPolicyFileFacts(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(4),
                reader.GetString(5), reader.GetString(6), NullableInt(reader, 7), NullableInt(reader, 8),
                NullableDouble(reader, 9), reader.GetInt64(3), NullableLong(reader, 10), NullableDouble(reader, 11),
                NullableInt(reader, 12), reader.GetString(13), reader.GetString(14), reader.GetString(15),
                Deserialize<List<LibraryAudioStreamMetadata>>(reader.GetString(16)),
                Deserialize<List<LibrarySubtitleStreamMetadata>>(reader.GetString(17)),
                reader.GetInt32(18), reader.GetInt32(19), (LibraryProbeStatus)reader.GetInt32(20), reader.GetString(21),
                reader.GetBoolean(22), reader.GetBoolean(23), reader.GetBoolean(24), reader.GetBoolean(25), reader.GetBoolean(26)));
        }
        return facts;
    }

    public string GetPolicyFactsRevision()
    {
        ThrowIfDisposed();
        using SqliteConnection connection = _database.OpenConnection(readOnly: true);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT " +
            "(SELECT COUNT(*)||':'||COALESCE(MAX(updated_utc_ticks),0) FROM indexed_files)||'|'||" +
            "(SELECT COUNT(*)||':'||COALESCE(MAX(last_attempt_utc_ticks),0) FROM media_metadata)||'|'||" +
            "(SELECT COUNT(*)||':'||COALESCE(MAX(updated_utc_ticks),0) FROM duplicate_file_protections)||'|'||" +
            "(SELECT COUNT(*)||':'||COALESCE(MAX(updated_utc_ticks),0) FROM duplicate_group_decisions)||'|'||" +
            "(SELECT COUNT(*)||':'||COALESCE(MAX(updated_utc_ticks),0) FROM visual_group_decisions)||'|'||" +
            "(SELECT COUNT(*)||':'||COALESCE(MAX(updated_utc_ticks),0) FROM visual_family_decisions)||'|'||" +
            "(SELECT COALESCE(MAX(id),0) FROM duplicate_analysis_runs)||'|'||" +
            "(SELECT COALESCE(MAX(id),0) FROM visual_analysis_runs);";
        return Convert.ToString(command.ExecuteScalar()) ?? "0";
    }

    private static T Deserialize<T>(string json) where T : new()
    {
        try { return JsonSerializer.Deserialize<T>(json) ?? new T(); }
        catch { return new T(); }
    }

    private static int? NullableInt(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
    private static long? NullableLong(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
    private static double? NullableDouble(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetDouble(ordinal);
}
