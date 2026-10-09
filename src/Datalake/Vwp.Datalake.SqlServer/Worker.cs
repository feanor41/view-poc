using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace Vwp.Datalake.SqlServer;

internal sealed class WorkerException(string message) : Exception(message);

internal sealed class Worker(Settings settings)
{
    private string Target => Settings.Quote(settings.DatalakeDatabase);
    private SqlConnection Connection(string database) => new(new SqlConnectionStringBuilder(settings.ConnectionString)
    { InitialCatalog = database }.ConnectionString);

    private SqlCommand Command(SqlConnection connection, string sql, SqlTransaction? transaction = null,
        params (string Name, object Value)[] parameters)
    {
        var command = new SqlCommand(sql, connection, transaction) { CommandTimeout = settings.CommandTimeoutSeconds };
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return command;
    }

    private async Task ExecuteAsync(SqlConnection connection, string sql, CancellationToken token,
        SqlTransaction? transaction = null, params (string Name, object Value)[] parameters)
    {
        await using var command = Command(connection, sql, transaction, parameters);
        await command.ExecuteNonQueryAsync(token);
    }

    private async Task<T> ScalarAsync<T>(SqlConnection connection, string sql, CancellationToken token,
        SqlTransaction? transaction = null, params (string Name, object Value)[] parameters)
    {
        await using var command = Command(connection, sql, transaction, parameters);
        var value = await command.ExecuteScalarAsync(token);
        if (value is null or DBNull) throw new WorkerException("Expected database metadata is missing; run explicit bootstrap.");
        return (T)Convert.ChangeType(value, typeof(T));
    }

    internal async Task BootstrapAsync(CancellationToken token)
    {
        await using var connection = Connection("master");
        await connection.OpenAsync(token);
        foreach (var database in new[] { settings.AccountsDatabase, settings.FinancialsDatabase, settings.DatalakeDatabase })
        {
            await ExecuteAsync(connection, "IF DB_ID(@name) IS NULL BEGIN DECLARE @ddl nvarchar(max)=N'CREATE DATABASE '+QUOTENAME(@name); EXEC(@ddl); END;", token,
                parameters: [("@name", database)]);
            await ExecuteAsync(connection, $"ALTER DATABASE {Settings.Quote(database)} SET ALLOW_SNAPSHOT_ISOLATION ON;", token);
        }
        foreach (var source in new[] { settings.AccountsDatabase, settings.FinancialsDatabase })
        {
            await ExecuteAsync(connection, $"""
                IF NOT EXISTS(SELECT 1 FROM sys.change_tracking_databases WHERE database_id=DB_ID(@source))
                  ALTER DATABASE {Settings.Quote(source)} SET CHANGE_TRACKING=ON (CHANGE_RETENTION={settings.CaptureRetentionDays} DAYS,AUTO_CLEANUP=ON);
                ELSE ALTER DATABASE {Settings.Quote(source)} SET CHANGE_TRACKING (CHANGE_RETENTION={settings.CaptureRetentionDays} DAYS,AUTO_CLEANUP=ON);
                """, token, parameters: [("@source", source)]);
            await ExecuteAsync(connection, $"""
                USE {Settings.Quote(source)};
                IF OBJECT_ID(N'dbo.VwpEtlSourceIdentity',N'U') IS NULL
                  CREATE TABLE dbo.VwpEtlSourceIdentity(Id int NOT NULL PRIMARY KEY CHECK(Id=1),Epoch uniqueidentifier NOT NULL);
                IF NOT EXISTS(SELECT 1 FROM dbo.VwpEtlSourceIdentity) INSERT dbo.VwpEtlSourceIdentity VALUES(1,NEWID());
                """, token);
        }
        foreach (var table in Table.All)
        {
            var source = Settings.Quote(table.AccountsSource ? settings.AccountsDatabase : settings.FinancialsDatabase);
            await ExecuteAsync(connection, $"""
                USE {source};
                IF OBJECT_ID(N'dbo.{table.Name}',N'U') IS NULL CREATE TABLE dbo.{table.Identifier}({table.Definition},PRIMARY KEY(Id));
                IF NOT EXISTS(SELECT 1 FROM sys.change_tracking_tables WHERE object_id=OBJECT_ID(N'dbo.{table.Name}'))
                  ALTER TABLE dbo.{table.Identifier} ENABLE CHANGE_TRACKING;
                """, token);
        }
        await ExecuteAsync(connection, $"USE {Target}; " + Schema.Metadata, token);
        foreach (var table in Table.All)
            await ExecuteAsync(connection, $"IF OBJECT_ID(N'dbo.{table.Name}',N'U') IS NULL CREATE TABLE dbo.{table.Identifier}({table.Definition},PRIMARY KEY(Id));", token);
        await ExecuteAsync(connection, """
            IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID('dbo.Operations') AND name='IX_Operations_AccountId')
              CREATE INDEX IX_Operations_AccountId ON dbo.Operations(AccountId);
            IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID('dbo.Transactions') AND name='IX_Transactions_OperationId')
              CREATE INDEX IX_Transactions_OperationId ON dbo.Transactions(OperationId);
            IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID('dbo.AccountHistory') AND name='IX_AccountHistory_OccurredAt')
              CREATE INDEX IX_AccountHistory_OccurredAt ON dbo.AccountHistory(OccurredAt,Id);
            IF EXISTS(SELECT 1 FROM etl.Settings WHERE AccountsSourceName IS NOT NULL AND (AccountsSourceName<>@accounts OR FinancialsSourceName<>@financials))
              THROW 51005,'Datalake is bound to different sources; use a distinct target database.',1;
            UPDATE etl.Settings SET RetentionYears=@years,AccountsSourceName=@accounts,FinancialsSourceName=@financials WHERE Id=1;
            """, token, parameters: [("@years", settings.RetentionYears), ("@accounts", settings.AccountsDatabase), ("@financials", settings.FinancialsDatabase)]);
        Console.WriteLine(JsonSerializer.Serialize(new { status = "bootstrapped", retentionYears = settings.RetentionYears, purgeEnabled = false }));
    }

    internal async Task<bool> SyncAsync(bool resnapshot, CancellationToken token)
    {
        await using var connection = Connection(settings.DatalakeDatabase);
        await connection.OpenAsync(token);
        await ExecuteAsync(connection, "SET XACT_ABORT ON;", token);
        var sourceBinding = await ScalarAsync<int>(connection, "SELECT COUNT(*) FROM etl.Settings WHERE AccountsSourceName=@accounts AND FinancialsSourceName=@financials;", token,
            parameters: [("@accounts", settings.AccountsDatabase), ("@financials", settings.FinancialsDatabase)]);
        if (sourceBinding != 1) throw new WorkerException("Datalake is not bootstrapped for these source names; use a distinct target database.");
        var acquired = await ScalarAsync<int>(connection, "DECLARE @result int; EXEC @result=sys.sp_getapplock @Resource=N'VwpDatalakeWriter',@LockMode='Exclusive',@LockOwner='Session',@LockTimeout=0; SELECT @result;", token);
        if (acquired < 0) throw new WorkerException("Another synchronization writer holds the Datalake lock.");
        var hold = Environment.GetEnvironmentVariable("VWP_ETL_HOLD_LOCK_SECONDS");
        if (hold is not null)
        {
            if (!int.TryParse(hold, out var seconds) || seconds < 0 || seconds > 300)
                throw new ArgumentException("VWP_ETL_HOLD_LOCK_SECONDS must be between 0 and 300.");
            Console.WriteLine(JsonSerializer.Serialize(new { eventName = "writer-lock-held", holdSeconds = seconds }));
            await Task.Delay(TimeSpan.FromSeconds(seconds), token);
        }
        if (resnapshot)
        {
            await ExecuteAsync(connection, "SET TRANSACTION ISOLATION LEVEL SNAPSHOT;", token);
            await using (var validation = (SqlTransaction)await connection.BeginTransactionAsync(token))
            {
                await ValidateSchemaAsync(connection, validation, token);
                await validation.CommitAsync(token);
            }
            await ExecuteAsync(connection, "SET TRANSACTION ISOLATION LEVEL READ COMMITTED;", token);
            await ResetAsync(connection, token);
        }
        if (await ScalarAsync<int>(connection, "SELECT COUNT(*) FROM etl.Window;", token) == 0)
            await CaptureAsync(connection, token);
        await ApplyAsync(connection, token);
        await ObserveAsync(connection, token);
        return await ScalarAsync<int>(connection, "SELECT COUNT(*) FROM etl.Window;", token) != 0;
    }

    private async Task ResetAsync(SqlConnection connection, CancellationToken token)
    {
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        await ExecuteAsync(connection, "DELETE etl.StageRows; DELETE etl.ProjectionQueue; DELETE etl.Window; DELETE etl.WindowTables; DELETE etl.Checkpoints; DELETE etl.TableCheckpoints; DELETE dbo.AccountOperations;", token, transaction);
        foreach (var table in Table.All) await ExecuteAsync(connection, $"DELETE dbo.{table.Identifier};", token, transaction);
        await transaction.CommitAsync(token);
    }

    private async Task CaptureAsync(SqlConnection connection, CancellationToken token)
    {
        await ExecuteAsync(connection, "SET TRANSACTION ISOLATION LEVEL SNAPSHOT;", token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        await ValidateSchemaAsync(connection, transaction, token);
        var snapshot = await ScalarAsync<int>(connection, "SELECT COUNT(*) FROM etl.Checkpoints;", token, transaction) == 0;
        var sourceVersions = new Dictionary<string, (long Current, long Previous, Guid Epoch)>();
        foreach (var source in new[] { settings.AccountsDatabase, settings.FinancialsDatabase })
        {
            await using var command = Command(connection, $"""
                USE {Settings.Quote(source)};
                SELECT CHANGE_TRACKING_CURRENT_VERSION(),Epoch FROM dbo.VwpEtlSourceIdentity WHERE Id=1;
                """, transaction);
            await using var reader = await command.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token) || reader.IsDBNull(0)) throw new WorkerException("Source capture is unavailable; run explicit bootstrap.");
            var current = reader.GetInt64(0);
            var epoch = reader.GetGuid(1);
            await reader.DisposeAsync();
            var previous = -1L;
            if (!snapshot)
            {
                await using var checkpoint = Command(connection, $"SELECT Version,SourceEpoch FROM {Target}.etl.Checkpoints WHERE SourceName=@source;", transaction, ("@source", source));
                await using var checkpointReader = await checkpoint.ExecuteReaderAsync(token);
                if (!await checkpointReader.ReadAsync(token) || checkpointReader.GetGuid(1) != epoch)
                    throw new WorkerException("Source identity changed or checkpoint is incomplete; use sync --resnapshot explicitly.");
                previous = checkpointReader.GetInt64(0);
                if (previous > current) throw new WorkerException("Source version moved backwards; use sync --resnapshot explicitly.");
            }
            sourceVersions[source] = (current, previous, epoch);
        }
        var accounts = sourceVersions[settings.AccountsDatabase];
        var financials = sourceVersions[settings.FinancialsDatabase];
        var snapshotHold = Environment.GetEnvironmentVariable("VWP_ETL_HOLD_SNAPSHOT_SECONDS");
        if (snapshotHold is not null)
        {
            if (!int.TryParse(snapshotHold, out var seconds) || seconds < 0 || seconds > 300)
                throw new ArgumentException("VWP_ETL_HOLD_SNAPSHOT_SECONDS must be between 0 and 300.");
            // Test hook: concurrent writes after the captured version must be caught by the next window.
            Console.WriteLine(JsonSerializer.Serialize(new { eventName = "snapshot-boundary", holdSeconds = seconds }));
            await Task.Delay(TimeSpan.FromSeconds(seconds), token);
        }
        await ExecuteAsync(connection, $"""
            USE {Target};
            INSERT etl.Window(Id,AccountsVersion,FinancialsVersion,AccountsEpoch,FinancialsEpoch,IsSnapshot,CapturedAt)
              VALUES(1,@av,@fv,@ae,@fe,@snapshot,SYSUTCDATETIME());
            """, token, transaction, ("@av", accounts.Current), ("@fv", financials.Current), ("@ae", accounts.Epoch), ("@fe", financials.Epoch), ("@snapshot", snapshot));
        foreach (var table in Table.All)
        {
            var source = table.AccountsSource ? settings.AccountsDatabase : settings.FinancialsDatabase;
            var version = sourceVersions[source];
            long beginVersion;
            int sourceTableId;
            await using (var tableMetadata = Command(connection, $"""
                USE {Settings.Quote(source)};
                SELECT object_id,begin_version FROM sys.change_tracking_tables WHERE object_id=OBJECT_ID(N'dbo.{table.Name}');
                """, transaction))
            await using (var metadataReader = await tableMetadata.ExecuteReaderAsync(token))
            {
                if (!await metadataReader.ReadAsync(token) || metadataReader.IsDBNull(1))
                    throw new WorkerException("A source table is not tracked; run explicit bootstrap and resnapshot.");
                sourceTableId = metadataReader.GetInt32(0);
                beginVersion = metadataReader.GetInt64(1);
            }
            if (!snapshot)
            {
                var identityMatches = await ScalarAsync<int>(connection, $"""
                    SELECT COUNT(*) FROM {Target}.etl.TableCheckpoints
                    WHERE TableName=@table AND SourceTableId=@id AND BeginVersion=@begin;
                    """, token, transaction, ("@table", table.Name), ("@id", sourceTableId), ("@begin", beginVersion));
                if (identityMatches != 1)
                    throw new WorkerException("Source table capture identity changed or was truncated; use sync --resnapshot explicitly.");
                var minimum = await ScalarAsync<long>(connection, $"USE {Settings.Quote(source)}; SELECT CONVERT(bigint,CHANGE_TRACKING_MIN_VALID_VERSION(OBJECT_ID(N'dbo.{table.Name}')));", token, transaction);
                if (version.Previous < minimum) throw new WorkerException("Capture retention expired or table was reset; use sync --resnapshot explicitly.");
            }
            await ExecuteAsync(connection, $"INSERT {Target}.etl.WindowTables(TableName,SourceTableId,BeginVersion) VALUES(@table,@id,@begin);", token,
                transaction, ("@table", table.Name), ("@id", sourceTableId), ("@begin", beginVersion));
            var count = await ScalarAsync<long>(connection, $"SELECT COUNT_BIG(*) FROM {Target}.etl.StageRows;", token, transaction);
            var columns = string.Join(",", table.Fields.Select(field => "S." + Settings.Quote(field.Name)));
            var from = snapshot ? $"dbo.{table.Identifier} S" : $"CHANGETABLE(CHANGES dbo.{table.Identifier},@previous) C LEFT JOIN dbo.{table.Identifier} S ON S.Id=C.Id";
            await ExecuteAsync(connection, $"""
                USE {Settings.Quote(source)};
                INSERT {Target}.etl.StageRows(TableName,RowId,Operation,Payload,ByteCount)
                SELECT TOP (@remaining) @table,{(snapshot ? "S.Id" : "C.Id")},{(snapshot ? "'I'" : "C.SYS_CHANGE_OPERATION")},P.Payload,DATALENGTH(P.Payload)+128
                FROM {from}
                CROSS APPLY(SELECT (SELECT {columns} FOR JSON PATH,WITHOUT_ARRAY_WRAPPER) AS Payload) P;
                """, token, transaction, ("@remaining", settings.MaxWindowRows - count + 1), ("@table", table.Name), ("@previous", version.Previous));
            await ExecuteAsync(connection, $"""
                USE {Target};
                IF (SELECT COUNT_BIG(*) FROM etl.StageRows)>@rows OR (SELECT COALESCE(SUM(ByteCount),0) FROM etl.StageRows)>@bytes
                  THROW 51001,'Staging window safety budget exceeded; increase budget or reduce source window explicitly.',1;
                IF EXISTS(SELECT 1 FROM etl.StageRows WHERE ByteCount>@batchBytes)
                  THROW 51002,'A source row exceeds the batch byte budget; increase batch byte budget explicitly.',1;
                """, token, transaction, ("@rows", settings.MaxWindowRows), ("@bytes", settings.MaxWindowBytes), ("@batchBytes", settings.MaxBytes));
        }
        await transaction.CommitAsync(token);
        await ExecuteAsync(connection, $"USE {Target}; SET TRANSACTION ISOLATION LEVEL READ COMMITTED;", token);
        Fault("after-stage-commit");
    }

    private void Fault(string point)
    {
        if (settings.Fault == point) throw new WorkerException($"Injected crash boundary: {point}.");
    }

    private async Task ApplyAsync(SqlConnection connection, CancellationToken token)
    {
        // Create the temporary table in the session batch, outside parameterized command scope.
        await ExecuteAsync(connection, "CREATE TABLE #Batch(Sequence bigint NOT NULL PRIMARY KEY,TableName nvarchar(128) NOT NULL,RowId uniqueidentifier NOT NULL,Operation char(1) NOT NULL,Payload nvarchar(max) NOT NULL);", token);
        await using (var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token))
        {
            await ValidateSchemaAsync(connection, transaction, token);
            await ExecuteAsync(connection, Schema.SelectBatch, token, transaction, ("@rows", settings.MaxRows), ("@bytes", settings.MaxBytes));
            foreach (var table in Table.All)
            {
                if (table.Name == "Accounts")
                    await ExecuteAsync(connection, """
                        INSERT etl.ProjectionQueue(OperationId)
                        SELECT O.Id FROM dbo.Operations O JOIN #Batch B ON O.AccountId=B.RowId AND B.TableName=N'Accounts'
                        WHERE NOT EXISTS(SELECT 1 FROM etl.ProjectionQueue Q WHERE Q.OperationId=O.Id);
                        """, token, transaction);
                if (table.Name == "Operations")
                    await ExecuteAsync(connection, """
                        INSERT etl.ProjectionQueue(OperationId)
                        SELECT B.RowId FROM #Batch B WHERE B.TableName=N'Operations'
                        AND NOT EXISTS(SELECT 1 FROM etl.ProjectionQueue Q WHERE Q.OperationId=B.RowId);
                        """, token, transaction);
                await ExecuteAsync(connection, $"""
                    DELETE T FROM dbo.{table.Identifier} T JOIN #Batch B ON B.RowId=T.Id WHERE B.TableName=@table AND B.Operation='D';
                    UPDATE T SET {table.Updates}
                      FROM dbo.{table.Identifier} T JOIN #Batch B ON B.RowId=T.Id
                      CROSS APPLY OPENJSON(B.Payload) WITH({table.JsonDefinition}) J
                      WHERE B.TableName=@table AND B.Operation<>'D';
                    INSERT dbo.{table.Identifier}({table.ColumnNames})
                      SELECT {string.Join(",", table.Fields.Select(field => "J." + Settings.Quote(field.Name)))} FROM #Batch B
                      CROSS APPLY OPENJSON(B.Payload) WITH({table.JsonDefinition}) J
                      WHERE B.TableName=@table AND B.Operation<>'D' AND NOT EXISTS(SELECT 1 FROM dbo.{table.Identifier} T WHERE T.Id=B.RowId);
                    """, token, transaction, ("@table", table.Name));
            }
            await ExecuteAsync(connection, "DELETE S FROM etl.StageRows S JOIN #Batch B ON B.Sequence=S.Sequence;", token, transaction);
            await ExecuteAsync(connection, "IF (SELECT COUNT_BIG(*) FROM etl.ProjectionQueue)>@maximum THROW 51004,'Derived fanout safety budget exceeded; increase window row budget explicitly.',1;", token, transaction, ("@maximum", settings.MaxWindowRows));
            // Derived fanout is drained separately, rather than rebuilding every Operation for each batch.
            await ExecuteAsync(connection, Schema.ApplyProjection, token, transaction, ("@rows", settings.MaxRows), ("@bytes", settings.MaxBytes));
            Fault("before-apply-commit");
            await transaction.CommitAsync(token);
        }
        Fault("after-apply-commit");
        if (await ScalarAsync<long>(connection, "SELECT (SELECT COUNT_BIG(*) FROM etl.StageRows)+(SELECT COUNT_BIG(*) FROM etl.ProjectionQueue);", token) != 0) return;
        await using var checkpointTransaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        await ExecuteAsync(connection, """
            UPDATE C SET Version=CASE WHEN C.SourceName=@accounts THEN W.AccountsVersion ELSE W.FinancialsVersion END,
              SourceEpoch=CASE WHEN C.SourceName=@accounts THEN W.AccountsEpoch ELSE W.FinancialsEpoch END,AppliedAt=SYSUTCDATETIME()
              FROM etl.Checkpoints C CROSS JOIN etl.Window W;
            INSERT etl.Checkpoints(SourceName,Version,SourceEpoch,AppliedAt)
              SELECT @accounts,AccountsVersion,AccountsEpoch,SYSUTCDATETIME() FROM etl.Window
              WHERE NOT EXISTS(SELECT 1 FROM etl.Checkpoints WHERE SourceName=@accounts);
            INSERT etl.Checkpoints(SourceName,Version,SourceEpoch,AppliedAt)
              SELECT @financials,FinancialsVersion,FinancialsEpoch,SYSUTCDATETIME() FROM etl.Window
              WHERE NOT EXISTS(SELECT 1 FROM etl.Checkpoints WHERE SourceName=@financials);
            UPDATE etl.Settings SET LastWindowCapturedAt=W.CapturedAt,LastWindowAppliedAt=SYSUTCDATETIME() FROM etl.Settings S CROSS JOIN etl.Window W;
            DELETE etl.TableCheckpoints;
            INSERT etl.TableCheckpoints(TableName,SourceTableId,BeginVersion) SELECT TableName,SourceTableId,BeginVersion FROM etl.WindowTables;
            DELETE etl.WindowTables;
            DELETE etl.Window;
            """, token, checkpointTransaction, ("@accounts", settings.AccountsDatabase), ("@financials", settings.FinancialsDatabase));
        Fault("before-checkpoint-commit");
        await checkpointTransaction.CommitAsync(token);
        Fault("after-checkpoint-commit");
    }

    internal async Task ObserveAsync(CancellationToken token)
    {
        await using var connection = Connection(settings.DatalakeDatabase);
        await connection.OpenAsync(token);
        await ObserveAsync(connection, token);
    }

    private async Task ValidateSchemaAsync(SqlConnection connection, SqlTransaction transaction, CancellationToken token)
    {
        var contracts = Table.All.SelectMany(table => new[]
        {
            (Table: table, Database: table.AccountsSource ? settings.AccountsDatabase : settings.FinancialsDatabase),
            (Table: table, Database: settings.DatalakeDatabase)
        }).Append((Table.Reporting, settings.DatalakeDatabase));
        foreach (var (table, database) in contracts)
        {
            var matches = await ScalarAsync<int>(connection, $"""
                USE {Settings.Quote(database)};
                DECLARE @id int=OBJECT_ID(N'dbo.{table.Name}',N'U');
                SELECT CASE WHEN @id IS NOT NULL
                  AND (SELECT COUNT(*) FROM sys.columns WHERE object_id=@id)={table.Fields.Length}
                  AND NOT EXISTS(
                    SELECT 1 FROM (VALUES {table.ExpectedColumnsSql}) E(ColumnName,TypeName,MaxLength,PrecisionValue,ScaleValue,IsNullable)
                    LEFT JOIN sys.columns C ON C.object_id=@id AND C.name=E.ColumnName
                    LEFT JOIN sys.types T ON T.user_type_id=C.user_type_id
                    WHERE C.column_id IS NULL OR T.name<>E.TypeName OR T.is_user_defined<>0
                      OR C.max_length<>E.MaxLength OR C.precision<>E.PrecisionValue OR C.scale<>E.ScaleValue
                      OR C.is_nullable<>E.IsNullable OR C.is_computed<>0 OR C.is_identity<>0)
                  AND EXISTS(
                    SELECT 1 FROM sys.indexes I JOIN sys.index_columns K ON K.object_id=I.object_id AND K.index_id=I.index_id
                    JOIN sys.columns C ON C.object_id=K.object_id AND C.column_id=K.column_id
                    WHERE I.object_id=@id AND I.is_primary_key=1 AND I.is_disabled=0 AND K.key_ordinal=1 AND C.name=N'Id'
                      AND NOT EXISTS(SELECT 1 FROM sys.index_columns Extra WHERE Extra.object_id=I.object_id AND Extra.index_id=I.index_id AND Extra.key_ordinal>1))
                  THEN 1 ELSE 0 END;
                """, token, transaction);
            if (matches != 1)
                throw new WorkerException("Source or target mirror schema drift detected; restore the fixed table contract before explicit resnapshot.");
        }
        await ExecuteAsync(connection, $"USE {Target};", token, transaction);
    }

    private async Task ObserveAsync(SqlConnection connection, CancellationToken token)
    {
        await using var command = Command(connection, """
            SELECT CONVERT(bit,1) AS countsEstimated,
              (SELECT COALESCE(SUM(CONVERT(bigint,rows)),0) FROM sys.partitions WHERE object_id=OBJECT_ID(N'dbo.Accounts') AND index_id IN(0,1)) AS accounts,
              (SELECT COALESCE(SUM(CONVERT(bigint,rows)),0) FROM sys.partitions WHERE object_id=OBJECT_ID(N'dbo.Operations') AND index_id IN(0,1)) AS operations,
              (SELECT COALESCE(SUM(CONVERT(bigint,rows)),0) FROM sys.partitions WHERE object_id=OBJECT_ID(N'dbo.AccountOperations') AND index_id IN(0,1)) AS accountOperations,
              (SELECT COALESCE(SUM(CONVERT(bigint,rows)),0) FROM sys.partitions WHERE object_id=OBJECT_ID(N'dbo.Transactions') AND index_id IN(0,1)) AS transactions,
              (SELECT COALESCE(SUM(CONVERT(bigint,rows)),0) FROM sys.partitions WHERE object_id=OBJECT_ID(N'dbo.AccountHistory') AND index_id IN(0,1)) AS accountHistory,
              (SELECT COUNT_BIG(*) FROM etl.StageRows) AS stagedRows,
              (SELECT COALESCE(SUM(ByteCount),0) FROM etl.StageRows) AS stagedBytes,
              (SELECT COUNT_BIG(*) FROM etl.ProjectionQueue) AS pendingProjections,
              (SELECT COUNT(*) FROM etl.Window) AS pendingWindows,
              (SELECT DATEDIFF_BIG(millisecond,CapturedAt,SYSUTCDATETIME()) FROM etl.Window) AS pendingWindowAgeMs,
              LastWindowCapturedAt,LastWindowAppliedAt,RetentionYears,
              DATEDIFF_BIG(millisecond,LastWindowCapturedAt,LastWindowAppliedAt) AS lastWindowApplyMs
            FROM etl.Settings WHERE Id=1;
            """);
        await using var reader = await command.ExecuteReaderAsync(token);
        if (await reader.ReadAsync(token))
        {
            var values = Enumerable.Range(0, reader.FieldCount).ToDictionary(reader.GetName, index =>
                reader.IsDBNull(index) ? null : reader.GetValue(index) is DateTime date
                    ? (object)DateTime.SpecifyKind(date, DateTimeKind.Utc) : reader.GetValue(index));
            Console.WriteLine(JsonSerializer.Serialize(values));
        }
    }
}
