using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace MediaFlux.Services.LibraryCatalog;

public sealed partial class SqliteLibraryCatalog : ILibraryVisualFamilyCatalog
{
    private sealed record FamilySourceEdge(long GroupId, long LeftFileId, long RightFileId, double Confidence, string Evidence);

    public VisualFamilyConstructionResult RebuildVisualFamilies(double minimumConfidence = 76, int maximumComponentSize = 128, int maximumCliques = 10_000)
    {
        ThrowIfDisposed();
        minimumConfidence = Math.Clamp(minimumConfidence, 0, 100);
        maximumComponentSize = Math.Clamp(maximumComponentSize, 3, 2_000);
        maximumCliques = Math.Clamp(maximumCliques, 1, 100_000);
        var timer = Stopwatch.StartNew();
        long runId;
        List<FamilySourceEdge> edges;
        using (SqliteConnection connection = _database.OpenConnection(readOnly: true))
        {
            using SqliteCommand run = connection.CreateCommand();
            run.CommandText = "SELECT id FROM visual_analysis_runs WHERE status=1 ORDER BY id DESC LIMIT 1;";
            object? value = run.ExecuteScalar();
            if (value == null || value == DBNull.Value)
                return new VisualFamilyConstructionResult(0, 0, 0, 0, timer.Elapsed);
            runId = Convert.ToInt64(value);
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT g.id,g.left_file_id,g.right_file_id,g.confidence_score,g.evidence_text
                FROM visual_similarity_groups g
                LEFT JOIN visual_group_decisions d ON d.group_key=g.group_key
                WHERE g.analysis_run_id=$run AND g.lifecycle_state=$active AND g.confidence_score>=$confidence
                  AND COALESCE(d.ignored,0)=0 AND COALESCE(d.not_match,0)=0
                ORDER BY g.left_file_id,g.right_file_id;
                """;
            command.Parameters.AddWithValue("$run", runId);
            command.Parameters.AddWithValue("$active", (int)LibraryMatchEligibilityState.Active);
            command.Parameters.AddWithValue("$confidence", minimumConfidence);
            using SqliteDataReader reader = command.ExecuteReader();
            edges = new List<FamilySourceEdge>();
            while (reader.Read())
                edges.Add(new FamilySourceEdge(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetDouble(3), reader.GetString(4)));
        }

        Dictionary<long, HashSet<long>> graph = BuildGraph(edges);
        List<HashSet<long>> components = ConnectedComponents(graph);
        var accepted = new List<HashSet<long>>();
        int ambiguous = 0;
        int largest = components.Count == 0 ? 0 : components.Max(x => x.Count);
        foreach (HashSet<long> component in components)
        {
            if (component.Count < 3) continue;
            if (component.Count > maximumComponentSize)
            {
                ambiguous++;
                continue;
            }
            bool overflow = false;
            var cliques = new List<HashSet<long>>();
            FindMaximalCliques(graph, new HashSet<long>(), new HashSet<long>(component), new HashSet<long>(),
                cliques, maximumCliques, ref overflow);
            cliques = cliques.Where(x => x.Count >= 3).GroupBy(FamilySetKey).Select(x => x.First()).ToList();
            if (overflow)
            {
                ambiguous++;
                continue;
            }
            HashSet<int> conflicts = new();
            for (int left = 0; left < cliques.Count; left++)
            for (int right = left + 1; right < cliques.Count; right++)
                if (cliques[left].Overlaps(cliques[right]))
                {
                    conflicts.Add(left);
                    conflicts.Add(right);
                }
            if (conflicts.Count > 0) ambiguous++;
            accepted.AddRange(cliques.Where((_, index) => !conflicts.Contains(index)));
        }

        WithWriteTransaction<object?>((connection, transaction) =>
        {
            using (SqliteCommand clear = connection.CreateCommand())
            {
                clear.Transaction = transaction;
                clear.CommandText = "DELETE FROM visual_families;";
                clear.ExecuteNonQuery();
            }
            Dictionary<(long, long), FamilySourceEdge> edgeMap = edges.ToDictionary(
                x => x.LeftFileId < x.RightFileId ? (x.LeftFileId, x.RightFileId) : (x.RightFileId, x.LeftFileId));
            foreach (HashSet<long> family in accepted)
                InsertFamily(connection, transaction, runId, family, edgeMap);
            return null;
        });
        timer.Stop();
        return new VisualFamilyConstructionResult(accepted.Count, ambiguous, edges.Count, largest, timer.Elapsed);
    }

    public VisualFamilyPage QueryVisualFamilies(VisualFamilyQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        ThrowIfDisposed();
        int limit = Math.Clamp(query.Limit, 1, MaximumPageSize);
        int offset = Math.Max(0, query.Offset);
        string common =
            """
            FROM visual_families f
            LEFT JOIN visual_family_decisions d ON d.family_key=f.family_key
            WHERE f.analysis_run_id=(SELECT id FROM visual_analysis_runs WHERE status=1 ORDER BY id DESC LIMIT 1)
              AND ($inactive=1 OR (
                    f.lifecycle_state=$active
                    AND NOT EXISTS(SELECT 1 FROM visual_family_members fm JOIN indexed_files x ON x.id=fm.file_id
                                   WHERE fm.family_id=f.id AND x.availability_state<>$present)
                    AND NOT EXISTS(SELECT 1 FROM visual_family_edges fe JOIN visual_similarity_groups g ON g.id=fe.visual_group_id
                                   WHERE fe.family_id=f.id AND g.lifecycle_state<>$active)))
              AND ($reviewed<0 OR COALESCE(d.reviewed,0)=$reviewed)
              AND ($ignored<0 OR COALESCE(d.ignored,0)=$ignored)
            """;
        using SqliteConnection connection = _database.OpenConnection(readOnly: true);
        using SqliteCommand count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) " + common;
        AddFamilyQueryParameters(count, query);
        long total = Convert.ToInt64(count.ExecuteScalar());
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT f.id,f.family_key,f.member_count,f.minimum_confidence,f.reclaimable_bytes,f.suggested_keeper_file_id,
                   (SELECT x.id FROM indexed_files x WHERE x.path_key=COALESCE(d.manual_keeper_path_key,'')),
                   COALESCE(d.reviewed,0),COALESCE(d.ignored,0),f.lifecycle_state,f.lifecycle_reason
            """ + " " + common + " ORDER BY COALESCE(d.reviewed,0),f.minimum_confidence DESC,f.id LIMIT $limit OFFSET $offset;";
        AddFamilyQueryParameters(command, query);
        command.Parameters.AddWithValue("$limit", limit);
        command.Parameters.AddWithValue("$offset", offset);
        using SqliteDataReader reader = command.ExecuteReader();
        var result = new List<VisualFamilyRecord>();
        while (reader.Read())
            result.Add(new VisualFamilyRecord(reader.GetInt64(0), reader.GetString(1), reader.GetInt32(2), reader.GetDouble(3),
                reader.GetInt64(4), reader.IsDBNull(5) ? null : reader.GetInt64(5), reader.IsDBNull(6) ? null : reader.GetInt64(6),
                reader.GetInt32(7) != 0, reader.GetInt32(8) != 0, (LibraryMatchEligibilityState)reader.GetInt32(9), reader.GetString(10)));
        return new VisualFamilyPage(total, result);
    }

    public VisualFamilyRecord? GetVisualFamily(long familyId) => GetVisualFamilyDirect(familyId);

    private VisualFamilyRecord? GetVisualFamilyDirect(long familyId)
    {
        ThrowIfDisposed();
        using SqliteConnection connection = _database.OpenConnection(readOnly: true);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT f.id,f.family_key,f.member_count,f.minimum_confidence,f.reclaimable_bytes,f.suggested_keeper_file_id,
                   (SELECT x.id FROM indexed_files x WHERE x.path_key=COALESCE(d.manual_keeper_path_key,'')),
                   COALESCE(d.reviewed,0),COALESCE(d.ignored,0),f.lifecycle_state,f.lifecycle_reason
            FROM visual_families f LEFT JOIN visual_family_decisions d ON d.family_key=f.family_key WHERE f.id=$id;
            """;
        command.Parameters.AddWithValue("$id", familyId);
        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read() ? new VisualFamilyRecord(reader.GetInt64(0), reader.GetString(1), reader.GetInt32(2), reader.GetDouble(3),
            reader.GetInt64(4), reader.IsDBNull(5) ? null : reader.GetInt64(5), reader.IsDBNull(6) ? null : reader.GetInt64(6),
            reader.GetInt32(7) != 0, reader.GetInt32(8) != 0, (LibraryMatchEligibilityState)reader.GetInt32(9), reader.GetString(10)) : null;
    }

    public IReadOnlyList<VisualFamilyMemberRecord> GetVisualFamilyMembers(long familyId)
    {
        ThrowIfDisposed();
        using SqliteConnection connection = _database.OpenConnection(readOnly: true);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT fm.family_id,x.id,x.full_path,COALESCE((SELECT l.path FROM file_location_memberships m JOIN library_locations l ON l.id=m.location_id WHERE m.file_id=x.id ORDER BY l.path LIMIT 1),''),
                   x.size_bytes,x.last_write_utc_ticks,x.availability_state,COALESCE(md.video_codec,''),md.width,md.height,md.total_bitrate,md.duration_seconds,
                   EXISTS(SELECT 1 FROM duplicate_file_protections p WHERE p.path_key=x.path_key),COALESCE(x.id=f.suggested_keeper_file_id,0),
                   COALESCE(x.path_key=COALESCE(d.manual_keeper_path_key,''),0),COALESCE(md.color_transfer,''),COALESCE(md.color_primaries,''),
                   COALESCE(md.audio_streams_json,'[]'),fm.minimum_member_confidence,md.frame_rate
            FROM visual_family_members fm JOIN visual_families f ON f.id=fm.family_id JOIN indexed_files x ON x.id=fm.file_id
            LEFT JOIN media_metadata md ON md.file_id=x.id LEFT JOIN visual_family_decisions d ON d.family_key=f.family_key
            WHERE fm.family_id=$id ORDER BY x.full_path;
            """;
        command.Parameters.AddWithValue("$id", familyId);
        using SqliteDataReader reader = command.ExecuteReader();
        var result = new List<VisualFamilyMemberRecord>();
        while (reader.Read())
            result.Add(new VisualFamilyMemberRecord(reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3),
                reader.GetInt64(4), FromUtcTicks(reader.GetInt64(5)), (IndexedFileAvailability)reader.GetInt32(6), reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetInt32(8), reader.IsDBNull(9) ? null : reader.GetInt32(9),
                reader.IsDBNull(10) ? null : reader.GetInt64(10), reader.IsDBNull(11) ? null : reader.GetDouble(11),
                reader.GetInt32(12) != 0, reader.GetInt32(13) != 0, reader.GetInt32(14) != 0,
                IsHdrTransfer(reader.GetString(15), reader.GetString(16)), BuildAudioSummary(reader.GetString(17)), reader.GetDouble(18),
                reader.IsDBNull(19) ? null : reader.GetDouble(19)));
        return result;
    }

    public IReadOnlyList<VisualFamilyEdgeRecord> GetVisualFamilyEdges(long familyId)
    {
        ThrowIfDisposed();
        using SqliteConnection connection = _database.OpenConnection(readOnly: true);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT family_id,visual_group_id,left_file_id,right_file_id,confidence_score,evidence_text FROM visual_family_edges WHERE family_id=$id ORDER BY left_file_id,right_file_id;";
        command.Parameters.AddWithValue("$id", familyId);
        using SqliteDataReader reader = command.ExecuteReader();
        var result = new List<VisualFamilyEdgeRecord>();
        while (reader.Read()) result.Add(new VisualFamilyEdgeRecord(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2),
            reader.GetInt64(3), reader.GetDouble(4), reader.GetString(5)));
        return result;
    }

    public void SetVisualFamilySuggestedKeeper(long familyId, long? fileId)
    {
        ThrowIfDisposed();
        WithWriteTransaction<object?>((connection, transaction) =>
        {
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE visual_families SET suggested_keeper_file_id=$file,updated_utc_ticks=$now WHERE id=$id AND ($file IS NULL OR EXISTS(SELECT 1 FROM visual_family_members WHERE family_id=$id AND file_id=$file));";
            command.Parameters.AddWithValue("$file", (object?)fileId ?? DBNull.Value);
            command.Parameters.AddWithValue("$now", DateTime.UtcNow.Ticks);
            command.Parameters.AddWithValue("$id", familyId);
            command.ExecuteNonQuery();
            return null;
        });
    }

    public void SaveVisualFamilyDecision(VisualFamilyDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ThrowIfDisposed();
        WithWriteTransaction<object?>((connection, transaction) =>
        {
            FamilyDecisionState before = CaptureFamilyDecisionState(connection, transaction, decision.FamilyId);
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO visual_family_decisions(family_key,manual_keeper_path_key,reviewed,ignored,updated_utc_ticks)
                SELECT f.family_key,COALESCE(x.path_key,''),$reviewed,$ignored,$now
                FROM visual_families f LEFT JOIN indexed_files x ON x.id=$keeper
                WHERE f.id=$id AND ($keeper IS NULL OR EXISTS(SELECT 1 FROM visual_family_members m WHERE m.family_id=f.id AND m.file_id=$keeper))
                ON CONFLICT(family_key) DO UPDATE SET manual_keeper_path_key=excluded.manual_keeper_path_key,
                    reviewed=excluded.reviewed,ignored=excluded.ignored,updated_utc_ticks=excluded.updated_utc_ticks;
                """;
            command.Parameters.AddWithValue("$keeper", (object?)decision.ManualKeeperFileId ?? DBNull.Value);
            command.Parameters.AddWithValue("$reviewed", decision.Reviewed ? 1 : 0);
            command.Parameters.AddWithValue("$ignored", decision.Ignored ? 1 : 0);
            command.Parameters.AddWithValue("$now", DateTime.UtcNow.Ticks);
            command.Parameters.AddWithValue("$id", decision.FamilyId);
            if (command.ExecuteNonQuery() != 1) throw new KeyNotFoundException($"Visual family {decision.FamilyId} does not exist.");
            FamilyDecisionState after = CaptureFamilyDecisionState(connection, transaction, decision.FamilyId);
            InsertDecisionEventCore(connection, transaction, LibraryDecisionTargetKind.VisualFamily, after.FamilyKey,
                FamilyEventKind(before, after), Serialize(before), Serialize(after), decision.BatchId, decision.Source);
            return null;
        });
    }

    public bool IsFileInActiveCleanupPlan(long fileId)
    {
        ThrowIfDisposed();
        using SqliteConnection connection = _database.OpenConnection(readOnly: true);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT EXISTS(
                SELECT 1 FROM visual_cleanup_plan_items i JOIN visual_cleanup_plans p ON p.id=i.plan_id
                WHERE i.file_id=$file AND p.status IN($ready,$running) AND i.status IN($planned,$validated)
                UNION ALL
                SELECT 1 FROM duplicate_cleanup_plan_items i JOIN duplicate_cleanup_plans p ON p.id=i.plan_id
                WHERE i.file_id=$file AND p.status IN($ready,$running) AND i.status IN($planned,$validated)
            );
            """;
        command.Parameters.AddWithValue("$file", fileId);
        command.Parameters.AddWithValue("$ready", (int)DuplicateCleanupStatus.Ready);
        command.Parameters.AddWithValue("$running", (int)DuplicateCleanupStatus.Running);
        command.Parameters.AddWithValue("$planned", (int)DuplicateCleanupItemStatus.Planned);
        command.Parameters.AddWithValue("$validated", (int)DuplicateCleanupItemStatus.Validated);
        return Convert.ToInt32(command.ExecuteScalar()) != 0;
    }

    private static void AddFamilyQueryParameters(SqliteCommand command, VisualFamilyQuery query)
    {
        command.Parameters.AddWithValue("$inactive", query.IncludeInactive ? 1 : 0);
        command.Parameters.AddWithValue("$active", (int)LibraryMatchEligibilityState.Active);
        command.Parameters.AddWithValue("$present", (int)IndexedFileAvailability.Present);
        command.Parameters.AddWithValue("$reviewed", query.Reviewed.HasValue ? (query.Reviewed.Value ? 1 : 0) : -1);
        command.Parameters.AddWithValue("$ignored", query.Ignored.HasValue ? (query.Ignored.Value ? 1 : 0) : -1);
    }

    private static Dictionary<long, HashSet<long>> BuildGraph(IEnumerable<FamilySourceEdge> edges)
    {
        var graph = new Dictionary<long, HashSet<long>>();
        foreach (FamilySourceEdge edge in edges)
        {
            if (!graph.TryGetValue(edge.LeftFileId, out HashSet<long>? left)) graph[edge.LeftFileId] = left = new();
            if (!graph.TryGetValue(edge.RightFileId, out HashSet<long>? right)) graph[edge.RightFileId] = right = new();
            left.Add(edge.RightFileId);
            right.Add(edge.LeftFileId);
        }
        return graph;
    }

    private static List<HashSet<long>> ConnectedComponents(Dictionary<long, HashSet<long>> graph)
    {
        var result = new List<HashSet<long>>();
        var unseen = graph.Keys.ToHashSet();
        while (unseen.Count > 0)
        {
            long start = unseen.First();
            var component = new HashSet<long>();
            var stack = new Stack<long>(); stack.Push(start); unseen.Remove(start);
            while (stack.Count > 0)
            {
                long current = stack.Pop(); component.Add(current);
                foreach (long next in graph[current])
                    if (unseen.Remove(next)) stack.Push(next);
            }
            result.Add(component);
        }
        return result;
    }

    private static void FindMaximalCliques(Dictionary<long, HashSet<long>> graph, HashSet<long> current,
        HashSet<long> possible, HashSet<long> excluded, List<HashSet<long>> result, int maximum, ref bool overflow)
    {
        if (overflow) return;
        if (possible.Count == 0 && excluded.Count == 0)
        {
            if (current.Count >= 3) result.Add(new HashSet<long>(current));
            if (result.Count > maximum) overflow = true;
            return;
        }
        long? pivot = possible.Concat(excluded).OrderByDescending(v => graph[v].Count(n => possible.Contains(n))).Cast<long?>().FirstOrDefault();
        long[] candidates = pivot.HasValue ? possible.Where(v => !graph[pivot.Value].Contains(v)).ToArray() : possible.ToArray();
        foreach (long vertex in candidates)
        {
            var next = new HashSet<long>(current) { vertex };
            FindMaximalCliques(graph, next, possible.Where(graph[vertex].Contains).ToHashSet(),
                excluded.Where(graph[vertex].Contains).ToHashSet(), result, maximum, ref overflow);
            possible.Remove(vertex);
            excluded.Add(vertex);
            if (overflow) return;
        }
    }

    private static string FamilySetKey(IEnumerable<long> members) => string.Join(",", members.OrderBy(x => x));

    private static void InsertFamily(SqliteConnection connection, SqliteTransaction transaction, long runId, HashSet<long> members,
        IReadOnlyDictionary<(long, long), FamilySourceEdge> edgeMap)
    {
        long[] ids = members.OrderBy(x => x).ToArray();
        var supporting = new List<FamilySourceEdge>();
        for (int left = 0; left < ids.Length; left++)
        for (int right = left + 1; right < ids.Length; right++)
            supporting.Add(edgeMap[(ids[left], ids[right])]);
        using SqliteCommand paths = connection.CreateCommand();
        paths.Transaction = transaction;
        paths.CommandText = $"SELECT path_key FROM indexed_files WHERE id IN({string.Join(",", ids)}) ORDER BY path_key;";
        var pathKeys = new List<string>();
        using (SqliteDataReader reader = paths.ExecuteReader()) while (reader.Read()) pathKeys.Add(reader.GetString(0));
        if (pathKeys.Count != ids.Length) return;
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', pathKeys))));
        long reclaimable;
        using (SqliteCommand sizes = connection.CreateCommand())
        {
            sizes.Transaction = transaction;
            sizes.CommandText = $"SELECT SUM(size_bytes)-MAX(size_bytes) FROM indexed_files WHERE id IN({string.Join(",", ids)});";
            reclaimable = Convert.ToInt64(sizes.ExecuteScalar());
        }
        long familyId;
        using (SqliteCommand insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO visual_families(family_key,analysis_run_id,member_count,minimum_confidence,reclaimable_bytes,updated_utc_ticks) VALUES($key,$run,$count,$confidence,$bytes,$now) RETURNING id;";
            insert.Parameters.AddWithValue("$key", key); insert.Parameters.AddWithValue("$run", runId);
            insert.Parameters.AddWithValue("$count", ids.Length); insert.Parameters.AddWithValue("$confidence", supporting.Min(x => x.Confidence));
            insert.Parameters.AddWithValue("$bytes", reclaimable); insert.Parameters.AddWithValue("$now", DateTime.UtcNow.Ticks);
            familyId = Convert.ToInt64(insert.ExecuteScalar());
        }
        foreach (long id in ids)
        {
            using SqliteCommand member = connection.CreateCommand(); member.Transaction = transaction;
            member.CommandText = "INSERT INTO visual_family_members(family_id,file_id,minimum_member_confidence) VALUES($family,$file,$confidence);";
            member.Parameters.AddWithValue("$family", familyId); member.Parameters.AddWithValue("$file", id);
            member.Parameters.AddWithValue("$confidence", supporting.Where(x => x.LeftFileId == id || x.RightFileId == id).Min(x => x.Confidence));
            member.ExecuteNonQuery();
        }
        foreach (FamilySourceEdge edge in supporting)
        {
            using SqliteCommand item = connection.CreateCommand(); item.Transaction = transaction;
            item.CommandText = "INSERT INTO visual_family_edges(family_id,visual_group_id,left_file_id,right_file_id,confidence_score,evidence_text) VALUES($family,$group,$left,$right,$confidence,$evidence);";
            item.Parameters.AddWithValue("$family", familyId); item.Parameters.AddWithValue("$group", edge.GroupId);
            item.Parameters.AddWithValue("$left", Math.Min(edge.LeftFileId, edge.RightFileId)); item.Parameters.AddWithValue("$right", Math.Max(edge.LeftFileId, edge.RightFileId));
            item.Parameters.AddWithValue("$confidence", edge.Confidence); item.Parameters.AddWithValue("$evidence", edge.Evidence);
            item.ExecuteNonQuery();
        }
    }
}
