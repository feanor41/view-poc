# Cross-database views and EF Core mapping

## PoC question

Can a service database expose rows owned by Accounts through a local SQL Server view and let a service-local EF Core model query them as a read-only graph, without copying the rows or synchronizing them?

For a single SQL Server instance, the answer for ordinary views is yes. Each consumer database can contain a local view with an explicit projection such as:

```sql
CREATE VIEW dbo.Accounts AS
SELECT Id, Name, Status, CreatedAt
FROM [Accounts].[dbo].[Accounts];
```

The SQL view resides in the consumer database. The three-part source name resolves against another database on the same instance. The view does not copy data; a later query reads the source rows subject to SQL Server's transaction isolation rules. With ordinary read-committed queries, a query started after the source transaction commits observes the committed update. A query running in an existing snapshot can continue to see its earlier snapshot.

The consumer views use explicit column lists. Changes to a source schema must be coordinated with the view and consumer clone. Because these views are not schema-bound, SQL Server may require sp_refreshview after source metadata changes.

## Service-local EF Core models

Each consumer declares its own Account, Asset, and/or ContactInformation CLR types with a stable key and the same column shape as the projected view. The types live in separate service assemblies and do not reference the Accounts assembly.

```csharp
modelBuilder.Entity<Account>(entity =>
{
    entity.HasKey(account => account.Id);
    entity.ToExternalReadOnlyView(
        viewName: "Accounts",
        sourceDatabase: "Accounts",
        sourceTable: "Accounts");

    entity.HasMany(account => account.Assets)
        .WithOne(asset => asset.Account)
        .HasForeignKey(asset => asset.AccountId);
});
```

ToExternalReadOnlyView maps queries to the consumer-local view and marks the EF entity metadata with its source name. Keys and relationships remain configured, so EF can materialize an Account and its view-backed related rows. The SQL databases do not define foreign keys to objects in another database; those relationships exist only in the consumer EF model.

The mapping extension does not create the SQL view. EF Core's ToView assumes the view already exists and does not create it in a migration. Here, database/init.sql owns the initial SQL schema, permissions, and view definitions.

## Read-only enforcement has two layers

Mapping a keyed entity with ToView is a query mapping, but it is not the complete safety boundary. EF Core allows a regular entity to have both a view mapping for queries and an explicit table mapping for updates. A simple SQL view may also be updatable.

This PoC uses two controls:

1. Consumer contexts default to no-tracking reads. ReadOnlyViewSaveChangesInterceptor detects changes and rejects Added, Modified, or Deleted states for entities marked by the mapping extension. The diagnostic routes exercise all three states.
2. Each consumer SQL login has SELECT access to its local views and the source tables needed to resolve them, with INSERT, UPDATE, and DELETE denied on those views and source objects. This database boundary also rejects EF bulk updates and raw DML that bypass SaveChanges.

The SaveChanges interceptor alone does not guard ExecuteUpdate, ExecuteDelete, or raw SQL. In this model, EF Core rejects ExecuteUpdate for a ToView-only entity during translation because that entity is not mapped to a table. The PoC probes this mapping-level rejection, then issues parameterized raw UPDATE statements against both a consumer view and the three-part Accounts source table. SQL Server rejects those statements under the consumer login's DML denials.

Consumer queries use AsNoTrackingWithIdentityResolution. Normal reads are read-only, and a fresh request queries SQL again so it does not return old values retained in a long-lived EF change tracker. A long-lived tracking context can return its existing tracked instance without overwriting it with new database values.

## Cross-database authorization and ownership

The SQL setup creates distinct logins for Accounts and each consumer. Consumer logins receive SELECT on only their local views and the source tables those views need. They receive no DML on Accounts data. The PoC leaves TRUSTWORTHY and database-level chaining off and does not enable server-wide cross-database ownership chaining.

Granting source SELECT to a consumer login means that login can also issue a direct read against those source objects. The PoC's security objective is to prevent consumer mutation and synchronization, not to hide the source from read-capable SQL principals. A stricter object-hiding design would need a separately evaluated permission-signing or ownership strategy.

## Indexed (materialized) views

SQL Server's materialized-view equivalent is an indexed view. SQL Server requires indexed views to use SCHEMABINDING, and the view can reference only base tables in the same database. It also requires a unique clustered index first, plus deterministic expressions and specific session SET options.

That rules out putting an indexed view in Financials, Cases, or Notifications that directly references [Accounts] tables. A view in Accounts could be indexed over Accounts-owned tables, but it is maintained during writes and moves the materialization cost to the owning service. A consumer could then read that owner-side object through an ordinary cross-database view; that remains an ordinary consumer view and is not a cross-database indexed view.

The runtime PoC therefore uses ordinary views. Indexed-view behavior and performance would need a separate benchmark with representative write rates and query shapes before being considered.

## Primary documentation

- [CREATE VIEW (Transact-SQL)](https://learn.microsoft.com/en-us/sql/t-sql/statements/create-view-transact-sql?view=sql-server-ver17)
- [Create indexed views](https://learn.microsoft.com/en-us/sql/relational-databases/views/create-indexed-views?view=sql-server-ver17)
- [EF Core entity types and view mapping](https://learn.microsoft.com/en-us/ef/core/modeling/entity-types#view-mapping)
- [Keyless entity types](https://learn.microsoft.com/en-us/ef/core/modeling/keyless-entity-types)
- [Tracking vs. no-tracking queries](https://learn.microsoft.com/en-us/ef/core/querying/tracking)
- [EF Core interceptors](https://learn.microsoft.com/en-us/ef/core/logging-events-diagnostics/interceptors)
- [ExecuteUpdate and ExecuteDelete](https://learn.microsoft.com/en-us/ef/core/saving/execute-insert-update-delete)
- [SQL Server foreign-key limitations](https://learn.microsoft.com/en-us/sql/t-sql/statements/create-table-transact-sql?view=sql-server-ver17#foreign-key-constraints)
- [Cross-database ownership chaining](https://learn.microsoft.com/en-us/sql/database-engine/configure-windows/cross-db-ownership-chaining-server-configuration-option?view=sql-server-ver17)
- [SQL Server transaction isolation](https://learn.microsoft.com/en-us/sql/t-sql/statements/set-transaction-isolation-level-transact-sql?view=sql-server-ver17)
