using System.Text.RegularExpressions;
using Npgsql;

namespace Vwp.Datalake.Postgres;

internal sealed record Table(string Name, string[] Columns, uint[] TypeIds)
{
    internal int TypeModifier(int index) => Columns[index] switch
    {
        "name" => Name == "payment_means" ? 104 : 204,
        "status" => 44,
        "account_name" => 204,
        "account_status" => 44,
        "description" => Name == "account_history" ? 1004 : 504,
        "amount" => (18 << 16) + 2 + 4,
        _ => -1
    };
}
internal sealed record Source(string Name, string Database, string Slot, Table[] Tables);

internal sealed record Options
{
    internal required string ConnectionString { get; init; }
    internal required string DatalakeDatabase { get; init; }
    internal required Source[] Sources { get; init; }
    internal int MaxRows { get; init; }
    internal int MaxProjectionRows { get; init; }
    internal long MaxProjectionBytes { get; init; }
    internal long MaxBytes { get; init; }
    internal int PollMilliseconds { get; init; }
    internal long MaxSlotLagBytes { get; init; }
    internal int RetentionYears { get; init; }
    internal int LockHoldMilliseconds { get; init; }
    internal int SnapshotHoldMilliseconds { get; init; }
    internal string? Failpoint { get; init; }

    internal static Options Load()
    {
        var connection = Environment.GetEnvironmentVariable("VWP_POSTGRES_CONNECTION") ?? throw new WorkerException("Set VWP_POSTGRES_CONNECTION through the environment.");
        try { _ = new NpgsqlConnectionStringBuilder(connection); }
        catch { throw new WorkerException("PostgreSQL connection configuration is invalid."); }
        var prefix = Name("VWP_POSTGRES_SLOT_PREFIX", "vwp_datalake");
        if (!Regex.IsMatch(prefix, "^[a-z0-9_]{1,45}$")) throw new WorkerException("Slot prefix must use lowercase letters, digits and underscores, max 45 characters.");
        var result = new Options
        {
            ConnectionString = connection, DatalakeDatabase = Name("VWP_POSTGRES_DATALAKE_DATABASE", "datalake"),
            MaxRows = (int)Number("VWP_ETL_MAX_ROWS", 10000, 1, 1000000),
            MaxProjectionRows = (int)Number("VWP_ETL_MAX_PROJECTION_ROWS", 100000, 1, 10000000),
            MaxProjectionBytes = Number("VWP_ETL_MAX_PROJECTION_BYTES", 8388608, 1024, 1073741824),
            MaxBytes = Number("VWP_ETL_MAX_BYTES", 8388608, 1024, 1073741824),
            PollMilliseconds = (int)Number("VWP_ETL_POLL_MS", 30000, 50, 300000),
            MaxSlotLagBytes = Number("VWP_ETL_MAX_SLOT_LAG_BYTES", 536870912, 1024, long.MaxValue),
            RetentionYears = (int)Number("VWP_ETL_RETENTION_YEARS", 5, 5, 1000),
            LockHoldMilliseconds = (int)Number("VWP_ETL_LOCK_HOLD_MS", 0, 0, 300000),
            SnapshotHoldMilliseconds = (int)Number("VWP_ETL_SNAPSHOT_HOLD_MS", 0, 0, 300000),
            Failpoint = Environment.GetEnvironmentVariable("VWP_ETL_FAILPOINT"),
            Sources = [
                new("accounts", Name("VWP_POSTGRES_ACCOUNTS_DATABASE", "vwp_etl_accounts"), prefix + "_accounts", [
                    new("accounts", ["id","name","status","created_at"], [2950,1043,1043,1184]),
                    new("account_history", ["id","account_id","description","occurred_at"], [2950,2950,1043,1184])]),
                new("financials", Name("VWP_POSTGRES_FINANCIALS_DATABASE", "vwp_etl_financials"), prefix + "_financials", [
                    new("operations", ["id","account_id","description","amount"], [2950,2950,1043,1700]),
                    new("payment_means", ["id","name"], [2950,1043]),
                    new("transactions", ["id","operation_id","payment_means_id","amount","occurred_at"], [2950,2950,2950,1700,1184]),
                    new("collection_orders", ["id","operation_id","due_at"], [2950,2950,1184])])]
        };
        if (result.Sources.Select(x => x.Database).Append(result.DatalakeDatabase).Distinct().Count() != 3)
            throw new WorkerException("Accounts, Financials and Datalake must be separate databases.");
        if (result.Failpoint is not (null or "" or "before-target-commit" or "after-target-commit-before-ack"))
            throw new WorkerException("Unknown failpoint.");
        return result;
    }

    internal string ConnectionFor(string database) => new NpgsqlConnectionStringBuilder(ConnectionString)
    { Database = database, IncludeErrorDetail = false, ApplicationName = "vwp-datalake", Pooling = false }.ConnectionString;

    internal async Task<NpgsqlConnection> OpenAsync(string database, CancellationToken cancellation = default)
    {
        var connection = new NpgsqlConnection(ConnectionFor(database));
        try { await connection.OpenAsync(cancellation); return connection; }
        catch { await connection.DisposeAsync(); throw; }
    }

    internal static string Quote(string name) => '"' + name.Replace("\"", "\"\"") + '"';
    private static string Name(string key, string fallback)
    {
        var value = Environment.GetEnvironmentVariable(key) ?? fallback;
        if (!Regex.IsMatch(value, "^[a-zA-Z][a-zA-Z0-9_]{0,62}$")) throw new WorkerException($"Invalid identifier in {key}.");
        return value;
    }
    private static long Number(string key, long fallback, long minimum, long maximum)
    {
        var text = Environment.GetEnvironmentVariable(key);
        if (text is null) return fallback;
        if (!long.TryParse(text, out var value) || value < minimum || value > maximum)
            throw new WorkerException($"Invalid numeric setting {key}.");
        return value;
    }
}
