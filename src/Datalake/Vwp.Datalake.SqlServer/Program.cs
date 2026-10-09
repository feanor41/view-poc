using System.Text.Json;
using Vwp.Datalake.SqlServer;

if (args.Length == 0 || args.Any(argument => argument is "--help" or "-h"))
{
    Console.WriteLine("Usage: bootstrap | sync [--loop] [--resnapshot] | observe. Configure VWP_SQLSERVER_CONNECTION and VWP_SQLSERVER_*_DATABASE through the environment.");
    return 0;
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
try
{
    if (args.Skip(1).Any(argument => argument is not "--loop" and not "--resnapshot") ||
        (args[0] != "sync" && args.Length > 1))
        throw new ArgumentException("Unsupported command options.");
    var settings = new Settings();
    settings.Validate();
    var worker = new Worker(settings);
    switch (args[0])
    {
        case "bootstrap": await worker.BootstrapAsync(cancellation.Token); break;
        case "observe": await worker.ObserveAsync(cancellation.Token); break;
        case "sync":
            var reset = args.Contains("--resnapshot");
            do
            {
                var pending = await worker.SyncAsync(reset, cancellation.Token);
                reset = false;
                if (!args.Contains("--loop")) break;
                if (!pending) await Task.Delay(TimeSpan.FromSeconds(settings.PollSeconds), cancellation.Token);
            } while (!cancellation.IsCancellationRequested);
            break;
        default: throw new ArgumentException("Unknown command. Use bootstrap, sync or observe.");
    }
    return 0;
}
catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return 0; }
catch (Exception exception)
{
    // SQL and connection exception text can contain user data or credential-bearing configuration.
    Console.Error.WriteLine(JsonSerializer.Serialize(new
    {
        status = "failed",
        type = exception.GetType().Name,
        sqlNumber = exception is Microsoft.Data.SqlClient.SqlException sql ? sql.Number : (int?)null,
        reason = exception switch
        {
            WorkerException => exception.Message,
            Microsoft.Data.SqlClient.SqlException { Number: 51001 } => "Staging window safety budget exceeded; increase window budgets explicitly.",
            Microsoft.Data.SqlClient.SqlException { Number: 51002 } => "A source row exceeds the batch byte budget; increase the byte budget explicitly.",
            Microsoft.Data.SqlClient.SqlException { Number: 51003 } => "A derived row exceeds the batch byte budget; increase the byte budget explicitly.",
            Microsoft.Data.SqlClient.SqlException { Number: 51004 } => "Derived fanout safety budget exceeded; increase the window row budget explicitly.",
            Microsoft.Data.SqlClient.SqlException { Number: 51005 } => "Datalake is bound to different source names; use a distinct target database.",
            _ => "Operation failed; inspect configuration and server diagnostics securely."
        }
    }));
    return 1;
}
