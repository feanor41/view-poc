using Microsoft.Data.SqlClient;

namespace Vwp.Datalake.SqlServer;

internal sealed record Settings
{
    internal string ConnectionString { get; init; } = Required("VWP_SQLSERVER_CONNECTION");
    internal string AccountsDatabase { get; init; } = Name("VWP_SQLSERVER_ACCOUNTS_DATABASE", "VwpEtlAccounts");
    internal string FinancialsDatabase { get; init; } = Name("VWP_SQLSERVER_FINANCIALS_DATABASE", "VwpEtlFinancials");
    internal string DatalakeDatabase { get; init; } = Name("VWP_SQLSERVER_DATALAKE_DATABASE", "Datalake");
    internal int MaxRows { get; init; } = Number("VWP_ETL_MAX_ROWS", 1000, 1);
    internal long MaxBytes { get; init; } = LongNumber("VWP_ETL_MAX_BYTES", 1048576, 1024);
    internal long MaxWindowRows { get; init; } = LongNumber("VWP_ETL_MAX_WINDOW_ROWS", 1000000, 1);
    internal long MaxWindowBytes { get; init; } = LongNumber("VWP_ETL_MAX_WINDOW_BYTES", 268435456, 1024);
    internal int PollSeconds { get; init; } = Number("VWP_ETL_POLL_SECONDS", 30, 1);
    internal int RetentionYears { get; init; } = Number("VWP_ETL_RETENTION_YEARS", 5, 5);
    internal int CaptureRetentionDays { get; init; } = Number("VWP_ETL_CT_RETENTION_DAYS", 7, 1);
    internal int CommandTimeoutSeconds { get; init; } = Number("VWP_ETL_COMMAND_TIMEOUT_SECONDS", 120, 1);
    internal string? Fault { get; init; } = Environment.GetEnvironmentVariable("VWP_ETL_FAULT");

    internal void Validate()
    {
        _ = new SqlConnectionStringBuilder(ConnectionString);
        if (new[] { AccountsDatabase, FinancialsDatabase, DatalakeDatabase }.Distinct(StringComparer.OrdinalIgnoreCase).Count() != 3)
            throw new ArgumentException("Source and target database names must be distinct.");
        if (MaxRows > MaxWindowRows || MaxBytes > MaxWindowBytes)
            throw new ArgumentException("Window safety budgets must be at least as large as batch budgets.");
        if (MaxWindowRows == long.MaxValue)
            throw new ArgumentException("The window row budget must leave room for an overflow sentinel row.");
        if (Fault is not null && Fault is not "after-stage-commit" and not "before-apply-commit" and not "after-apply-commit" and not "before-checkpoint-commit" and not "after-checkpoint-commit")
            throw new ArgumentException("Unknown fault hook.");
    }

    internal static string Quote(string value) => "[" + value.Replace("]", "]]", StringComparison.Ordinal) + "]";
    private static string Required(string key) => Environment.GetEnvironmentVariable(key) is { Length: > 0 } value
        ? value : throw new ArgumentException($"Set {key} through the environment.");
    private static string Name(string key, string fallback)
    {
        var value = Environment.GetEnvironmentVariable(key) ?? fallback;
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value.Any(char.IsControl))
            throw new ArgumentException($"Invalid database identifier in {key}.");
        return value;
    }
    private static int Number(string key, int fallback, int minimum)
    {
        var raw = Environment.GetEnvironmentVariable(key);
        if (raw is null) return fallback;
        return int.TryParse(raw, out var number) && number >= minimum ? number
            : throw new ArgumentException($"{key} must be an integer of at least {minimum}.");
    }
    private static long LongNumber(string key, long fallback, long minimum)
    {
        var raw = Environment.GetEnvironmentVariable(key);
        if (raw is null) return fallback;
        return long.TryParse(raw, out var number) && number >= minimum ? number
            : throw new ArgumentException($"{key} must be an integer of at least {minimum}.");
    }
}
