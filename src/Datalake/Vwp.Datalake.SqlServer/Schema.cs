namespace Vwp.Datalake.SqlServer;

internal static class Schema
{
    internal static readonly string Metadata = """
        IF SCHEMA_ID(N'etl') IS NULL EXEC(N'CREATE SCHEMA etl');
        IF OBJECT_ID(N'etl.Settings',N'U') IS NULL
        BEGIN
          CREATE TABLE etl.Settings(Id int NOT NULL PRIMARY KEY CHECK(Id=1),AccountsSourceName nvarchar(128) NULL,FinancialsSourceName nvarchar(128) NULL,RetentionYears int NOT NULL CHECK(RetentionYears>=5),LastWindowCapturedAt datetime2(7) NULL,LastWindowAppliedAt datetime2(7) NULL);
          INSERT etl.Settings(Id,RetentionYears) VALUES(1,5);
        END;
        IF OBJECT_ID(N'etl.Checkpoints',N'U') IS NULL CREATE TABLE etl.Checkpoints(SourceName nvarchar(128) NOT NULL PRIMARY KEY,Version bigint NOT NULL,SourceEpoch uniqueidentifier NOT NULL,AppliedAt datetime2(7) NOT NULL);
        IF OBJECT_ID(N'etl.Window',N'U') IS NULL CREATE TABLE etl.Window(Id int NOT NULL PRIMARY KEY CHECK(Id=1),AccountsVersion bigint NOT NULL,FinancialsVersion bigint NOT NULL,AccountsEpoch uniqueidentifier NOT NULL,FinancialsEpoch uniqueidentifier NOT NULL,IsSnapshot bit NOT NULL,CapturedAt datetime2(7) NOT NULL);
        IF OBJECT_ID(N'etl.WindowTables',N'U') IS NULL CREATE TABLE etl.WindowTables(TableName nvarchar(128) NOT NULL PRIMARY KEY,SourceTableId int NOT NULL,BeginVersion bigint NOT NULL);
        IF OBJECT_ID(N'etl.TableCheckpoints',N'U') IS NULL CREATE TABLE etl.TableCheckpoints(TableName nvarchar(128) NOT NULL PRIMARY KEY,SourceTableId int NOT NULL,BeginVersion bigint NOT NULL);
        IF OBJECT_ID(N'etl.StageRows',N'U') IS NULL CREATE TABLE etl.StageRows(Sequence bigint IDENTITY NOT NULL PRIMARY KEY,TableName nvarchar(128) NOT NULL,RowId uniqueidentifier NOT NULL,Operation char(1) NOT NULL,Payload nvarchar(max) NOT NULL,ByteCount bigint NOT NULL,UNIQUE(TableName,RowId));
        IF OBJECT_ID(N'etl.ProjectionQueue',N'U') IS NULL CREATE TABLE etl.ProjectionQueue(OperationId uniqueidentifier NOT NULL PRIMARY KEY);
        """ + $"IF OBJECT_ID(N'dbo.AccountOperations',N'U') IS NULL CREATE TABLE dbo.{Table.Reporting.Identifier}({Table.Reporting.Definition},PRIMARY KEY(Id));";

    internal const string SelectBatch = """
        DELETE #Batch;
        ;WITH Candidate AS (SELECT TOP (@rows) * FROM etl.StageRows ORDER BY Sequence),
        Sized AS (SELECT *,SUM(ByteCount) OVER(ORDER BY Sequence ROWS UNBOUNDED PRECEDING) AS RunningBytes FROM Candidate)
        INSERT #Batch(Sequence,TableName,RowId,Operation,Payload) SELECT Sequence,TableName,RowId,Operation,Payload FROM Sized WHERE RunningBytes<=@bytes;
        """;

    internal const string ApplyProjection = """
        IF OBJECT_ID('tempdb..#Projection') IS NOT NULL DROP TABLE #Projection;
        ;WITH Candidate AS (
          SELECT TOP (@rows) Q.OperationId,128+COALESCE(DATALENGTH(A.Name),0)+COALESCE(DATALENGTH(A.Status),0)+COALESCE(DATALENGTH(O.Description),0) AS Bytes
          FROM etl.ProjectionQueue Q LEFT JOIN dbo.Operations O ON O.Id=Q.OperationId LEFT JOIN dbo.Accounts A ON A.Id=O.AccountId ORDER BY Q.OperationId),
        Sized AS (SELECT *,SUM(CONVERT(bigint,Bytes)) OVER(ORDER BY OperationId ROWS UNBOUNDED PRECEDING) AS RunningBytes FROM Candidate)
        SELECT OperationId INTO #Projection FROM Sized WHERE RunningBytes<=@bytes;
        IF EXISTS(SELECT 1 FROM etl.ProjectionQueue) AND NOT EXISTS(SELECT 1 FROM #Projection)
          THROW 51003,'A derived row exceeds the batch byte budget; increase batch byte budget explicitly.',1;
        DELETE R FROM dbo.AccountOperations R JOIN #Projection P ON P.OperationId=R.Id;
        INSERT dbo.AccountOperations(Id,AccountId,AccountName,AccountStatus,AccountMissing,Description,Amount,SourceAccountCreatedAt,ReportedAt)
          SELECT O.Id,O.AccountId,A.Name,A.Status,CONVERT(bit,CASE WHEN A.Id IS NULL THEN 1 ELSE 0 END),O.Description,O.Amount,A.CreatedAt,SYSUTCDATETIME()
          FROM #Projection P JOIN dbo.Operations O ON O.Id=P.OperationId LEFT JOIN dbo.Accounts A ON A.Id=O.AccountId;
        DELETE Q FROM etl.ProjectionQueue Q JOIN #Projection P ON P.OperationId=Q.OperationId;
        """;
}
