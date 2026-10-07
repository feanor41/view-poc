# VWP: cross-database views with EF Core

VWP is a runnable proof of concept for four .NET 10 services on one SQL Server instance. Each service owns a separate database. Financials, Cases, and Notifications expose service-local EF Core clones of selected Accounts entities through ordinary SQL Server views.

## Documentation map

- [Architecture and data ownership](docs/architecture.md) — service boundaries, shared read models, request flow, and design limits.
- [Local Kubernetes guide](docs/local-kubernetes.md) — prerequisites, setup, verification, troubleshooting, and teardown.
- [EF Core mapping analysis](docs/ef-core-cross-database-views.md) — view-only mapping, write rejection, SQL permissions, and indexed-view assessment.
- [Performance baseline and reproduction](docs/performance-baseline.md) — historical local results and bounded load commands.

For a clean checkout, read the architecture overview, follow the local guide, run its repeatable verifier, and then use the performance guide if you want to collect a separate load result.

## Services and data ownership

Accounts owns the shared records. Financials, Cases, and Notifications keep their own consumer types and read the subsets they need through views in their databases. The [architecture guide](docs/architecture.md) has the full ownership matrix, dependency diagram, and Account-to-Operation data flow.

## Run the local proof of concept

See [the local Kubernetes guide](docs/local-kubernetes.md) for prerequisites, cluster setup, secret handling, and teardown.

```bash
./scripts/local-up.sh
./scripts/local-forward.sh
```

In a second terminal, run the repeatable cross-service verification:

```bash
python3 scripts/verify-local.py
```

The verification writes and updates Account, Asset, and ContactInformation through Accounts; reads the consumer clones; confirms the updated values appear on subsequent requests; and checks native EF Core rejection of INSERT, UPDATE, DELETE, ExecuteUpdate, and ExecuteDelete against view-only entities. Separate raw UPDATE probes check SQL permissions on the consumer views and source tables.

The [performance guide](docs/performance-baseline.md) explains how to run concurrent Account-to-Operation flows and inspect the raw per-request latency and failure results. Its checked-in measurements are a single-node local baseline recorded before the VWP-6 mapping change; they are not production capacity or a post-VWP-6 performance result.

Accounts API routes:

- PUT /accounts/{accountId}
- PUT /accounts/{accountId}/assets/{assetId}
- PUT /accounts/{accountId}/contact-information
- GET /accounts/{accountId}

Each consumer exposes GET /accounts/{accountId} and diagnostic POST /accounts/{accountId}/write-probe/{insert|update|delete} plus /write-probe/execute-update and /write-probe/execute-delete routes. The probe routes exist to make the read-only controls visible in this PoC.

Financials also exposes POST /operations and GET /operations/{operationId}. It validates the Account through its local view before writing its own Operation row.

## Mapping decision and SQL Server limits

Read [the architecture overview](docs/architecture.md) for the service and database layout, then [the focused mapping analysis](docs/ef-core-cross-database-views.md) for the ToExternalReadOnlyView extension, EF Core write protection, SQL permissions, freshness behavior, and the separate indexed-view assessment.

The short version: ordinary three-part-name views work when the source and consumer databases are on the same SQL Server instance. A normal view query reads the current committed source rows; it does not maintain a replica. SQL Server indexed views cannot reference another database, so they cannot directly replace these consumer views.
