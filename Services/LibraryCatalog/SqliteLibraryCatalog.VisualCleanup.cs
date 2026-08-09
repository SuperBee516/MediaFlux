using Microsoft.Data.Sqlite;

namespace MediaFlux.Services.LibraryCatalog
{
    public sealed partial class SqliteLibraryCatalog
    {
        public long CreateVisualCleanupPlan(DuplicateCleanupAction action, string quarantineRoot, bool allowUnreviewed,
            double minimumConfidence, IReadOnlyCollection<VisualCleanupPlanItemRecord> items)
        {
            ArgumentNullException.ThrowIfNull(items);
            if (items.Count == 0) throw new ArgumentException("A visual cleanup plan must contain at least one item.", nameof(items));
            if (action == DuplicateCleanupAction.Quarantine && string.IsNullOrWhiteSpace(quarantineRoot))
                throw new ArgumentException("A quarantine root is required.", nameof(quarantineRoot));
            minimumConfidence = Math.Clamp(minimumConfidence, 0, 100);
            return WithWriteTransaction((connection, transaction) =>
            {
                using SqliteCommand plan = connection.CreateCommand();
                plan.Transaction = transaction;
                plan.CommandText = "INSERT INTO visual_cleanup_plans(action,status,quarantine_root,allow_unreviewed,minimum_confidence,created_utc_ticks) VALUES($action,$status,$root,$unreviewed,$confidence,$now) RETURNING id;";
                plan.Parameters.AddWithValue("$action", (int)action);
                plan.Parameters.AddWithValue("$status", (int)DuplicateCleanupStatus.Ready);
                plan.Parameters.AddWithValue("$root", quarantineRoot ?? "");
                plan.Parameters.AddWithValue("$unreviewed", allowUnreviewed ? 1 : 0);
                plan.Parameters.AddWithValue("$confidence", minimumConfidence);
                plan.Parameters.AddWithValue("$now", DateTime.UtcNow.Ticks);
                long planId = Convert.ToInt64(plan.ExecuteScalar());
                foreach (VisualCleanupPlanItemRecord source in items)
                {
                    using SqliteCommand item = connection.CreateCommand();
                    item.Transaction = transaction;
                    item.CommandText = """
                        INSERT INTO visual_cleanup_plan_items(plan_id,group_key,group_id,file_id,keeper_file_id,source_path,source_size_bytes,
                        source_last_write_utc_ticks,source_volume_id,source_file_identity,keeper_path,keeper_size_bytes,keeper_last_write_utc_ticks,
                        keeper_volume_id,keeper_file_identity,confidence_score,exact_hash,cleanup_intent,status)
                        VALUES($plan,$key,$group,$file,$keeper,$source,$source_size,$source_time,$source_volume,$source_identity,
                        $keeper_path,$keeper_size,$keeper_time,$keeper_volume,$keeper_identity,$confidence,$hash,$intent,$status);
                        """;
                    item.Parameters.AddWithValue("$plan", planId); item.Parameters.AddWithValue("$key", source.GroupKey);
                    item.Parameters.AddWithValue("$group", source.GroupId); item.Parameters.AddWithValue("$file", source.FileId);
                    item.Parameters.AddWithValue("$keeper", source.KeeperFileId); item.Parameters.AddWithValue("$source", source.SourcePath);
                    item.Parameters.AddWithValue("$source_size", source.SourceSizeBytes); item.Parameters.AddWithValue("$source_time", source.SourceLastWriteUtc.Ticks);
                    item.Parameters.AddWithValue("$source_volume", source.SourceVolumeId ?? ""); item.Parameters.AddWithValue("$source_identity", source.SourceFileIdentity ?? "");
                    item.Parameters.AddWithValue("$keeper_path", source.KeeperPath); item.Parameters.AddWithValue("$keeper_size", source.KeeperSizeBytes);
                    item.Parameters.AddWithValue("$keeper_time", source.KeeperLastWriteUtc.Ticks); item.Parameters.AddWithValue("$keeper_volume", source.KeeperVolumeId ?? "");
                    item.Parameters.AddWithValue("$keeper_identity", source.KeeperFileIdentity ?? ""); item.Parameters.AddWithValue("$confidence", source.ConfidenceScore);
                    item.Parameters.AddWithValue("$hash", (object?)source.ExactHash ?? DBNull.Value); item.Parameters.AddWithValue("$intent", (int)source.Intent); item.Parameters.AddWithValue("$status", (int)DuplicateCleanupItemStatus.Planned);
                    item.ExecuteNonQuery();
                }
                return planId;
            });
        }

        public VisualCleanupPlanRecord? GetVisualCleanupPlan(long planId)
        {
            ThrowIfDisposed();
            using SqliteConnection connection = _database.OpenConnection(readOnly: true);
            using SqliteCommand plan = connection.CreateCommand();
            plan.CommandText = "SELECT action,status,quarantine_root,allow_unreviewed,minimum_confidence,created_utc_ticks,completed_utc_ticks,error_text FROM visual_cleanup_plans WHERE id=$id;";
            plan.Parameters.AddWithValue("$id", planId);
            using SqliteDataReader pr = plan.ExecuteReader();
            if (!pr.Read()) return null;
            var action=(DuplicateCleanupAction)pr.GetInt32(0); var status=(DuplicateCleanupStatus)pr.GetInt32(1); string root=pr.GetString(2);
            bool unreviewed=pr.GetInt32(3)!=0; double confidence=pr.GetDouble(4); DateTime created=FromUtcTicks(pr.GetInt64(5));
            DateTime? completed=pr.IsDBNull(6)?null:FromUtcTicks(pr.GetInt64(6)); string error=pr.GetString(7); pr.Close();
            using SqliteCommand items = connection.CreateCommand();
            items.CommandText = "SELECT plan_id,group_key,group_id,file_id,keeper_file_id,source_path,source_size_bytes,source_last_write_utc_ticks,source_volume_id,source_file_identity,keeper_path,keeper_size_bytes,keeper_last_write_utc_ticks,keeper_volume_id,keeper_file_identity,confidence_score,exact_hash,cleanup_intent,status,destination_path,validation_error FROM visual_cleanup_plan_items WHERE plan_id=$id ORDER BY group_id,file_id;";
            items.Parameters.AddWithValue("$id", planId);
            using SqliteDataReader r=items.ExecuteReader(); var result=new List<VisualCleanupPlanItemRecord>();
            while(r.Read()) result.Add(new VisualCleanupPlanItemRecord(r.GetInt64(0),r.GetString(1),r.GetInt64(2),r.GetInt64(3),r.GetInt64(4),r.GetString(5),r.GetInt64(6),FromUtcTicks(r.GetInt64(7)),r.GetString(8),r.GetString(9),r.GetString(10),r.GetInt64(11),FromUtcTicks(r.GetInt64(12)),r.GetString(13),r.GetString(14),r.GetDouble(15),r.IsDBNull(16)?null:(byte[])r[16],(VisualCleanupIntent)r.GetInt32(17),(DuplicateCleanupItemStatus)r.GetInt32(18),r.GetString(19),r.GetString(20)));
            return new VisualCleanupPlanRecord(planId,action,status,root,unreviewed,confidence,created,completed,error,result);
        }

        public void UpdateVisualCleanupPlanItem(long planId,long fileId,DuplicateCleanupItemStatus status,string destinationPath,string validationError) =>
            WithWriteTransaction<object?>((c,t)=>{using SqliteCommand q=c.CreateCommand();q.Transaction=t;q.CommandText="UPDATE visual_cleanup_plan_items SET status=$s,destination_path=$d,validation_error=$e WHERE plan_id=$p AND file_id=$f;";q.Parameters.AddWithValue("$s",(int)status);q.Parameters.AddWithValue("$d",destinationPath??"");q.Parameters.AddWithValue("$e",validationError??"");q.Parameters.AddWithValue("$p",planId);q.Parameters.AddWithValue("$f",fileId);q.ExecuteNonQuery();return null;});

        public void CompleteVisualCleanupPlan(long planId,DuplicateCleanupStatus status,string errorText="") =>
            WithWriteTransaction<object?>((c,t)=>{using SqliteCommand q=c.CreateCommand();q.Transaction=t;q.CommandText="UPDATE visual_cleanup_plans SET status=$s,completed_utc_ticks=$n,error_text=$e WHERE id=$p;";q.Parameters.AddWithValue("$s",(int)status);q.Parameters.AddWithValue("$n",DateTime.UtcNow.Ticks);q.Parameters.AddWithValue("$e",errorText??"");q.Parameters.AddWithValue("$p",planId);q.ExecuteNonQuery();return null;});

        public void AppendVisualCleanupAudit(long planId,long fileId,string sourcePath,string destinationPath,DuplicateCleanupAction action,DuplicateCleanupItemStatus outcome,string message) =>
            WithWriteTransaction<object?>((c,t)=>{using SqliteCommand q=c.CreateCommand();q.Transaction=t;q.CommandText="INSERT INTO visual_cleanup_audit(plan_id,file_id,source_path,destination_path,action,outcome,message,occurred_utc_ticks) VALUES($p,$f,$s,$d,$a,$o,$m,$n);";q.Parameters.AddWithValue("$p",planId);q.Parameters.AddWithValue("$f",fileId);q.Parameters.AddWithValue("$s",sourcePath??"");q.Parameters.AddWithValue("$d",destinationPath??"");q.Parameters.AddWithValue("$a",(int)action);q.Parameters.AddWithValue("$o",(int)outcome);q.Parameters.AddWithValue("$m",message??"");q.Parameters.AddWithValue("$n",DateTime.UtcNow.Ticks);q.ExecuteNonQuery();return null;});
    }
}
