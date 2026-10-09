using System.Reflection;
using System.Text.Json;
using Npgsql;

namespace Vwp.Datalake.Postgres;

internal static class Program
{
    internal static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length == 0 || args[0] is "help" or "--help")
            {
                Console.WriteLine("Commands: bootstrap | snapshot [--resnapshot] | sync [--seconds N] | observe. Credentials: VWP_POSTGRES_CONNECTION. PostgreSQL 18, wal_level=logical required. Bootstrap alone creates schemas; resnapshot is an explicit owned-slot reset. No purge is implemented.");
                return 0;
            }
            var options = Options.Load();
            if (args[0] == "bootstrap") { await BootstrapAsync(options); return 0; }
            await using var writer = await options.OpenAsync(options.DatalakeDatabase);
            if (args[0] == "observe") { await CaptureWorker.ObserveAsync(options, writer); return 0; }
            await using (var command = new NpgsqlCommand("SELECT pg_try_advisory_lock(867530911)", writer))
                if (!(bool)(await command.ExecuteScalarAsync())!) throw new WorkerException("Another Datalake writer holds the advisory lock.");
            Log(new { status = "writer-lock-held" });
            if (options.LockHoldMilliseconds > 0) await Task.Delay(options.LockHoldMilliseconds);
            await CaptureWorker.ValidateTargetAsync(options, writer);
            var worker = new CaptureWorker(options);
            switch (args[0])
            {
                case "snapshot":
                    if (args.Skip(1).Any(x => x != "--resnapshot")) throw new WorkerException("Unknown snapshot argument.");
                    foreach (var source in options.Sources) await worker.SnapshotAsync(source, args.Contains("--resnapshot"));
                    break;
                case "sync":
                    var seconds = 30;
                    if (args.Length > 1 && (args.Length != 3 || args[1] != "--seconds" || !int.TryParse(args[2], out seconds) || seconds is < 1 or > 86400))
                        throw new WorkerException("Use sync --seconds N, 1 <= N <= 86400.");
                    using (var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(seconds)))
                    {
                        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
                        try { await Task.WhenAll(options.Sources.Select(async x =>
                        {
                            try { await worker.SyncAsync(x, cancellation.Token); }
                            catch { cancellation.Cancel(); throw; }
                        })); }
                        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
                    }
                    break;
                default: throw new WorkerException("Unknown command. Use --help.");
            }
            return 0;
        }
        catch (Exception error)
        {
            // Connection strings, row values and PostgreSQL detail fields are never logged.
            Log(new { status = "failed", error = error is WorkerException ? error.Message : error.GetType().Name,
                sqlState = (error as PostgresException)?.SqlState });
            return 1;
        }
    }

    internal static void Log(object value) => Console.WriteLine(JsonSerializer.Serialize(value));

    internal static async Task ExecuteAsync(NpgsqlConnection connection, string sql, NpgsqlTransaction? transaction = null)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task BootstrapAsync(Options options)
    {
        await using var admin = await options.OpenAsync("postgres");
        foreach (var database in options.Sources.Select(x => x.Database).Append(options.DatalakeDatabase))
        {
            await using var exists = new NpgsqlCommand("SELECT EXISTS(SELECT FROM pg_database WHERE datname=@name)", admin);
            exists.Parameters.AddWithValue("name", database);
            if (!(bool)(await exists.ExecuteScalarAsync())!) await ExecuteAsync(admin, $"CREATE DATABASE {Options.Quote(database)}");
        }
        foreach (var item in options.Sources.Select(x => (x.Database, x.Name)).Append((options.DatalakeDatabase, "datalake")))
        {
            await using var connection = await options.OpenAsync(item.Item1);
            var resource = Assembly.GetExecutingAssembly().GetManifestResourceNames().Single(x => x.EndsWith($".{item.Item2}.sql", StringComparison.Ordinal));
            await using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            await ExecuteAsync(connection, await reader.ReadToEndAsync());
            if (item.Item2 == "datalake")
            {
                await using var settings = new NpgsqlCommand("INSERT INTO sync.settings(singleton,retention_years) VALUES(true,@years) ON CONFLICT(singleton) DO UPDATE SET retention_years=EXCLUDED.retention_years", connection);
                settings.Parameters.AddWithValue("years", options.RetentionYears);
                await settings.ExecuteNonQueryAsync();
            }
        }
        Log(new { status = "bootstrapped", retentionYears = options.RetentionYears, purgeEnabled = false });
    }
}
