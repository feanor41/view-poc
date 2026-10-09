namespace Vwp.Datalake.Postgres;

// Only intentional worker messages may be emitted. Provider exception messages
// can contain connection details or source values and remain redacted.
internal sealed class WorkerException(string message) : Exception(message);
