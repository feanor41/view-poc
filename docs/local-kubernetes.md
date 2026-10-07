# Local Kubernetes environment

The VWP proof of concept uses a dedicated disposable kind cluster named `vwp`, namespace `vwp`, four HTTP APIs, and one SQL Server Developer instance with four databases. All commands target `kind-vwp` explicitly rather than the active kubectl context. Set `VWP_CLUSTER_NAME` consistently to use another dedicated cluster name. See the [architecture overview](architecture.md) for service ownership and the cross-database view flow.

## Prerequisites and pinned images

Use an x86_64 Linux Docker environment with at least 8 GiB available memory, Docker daemon access, Bash, OpenSSL, kubectl, kind v0.33.0, and Python 3.10 or later. The bootstrap downloads images and the API builds restore NuGet packages, so network access is required. The verification and performance scripts use only the Python standard library. Docker Desktop also works with a Linux x86_64 engine; ARM SQL Server emulation is outside this bundle.

| Component | Image |
| --- | --- |
| kind node | `kindest/node:v1.37.0@sha256:a1ed56cfb0e7b93589bdf97c8cd566405a265939e3620fc4f5de89adff580ae5` |
| SQL Server | `mcr.microsoft.com/mssql/server:2022-CU23-ubuntu-22.04` |
| .NET build | `mcr.microsoft.com/dotnet/sdk:10.0.302` |
| .NET runtime | `mcr.microsoft.com/dotnet/aspnet:10.0.12` |

The node digest is the published default for the [official kind v0.33.0 release](https://github.com/kubernetes-sigs/kind/releases/tag/v0.33.0). Microsoft image tags pin the release/patch but remain registry tags; resolve and record their digests before using this bundle as a supply-chain artifact. The four application images are built from this working tree as `vwp/<service>:local-v1`, loaded into kind, and configured with `imagePullPolicy: Never`.

## Start and access

From the repository root:

```bash
./scripts/local-up.sh
./scripts/local-forward.sh
```

`local-up.sh` creates the dedicated cluster if absent, generates secrets, starts SQL Server, waits for its startup query, runs and waits for the schema bootstrap Job, builds/loads the four API images, and waits for their database readiness probes. It removes the completed administrative Job and its SQL ConfigMap. Repeating the script refreshes views and API deployments while preserving existing tables and login passwords. Existing tables are never altered by the bootstrap: future schema changes need explicit migrations or a deliberate disposable-cluster rebuild.

`local-forward.sh` keeps loopback-only forwards in the foreground. Ctrl-C stops all four forwards.
After `local-up.sh` replaces API pods, restart `local-forward.sh` because an existing port-forward can end with the old pods.

| API | Local URL |
| --- | --- |
| Accounts | `http://127.0.0.1:5101` |
| Financials | `http://127.0.0.1:5102` |
| Cases | `http://127.0.0.1:5103` |
| Notifications | `http://127.0.0.1:5104` |

Each API exposes `/health/live` for process liveness and `/health/ready` for database connectivity. The local verifier checks readiness itself, so there is no need to wait with a separate polling command.

## Verify immediate Financials use

With the forwards running, execute:

```bash
python3 scripts/verify-local.py
```

After checking service readiness, the verifier creates a unique Account through Accounts. Its very next HTTP request reads that Account through Financials' local Accounts view, with no polling, delay, or synchronization step. It then posts `accountId`, `description`, and `amount` to Financials `POST /operations`, expects HTTP 201 with the matching AccountId and new OperationId, and reads `GET /operations/{operationId}` to confirm persistence in a separate request. An unknown AccountId must return HTTP 404.

Financials validates the Account through a fresh no-tracking view query before adding or saving its owned Operation. It sets only the Operation's AccountId and leaves the read-only Account graph unattached. The saved row lives in `Financials.dbo.Operations`; the Accounts object remains a view. The verifier subsequently runs the existing source-update visibility and read-only write checks. Each run retains one new Account and one Operation in the disposable databases.

On success, the verifier prints PASS lines for the immediate Account-to-Operation flow and the full set of Account visibility, EF write-rejection, raw SQL permission, and unchanged-read checks. A non-zero exit means at least one check failed. The [performance guide](performance-baseline.md) uses the same local cluster and documents fresh bounded load runs; repeated runs add rows and do not clean them up.

There is no cross-database foreign key: this check validates existence at query time. Concurrent source deletion after validation is not prevented by this minimal flow.

## Data and security model

`database/init.sql` creates Accounts, Financials, Cases, and Notifications. Accounts owns Accounts, Assets, AccountHistory, and ContactInformation. The remaining databases own their minimal business tables. Foreign keys exist only between physical tables inside one database; account/asset references in consumer-owned tables are logical references with no remote FK.

Consumer `dbo` views select explicit columns from `Accounts.dbo` objects:

| Database | Views |
| --- | --- |
| Financials | Accounts, Assets |
| Cases | Accounts, Assets, ContactInformation |
| Notifications | Accounts, ContactInformation |

Each service has its own login. Accounts has CRUD rights on its owned schema. Consumers have CRUD rights on their own physical tables and SELECT on their imported views plus the necessary source tables. INSERT/UPDATE/DELETE is explicitly denied on those views and source objects. Source SELECT grants are deliberate: cross-database authorization is checked using the same login in both databases. Thus the views expose a read model, and database permissions enforce read-only behavior even when code bypasses EF Core SaveChanges. A simple projection view can otherwise be SQL-updatable.

The bootstrap sets `TRUSTWORTHY OFF` and `DB_CHAINING OFF` on each database. It does not enable server-wide cross-database ownership chaining. It is scoped to these dedicated local database names and never runs on an arbitrary SQL host.

Secrets are generated using OpenSSL into a mode-700 temporary directory, passed to `kubectl` via files, and removed on script exit. No password or connection string is committed or printed. Service Secrets contain each runtime connection string; `sql-admin` is used only by SQL Server and the bootstrap; `sql-bootstrap-passwords` retains the matching login passwords for repeatable bootstrap. Existing secrets are reused rather than rotated implicitly. Missing secrets in a partial installation cause the script to stop. Do not enable shell tracing or copy Secret values into logs. Local TLS uses `Encrypt=True;TrustServerCertificate=True` because the disposable SQL instance has no externally trusted certificate.

The SQL Server deployment uses `emptyDir`: data survives container restarts within the same Pod but is lost when the SQL Pod is replaced or the cluster is deleted. This is a disposable proof of concept, with no backup or production durability claim. The script re-creates absent databases/tables after a replacement. Runtime service logins cannot create/alter database objects or administer SQL Server.

## Troubleshooting

```bash
kubectl --context kind-vwp -n vwp get pods,jobs,services
kubectl --context kind-vwp -n vwp logs deployment/sqlserver
kubectl --context kind-vwp -n vwp logs job/sql-bootstrap
kubectl --context kind-vwp -n vwp logs deployment/accounts
```

The bootstrap Job is available after a failed startup, and is removed after success. SQL startup gets up to 450 seconds for its query probe; the bootstrap and deployment wait commands allow up to 600 seconds. If Docker socket access is denied, fix local Docker permissions or run the command from an authorized host shell; no cluster was created by a syntax/static check alone.

## Teardown

```bash
./scripts/local-down.sh
```

This explicitly deletes the dedicated kind cluster, including its four databases, data, and Kubernetes secrets. It does not remove Docker images or touch other clusters. Use it only when the disposable PoC data can be discarded.
