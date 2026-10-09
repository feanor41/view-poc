using System.Data;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;
using Npgsql.Replication;
using Npgsql.Replication.PgOutput;
using Npgsql.Replication.PgOutput.Messages;
using NpgsqlTypes;

namespace Vwp.Datalake.Postgres;

internal sealed record Change(string Table, char Action, Guid Id, string Payload);
internal sealed record Checkpoint(string Database, string SystemId, string Slot, NpgsqlLogSequenceNumber Lsn);

internal sealed class CaptureWorker(Options options)
{
    private readonly SemaphoreSlim applyGate = new(1, 1);

    internal async Task SnapshotAsync(Source source, bool reset)
    {
        await using var target = await options.OpenAsync(options.DatalakeDatabase);
        await using var sourceConnection = await options.OpenAsync(source.Database);
        await ValidateSchemaAsync(sourceConnection, source);
        await using var replication = new LogicalReplicationConnection(options.ConnectionFor(source.Database));
        await replication.Open();
        var identity = await replication.IdentifySystem();
        await using (var query = new NpgsqlCommand("SELECT database,plugin,active FROM pg_replication_slots WHERE slot_name=@slot", sourceConnection))
        {
            query.Parameters.AddWithValue("slot", source.Slot);
            await using var reader = await query.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                if (reader.GetString(0) != source.Database || reader.GetString(1) != "pgoutput" || reader.GetBoolean(2))
                    throw new WorkerException("Slot belongs to a different source/plugin or is active; refusing to reset it.");
                if (!reset) throw new WorkerException("Source slot already exists. Use snapshot --resnapshot for explicit recovery.");
            }
        }
        if (reset)
        {
            await using var drop = new NpgsqlCommand("SELECT pg_drop_replication_slot(slot_name) FROM pg_replication_slots WHERE slot_name=@slot AND database=current_database()", sourceConnection);
            drop.Parameters.AddWithValue("slot", source.Slot);
            await drop.ExecuteNonQueryAsync();
        }
        else if (await ReadCheckpointAsync(target, source.Name) is not null)
            throw new WorkerException("Checkpoint exists without a slot; use snapshot --resnapshot.");
        // No further command is sent on this replication session until snapshot import/copy completes.
        var slot = await replication.CreatePgOutputReplicationSlot(source.Slot, slotSnapshotInitMode: LogicalSlotSnapshotInitMode.Export);
        if (slot.SnapshotName is null || !Regex.IsMatch(slot.SnapshotName, "^[0-9a-fA-F]+-[0-9a-fA-F]+-[0-9]+$"))
            throw new WorkerException("PostgreSQL did not export a valid snapshot.");
        await using var snapshot = await sourceConnection.BeginTransactionAsync(IsolationLevel.RepeatableRead);
        await Program.ExecuteAsync(sourceConnection, $"SET TRANSACTION SNAPSHOT '{slot.SnapshotName}'", snapshot);
        Program.Log(new { status = "snapshot-imported", source = source.Name, lsn = slot.ConsistentPoint.ToString() });
        if (options.SnapshotHoldMilliseconds > 0) await Task.Delay(options.SnapshotHoldMilliseconds);
        await using var targetTransaction = await target.BeginTransactionAsync();
        long rows = 0;
        foreach (var table in source.Tables)
        {
            var targetTable = $"{Options.Quote("ingest_" + source.Name)}.{Options.Quote(table.Name)}";
            await Program.ExecuteAsync(target, $"DELETE FROM {targetTable}", targetTransaction);
            var columns = string.Join(',', table.Columns.Select(Options.Quote));
            await using var select = new NpgsqlCommand($"SELECT {columns} FROM public.{Options.Quote(table.Name)}", sourceConnection, snapshot);
            await using var reader = await select.ExecuteReaderAsync(CommandBehavior.SequentialAccess);
            await using var copy = await target.BeginBinaryImportAsync($"COPY {targetTable} ({columns}) FROM STDIN (FORMAT BINARY)");
            while (await reader.ReadAsync())
            {
                var values = new object[reader.FieldCount];
                reader.GetValues(values);
                if (Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(values)) > options.MaxBytes)
                    throw new WorkerException("Snapshot row exceeds configured byte bound; snapshot rolled back. Adjust the bound before explicit resnapshot.");
                await copy.WriteRowAsync(default, values);
                rows++;
            }
            await copy.CompleteAsync();
        }
        await using (var project = new NpgsqlCommand("SELECT sync.rebuild_projection(clock_timestamp())", target, targetTransaction)) await project.ExecuteNonQueryAsync();
        await SaveCheckpointAsync(target, targetTransaction, source, identity.SystemId, slot.ConsistentPoint, null, rows, true);
        Fail("before-target-commit");
        await targetTransaction.CommitAsync();
        Fail("after-target-commit-before-ack");
        await snapshot.CommitAsync();
        // The slot is not acknowledged before the snapshot and checkpoint are durable.
        Program.Log(new { status = "snapshotted", source = source.Name, rows, lsn = slot.ConsistentPoint.ToString() });
    }

    internal async Task SyncAsync(Source source, CancellationToken cancellation)
    {
        await using var target = await options.OpenAsync(options.DatalakeDatabase, cancellation);
        var checkpoint = await ReadCheckpointAsync(target, source.Name) ?? throw new WorkerException("No initial snapshot. Run snapshot first.");
        await using var control = await options.OpenAsync(source.Database, cancellation);
        await ValidateSchemaAsync(control, source);
        await using var replication = new LogicalReplicationConnection(options.ConnectionFor(source.Database));
        await replication.Open(cancellation);
        var identity = await replication.IdentifySystem(cancellation);
        if (checkpoint.Database != source.Database || checkpoint.SystemId != identity.SystemId || checkpoint.Slot != source.Slot)
            throw new WorkerException("Source identity changed; use snapshot --resnapshot.");
        await ValidateSlotAsync(control, source, checkpoint.Lsn, cancellation);
        replication.SetReplicationStatus(checkpoint.Lsn);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        var monitor = MonitorAsync(source, linked.Token);
        var consume = ConsumeAsync();
        var first = await Task.WhenAny(consume, monitor);
        if (first.IsFaulted) linked.Cancel();
        try { await Task.WhenAll(consume, monitor); }
        finally { linked.Cancel(); }

        async Task ConsumeAsync()
        {
            var changes = new List<Change>();
            long bytes = 0;
            var inTransaction = false;
            var committed = checkpoint.Lsn;
            var settings = new PgOutputReplicationOptions("vwp_datalake", PgOutputProtocolVersion.V1, binary: false, streamingMode: PgOutputStreamingMode.Off, messages: true);
            await foreach (var message in replication.StartReplication(new PgOutputReplicationSlot(source.Slot), settings, linked.Token, checkpoint.Lsn))
            {
                switch (message)
                {
                    case BeginMessage:
                        if (inTransaction) throw new WorkerException("Unexpected nested source transaction.");
                        inTransaction = true; changes.Clear(); bytes = 0; break;
                    case RelationMessage relation: ValidateRelation(source, relation); break;
                    case InsertMessage insert:
                        Add(await DecodeAsync(insert.Relation, insert.NewRow, 'I', linked.Token)); break;
                    case FullUpdateMessage update:
                        var oldFull = await DecodeAsync(update.Relation, update.OldRow, 'D', linked.Token);
                        var newFull = await DecodeAsync(update.Relation, update.NewRow, 'U', linked.Token);
                        if (oldFull.Id != newFull.Id) Add(oldFull);
                        Add(newFull); break;
                    case IndexUpdateMessage update:
                        var oldKey = await DecodeAsync(update.Relation, update.Key, 'D', linked.Token);
                        var newKey = await DecodeAsync(update.Relation, update.NewRow, 'U', linked.Token);
                        if (oldKey.Id != newKey.Id) Add(oldKey);
                        Add(newKey); break;
                    case UpdateMessage update:
                        Add(await DecodeAsync(update.Relation, update.NewRow, 'U', linked.Token)); break;
                    case KeyDeleteMessage delete:
                        Add(await DecodeAsync(delete.Relation, delete.Key, 'D', linked.Token)); break;
                    case FullDeleteMessage delete:
                        Add(await DecodeAsync(delete.Relation, delete.OldRow, 'D', linked.Token)); break;
                    case TruncateMessage: throw new WorkerException("TRUNCATE requires explicit snapshot --resnapshot.");
                    case LogicalDecodingMessage: break; // Only transaction commit advances the durable checkpoint.
                    case CommitMessage commit:
                        if (!inTransaction) throw new WorkerException("Commit without a source transaction.");
                        if (commit.TransactionEndLsn > committed)
                        {
                            await ApplyAsync(source, identity.SystemId, target, changes, commit.TransactionEndLsn,
                                commit.TransactionCommitTimestamp, linked.Token);
                            committed = commit.TransactionEndLsn;
                        }
                        changes.Clear(); bytes = 0; inTransaction = false;
                        // Never acknowledge WalEnd: it can include unread/incomplete transactions.
                        replication.SetReplicationStatus(committed);
                        await replication.SendStatusUpdate(linked.Token);
                        break;
                    default: throw new WorkerException("Unsupported replication message; capture stopped without acknowledgement.");
                }
            }
            void Add(Change change)
            {
                if (!inTransaction) throw new WorkerException("Row outside a source transaction.");
                bytes += Encoding.UTF8.GetByteCount(change.Payload) + 128;
                if (changes.Count >= options.MaxRows || bytes > options.MaxBytes)
                    throw new WorkerException("Source transaction exceeds bounded row/byte limits. No checkpoint or acknowledgement advanced; split producer transactions or increase configured bounds explicitly.");
                changes.Add(change);
            }
        }
    }

    private async Task MonitorAsync(Source source, CancellationToken cancellation)
    {
        await using var control = await options.OpenAsync(source.Database, cancellation);
        while (true)
        {
            await ValidateSlotAsync(control, source, null, cancellation);
            // Transactional logical heartbeats keep idle source slots progressing even when
            // only unrelated databases (including Datalake) generate WAL in this cluster.
            await using var heartbeat = new NpgsqlCommand("SELECT pg_logical_emit_message(true,'vwp_datalake','heartbeat')", control);
            await heartbeat.ExecuteScalarAsync(cancellation);
            await Task.Delay(options.PollMilliseconds, cancellation);
        }
    }

    private async Task ApplyAsync(Source source, string systemId, NpgsqlConnection target, List<Change> changes,
        NpgsqlLogSequenceNumber lsn, DateTime commitTime, CancellationToken cancellation)
    {
        await applyGate.WaitAsync(cancellation);
        try
        {
            await using var transaction = await target.BeginTransactionAsync(cancellation);
            var batch = Guid.NewGuid();
            if (changes.Count > 0)
            {
                await using (var copy = await target.BeginBinaryImportAsync("COPY sync.events(batch_id,sequence,source_name,table_name,action,row_id,payload) FROM STDIN(FORMAT BINARY)", cancellation))
                {
                    for (var i = 0; i < changes.Count; i++)
                    {
                        var change = changes[i];
                        await copy.StartRowAsync(cancellation);
                        await copy.WriteAsync(batch, NpgsqlDbType.Uuid, cancellation);
                        await copy.WriteAsync(i, NpgsqlDbType.Integer, cancellation);
                        await copy.WriteAsync(source.Name, NpgsqlDbType.Text, cancellation);
                        await copy.WriteAsync(change.Table, NpgsqlDbType.Text, cancellation);
                        await copy.WriteAsync(change.Action.ToString(), NpgsqlDbType.Char, cancellation);
                        await copy.WriteAsync(change.Id, NpgsqlDbType.Uuid, cancellation);
                        await copy.WriteAsync(change.Payload, NpgsqlDbType.Jsonb, cancellation);
                    }
                    await copy.CompleteAsync(cancellation);
                }
                await using var transform = new NpgsqlCommand("SELECT sync.apply_batch(@batch,@source,@commit,@max,@bytes)", target, transaction);
                transform.Parameters.AddWithValue("batch", batch);
                transform.Parameters.AddWithValue("source", source.Name);
                transform.Parameters.AddWithValue("commit", commitTime);
                transform.Parameters.AddWithValue("max", options.MaxProjectionRows);
                transform.Parameters.AddWithValue("bytes", options.MaxProjectionBytes);
                await transform.ExecuteNonQueryAsync(cancellation);
            }
            await SaveCheckpointAsync(target, transaction, source, systemId, lsn, commitTime, changes.Count, false, cancellation);
            if (changes.Count > 0) Fail("before-target-commit");
            await transaction.CommitAsync(cancellation);
            if (changes.Count > 0) Fail("after-target-commit-before-ack");
            if (changes.Count > 0) Program.Log(new { status = "applied", source = source.Name, rows = changes.Count, lsn = lsn.ToString(),
                lagMilliseconds = Math.Max(0, (DateTime.UtcNow - commitTime).TotalMilliseconds) });
        }
        finally { applyGate.Release(); }
    }

    private async Task<Change> DecodeAsync(RelationMessage relation, ReplicationTuple tuple, char action, CancellationToken cancellation)
    {
        var payload = new Dictionary<string, object?>();
        long bytes = 0;
        await foreach (var value in tuple.WithCancellation(cancellation))
        {
            bytes += Math.Max(value.Length, 0);
            if (bytes > options.MaxBytes) throw new WorkerException("Source row exceeds byte bound; no acknowledgement advanced.");
            if (value.IsUnchangedToastedValue) continue;
            payload[value.GetFieldName()] = value.IsDBNull ? null : await value.Get(cancellation);
        }
        if (!payload.TryGetValue("id", out var id) || !Guid.TryParse(Convert.ToString(id, System.Globalization.CultureInfo.InvariantCulture), out var guid))
            throw new WorkerException("Source row has no supported UUID replica identity.");
        // Non-key fields in pgoutput key DELETE tuples are null placeholders,
        // not row values. Only the replica identity belongs in a delete payload.
        return new Change(relation.RelationName, action, guid, action == 'D' ? JsonSerializer.Serialize(new { id = guid }) : JsonSerializer.Serialize(payload));
    }

    private static void ValidateRelation(Source source, RelationMessage relation)
    {
        var table = source.Tables.SingleOrDefault(x => x.Name == relation.RelationName);
        if (relation.Namespace != "public" || table is null || relation.Columns.Count != table.Columns.Length)
            throw new WorkerException("Source schema evolution is unsupported; align source/target code and explicitly resnapshot.");
        for (var i = 0; i < table.Columns.Length; i++)
            if (relation.Columns[i].ColumnName != table.Columns[i] || relation.Columns[i].DataTypeId != table.TypeIds[i] || relation.Columns[i].TypeModifier != table.TypeModifier(i))
                throw new WorkerException("Source column/type changed; align source/target schema and explicitly resnapshot.");
    }

    private static async Task ValidateSchemaAsync(NpgsqlConnection connection, Source source)
    {
        await using (var flags = new NpgsqlCommand("SELECT pubinsert AND pubupdate AND pubdelete AND pubtruncate FROM pg_publication WHERE pubname='vwp_datalake'", connection))
            if (await flags.ExecuteScalarAsync() is not true)
                throw new WorkerException("Publication must capture inserts, updates, deletes and truncates; explicit reconciliation is required.");
        await using var publication = new NpgsqlCommand("SELECT schemaname,tablename,rowfilter IS NOT NULL,attnames FROM pg_publication_tables WHERE pubname='vwp_datalake' ORDER BY tablename", connection);
        var published = new List<string>();
        await using (var reader = await publication.ExecuteReaderAsync())
            while (await reader.ReadAsync())
            {
                var table = source.Tables.SingleOrDefault(x => x.Name == reader.GetString(1));
                if (reader.GetBoolean(2) || table is null || !reader.GetFieldValue<string[]>(3).SequenceEqual(table.Columns))
                    throw new WorkerException("Publication row/column filters are unsupported; explicit schema reconciliation is required.");
                published.Add(reader.GetString(0) + "." + reader.GetString(1));
            }
        if (!published.SequenceEqual(source.Tables.Select(x => "public." + x.Name).Order()))
            throw new WorkerException("Publication tables differ from the supported schema; explicit schema reconciliation is required.");
        foreach (var table in source.Tables)
        {
            await using (var identity = new NpgsqlCommand("SELECT EXISTS(SELECT FROM pg_index i JOIN pg_class c ON c.oid=i.indrelid WHERE i.indrelid=@table::regclass AND i.indisprimary AND i.indnkeyatts=1 AND i.indkey[0]=1 AND c.relreplident IN ('d','f'))", connection))
            {
                identity.Parameters.AddWithValue("table", "public." + table.Name);
                if (await identity.ExecuteScalarAsync() is not true)
                    throw new WorkerException("Source must retain its single UUID id primary key and DEFAULT/FULL replica identity; explicit schema reconciliation is required.");
            }
            await using var query = new NpgsqlCommand("SELECT attname,atttypid::bigint,atttypmod FROM pg_attribute WHERE attrelid=@table::regclass AND attnum>0 AND NOT attisdropped ORDER BY attnum", connection);
            query.Parameters.AddWithValue("table", "public." + table.Name);
            await using var reader = await query.ExecuteReaderAsync();
            var index = 0;
            while (await reader.ReadAsync())
            {
                if (index >= table.Columns.Length || reader.GetString(0) != table.Columns[index] || reader.GetInt64(1) != table.TypeIds[index] || reader.GetInt32(2) != table.TypeModifier(index))
                    throw new WorkerException("Source schema evolution requires code/schema alignment and explicit resnapshot.");
                index++;
            }
            if (index != table.Columns.Length) throw new WorkerException("Source column missing; explicit schema reconciliation is required.");
        }
    }

    internal static async Task ValidateTargetAsync(Options options, NpgsqlConnection connection)
    {
        foreach (var source in options.Sources)
            foreach (var table in source.Tables)
                await ValidateTargetTableAsync(connection, "ingest_" + source.Name, table, []);
        await ValidateTargetTableAsync(connection, "reporting",
            new Table("account_operations", ["operation_id","account_id","account_name","account_status","account_missing",
                "description","amount","transaction_total","transaction_count","last_source_commit_at","materialized_at"],
                [2950,2950,1043,1043,16,1043,1700,1700,20,1184,1184]), ["account_name","account_status"]);
    }

    private static async Task ValidateTargetTableAsync(NpgsqlConnection connection, string schema, Table table, string[] nullableColumns)
    {
        await using (var identity = new NpgsqlCommand("SELECT EXISTS(SELECT FROM pg_index i WHERE i.indrelid=@table::regclass AND i.indisprimary AND i.indnkeyatts=1 AND i.indkey[0]=1)", connection))
        {
            identity.Parameters.AddWithValue("table", schema + "." + table.Name);
            if (await identity.ExecuteScalarAsync() is not true)
                throw new WorkerException("Datalake mirror/projection primary key changed; explicit target schema reconciliation is required.");
        }
        await using var query = new NpgsqlCommand("SELECT attname,atttypid::bigint,atttypmod,attnotnull FROM pg_attribute WHERE attrelid=@table::regclass AND attnum>0 AND NOT attisdropped ORDER BY attnum", connection);
        query.Parameters.AddWithValue("table", schema + "." + table.Name);
        await using var reader = await query.ExecuteReaderAsync();
        var index = 0;
        while (await reader.ReadAsync())
        {
            if (index >= table.Columns.Length || reader.GetString(0) != table.Columns[index] || reader.GetInt64(1) != table.TypeIds[index]
                || reader.GetInt32(2) != table.TypeModifier(index) || reader.GetBoolean(3) == nullableColumns.Contains(table.Columns[index]))
                throw new WorkerException("Datalake mirror/projection columns, precision or nullability changed; explicit target schema reconciliation is required.");
            index++;
        }
        if (index != table.Columns.Length) throw new WorkerException("Datalake mirror/projection column missing; explicit target schema reconciliation is required.");
    }

    private async Task ValidateSlotAsync(NpgsqlConnection connection, Source source, NpgsqlLogSequenceNumber? checkpoint, CancellationToken cancellation)
    {
        await using var query = new NpgsqlCommand("SELECT database,plugin,restart_lsn IS NULL OR wal_status='lost' OR invalidation_reason IS NOT NULL, coalesce(pg_wal_lsn_diff(pg_current_wal_lsn(),restart_lsn),0)::bigint,confirmed_flush_lsn FROM pg_replication_slots WHERE slot_name=@slot", connection);
        query.Parameters.AddWithValue("slot", source.Slot);
        await using var reader = await query.ExecuteReaderAsync(cancellation);
        if (!await reader.ReadAsync(cancellation) || reader.GetString(0) != source.Database || reader.GetString(1) != "pgoutput" || reader.GetBoolean(2))
            throw new WorkerException("Source slot is missing/invalid/unavailable; use snapshot --resnapshot for explicit recovery.");
        if (reader.GetInt64(3) > options.MaxSlotLagBytes)
            throw new WorkerException("Source slot WAL lag exceeds configured bound. Capture stopped; inspect retention/load before explicit resnapshot or raising the recovery bound.");
        if (checkpoint.HasValue && !reader.IsDBNull(4) && reader.GetFieldValue<NpgsqlLogSequenceNumber>(4) > checkpoint.Value)
            throw new WorkerException("Source slot advanced beyond durable Datalake checkpoint; explicit resnapshot is required.");
    }

    private static async Task<Checkpoint?> ReadCheckpointAsync(NpgsqlConnection target, string source)
    {
        await using var command = new NpgsqlCommand("SELECT database_name,system_id,slot_name,lsn FROM sync.checkpoints WHERE source_name=@source", target);
        command.Parameters.AddWithValue("source", source);
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() ? new(reader.GetString(0),reader.GetString(1),reader.GetString(2),reader.GetFieldValue<NpgsqlLogSequenceNumber>(3)) : null;
    }

    private static async Task SaveCheckpointAsync(NpgsqlConnection target, NpgsqlTransaction transaction, Source source, string systemId,
        NpgsqlLogSequenceNumber lsn, DateTime? commitTime, long rows, bool snapshot, CancellationToken cancellation = default)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO sync.checkpoints(source_name,database_name,system_id,slot_name,lsn,last_commit_at,applied_at,rows_applied,batches_applied)
            VALUES(@source,@database,@system,@slot,@lsn,@commit,clock_timestamp(),@rows,@batches)
            ON CONFLICT(source_name) DO UPDATE SET database_name=EXCLUDED.database_name,system_id=EXCLUDED.system_id,
             slot_name=EXCLUDED.slot_name,lsn=EXCLUDED.lsn,last_commit_at=EXCLUDED.last_commit_at,applied_at=EXCLUDED.applied_at,
             rows_applied=CASE WHEN @snapshot THEN EXCLUDED.rows_applied ELSE sync.checkpoints.rows_applied+EXCLUDED.rows_applied END,
             batches_applied=CASE WHEN @snapshot THEN EXCLUDED.batches_applied ELSE sync.checkpoints.batches_applied+EXCLUDED.batches_applied END
            """, target, transaction);
        command.Parameters.AddWithValue("source", source.Name);
        command.Parameters.AddWithValue("database", source.Database);
        command.Parameters.AddWithValue("system", systemId);
        command.Parameters.AddWithValue("slot", source.Slot);
        command.Parameters.AddWithValue("lsn", NpgsqlDbType.PgLsn, lsn);
        command.Parameters.AddWithValue("commit", NpgsqlDbType.TimestampTz, (object?)commitTime ?? DBNull.Value);
        command.Parameters.AddWithValue("rows", rows);
        command.Parameters.AddWithValue("batches", rows > 0 ? 1L : 0L);
        command.Parameters.AddWithValue("snapshot", snapshot);
        await command.ExecuteNonQueryAsync(cancellation);
    }

    internal static async Task ObserveAsync(Options options, NpgsqlConnection target)
    {
        await using var settings = new NpgsqlCommand("SELECT retention_years FROM sync.settings WHERE singleton", target);
        var persistedRetention = (int)(await settings.ExecuteScalarAsync() ?? throw new WorkerException("Datalake retention settings are missing; explicit bootstrap reconciliation is required."));
        var sources = new List<object>();
        foreach (var source in options.Sources)
        {
            await using var connection = await options.OpenAsync(source.Database);
            await using var command = new NpgsqlCommand("SELECT slot_name,wal_status,invalidation_reason,coalesce(pg_wal_lsn_diff(pg_current_wal_lsn(),restart_lsn),0)::bigint FROM pg_replication_slots WHERE slot_name=@slot", connection);
            command.Parameters.AddWithValue("slot", source.Slot);
            string? status = null, invalidation = null; long? bytes = null;
            await using (var reader = await command.ExecuteReaderAsync())
                if (await reader.ReadAsync()) { status=reader.IsDBNull(1)?null:reader.GetString(1); invalidation=reader.IsDBNull(2)?null:reader.GetString(2); bytes=reader.GetInt64(3); }
            await using var checkpoint = new NpgsqlCommand("SELECT lsn::text,last_commit_at,applied_at,rows_applied,batches_applied FROM sync.checkpoints WHERE source_name=@source", target);
            checkpoint.Parameters.AddWithValue("source",source.Name);
            await using var checkpointReader = await checkpoint.ExecuteReaderAsync();
            if (await checkpointReader.ReadAsync()) sources.Add(new { source=source.Name, database=source.Database, slot=source.Slot, walStatus=status,
                invalidationReason=invalidation, slotLagBytes=bytes, checkpoint=checkpointReader.GetString(0),
                lastCommitAt=checkpointReader.IsDBNull(1)?(DateTime?)null:checkpointReader.GetDateTime(1), appliedAt=checkpointReader.GetDateTime(2),
                rowsApplied=checkpointReader.GetInt64(3), batchesApplied=checkpointReader.GetInt64(4),
                checkpointAgeSeconds=(DateTime.UtcNow-checkpointReader.GetDateTime(2)).TotalSeconds });
            else sources.Add(new {source=source.Name, database=source.Database, slot=source.Slot, walStatus=status, initialized=false});
        }
        Program.Log(new {status="observed", retentionYears=persistedRetention, configuredRetentionYears=options.RetentionYears, purgeEnabled=false, sources});
    }

    private void Fail(string point)
    {
        if (options.Failpoint == point) throw new WorkerException($"Injected crash boundary: {point}.");
    }
}
