# VWP architecture and data ownership

VWP tests whether each microservice can keep its own EF Core domain types while reading selected data from the Accounts service through ordinary SQL Server views. The local environment runs four .NET 10 APIs and one SQL Server 2022 Developer instance. Each API connects to its own database; the consumer databases also contain views that select from Accounts on that same SQL Server instance.

## Services, databases, and views

~~~mermaid
flowchart LR
    subgraph SQL["One SQL Server instance"]
        subgraph ADB["Accounts database"]
            AT["Owned tables<br/>Accounts · Assets · AccountHistory · ContactInformation"]
        end
        subgraph FDB["Financials database"]
            FT["Owned tables<br/>Operations · Transactions · PaymentMeans · CollectionOrders"]
            FV["Read views<br/>Accounts · Assets"]
        end
        subgraph CDB["Cases database"]
            CT["Owned tables<br/>Cases · CaseTypes · CaseGroup"]
            CV["Read views<br/>Accounts · Assets · ContactInformation"]
        end
        subgraph NDB["Notifications database"]
            NT["Owned tables<br/>Notifications · NotificationTypes"]
            NV["Read views<br/>Accounts · ContactInformation"]
        end
        AT -->|"three-part-name SELECT"| FV
        AT -->|"three-part-name SELECT"| CV
        AT -->|"three-part-name SELECT"| NV
    end
    AA["Accounts API"] --> ADB
    FA["Financials API"] --> FDB
    CA["Cases API"] --> CDB
    NA["Notifications API"] --> NDB
~~~

Each consumer owns a database and CLR types in its own service assembly. It does not reference Accounts entity assemblies. The SQL views provide the shared columns locally, while consumer EF models keep their own keys and navigation relationships.

| Service / database | Physical tables owned by the service | Accounts-backed views in this database |
| --- | --- | --- |
| Accounts | Accounts, Assets, AccountHistory, ContactInformation | — |
| Financials | Operations, Transactions, PaymentMeans, CollectionOrders | Accounts, Assets |
| Cases | Cases, CaseTypes, CaseGroup | Accounts, Assets, ContactInformation |
| Notifications | Notifications, NotificationTypes | Accounts, ContactInformation |

The database bootstrap in database/init.sql owns the tables, view definitions, logins, and grants. A view uses an explicit projection such as Financials.dbo.Accounts selecting from Accounts.dbo.Accounts. It does not store another copy of the rows. The SQL Server instance must host all four databases because these three-part names do not provide a cross-server link.

## Account-to-Operation flow

The repeatable local verifier exercises the key cross-boundary path:

1. Accounts accepts a write for a unique Account and commits it to Accounts.dbo.Accounts.
2. The verifier's next HTTP request reads the Account through Financials.dbo.Accounts. There is no polling, queue, replica table, or synchronization worker in between.
3. Financials validates AccountId with a fresh no-tracking query of that view, then writes only its own Operations row and scalar AccountId.
4. A later request reads the Operation back from Financials.

Under the normal read-committed request path, a new view query after the source transaction commits can observe that committed row. A query already running inside an older snapshot may continue to see its earlier snapshot. This design removes data-copy lag; it does not create a cross-database foreign key or guarantee that an Account cannot be deleted just after Financials validates it.

## Read-only consumer mapping

Consumer Account, Asset, and ContactInformation clones use the shared ToExternalReadOnlyView mapping extension and retain keys and EF relationships. ReadOnlyViewsDbContext registers a final-model convention that rejects a marked entity if it also gains a table, table-fragment, or write stored-procedure mapping. With a ToView-only model, EF Core 10.0.12 rejects mapped SaveChanges writes and rejects ExecuteUpdate/Delete when translating the query.

Raw SQL bypasses the EF mapping. SQL grants SELECT on consumer views and the source objects they need, then explicitly denies INSERT, UPDATE, and DELETE to each consumer login. This PoC protects writes through both EF and SQL permissions; it does not hide Accounts source data from a consumer principal that has SELECT permission. See the [EF Core mapping analysis](ef-core-cross-database-views.md) for behavior and edge cases.

## Local operation and evidence

Start the disposable local environment with the [Kubernetes guide](local-kubernetes.md). It documents prerequisites, secret handling, local-only port forwards, verification, troubleshooting, and deletion of the dedicated kind cluster. The [performance guide](performance-baseline.md) includes commands for new bounded runs and the raw JSON evidence already collected.

The checked-in benchmark includes a 3,510-flow baseline before VWP-6 and a 3,510-flow repeat after VWP-6; both completed every scheduled Account-to-Operation flow without failures at offered rates from 2 to 150 flows per second. Each used one kind node, one pod per API, and one SQL Server pod on one developer machine. The [performance guide](performance-baseline.md) has the raw evidence and comparison. These are local characterizations, not production capacity or horizontal-scaling claims.

## Indexed-view assessment

SQL Server indexed views require schema binding and can reference base tables only in the same database. An indexed view in a consumer database therefore cannot directly materialize Accounts tables across the database boundary. Accounts could maintain an indexed view over its own tables and consumers could read it through another ordinary view, but writes would carry the materialization cost at the owner. VWP uses ordinary views for runtime; the [mapping analysis](ef-core-cross-database-views.md#indexed-materialized-views) explains the trade-off and links the primary SQL Server documentation.
