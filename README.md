# VWP: cross-database views with EF Core

VWP is a runnable proof of concept for four .NET 10 services on one SQL Server instance. Each service owns a separate database. Financials, Cases, and Notifications expose service-local EF Core clones of selected Accounts entities through ordinary SQL Server views.

## Services and data ownership

| Service | Owned entities | Accounts views |
| --- | --- | --- |
| Accounts | Account, Asset, AccountHistory, ContactInformation | — |
| Financials | Operation, Transaction, PaymentMean, CollectionOrder | Account, Asset |
| Cases | Case, CaseType, CaseGroup | Account, Asset, ContactInformation |
| Notifications | Notification, NotificationType | Account, ContactInformation |

The consumer CLR types are local to their service assemblies. They share no entity assembly with Accounts. EF Core maps their keys and navigation graphs to local views, while the SQL bootstrap creates the views and grants read-only access.

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

The verification writes and updates Account, Asset, and ContactInformation through Accounts; reads the consumer clones; confirms the updated values appear on subsequent requests; and probes INSERT, UPDATE, and DELETE through EF Core, ExecuteUpdate translation, and raw SQL against the consumer views and source tables.

Accounts API routes:

- PUT /accounts/{accountId}
- PUT /accounts/{accountId}/assets/{assetId}
- PUT /accounts/{accountId}/contact-information
- GET /accounts/{accountId}

Each consumer exposes GET /accounts/{accountId} and diagnostic POST /accounts/{accountId}/write-probe/{insert|update|delete} plus /write-probe/execute-update routes. The probe routes exist to make the read-only controls visible in this PoC.

## Mapping decision and SQL Server limits

Read [the focused mapping analysis](docs/ef-core-cross-database-views.md) for the ToExternalReadOnlyView extension, EF Core write protection, SQL permissions, freshness behavior, and the separate indexed-view assessment.

The short version: ordinary three-part-name views work when the source and consumer databases are on the same SQL Server instance. A normal view query reads the current committed source rows; it does not maintain a replica. SQL Server indexed views cannot reference another database, so they cannot directly replace these consumer views.
