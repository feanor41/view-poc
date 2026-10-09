# Native Datalake verification

The scripts verify the independent SQL Server and PostgreSQL synchronization
console applications. They do not install software, provision containers, deploy
workloads, or touch the existing Kubernetes view demonstration. A coordinator
must first provision separate disposable instances and build the console projects
in Release configuration. The provided runtime helper supports that provisioning:

```sh
scripts/datalake-test-runtime.sh up --runtime-dir /tmp/vwp11-runtime
dotnet build VWP.sln --configuration Release
```

The helper requires Docker and Python 3 already installed. It uses SQL Server
2022 CU23 (Developer) with a 3 GiB memory limit and PostgreSQL 18.3 with a 1 GiB
limit, published only on `127.0.0.1:15043` and `127.0.0.1:15433`. PostgreSQL has
logical replication and ten replication slots/WAL senders. Runtime directory mode
is `0700`; credentials are generated once into `worker.env` mode `0600`. Existing
containers must have the `pitcrew.issue=VWP-11` label, the expected image and
loopback mappings, and their existing private environment file. A stopped or
unowned container is refused. No credentials are silently rotated, no host tools
are installed, and no default deletion command is provided. No optional Python database driver is required: fixtures
and queries use the database tools already present inside each test container.

## Isolated runtime

Use a private environment file outside Git, mode `0600`, containing raw
`KEY=value` lines; do not source it as a shell script. Supply the following keys:

- `VWP_DATALAKE_DISPOSABLE=1`.
- `VWP_SQLSERVER_CONTAINER` and `VWP_POSTGRES_CONTAINER`: disposable Docker
  container names beginning with `vwp11-`.
- `VWP_SQLSERVER_CONNECTION` and `VWP_POSTGRES_CONNECTION`: connection strings
  for the console applications. Credentials remain in the private file.
- `VWP_SQLSERVER_ACCOUNTS_DATABASE`, `VWP_SQLSERVER_FINANCIALS_DATABASE`, and
  `VWP_SQLSERVER_DATALAKE_DATABASE`: three distinct names beginning with `vwp11_`.
- Equivalent `VWP_POSTGRES_*_DATABASE` keys with three distinct `vwp11_` names.
- `VWP_POSTGRES_SLOT_PREFIX`: unique replication-slot prefix owned by this test
  runtime; use `vwp11_` plus a unique suffix.

The SQL Server container must provide `/opt/mssql-tools18/bin/sqlcmd` and its
`MSSQL_SA_PASSWORD` environment variable. The script passes no password in command
arguments. PostgreSQL fixtures use `docker exec -u postgres psql` with the local
socket; the disposable image must allow that administrator connection. The
PostgreSQL instance needs logical replication configured and sufficient slots for
both source databases. These privileges are for isolated tests only.

Build before running; scripts use `dotnet run --no-build --configuration Release`.
`--configuration Debug` is available when deliberately testing a Debug build.
Docker access permissions and local connection ports belong to the provisioning
workflow, not to these scripts. All evidence paths must be outside the checkout.
The scripts refuse missing isolation markers, unexpected container names,
non-isolated database names, or a world/group-readable environment file.

## Behavioral integration checks

```sh
python3 scripts/datalake-verify.py --env-file /tmp/vwp11-runtime/worker.env \
  --engine both --output /tmp/vwp11-runtime/evidence/native-verification.json
```

The verifier bootstraps the synthetic sources and target, then checks:

1. Initial snapshot with concurrent committed Account writes; the final mirror
   and projection must match after incremental drain. Existing historical rows
   are copied without creating new business history.
2. Inserts, updates, deletes and Operation reparenting. Transaction amount changes, reparenting and deletion update mirrors and
   PostgreSQL reporting transaction totals/counts. A joined Account change
   invalidates an unchanged Operation. An Operation survives a missing Account
   with `account_missing`, and its join is restored when the Account returns.
3. Failures before target commit and after durable target work, followed by
   process restart and replay. Uncommitted effects remain absent; replay preserves
   the exact projection and source mirrors.
4. A two-row batch limit over twelve independent source transactions; repeated
   invocations must converge rather than lose the remainder. PostgreSQL never
   splits an indivisible source transaction to satisfy a configured row limit.
5. Oversized byte rows and PostgreSQL indivisible oversized transactions refuse
   atomic application without advancing business progress or acknowledging the
   rejected transaction. A preceding idle PostgreSQL heartbeat may advance the
   financial LSN, but it must remain before the rejected commit. A larger
   configured budget resumes safely. PostgreSQL projection fanout and derived-byte rejection preserve the prior report.
6. A deliberately held writer lock rejects a second consumer process.
7. SQL Server source decimal precision drift and both engines’ mirror precision
   drift refuse progress before
   publishing rounded values. Both engines also refuse incompatible physical
   reporting amount precision before publishing new financial work. Explicit type/value restoration and resnapshot recover.
8. SQL Server Change Tracking history becomes unavailable after a synthetic
   history-table truncate; PostgreSQL loses an owned replication slot. Normal
   synchronization must refuse to proceed; explicit resnapshot recovers.

Each passing check records elapsed time and its scope. Failed checks retain the
partial evidence. Arbitrary driver error output is withheld because connection
exceptions can contain credentials. A failure is never recorded as a pass.
Check the JSON result and process exit code, not merely the presence of a file.
Repeated runs append uniquely identified synthetic fixtures; use a new disposable
runtime when a clean-size benchmark is required. The verifier does not delete the
container or the databases.

The existing cross-database view APIs require their separate
`scripts/verify-local.py` regression check against the original demonstration.
Passing these synchronization checks alone does not prove original API behavior
or preservation of shared cluster workloads.

## Bounded baseline and burst measurements

```sh
python3 scripts/datalake-benchmark.py --env-file /tmp/vwp11-runtime/worker.env \
  --engine both --accounts 100 --operations 1000 \
  --rate 58 --duration-seconds 10 --burst-multiplier 2 --burst-seconds 5 \
  --batch-rows 1000 --batch-bytes 1048576 --cadence-seconds 1 \
  --retention-years 5 --output /tmp/vwp11-runtime/evidence/native-benchmark.json
```

Parameters cover synthetic seed size, offered rate, burst multiplier or explicit burst rate, duration, batch rows
and bytes, observation cadence, drain deadline, and retention configuration.
The runner records seed snapshot duration, baseline/final consumer observations,
committed and observed row counts, maximum pending rows, backlog samples, sync
invocation durations, and p50/p95/p99/max commit-to-observed-report latency.
Commit timing begins after the source SQL process returns; the report timing ends
when a polling query observes the row. The measurement includes polling delay and
host process overhead. Offered rate can exceed achieved rate; both counts and
wall-clock throughput and missed schedule ticks are reported. Source writes use
100 ms batches to reduce process-per-row overhead; PostgreSQL polling is explicitly
100 ms for these measurements. The default 58/s approximates the uniform worst-case
daily average; its 116/s burst is a synthetic stress setting, not a new production
capacity requirement. Failed or unfinished profiles fail the run.

The sizing reference is a worst case of two million vehicles at 2.5 business
transactions per vehicle per day: five million transactions/day, about 57.87/s
on a uniform average, and 9.125 billion over five 365-day years before row fanout.
These scripts generate bounded local datasets, never that entire theoretical
history. Local results do not establish worst-case throughput, realistic peak,
multi-year storage feasibility, index/partition maintenance cost, or behavior
under production contention. Retention is configurable upward from five years;
no automatic purge or historical deletion is performed. Business-data retention
is separate from Change Tracking/WAL retention and synchronization freshness.

## Executed local sample (2026-10-08)

The isolated SQL Server 2022 CU23 and PostgreSQL 18.3 runs passed all eighteen
behavioral scenarios, nine per engine. The bounded benchmark added 100 accounts
and 1,000 operations per engine, then offered 580 additional business rows at
58/s for ten seconds and 580 at 116/s for five seconds. All 2,320 offered rows
were committed and observed, with no remaining backlog after drain.

| Engine | Profile | Observed rows | p95 commit-to-observed report |
| --- | --- | ---: | ---: |
| SQL Server | Baseline, 58/s | 580 | 2.339 s |
| SQL Server | Burst, 116/s | 580 | 1.170 s |
| PostgreSQL | Baseline, 58/s | 580 | 2.677 s |
| PostgreSQL | Burst, 116/s | 580 | 2.678 s |

This is one short synthetic sample using a 1,000-row/1 MiB capture budget,
100 ms PostgreSQL polling, and one-second host observation cadence. The databases
already contained earlier verification fixtures: SQL Server had 109 accounts and
77 operations; PostgreSQL had 10 accounts and 60 operations before benchmark
seeding. SQL Server's source producer missed 42 baseline and six burst schedule
ticks by more than 100 ms; PostgreSQL missed none. Catch-up batching and warm-up
can affect these samples. They do not establish a production capacity limit or
multi-year feasibility.

Private JSON evidence includes exact preseed counts, backlog and percentile
samples, harness hashes and console binary hashes. The recorded paths are
`/tmp/vwp11-runtime/evidence/native-verification-final.json` and
`/tmp/vwp11-runtime/evidence/native-benchmark-final.json`; they are local evidence,
not repository or published artifacts. VWP-11 records the durable verification
summary and its independent review.
